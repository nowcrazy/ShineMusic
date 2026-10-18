// MediaProcessingService.cs
// 媒体处理核心服务：封装 ffprobe 探测、TagLib# 标签解析以及 ffmpeg 转码到 AAC(m4a) 的完整流程。
// 该文件包含三个职责内聚的类型：
//   1. MediaProcessingException：媒体处理统一异常，携带机器可读的错误码；
//   2. FfmpegMediaService：调用 ffprobe/ffmpeg 外部进程完成容器探测与转码；
//   3. AudioMetadataParser：通过 TagLib# 解析音频元数据并提取封面，写入隔离存储区。
//
// 注意：FfmpegMediaService 与 AudioMetadataParser 在依赖注入容器中以单例形式注册，
// 其构造依赖（FfmpegOptions、QuarantineStoragePaths）均为无状态配置/路径解析器，
// 多线程并发调用安全；外部进程调用互不共享状态，可被多个导入任务并行使用。

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ShineMusic.Api.Data;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Options;

namespace ShineMusic.Api.Services;

/// <summary>
/// 媒体处理统一异常。
/// 携带机器可读的错误码（Code），便于上层根据错误类型向用户返回友好提示或进行重试决策。
/// </summary>
public sealed class MediaProcessingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// ffprobe 探测得到的媒体信息。
/// </summary>
/// <param name="Container">归一化后的容器格式，例如 flac、mp3、m4a。</param>
/// <param name="Codec">音频编码器名称，例如 aac、flac。</param>
/// <param name="BitrateKbps">比特率（kbps），探测失败时为 null。</param>
/// <param name="DurationMs">时长（毫秒），探测失败时为 null。</param>
/// <param name="FormatNames">ffprobe 返回的原始 format_name 逗号分隔列表。</param>
public sealed record ProbedMedia(string Container, string Codec, int? BitrateKbps, long? DurationMs, string FormatNames);

/// <summary>
/// TagLib# 解析得到的音频元数据。
/// </summary>
/// <param name="Title">曲目标题。</param>
/// <param name="Artists">艺术家列表。</param>
/// <param name="AlbumTitle">专辑名称。</param>
/// <param name="TrackNumber">曲目序号。</param>
/// <param name="DiscNumber">碟片序号。</param>
/// <param name="ReleaseYear">发行年份。</param>
/// <param name="Genres">流派列表。</param>
/// <param name="DurationMs">时长（毫秒）。</param>
/// <param name="ArtworkStorageKey">封面在隔离存储中的存储键。</param>
/// <param name="Lyrics">从标签中读取的内嵌歌词（ID3v2 USLT 等）；无内嵌歌词时为 null。</param>
/// <param name="Warnings">解析过程中收集的非致命告警（如缺少标题/艺术家）。</param>
public sealed record ParsedAudioMetadata(
    string? Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    int? TrackNumber,
    int? DiscNumber,
    int? ReleaseYear,
    IReadOnlyList<string> Genres,
    long? DurationMs,
    string? ArtworkStorageKey,
    string? Lyrics,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 基于 ffmpeg/ffprobe 的媒体服务。
/// 负责通过外部进程完成音频容器探测与转码，所有进程调用均带超时与取消处理，
/// 超时或非零退出码会抛出带错误码的 <see cref="MediaProcessingException"/>。
/// </summary>
public sealed class FfmpegMediaService(IOptions<FfmpegOptions> options)
{
    private readonly FfmpegOptions _options = options.Value;

    /// <summary>
    /// 使用 ffprobe 探测指定音频文件的容器、编码器、比特率与时长。
    /// </summary>
    /// <param name="sourcePath">源文件绝对路径（位于隔离存储区）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>归一化后的媒体信息。</returns>
    /// <exception cref="MediaProcessingException">文件不含音频流或 ffprobe 输出无法解析时抛出。</exception>
    public async Task<ProbedMedia> ProbeAsync(string sourcePath, CancellationToken cancellationToken)
    {
        // 调用 ffprobe：仅输出 format 与 stream 的关键字段，格式为 JSON，便于解析。
        var json = await RunAsync(
            _options.ProbeExecutablePath,
            ["-v", "error", "-show_entries", "format=format_name,duration,bit_rate", "-show_entries", "stream=codec_type,codec_name,bit_rate", "-of", "json", sourcePath],
            cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            // 校验是否存在音频流，若不存在直接拒绝该文件。
            if (!root.TryGetProperty("streams", out var streams) ||
                !streams.EnumerateArray().Any(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio"))
                throw new MediaProcessingException("media_parse_failed", "The file does not contain an audio stream.");

            // 取第一条音频流作为探测目标。
            var audioStream = streams.EnumerateArray().First(stream =>
                stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");
            var codec = ReadString(audioStream, "codec_name") ?? "unknown";
            var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
            var formatNames = ReadString(format, "format_name") ?? "unknown";
            // 将 ffprobe 的原始容器名归一化为系统内部使用的统一标识。
            var canonicalContainer = CanonicalContainer(formatNames, codec);
            // 比特率优先取音频流级别，回退到容器级别。
            var bitrate = ParseLong(ReadString(audioStream, "bit_rate")) ?? ParseLong(ReadString(format, "bit_rate"));
            var duration = ParseDouble(ReadString(format, "duration"));
            return new ProbedMedia(
                canonicalContainer,
                codec,
                // 比特率从 bps 转换为 kbps，并夹紧到合法范围。
                bitrate.HasValue ? (int)Math.Clamp(bitrate.Value / 1000, 1, int.MaxValue) : null,
                // 时长从秒转换为毫秒，采用远离零的四舍五入。
                duration.HasValue ? (long)Math.Round(duration.Value * 1000, MidpointRounding.AwayFromZero) : null,
                formatNames);
        }
        catch (JsonException exception)
        {
            throw new MediaProcessingException("media_parse_failed", $"ffprobe returned invalid metadata: {exception.Message}");
        }
    }

    /// <summary>
    /// 将源音频转码为 AAC 编码的 M4A 文件。
    /// 使用 -movflags +faststart 使 moov atom 前置，便于流式播放；
    /// 仅映射第一条音频流并丢弃视频流（-vn）。
    /// </summary>
    /// <param name="sourcePath">源文件绝对路径。</param>
    /// <param name="destinationPath">目标 m4a 文件绝对路径。</param>
    /// <param name="bitrateKbps">目标比特率（kbps）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task TranscodeToAacAsync(string sourcePath, string destinationPath, int bitrateKbps, CancellationToken cancellationToken)
    {
        // 确保目标目录存在。
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        return RunForExitAsync(
            _options.ExecutablePath,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath, "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", $"{bitrateKbps}k", "-movflags", "+faststart", destinationPath],
            cancellationToken);
    }

    /// <summary>
    /// 运行外部进程并捕获其标准输出。
    /// </summary>
    private async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(executable, arguments, captureOutput: true, cancellationToken);
        return result.StandardOutput;
    }

    /// <summary>
    /// 运行外部进程，仅等待其退出，不捕获标准输出（用于转码等不需要输出的场景）。
    /// </summary>
    private async Task RunForExitAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await RunProcessAsync(executable, arguments, captureOutput: false, cancellationToken);
    }

    /// <summary>
    /// 启动并等待外部进程完成，统一处理启动失败、超时与非零退出码。
    /// </summary>
    private async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool captureOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new MediaProcessingException("media_processor_unavailable", "The media processor could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 可执行文件不存在或无执行权限时，转换为统一异常。
            throw new MediaProcessingException("media_processor_unavailable", $"Unable to start {Path.GetFileName(executable)}.");
        }

        // 将调用方取消令牌与超时令牌联动，超时上限夹紧在 1~120 分钟。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 120)));
        var outputTask = captureOutput ? process.StandardOutput.ReadToEndAsync(timeout.Token) : Task.FromResult(string.Empty);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var standardOutput = await outputTask;
            var standardError = await errorTask;
            // 非零退出码视为处理失败，使用 stderr 内容作为错误信息。
            if (process.ExitCode != 0)
                throw new MediaProcessingException("media_parse_failed", string.IsNullOrWhiteSpace(standardError) ? "The media processor rejected the file." : standardError.Trim());
            return new ProcessResult(standardOutput, standardError);
        }
        catch (OperationCanceledException)
        {
            // 取消或超时时强制结束整个进程树，避免遗留僵尸进程。
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            // 区分是调用方主动取消还是超时：主动取消则向上传播，超时则抛出超时异常。
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new MediaProcessingException("media_processing_timeout", "Media processing exceeded the configured timeout.");
        }
    }

    /// <summary>
    /// 从 JSON 元素中安全读取字符串属性。
    /// </summary>
    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    /// <summary>
    /// 将 ffprobe 返回的原始容器名列表归一化为系统内部统一的容器标识。
    /// 处理常见别名（如 mov/mp4 → m4a、asf+wmav → wma）。
    /// </summary>
    private static string CanonicalContainer(string formatNames, string codec)
    {
        var formats = formatNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (formats.Contains("flac", StringComparer.OrdinalIgnoreCase)) return "flac";
        if (formats.Contains("mp3", StringComparer.OrdinalIgnoreCase)) return "mp3";
        if (formats.Contains("wav", StringComparer.OrdinalIgnoreCase)) return "wav";
        if (formats.Contains("ogg", StringComparer.OrdinalIgnoreCase)) return "ogg";
        if (formats.Contains("aiff", StringComparer.OrdinalIgnoreCase) || formats.Contains("aifc", StringComparer.OrdinalIgnoreCase)) return "aiff";
        if (formats.Contains("ape", StringComparer.OrdinalIgnoreCase)) return "ape";
        if (formats.Contains("wv", StringComparer.OrdinalIgnoreCase)) return "wv";
        if (formats.Contains("dsf", StringComparer.OrdinalIgnoreCase)) return "dsf";
        if (formats.Contains("mpc", StringComparer.OrdinalIgnoreCase)) return "mpc";
        if (formats.Contains("aac", StringComparer.OrdinalIgnoreCase)) return "aac";
        if (formats.Contains("mov", StringComparer.OrdinalIgnoreCase) || formats.Contains("mp4", StringComparer.OrdinalIgnoreCase)) return "m4a";
        if (formats.Contains("asf", StringComparer.OrdinalIgnoreCase) && codec.StartsWith("wmav", StringComparison.OrdinalIgnoreCase)) return "wma";
        return formats.FirstOrDefault() ?? "unknown";
    }

    /// <summary>
    /// 将字符串安全解析为 long，解析失败返回 null。
    /// </summary>
    private static long? ParseLong(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>
    /// 将字符串安全解析为 double，解析失败返回 null。
    /// </summary>
    private static double? ParseDouble(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>
    /// 外部进程执行结果，包含标准输出与标准错误。
    /// </summary>
    private sealed record ProcessResult(string StandardOutput, string StandardError);
}

/// <summary>
/// 音频元数据解析器，基于 TagLib#。
/// 负责读取音频文件的标题、艺术家、专辑、流派等标签，并提取封面写入隔离存储区。
/// 解析失败时返回带告警的空元数据，不向上抛出异常，确保导入流程可继续。
/// </summary>
public sealed class AudioMetadataParser(QuarantineStoragePaths storagePaths)
{
    /// <summary>
    /// 解析指定音频文件的元数据。
    /// </summary>
    /// <param name="sourcePath">源文件绝对路径（位于隔离存储区）。</param>
    /// <param name="itemId">导入项 ID，用于生成封面存储键。</param>
    /// <returns>解析后的元数据；解析失败时返回带 metadata_parse_failed 告警的空对象。</returns>
    public ParsedAudioMetadata Parse(string sourcePath, Guid itemId)
    {
        try
        {
            // 通过 TagLib# 打开文件并读取标签。
            using var media = TagLib.File.Create(sourcePath);
            var tag = media.Tag;
            // 艺术家与流派去重（大小写不敏感）并去除空白项。
            var artists = (tag.Performers ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var genres = (tag.Genres ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // 提取并保存封面到隔离存储区。
            var artworkStorageKey = SaveArtwork(tag.Pictures, itemId);
            var warnings = new List<string>();
            // 收集非致命告警：缺少标题或艺术家。
            if (string.IsNullOrWhiteSpace(tag.Title)) warnings.Add("missing_title");
            if (artists.Length == 0) warnings.Add("missing_artist");

            // 读取内嵌歌词：TagLib# 的 Lyrics 属性对应 ID3v2 USLT 帧等；
            // 仅当内容非空白时才保留，避免把空字符串当成"有歌词"。
            var lyrics = NullIfBlank(tag.Lyrics);

            return new ParsedAudioMetadata(
                NullIfBlank(tag.Title),
                artists,
                NullIfBlank(tag.Album),
                tag.Track > 0 ? (int)tag.Track : null,
                tag.Disc > 0 ? (int)tag.Disc : null,
                tag.Year > 0 ? (int)tag.Year : null,
                genres,
                media.Properties.Duration > TimeSpan.Zero ? (long)media.Properties.Duration.TotalMilliseconds : null,
                artworkStorageKey,
                lyrics,
                warnings);
        }
        catch (Exception exception) when (exception is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or ArgumentException)
        {
            // 格式不支持、文件损坏或 IO 异常时，返回带告警的空元数据，避免中断导入流水线。
            return new ParsedAudioMetadata(null, [], null, null, null, null, [], null, null, null, ["metadata_parse_failed"]);
        }
    }

    /// <summary>
    /// 从内嵌图片中选取第一张合法封面（大小不超过 10MB），保存到隔离存储区并返回存储键。
    /// </summary>
    /// <param name="pictures">TagLib# 解析出的内嵌图片数组。</param>
    /// <param name="itemId">导入项 ID，用于生成唯一存储键。</param>
    /// <returns>封面存储键；无合法封面时返回 null。</returns>
    private string? SaveArtwork(TagLib.IPicture[] pictures, Guid itemId)
    {
        // 选取第一张大小在 (0, 10MB] 区间的图片作为封面。
        var picture = pictures.FirstOrDefault(item => item.Data.Count is > 0 and <= 10485760);
        if (picture is null)
            return null;
        // 根据 MIME 类型确定扩展名，仅支持 jpg/png/webp。
        var extension = picture.MimeType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => null
        };
        if (extension is null)
            return null;

        // 存储键格式：artwork/{itemId}.{extension}
        var storageKey = $"artwork/{itemId:N}.{extension}";
        // 通过隔离存储路径解析器定位物理路径。
        if (!storagePaths.TryResolve(storageKey, out var path))
            return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, picture.Data.Data);
        return storageKey;
    }

    /// <summary>
    /// 将空白字符串转换为 null，非空白字符串则去除首尾空白。
    /// </summary>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}