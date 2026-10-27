// 职责：咪咕音乐音源 provider（HTTP 直链型，含加密响应解密）。
// 端点链（2026-10 实测）：
//   1. 搜索：app.c.nf.migu.cn/MIGUM2.0/v1.0/content/search_all.do —— 零签名，仅需 Referer；返回 copyrightId/contentId/songId/歌手/专辑/时长/封面；
//   2. 详情：app.c.nf.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do —— 按 copyrightId 取 contentId 等完整元数据；
//   3. 下载：c.musicapp.migu.cn/strategy/listen-url/h5/v2.4 —— 响应为自定义加密二进制（头 0xAB 0xCD 0x01 <offset>，
//      明文[i] = 密文[i+4] + offset - key[i % keyLen]），解密后 JSON 含 data.url（PQ 128k），
//      再按音质替换 URL 路径段与扩展名得到 HQ(320k)/SQ(无损flac) 直链。
// 优势：咪咕为中国移动旗下，拥有大量腾讯独家版权曲（如周杰伦原曲），且全链路无需登录/签名/反爬。

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>咪咕音乐音源提供者。</summary>
/// <remarks>
/// 咪咕 WAF 按 TLS 指纹拦截 .NET HttpClient（HTTP 端点可用，HTTPS 端点握手即被 RST）。
/// 因此所有 HTTPS 请求（h5/v2.4 取直链 + CDN 音频文件下载）走系统 curl.exe 子进程。
/// </remarks>
public sealed partial class MiguSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider, ISourceFileDownloader
{
    /// <summary>系统 curl.exe 路径（Windows 自带于 System32）。</summary>
    private static readonly string CurlPath = ResolveCurlPath();

    private static string ResolveCurlPath()
    {
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "curl.exe");
        if (File.Exists(sys32)) return sys32;
        return "curl"; // 回退到 PATH
    }
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称。</summary>
    public const string HttpClientName = "migu";

    private const string SearchBase = "http://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/search_all.do";
    private const string ResourceInfoBase = "http://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do";
    private const string ListenUrlH5Base = "https://c.musicapp.migu.cn/strategy/listen-url/h5/v2.4";

    /// <summary>h5/v2.4 加密响应的解密密钥（来自 migu-api-enhanced 逆向）。</summary>
    private const string DecryptKey = "Jk8qzuePiJ1qE3mDYhLQ3T73DtDoAhLP";

    /// <summary>音质 → URL 路径段（与 JS encodeURI 一致：中文转义，斜杠保留）。</summary>
    private static readonly Dictionary<string, string> ToneUrlSegments = new()
    {
        ["PQ"] = "%E6%A0%87%E6%B8%85%E9%AB%98%E6%B8%85/MP3_128_16_Stero",  // 标清高清/MP3_128_16_Stero
        ["HQ"] = "%E6%A0%87%E6%B8%85%E9%AB%98%E6%B8%85/MP3_320_16_Stero",  // 标清高清/MP3_320_16_Stero
        ["SQ"] = "%E6%AD%8C%E6%9B%B2%E4%B8%8B%E8%BD%BD/flac",              // 歌曲下载/flac
    };

    /// <summary>音质 → 文件扩展名。</summary>
    private static readonly Dictionary<string, string> ToneExtensions = new()
    {
        ["PQ"] = ".mp3",
        ["HQ"] = ".mp3",
        ["SQ"] = ".flac",
    };

    /// <inheritdoc />
    public string Provider => "migu";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Migu.Enabled;

    /// <inheritdoc />
    public string? DisabledReason => IsEnabled ? null : "咪咕音源未启用（SourceDownload:Migu:Enabled=false）。";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{SearchBase}?text={Uri.EscapeDataString(query)}&pageNo=1&pageSize={Math.Clamp(limit, 1, 50)}"
            + $"&searchSwitch={Uri.EscapeDataString("{\"song\":1}")}&isCopyright=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", "http://m.music.migu.cn/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("songResultData", out var songResultData)
            || !songResultData.TryGetProperty("result", out var list)
            || list.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SourceSearchResult>();
        foreach (var entry in list.EnumerateArray())
        {
            var copyrightId = GetString(entry, "copyrightId");
            var name = GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(copyrightId) || string.IsNullOrWhiteSpace(name))
                continue;
            var artists = entry.TryGetProperty("singers", out var singers) && singers.ValueKind == JsonValueKind.Array
                ? singers.EnumerateArray()
                    .Select(s => GetString(s, "name"))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .ToList()
                : new List<string>();
            var album = entry.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array
                && albums.GetArrayLength() > 0
                ? GetString(albums[0], "name")
                : null;
            long? durationMs = ParseDurationMs(entry, "duration");
            var coverUrl = ExtractCoverUrl(entry);
            var songId = GetString(entry, "id");
            results.Add(new SourceSearchResult(
                Provider,
                copyrightId,
                name,
                artists,
                album,
                durationMs,
                [],
                coverUrl,
                SourcePageUrl: string.IsNullOrWhiteSpace(songId) ? null : $"https://music.migu.cn/v3/music/song/{songId}",
                License: null,
                InferQuality(entry),
                IsLosslessAvailable: HasQuality(entry, "SQ")));
        }
        return results.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{ResourceInfoBase}?copyrightId={Uri.EscapeDataString(providerTrackId)}&resourceType=2";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", "http://m.music.migu.cn/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("resource", out var resource)
            || resource.ValueKind != JsonValueKind.Array
            || resource.GetArrayLength() == 0)
            return null;
        var entry = resource[0];
        var name = GetString(entry, "songName");
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var artists = entry.TryGetProperty("artists", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array
            ? artistsEl.EnumerateArray()
                .Select(a => GetString(a, "name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList()
            : new List<string>();
        var album = GetString(entry, "album");
        long? durationMs = ParseDurationMs(entry, "length");
        var coverUrl = ExtractCoverUrl(entry);
        var songId = GetString(entry, "songId");
        return new SourceSearchResult(
            Provider,
            providerTrackId,
            name,
            artists,
            string.IsNullOrWhiteSpace(album) ? null : album,
            durationMs,
            [],
            coverUrl,
            SourcePageUrl: string.IsNullOrWhiteSpace(songId) ? null : $"https://music.migu.cn/v3/music/song/{songId}",
            License: null,
            InferQuality(entry),
            IsLosslessAvailable: HasQuality(entry, "SQ"));
    }

    /// <summary>按 copyrightId 调 resourceinfo 取 contentId（HTTP 端点，.NET 可直连）。</summary>
    private async Task<string?> GetContentIdAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var infoUrl = $"{ResourceInfoBase}?copyrightId={Uri.EscapeDataString(providerTrackId)}&resourceType=2";
        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, infoUrl);
        infoRequest.Headers.TryAddWithoutValidation("Referer", "http://m.music.migu.cn/");
        infoRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var infoResponse = await client.SendAsync(infoRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!infoResponse.IsSuccessStatusCode)
            return null;
        var infoJson = await infoResponse.Content.ReadAsStringAsync(cancellationToken);
        using var infoDoc = JsonDocument.Parse(infoJson);
        if (infoDoc.RootElement.TryGetProperty("resource", out var res)
            && res.ValueKind == JsonValueKind.Array
            && res.GetArrayLength() > 0)
            return GetString(res[0], "contentId");
        return null;
    }

    /// <inheritdoc />
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        // 1) 取 contentId（resourceinfo 按 copyrightId 查询）。
        var contentId = await GetContentIdAsync(providerTrackId, cancellationToken);
        if (string.IsNullOrWhiteSpace(contentId))
            throw new SourceUnavailableException("咪咕未返回 contentId，无法解析下载地址。");

        // 2) 调 h5/v2.4 取 PQ 直链（响应加密）。
        var desiredTone = options.Value.Migu.ToneFlag;
        if (!ToneUrlSegments.ContainsKey(desiredTone))
            desiredTone = "HQ";
        var listenUrl = $"{ListenUrlH5Base}?contentId={Uri.EscapeDataString(contentId)}"
            + $"&copyrightId={Uri.EscapeDataString(providerTrackId)}&resourceType=2&netType=01"
            + $"&toneFlag=PQ&scene=&lowerQualityContentId={Uri.EscapeDataString(contentId)}";
        var listenHeaders = new Dictionary<string, string>
        {
            ["birth"] = "h5page",
            ["channel"] = "014X031",
            ["Referer"] = "https://y.migu.cn/",
            ["location-data"] = "30.6698676660,104.1229614820",
            ["location-info"] = "",
        };
        // h5/v2.4 为 HTTPS，.NET HttpClient 被咪咕 WAF 按 TLS 指纹拦截，改用 curl。
        var encrypted = await CurlGetBytesAsync(listenUrl, listenHeaders, TimeSpan.FromSeconds(60), cancellationToken);

        // 3) 解密响应。
        var decrypted = DecryptResponse(encrypted);
        if (decrypted is null)
            throw new SourceUnavailableException("咪咕下载响应解密失败。");
        using var listenDoc = JsonDocument.Parse(decrypted);
        if (!listenDoc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String)
            throw new SourceUnavailableException("咪咕未返回下载地址。");
        var pqUrl = urlEl.GetString()!;
        if (string.IsNullOrWhiteSpace(pqUrl) || !pqUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new SourceUnavailableException("咪咕下载地址无效。");

        // 4) 切换到目标音质（替换路径段与扩展名）。
        var finalUrl = ChangeQuality(pqUrl, desiredTone);
        var isLossless = desiredTone == "SQ";
        int? bitrate = desiredTone switch
        {
            "PQ" => 128,
            "HQ" => 320,
            "SQ" => null,
            _ => 320,
        };
        var quality = isLossless ? SourceQualities.Lossless : (bitrate >= 256 ? SourceQualities.High : SourceQualities.Standard);
        var container = ToneExtensions.TryGetValue(desiredTone, out var ext) ? ext.TrimStart('.') : "mp3";
        return new SourceDownloadInfo(finalUrl, container, isLossless, bitrate, quality);
    }

    /// <summary>
    /// 解密 h5/v2.4 响应：头 4 字节 [0xAB, 0xCD, 0x01, offset]，
    /// 之后每个明文字节 = 密文字节 + offset - key[i % keyLen]。
    /// </summary>
    private static byte[]? DecryptResponse(byte[] encrypted)
    {
        if (encrypted.Length < 4)
            return null;
        if (encrypted[0] != 0xAB || encrypted[1] != 0xCD || encrypted[2] != 0x01)
            return null;
        var offset = encrypted[3];
        var keyBytes = Encoding.UTF8.GetBytes(DecryptKey);
        var keyLen = keyBytes.Length;
        var result = new byte[encrypted.Length - 4];
        for (var i = 0; i < result.Length; i++)
            result[i] = (byte)(encrypted[i + 4] + offset - keyBytes[i % keyLen]);
        return result;
    }

    /// <summary>将 PQ 直链的路径段与扩展名替换为目标音质。</summary>
    private static string ChangeQuality(string url, string targetTone)
    {
        // 去掉查询串。
        var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex >= 0 ? url[..queryIndex] : url;
        var query = queryIndex >= 0 ? url[queryIndex..] : string.Empty;

        if (ToneUrlSegments.TryGetValue("PQ", out var pqSegment)
            && ToneUrlSegments.TryGetValue(targetTone, out var targetSegment))
            path = path.Replace(pqSegment, targetSegment, StringComparison.Ordinal);

        // 替换扩展名。
        if (ToneExtensions.TryGetValue(targetTone, out var targetExt))
        {
            var extIndex = path.LastIndexOf('.');
            if (extIndex > 0)
                path = path[..extIndex] + targetExt;
        }
        return path + query;
    }

    /// <summary>从搜索/详情条目推断音质等级。</summary>
    private static string InferQuality(JsonElement entry)
    {
        if (HasQuality(entry, "SQ"))
            return SourceQualities.Lossless;
        if (HasQuality(entry, "HQ"))
            return SourceQualities.High;
        return SourceQualities.Standard;
    }

    /// <summary>检查条目是否提供指定音质（遍历 newRateFormats/rateFormats）。</summary>
    private static bool HasQuality(JsonElement entry, string formatType)
    {
        return TryGetRateFormats(entry, out var formats)
            && formats.EnumerateArray().Any(f => string.Equals(GetString(f, "formatType"), formatType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>优先取 newRateFormats，回退 rateFormats。</summary>
    private static bool TryGetRateFormats(JsonElement entry, out JsonElement formats)
    {
        if (entry.TryGetProperty("newRateFormats", out var newFormats) && newFormats.ValueKind == JsonValueKind.Array)
        {
            formats = newFormats;
            return true;
        }
        if (entry.TryGetProperty("rateFormats", out var oldFormats) && oldFormats.ValueKind == JsonValueKind.Array)
        {
            formats = oldFormats;
            return true;
        }
        formats = default;
        return false;
    }

    /// <summary>从条目提取封面图 URL（优先 albumImgs）。</summary>
    private static string? ExtractCoverUrl(JsonElement entry)
    {
        if (entry.TryGetProperty("albumImgs", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
        {
            var img = GetString(imgs[0], "img");
            if (!string.IsNullOrWhiteSpace(img))
                return img;
        }
        return null;
    }

    /// <summary>宽松解析时长字段（秒数字符串或数字），返回毫秒；解析失败返回 null。</summary>
    private static long? ParseDurationMs(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var el))
            return null;
        double seconds = el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => s,
            _ => 0
        };
        return seconds > 0 ? (long)(seconds * 1000) : null;
    }

    /// <summary>从 JsonElement 安全取字符串字段。</summary>
    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <inheritdoc />
    /// <remarks>
    /// 咪咕 HTTPS 端点（h5/v2.4 取直链 + CDN 音频文件）被 WAF 按 TLS 指纹拦截 .NET HttpClient，
    /// 故完整流程走 curl.exe：取 contentId → curl 取加密直链 → 解密换音质 → curl 下载音频文件。
    /// </remarks>
    public async Task<SourceFileDownload> DownloadToFileAsync(
        string providerTrackId, string outputDirectory, string fileBaseName, CancellationToken cancellationToken)
    {
        var desiredTone = options.Value.Migu.ToneFlag;
        if (!ToneUrlSegments.ContainsKey(desiredTone))
            desiredTone = "HQ";

        // 1) 取 contentId（resourceinfo 为 HTTP，.NET 可直连）。
        var contentId = await GetContentIdAsync(providerTrackId, cancellationToken);
        if (string.IsNullOrWhiteSpace(contentId))
            throw new SourceUnavailableException("咪咕未返回 contentId，无法解析下载地址。");

        // 2) curl 取 h5/v2.4 加密响应并解密得到 PQ 直链。
        var listenUrl = $"{ListenUrlH5Base}?contentId={Uri.EscapeDataString(contentId)}"
            + $"&copyrightId={Uri.EscapeDataString(providerTrackId)}&resourceType=2&netType=01"
            + $"&toneFlag=PQ&scene=&lowerQualityContentId={Uri.EscapeDataString(contentId)}";
        var listenHeaders = new Dictionary<string, string>
        {
            ["birth"] = "h5page",
            ["channel"] = "014X031",
            ["Referer"] = "https://y.migu.cn/",
            ["location-data"] = "30.6698676660,104.1229614820",
            ["location-info"] = "",
        };
        var encrypted = await CurlGetBytesAsync(listenUrl, listenHeaders, TimeSpan.FromSeconds(60), cancellationToken);
        var decrypted = DecryptResponse(encrypted);
        if (decrypted is null)
            throw new SourceUnavailableException("咪咕下载响应解密失败。");
        using var listenDoc = JsonDocument.Parse(decrypted);
        if (!listenDoc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String)
            throw new SourceUnavailableException("咪咕未返回下载地址。");
        var pqUrl = urlEl.GetString()!;
        if (string.IsNullOrWhiteSpace(pqUrl) || !pqUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new SourceUnavailableException("咪咕下载地址无效。");

        // 3) 切换到目标音质。
        var finalUrl = ChangeQuality(pqUrl, desiredTone);
        var extension = ToneExtensions.TryGetValue(desiredTone, out var ext) ? ext : ".mp3";
        var isLossless = desiredTone == "SQ";
        var quality = isLossless ? SourceQualities.Lossless : (desiredTone == "HQ" ? SourceQualities.High : SourceQualities.Standard);
        var outputPath = Path.Combine(outputDirectory, fileBaseName + extension);

        // 4) curl 下载音频文件到隔离区（CDN 同样拦截 .NET HttpClient）。
        await CurlDownloadToFileAsync(finalUrl, new Dictionary<string, string> { ["Referer"] = "https://y.migu.cn/" },
            outputPath, TimeSpan.FromSeconds(600), cancellationToken);

        return new SourceFileDownload(outputPath, extension.TrimStart('.'), quality);
    }

    /// <summary>用 curl.exe 请求 URL 并将响应体作为字节数组返回（用于加密二进制响应）。</summary>
    private static async Task<byte[]> CurlGetBytesAsync(
        string url, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = BuildCurlStartInfo(url, headers, outputFile: null, timeout);
        using var process = new Process { StartInfo = startInfo };
        using var ms = new MemoryStream();
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        if (!process.Start())
            throw new SourceUnavailableException("curl.exe 无法启动。");
        // 异步读取 stdout（二进制流），避免管道缓冲区写满卡死。
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SourceUnavailableException($"curl 未在 {(int)timeout.TotalSeconds}s 内完成。");
        }
        await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new SourceUnavailableException($"curl 请求失败 (exit={process.ExitCode}): {stderr.Trim()}");
        return ms.ToArray();
    }

    /// <summary>用 curl.exe 将 URL 内容下载到指定文件（用于大体积音频文件，curl 直接写盘不占内存）。</summary>
    private static async Task CurlDownloadToFileAsync(
        string url, IReadOnlyDictionary<string, string> headers, string outputPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = BuildCurlStartInfo(url, headers, outputPath, timeout);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new SourceUnavailableException("curl.exe 无法启动。");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SourceUnavailableException($"curl 下载未在 {(int)timeout.TotalSeconds}s 内完成。");
        }
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new SourceUnavailableException($"curl 下载失败 (exit={process.ExitCode}): {stderr.Trim()}");
        }
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new SourceUnavailableException("curl 下载文件为空或不存在。");
    }

    /// <summary>构建 curl ProcessStartInfo：统一参数、头、超时；outputFile 为 null 时输出到 stdout。</summary>
    private static ProcessStartInfo BuildCurlStartInfo(
        string url, IReadOnlyDictionary<string, string> headers, string? outputFile, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CurlPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = outputFile is null,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-sS");
        startInfo.ArgumentList.Add("--max-time");
        startInfo.ArgumentList.Add(((int)timeout.TotalSeconds).ToString());
        startInfo.ArgumentList.Add("--retry");
        startInfo.ArgumentList.Add("2");
        // 仿浏览器 UA，降低被识别概率。
        startInfo.ArgumentList.Add("-A");
        startInfo.ArgumentList.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        foreach (var (name, value) in headers)
        {
            startInfo.ArgumentList.Add("-H");
            startInfo.ArgumentList.Add($"{name}: {value}");
        }
        if (outputFile is not null)
        {
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(outputFile);
        }
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    /// <summary>强制终止 curl 子进程。</summary>
    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
    }
}
