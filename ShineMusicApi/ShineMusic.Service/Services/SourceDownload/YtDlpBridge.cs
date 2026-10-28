// 职责：yt-dlp 子进程桥接（DI 单例，随主后端启动即具备音源能力，无独立 sidecar）。
// 统一管理 yt-dlp.exe：路径定位 → 惰性自检（--version，结果缓存）→ 三类调用：
//   1. 搜索：--flat-playlist --dump-json 逐行解析（ytmusic 用 music.youtube.com/search URL，soundcloud 用 scsearch 前缀）；
//   2. 单曲元数据：--dump-json 完整抓取（artist/album/track 等字段在 music.youtube.com 页面可用）；
//   3. 音频下载：-f bestaudio/best -x --audio-format wav（保留原始码率不二次有损，容器统一 wav 由后端 ffprobe 定级）。
// 二进制缺失或自检失败时 IsAvailable=false + UnavailableReason，ytmusic/soundcloud 两个 provider 优雅降级，
// 不影响主服务与其他音源。所有子进程调用经信号量限流（默认并发 2），并对 YouTube 风控错误给出 cookies 配置提示。

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>yt-dlp 搜索/元数据条目（解析 --dump-json 输出的单行 JSON，字段跨 flat/完整模式取并集）。</summary>
public sealed record YtDlpEntry(
    string Id,
    string? Title,
    string? Uploader,
    long? DurationMs,
    string? WebpageUrl,
    string? ThumbnailUrl,
    string? Artist,
    string? Album);

/// <summary>yt-dlp 歌单（播放列表）元数据：标题 + 条目列表（--flat-playlist --dump-single-json 输出）。</summary>
public sealed record YtDlpPlaylist(string Title, IReadOnlyList<YtDlpEntry> Entries);

/// <summary>yt-dlp 子进程桥接服务（单例，进程退出时由 DI 容器释放信号量）。</summary>
public sealed class YtDlpBridge(
    IOptions<SourceDownloadOptions> options,
    IHostEnvironment environment,
    ILogger<YtDlpBridge> logger) : IDisposable
{
    /// <summary>yt-dlp 子进程并发上限（搜索/元数据/下载共享，避免触发平台限流）。</summary>
    private readonly SemaphoreSlim _processGate = new(2, 2);

    /// <summary>自检修 Gate：--version 只跑一次，结果缓存。</summary>
    private readonly SemaphoreSlim _selfCheckGate = new(1, 1);

    private bool? _selfCheckPassed;
    private string? _selfCheckFailure;

    /// <summary>解析后的 yt-dlp 可执行文件绝对路径（相对路径按内容根目录展开）。</summary>
    public string ResolvedExecutablePath
    {
        get
        {
            var configured = options.Value.YtDlp.ExecutablePath;
            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(configured, environment.ContentRootPath);
        }
    }

    /// <summary>二进制文件是否存在（同步检查，供 provider 的 IsEnabled 属性使用；不代表自检已通过）。</summary>
    public bool BinaryPresent => File.Exists(ResolvedExecutablePath);

    /// <summary>自检失败的原因说明（供 provider 的 DisabledReason 使用）；未自检或自检通过时为 null。</summary>
    public string? UnavailableReason =>
        !BinaryPresent ? $"yt-dlp executable was not found at {ResolvedExecutablePath}." : _selfCheckFailure;

    /// <summary>确保桥接可用：二进制缺失或 --version 自检失败时抛出 <see cref="SourceUnavailableException"/>。</summary>
    public async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        if (!BinaryPresent)
            throw new SourceUnavailableException($"yt-dlp executable was not found at {ResolvedExecutablePath}.");
        if (_selfCheckPassed == true)
            return;
        await _selfCheckGate.WaitAsync(cancellationToken);
        try
        {
            if (_selfCheckPassed is not null)
            {
                if (_selfCheckPassed.Value) return;
                throw new SourceUnavailableException(_selfCheckFailure ?? "yt-dlp self-check failed.");
            }
            try
            {
                var result = await RunProcessAsync(["--version"], TimeSpan.FromSeconds(15), cancellationToken, acquireGate: false);
                if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
                {
                    _selfCheckPassed = true;
                    logger.LogInformation("yt-dlp self-check passed (version {Version}) at {Path}.",
                        result.StdOut.Trim().Split('\n')[0], ResolvedExecutablePath);
                    return;
                }
                _selfCheckPassed = false;
                _selfCheckFailure = $"yt-dlp self-check failed (exit code {result.ExitCode}): {Tail(result.StdErr)}";
            }
            catch (SourceUnavailableException)
            {
                _selfCheckPassed = false;
                _selfCheckFailure = "yt-dlp self-check timed out or the process could not be started.";
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _selfCheckPassed = false;
                _selfCheckFailure = $"yt-dlp self-check failed: {exception.Message}";
            }
            throw new SourceUnavailableException(_selfCheckFailure);
        }
        finally
        {
            _selfCheckGate.Release();
        }
    }

    /// <summary>
    /// 搜索音源平台：target 为搜索 URL（music.youtube.com/search?q=…）或 yt-dlp 搜索前缀（scsearch…）。
    /// 逐行解析 --flat-playlist --dump-json 输出；单条解析失败跳过，整体失败抛 <see cref="SourceUnavailableException"/>。
    /// </summary>
    public async Task<IReadOnlyList<YtDlpEntry>> SearchAsync(string target, int limit, CancellationToken cancellationToken)
    {
        await EnsureAvailableAsync(cancellationToken);
        var args = new List<string>
        {
            target,
            "--flat-playlist",
            "--dump-json",
            "--no-warnings",
            "--ignore-errors",
            "--playlist-end", Math.Clamp(limit, 1, 50).ToString()
        };
        AppendCommonArgs(args);
        var result = await RunProcessAsync(args, TimeSpan.FromSeconds(Math.Clamp(options.Value.YtDlp.SearchTimeoutSeconds, 10, 300)), cancellationToken);
        var entries = ParseEntryLines(result.StdOut);
        // --ignore-errors 下部分条目失败仍返回剩余结果；stdout 全空且退出码非 0 才视为整体失败。
        if (entries.Count == 0 && result.ExitCode != 0)
            throw new SourceUnavailableException(MapProcessError(result.StdErr));
        return entries;
    }

    /// <summary>抓取单曲完整元数据（不下载音频）；未找到/解析失败返回 null，平台级错误抛 <see cref="SourceUnavailableException"/>。</summary>
    public async Task<YtDlpEntry?> GetTrackMetadataAsync(string pageUrl, CancellationToken cancellationToken)
    {
        await EnsureAvailableAsync(cancellationToken);
        var args = new List<string> { pageUrl, "--dump-json", "--no-download", "--no-warnings", "--no-playlist" };
        AppendCommonArgs(args);
        var result = await RunProcessAsync(args, TimeSpan.FromSeconds(Math.Clamp(options.Value.YtDlp.MetadataTimeoutSeconds, 5, 120)), cancellationToken);
        var firstLine = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (firstLine is null)
        {
            if (result.ExitCode != 0)
                throw new SourceUnavailableException(MapProcessError(result.StdErr));
            return null;
        }
        return ParseEntry(firstLine);
    }

    /// <summary>
    /// 下载曲目音频到指定目录：bestaudio/best 源 → ffmpeg 提取为 wav（不二次有损）。
    /// 成功返回最终文件绝对路径；失败抛 <see cref="SourceUnavailableException"/>（调用方负责清理半成品）。
    /// </summary>
    public async Task<string> DownloadAudioAsync(string pageUrl, string outputDirectory, string fileBaseName, CancellationToken cancellationToken)
    {
        await EnsureAvailableAsync(cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        var outputTemplate = Path.Combine(outputDirectory, fileBaseName + ".%(ext)s");
        var args = new List<string>
        {
            "-f", "bestaudio/best",
            "-x", "--audio-format", "wav",
            "-o", outputTemplate,
            "--no-playlist",
            "--no-warnings",
            "--force-overwrites",
            pageUrl
        };
        AppendCommonArgs(args);
        // wav 提取 = 下载 + ffmpeg 解码，时长按下载配置上限放宽（下限 180s 保底）。
        var timeout = TimeSpan.FromSeconds(Math.Max(180, options.Value.Download.TimeoutSeconds));
        var result = await RunProcessAsync(args, timeout, cancellationToken);
        var expected = Path.Combine(outputDirectory, fileBaseName + ".wav");
        if (result.ExitCode == 0 && File.Exists(expected))
            return expected;
        // 兜底：个别场景 yt-dlp 输出扩展名可能与预期不一致，按文件名主干扫描实际产物。
        var produced = Directory.EnumerateFiles(outputDirectory, fileBaseName + ".*")
            .FirstOrDefault(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase));
        if (result.ExitCode == 0 && produced is not null)
            return produced;
        throw new SourceUnavailableException(MapProcessError(result.StdErr));
    }

    /// <summary>
    /// 抓取歌单（播放列表）元数据：--flat-playlist --dump-single-json 一次性输出整个歌单的 JSON。
    /// 返回歌单标题与条目列表；失败抛 <see cref="SourceUnavailableException"/>。
    /// </summary>
    public async Task<YtDlpPlaylist> GetPlaylistAsync(string playlistUrl, CancellationToken cancellationToken)
    {
        await EnsureAvailableAsync(cancellationToken);
        var args = new List<string>
        {
            playlistUrl,
            "--flat-playlist",
            "--dump-single-json",
            "--no-warnings",
            "--ignore-errors"
        };
        AppendCommonArgs(args);
        var result = await RunProcessAsync(args, TimeSpan.FromSeconds(Math.Clamp(options.Value.YtDlp.SearchTimeoutSeconds, 30, 300)), cancellationToken);
        var firstLine = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (firstLine is null)
            throw new SourceUnavailableException(MapProcessError(result.StdErr));
        try
        {
            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;
            var title = GetString(root, "title") ?? "YouTube Music 歌单";
            var entries = new List<YtDlpEntry>();
            if (root.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var entry = ParseEntry(item.GetRawText());
                    if (entry is not null)
                        entries.Add(entry);
                }
            }
            if (entries.Count == 0)
                throw new SourceUnavailableException("The playlist is empty or could not be parsed.");
            return new YtDlpPlaylist(title.Trim(), entries);
        }
        catch (JsonException)
        {
            throw new SourceUnavailableException("The playlist metadata returned by yt-dlp could not be parsed.");
        }
    }

    /// <summary>按配置向参数列表追加通用参数：cookies 文件（YouTube 风控）与 JS 运行时（YouTube 签名解密必需）。</summary>
    private void AppendCommonArgs(List<string> args)
    {
        // 代理（国内访问 YouTube/SoundCloud 必需）：配置后所有 yt-dlp 调用统一走代理
        var proxy = options.Value.YtDlp.Proxy;
        if (!string.IsNullOrWhiteSpace(proxy))
            args.AddRange(["--proxy", proxy.Trim()]);
        var cookiesFile = options.Value.YtDlp.CookiesFile;
        if (!string.IsNullOrWhiteSpace(cookiesFile))
        {
            var resolved = Path.IsPathRooted(cookiesFile) ? cookiesFile : Path.GetFullPath(cookiesFile, environment.ContentRootPath);
            if (File.Exists(resolved))
                args.AddRange(["--cookies", resolved]);
            else
                logger.LogWarning("Configured yt-dlp cookies file was not found at {Path}; continuing without cookies.", resolved);
        }
        // YouTube 的 n-challenge/签名解密依赖外部 JS 运行时（exe 已内置 ejs 求解器，运行时需显式启用）。
        if (!string.IsNullOrWhiteSpace(options.Value.YtDlp.JsRuntimes))
            args.AddRange(["--js-runtimes", options.Value.YtDlp.JsRuntimes]);
        // YouTube CDN 直链 403 需要 PO Token：bgutil 插件 + 本地 node 脚本按需生成（无常驻服务）。
        // 两个目录任一缺失即整体跳过（未部署时优雅降级，不影响 SoundCloud 等免 POT 平台）。
        var yt = options.Value.YtDlp;
        if (!string.IsNullOrWhiteSpace(yt.PotPluginDir) && !string.IsNullOrWhiteSpace(yt.PotServerHome))
        {
            var pluginDir = Path.IsPathRooted(yt.PotPluginDir) ? yt.PotPluginDir : Path.GetFullPath(yt.PotPluginDir, environment.ContentRootPath);
            var serverHome = Path.IsPathRooted(yt.PotServerHome) ? yt.PotServerHome : Path.GetFullPath(yt.PotServerHome, environment.ContentRootPath);
            if (Directory.Exists(pluginDir) && Directory.Exists(serverHome))
            {
                args.AddRange(["--plugin-dirs", pluginDir]);
                args.AddRange(["--extractor-args", $"youtubepot-bgutilscript:server_home={serverHome}"]);
            }
        }
        // player_client 必须作为独立的 --extractor-args 传递（同一串里分号拼接会被吞掉）。
        // mweb 客户端会触发 gvs POT 流程（默认客户端集不触发，CDN 必然 403）。
        if (!string.IsNullOrWhiteSpace(yt.PlayerClients))
            args.AddRange(["--extractor-args", $"youtube:player_client={yt.PlayerClients}"]);
    }

    /// <summary>运行 yt-dlp 子进程：参数列表传递（无 shell 注入面），stdout/stderr 全量捕获，取消/超时即杀进程。</summary>
    private async Task<YtDlpProcessResult> RunProcessAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken, bool acquireGate = true)
    {
        if (acquireGate)
            await _processGate.WaitAsync(cancellationToken);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolvedExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = startInfo };
            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();
            // 事件回调异步吸流，避免管道缓冲区写满导致子进程卡死。
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) stdOut.AppendLine(eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) stdErr.AppendLine(eventArgs.Data); };
            try
            {
                if (!process.Start())
                    throw new SourceUnavailableException("The yt-dlp process could not be started.");
            }
            catch (Exception exception) when (exception is not SourceUnavailableException and not OperationCanceledException)
            {
                throw new SourceUnavailableException($"The yt-dlp process could not be started: {exception.Message}");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                    throw;
                throw new SourceUnavailableException($"yt-dlp did not finish within {(int)timeout.TotalSeconds}s.");
            }
            // WaitForExitAsync 返回后事件回调可能仍在排空，再等一次确保 stdout/stderr 捕获完整。
            try { process.WaitForExit(); } catch (Exception) { /* 进程已退出，忽略排空异常 */ }
            return new YtDlpProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
        }
        finally
        {
            if (acquireGate)
                _processGate.Release();
        }
    }

    /// <summary>把 yt-dlp stderr 映射为脱敏的用户可读错误；识别 YouTube 风控场景并提示 cookies 配置。</summary>
    private static string MapProcessError(string stdErr)
    {
        if (stdErr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
            || stdErr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
            return "The source platform rejected the request (anti-bot check). Configure SourceDownload:YtDlp:CookiesFile to continue.";
        if (stdErr.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || stdErr.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || stdErr.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return "The track is unavailable on the source platform.";
        var tail = Tail(stdErr);
        return string.IsNullOrWhiteSpace(tail)
            ? "The source download helper failed without an error message."
            : $"The source download helper failed: {tail}";
    }

    /// <summary>逐行解析 --dump-json 输出为条目列表；坏行跳过。</summary>
    private static List<YtDlpEntry> ParseEntryLines(string stdOut)
    {
        var entries = new List<YtDlpEntry>();
        foreach (var line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = ParseEntry(line);
            if (entry is not null)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>解析单行 JSON 为统一条目；缺 id 或 title 时返回 null。字段兼容 flat 与完整两种模式。</summary>
    private static YtDlpEntry? ParseEntry(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = GetString(root, "id");
            var title = GetString(root, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                return null;
            // 页面链接：完整模式给 webpage_url，flat 模式给 url（跳转 stub）。
            var pageUrl = GetString(root, "webpage_url") ?? GetString(root, "url");
            // 时长单位是秒（int 或 float 均可能出现）。
            long? durationMs = null;
            if (root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                && duration.TryGetDouble(out var seconds))
                durationMs = (long)(seconds * 1000);
            // 封面：完整模式给 thumbnail 字符串；flat 模式只给 thumbnails 数组（取最后一个，通常分辨率最高）。
            var thumbnail = GetString(root, "thumbnail") ?? GetLastThumbnail(root);
            return new YtDlpEntry(
                id.Trim(),
                title.Trim(),
                Uploader: GetString(root, "uploader") ?? GetString(root, "channel"),
                durationMs,
                pageUrl,
                thumbnail,
                Artist: GetString(root, "artist"),
                Album: GetString(root, "album"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从 thumbnails 数组取最后一个 URL（yt-dlp 按分辨率升序排列）。</summary>
    private static string? GetLastThumbnail(JsonElement root)
    {
        if (!root.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Array)
            return null;
        string? last = null;
        foreach (var item in thumbnails.EnumerateArray())
        {
            var url = GetString(item, "url");
            if (!string.IsNullOrWhiteSpace(url))
                last = url;
        }
        return last;
    }

    /// <summary>取 stderr 的最后一行有效内容（截断 300 字符，避免错误信息过长）。</summary>
    private static string Tail(string stdErr)
    {
        var lines = stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = lines.Length > 0 ? lines[^1] : string.Empty;
        return tail.Length > 300 ? tail[..300] : tail;
    }

    /// <summary>尽力杀死子进程树（超时/取消路径；失败仅忽略）。</summary>
    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* 进程可能已退出 */ }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>子进程运行结果快照。</summary>
    private sealed record YtDlpProcessResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>释放信号量（DI 容器在进程关闭时调用）。</summary>
    public void Dispose()
    {
        _processGate.Dispose();
        _selfCheckGate.Dispose();
    }
}

