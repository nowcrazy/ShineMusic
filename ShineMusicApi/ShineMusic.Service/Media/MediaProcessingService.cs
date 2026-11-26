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
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;

namespace ShineMusic.Service;

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
/// <param name="SampleRateHz">采样率（Hz）；Phase 3 新增，探测失败时为 null。</param>
/// <param name="BitsPerSample">无损位深；Phase 3 新增，优先取 bits_per_raw_sample，回退 bits_per_sample。</param>
/// <param name="Channels">声道数；Phase 3 新增，探测失败时为 null。</param>
/// <param name="CodecProfile">编码配置，例如 AAC LC；探测失败时为 null。</param>
/// <remarks>
/// 后三个参数是 V2 Phase 3 为 AudioValidator 追加的，带默认值以保全既有 5 参数构造
/// （MediaTranscoderAdapter / LibraryImportJob / SourceDownloadJob 无需改动）。
/// </remarks>
public sealed record ProbedMedia(
    string Container,
    string Codec,
    int? BitrateKbps,
    long? DurationMs,
    string FormatNames,
    int? SampleRateHz = null,
    int? BitsPerSample = null,
    int? Channels = null,
    string? CodecProfile = null);

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
/// <param name="AlbumArtists">标签中的专辑艺人。</param>
/// <param name="Isrc">标签中的录音代码。</param>
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
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? AlbumArtists = null,
    string? Isrc = null);

/// <summary>
/// 基于 ffmpeg/ffprobe 的媒体服务。
/// 负责通过外部进程完成音频容器探测与转码，所有进程调用均带超时与取消处理，
/// 超时或非零退出码会抛出带错误码的 <see cref="MediaProcessingException"/>。
/// </summary>
public sealed class FfmpegMediaService(IOptions<FfmpegOptions> options, ILogger<FfmpegMediaService> logger)
{
    private readonly FfmpegOptions _options = options.Value;
    private readonly ILogger<FfmpegMediaService> _logger = logger;

    /// <summary>Decode every audio frame without creating a second lossy file.</summary>
    public async Task ValidateCompleteAsync(string path, CancellationToken cancellationToken)
    {
        var integrity = await ReadFlacIntegrityAsync(path, cancellationToken);
        if (integrity is null)
        {
            await RunForExitAsync(_options.ExecutablePath,
                ["-nostdin", "-hide_banner", "-v", "error", "-xerror", "-i", path,
                 "-map", "0:a:0", "-f", "null", "-"], cancellationToken, $"validate {path}", classifyCorruptInput: true);
            return;
        }

        DecodedIntegrity decoded;
        try
        {
            // Verify the count even when ffmpeg exits successfully: a FLAC truncated exactly
            // between frames may produce no decoder error while still missing promised samples.
            decoded = await DecodeFlacIntegrityAsync(path, integrity, strict: true, cancellationToken);
        }
        catch (MediaProcessingException exception) when (exception.Code == ErrorCodes.MediaCorrupt)
        {
            // Some FLAC producers append non-audio bytes after the last frame. ffmpeg reports
            // them as broken frames even when every original PCM sample is intact. Only an
            // exact STREAMINFO sample count AND PCM MD5 can safely distinguish this case.
            decoded = await DecodeFlacIntegrityAsync(path, integrity, strict: false, cancellationToken);
        }
        if (decoded.SampleBytes != integrity.SampleBytes ||
            !CryptographicOperations.FixedTimeEquals(decoded.Md5, integrity.Md5))
            throw new MediaProcessingException(ErrorCodes.MediaCorrupt, ImportFormatting.SafeErrorMessage(ErrorCodes.MediaCorrupt));
    }

    private sealed record FlacIntegrity(int BitsPerSample, int Channels, long SampleBytes, byte[] Md5);
    private sealed record DecodedIntegrity(long SampleBytes, byte[] Md5);

    private static async Task<FlacIntegrity?> ReadFlacIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        // Native FLAC starts with fLaC and a mandatory 34-byte STREAMINFO metadata block.
        // Unusual headers, unsupported bit depths and unspecified checksums retain strict validation.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[42];
        if (await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken) != header.Length ||
            !header.AsSpan(0, 4).SequenceEqual("fLaC"u8) || (header[4] & 0x7f) != 0 ||
            header[5] != 0 || header[6] != 0 || header[7] != 34)
            return null;
        var channels = ((header[20] >> 1) & 7) + 1;
        var bits = (((header[20] & 1) << 4) | (header[21] >> 4)) + 1;
        var samples = ((long)(header[21] & 15) << 32) | ((long)header[22] << 24) |
            ((long)header[23] << 16) | ((long)header[24] << 8) | header[25];
        var md5 = header.AsSpan(26, 16).ToArray();
        if (bits is not (16 or 24 or 32) || samples <= 0 || md5.All(value => value == 0))
            return null;
        return new FlacIntegrity(bits, channels, checked(samples * channels * (bits / 8)), md5);
    }

    private async Task<DecodedIntegrity> DecodeFlacIntegrityAsync(string path, FlacIntegrity integrity, bool strict, CancellationToken cancellationToken)
    {
        var pcmFormat = $"s{integrity.BitsPerSample}le";
        List<string> arguments = ["-nostdin", "-hide_banner", "-v", "error"];
        if (strict) arguments.Add("-xerror");
        arguments.AddRange(["-i", path, "-map", "0:a:0", "-vn", "-c:a", "pcm_" + pcmFormat, "-f", pcmFormat, "-"]);
        var result = await RunProcessAsync(_options.ExecutablePath,
            arguments,
            captureOutput: false, cancellationToken, $"verify FLAC PCM {path}", ErrorCodes.MediaParseFailed,
            classifyCorruptInput: true,
            consumeOutput: async (stream, token) =>
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[81920];
                long bytes = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, token)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    bytes += read;
                }
                return new DecodedIntegrity(bytes, hash.GetHashAndReset());
            }, allowDecodeFailureWithOutput: !strict);
        return result.Decoded!;
    }

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
            ["-v", "error", "-show_entries", "format=format_name,duration,bit_rate,size",
             "-show_entries", "stream=index,codec_type,codec_name,profile,bit_rate,sample_rate,bits_per_raw_sample,bits_per_sample,channels",
             "-of", "json", sourcePath],
            cancellationToken,
            $"probe {sourcePath}");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            // 校验是否存在音频流，若不存在直接拒绝该文件。
            if (!root.TryGetProperty("streams", out var streams) ||
                !streams.EnumerateArray().Any(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio"))
            {
                _logger.LogWarning("Probe found no audio stream in {Source}.", sourcePath);
                throw new MediaProcessingException(ErrorCodes.MediaParseFailed, "The file does not contain an audio stream.");
            }

            // 取第一条音频流作为探测目标。
            var audioStream = streams.EnumerateArray().First(stream =>
                stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");
            var codec = ReadString(audioStream, "codec_name") ?? "unknown";
            var codecProfile = ReadString(audioStream, "profile");
            var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
            var formatNames = ReadString(format, "format_name") ?? "unknown";
            // 将 ffprobe 的原始容器名归一化为系统内部使用的统一标识。
            var canonicalContainer = CanonicalContainer(formatNames, codec);
            // 有损准入只能使用音频流码率，容器总码率可能包含封面、视频或额外轨道。
            // VBR 使用 ffprobe 报告的音频流平均码率；无法确认时保持未知。
            var bitrate = ParseLong(ReadString(audioStream, "bit_rate"));
            if (ShineMusic.Model.Domain.AudioAdmissionPolicy.IsLosslessCodec(codec))
                bitrate ??= ParseLong(ReadString(format, "bit_rate"));
            var duration = ParseDouble(ReadString(format, "duration"));
            // Opus and Vorbis frequently omit stream.bit_rate. Sum only compressed audio packets;
            // container bytes may include artwork, tags, other streams and cannot establish quality.
            if ((bitrate is null or <= 0) && codec.ToLowerInvariant() is ("opus" or "vorbis"))
            {
                var packetStats = await ReadAudioPacketStatisticsAsync(sourcePath, cancellationToken);
                if (packetStats.Bytes > 0 && packetStats.DurationSeconds > 0)
                    bitrate = checked((long)Math.Round(packetStats.Bytes * 8.0 / packetStats.DurationSeconds, MidpointRounding.AwayFromZero));
            }
            // Phase 3：ffprobe 对 FLAC 通常给 bits_per_raw_sample（16/24），部分版本给 bits_per_sample。
            // 两者都取，优先 raw；非正数（部分容器回填 0）视为缺失，避免把「未知」误判成不达标。
            var bitsPerSample = PositiveOrNull(ParseLong(ReadString(audioStream, "bits_per_raw_sample")))
                                ?? PositiveOrNull(ParseLong(ReadString(audioStream, "bits_per_sample")));
            var sampleRate = PositiveOrNull(ParseLong(ReadString(audioStream, "sample_rate")));
            var channels = PositiveOrNull(ParseLong(ReadString(audioStream, "channels")));
            return new ProbedMedia(
                canonicalContainer,
                codec,
                // 比特率从 bps 转换为 kbps，并夹紧到合法范围。
                bitrate.HasValue ? (int)Math.Clamp(bitrate.Value / 1000, 1, int.MaxValue) : null,
                // 时长从秒转换为毫秒，采用远离零的四舍五入。
                duration.HasValue ? (long)Math.Round(duration.Value * 1000, MidpointRounding.AwayFromZero) : null,
                formatNames,
                sampleRate.HasValue ? (int)Math.Clamp(sampleRate.Value, 1, int.MaxValue) : null,
                bitsPerSample.HasValue ? (int)Math.Clamp(bitsPerSample.Value, 1, int.MaxValue) : null,
                channels.HasValue ? (int)Math.Clamp(channels.Value, 1, int.MaxValue) : null,
                codecProfile);
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "ffprobe returned unparseable metadata for {Source}.", sourcePath);
            throw new MediaProcessingException(ErrorCodes.MediaParseFailed, $"ffprobe returned invalid metadata: {exception.Message}");
        }
    }

    private async Task<AudioPacketStatistics> ReadAudioPacketStatisticsAsync(string sourcePath, CancellationToken cancellationToken)
    {
        AudioPacketStatistics? statistics = null;
        await RunProcessAsync(_options.ProbeExecutablePath,
            ["-v", "error", "-select_streams", "a:0", "-show_packets", "-show_entries", "packet=size,duration_time",
             "-of", "compact=p=0:nk=0", sourcePath],
            captureOutput: false, cancellationToken, $"measure audio packets {sourcePath}",
            consumeTextOutput: async (reader, token) =>
            {
                long bytes = 0;
                double seconds = 0;
                string? line;
                while ((line = await reader.ReadLineAsync(token)) is not null)
                {
                    token.ThrowIfCancellationRequested();
                    long? size = null;
                    double? duration = null;
                    foreach (var field in line.Split('|'))
                    {
                        if (field.StartsWith("size=", StringComparison.Ordinal)) size = ParseLong(field[5..]);
                        else if (field.StartsWith("duration_time=", StringComparison.Ordinal)) duration = ParseDouble(field[14..]);
                    }
                    if (size is > 0 && duration is > 0 && double.IsFinite(duration.Value))
                    {
                        bytes = checked(bytes + size.Value);
                        seconds += duration.Value;
                    }
                }
                statistics = new AudioPacketStatistics(bytes, seconds);
            });
        return statistics ?? new AudioPacketStatistics(0, 0);
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
            cancellationToken,
            $"transcode {sourcePath} -> {destinationPath}", ErrorCodes.TranscodeFailed);
    }

    /// <summary>
    /// 运行外部进程并捕获其标准输出。
    /// </summary>
    private async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? operationLabel = null)
    {
        var result = await RunProcessAsync(executable, arguments, captureOutput: true, cancellationToken, operationLabel);
        return result.StandardOutput;
    }

    /// <summary>
    /// 运行外部进程，仅等待其退出，不捕获标准输出（用于转码等不需要输出的场景）。
    /// </summary>
    private async Task RunForExitAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        string? operationLabel = null, string failureCode = ErrorCodes.MediaParseFailed, bool classifyCorruptInput = false)
    {
        await RunProcessAsync(executable, arguments, captureOutput: false, cancellationToken, operationLabel, failureCode, classifyCorruptInput);
    }

    /// <summary>
    /// 启动并等待外部进程完成，统一处理启动失败、超时与非零退出码。
    /// </summary>
    private async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool captureOutput,
        CancellationToken cancellationToken,
        string? operationLabel = null,
        string failureCode = ErrorCodes.MediaParseFailed,
        bool classifyCorruptInput = false,
        Func<Stream, CancellationToken, Task<DecodedIntegrity>>? consumeOutput = null,
        bool allowDecodeFailureWithOutput = false,
        Func<StreamReader, CancellationToken, Task>? consumeTextOutput = null)
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
            // 可执行文件不存在或无执行权限时，转换为统一异常，并记录日志便于定位（如 ffmpeg 未安装/路径错）。
            _logger.LogError(exception, "Media processor {Executable} failed to start ({Operation}).", Path.GetFileName(executable), operationLabel ?? executable);
            throw new MediaProcessingException("media_processor_unavailable", $"Unable to start {Path.GetFileName(executable)}.");
        }

        // 将调用方取消令牌与超时令牌联动，超时上限夹紧在 1~120 分钟。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 120)));
        var outputTask = captureOutput ? process.StandardOutput.ReadToEndAsync(timeout.Token) : Task.FromResult(string.Empty);
        var decodedTask = consumeOutput?.Invoke(process.StandardOutput.BaseStream, timeout.Token);
        var textOutputTask = consumeTextOutput?.Invoke(process.StandardOutput, timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var standardOutput = await outputTask;
            var standardError = await errorTask;
            var decoded = decodedTask is null ? null : await decodedTask;
            if (textOutputTask is not null) await textOutputTask;
            // 非零退出码视为处理失败，使用 stderr 内容作为错误信息。
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(standardError) ? "The media processor rejected the file." : standardError.Trim();
                if (allowDecodeFailureWithOutput && IsCorruptAudioDiagnostic(standardError))
                    return new ProcessResult(standardOutput, standardError, decoded);
                _logger.LogError("Media processing failed ({Operation}): {Detail}", operationLabel ?? executable, detail);
                // Only decoder diagnostics prove a permanent input failure. Storage, permissions,
                // unavailable codecs and process configuration errors remain eligible for retry.
                var code = classifyCorruptInput && IsCorruptAudioDiagnostic(standardError)
                    ? ErrorCodes.MediaCorrupt : failureCode;
                throw new MediaProcessingException(code, detail);
            }
            return new ProcessResult(standardOutput, standardError, decoded);
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
            _logger.LogError("Media processing timed out ({Operation}) after the configured limit.", operationLabel ?? executable);
            throw new MediaProcessingException("media_processing_timeout", "Media processing exceeded the configured timeout.");
        }
    }

    internal static bool IsCorruptAudioDiagnostic(string diagnostic) =>
        new[] { "invalid sync code", "invalid frame header", "decode_frame() failed",
            "error while decoding", "corrupt decoded frame", "packet corrupt", "crc mismatch" }
            .Any(marker => diagnostic.Contains(marker, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>非正数视为「探测不到」，避免 0 被当成真实位深/采样率/声道数参与质量判定。</summary>
    private static long? PositiveOrNull(long? value) => value is > 0 ? value : null;

    /// <summary>
    /// 将字符串安全解析为 double，解析失败返回 null。
    /// </summary>
    private static double? ParseDouble(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>
    /// 外部进程执行结果，包含标准输出与标准错误。
    /// </summary>
    private sealed record ProcessResult(string StandardOutput, string StandardError, DecodedIntegrity? Decoded = null);
    private sealed record AudioPacketStatistics(long Bytes, double DurationSeconds);
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
    /// <param name="attemptId">后台执行代次，避免恢复任务的封面路径互相覆盖。</param>
    /// <returns>解析后的元数据；解析失败时返回带 metadata_parse_failed 告警的空对象。</returns>
    public ParsedAudioMetadata Parse(string sourcePath, Guid itemId, Guid? attemptId = null)
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
            var artworkStorageKey = SaveArtwork(tag.Pictures, itemId, attemptId);
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
                warnings,
                (tag.AlbumArtists ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToArray(),
                NullIfBlank(tag.ISRC));
        }
        catch (Exception exception) when (exception is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or ArgumentException)
        {
            // 格式不支持、文件损坏或 IO 异常时，返回带告警的空元数据，避免中断导入流水线。
            // 注意：这里进的是 Warnings（告警码，原样展示在管理端导入列表里），
            // 不要替换成 ErrorCodes.MediaParseFailed —— 那是 ImportQueue.SafeErrorMessage 的失败码，
            // 两者是不同通道，历史上就写成了 metadata_parse_failed，保留原值避免前端展示变化。
            return new ParsedAudioMetadata(null, [], null, null, null, null, [], null, null, null, ["metadata_parse_failed"]);
        }
    }

    /// <summary>
    /// 从内嵌图片中选取第一张合法封面（大小不超过 10MB），保存到隔离存储区并返回存储键。
    /// </summary>
    /// <param name="pictures">TagLib# 解析出的内嵌图片数组。</param>
    /// <param name="itemId">导入项 ID，用于生成唯一存储键。</param>
    /// <param name="attemptId">后台执行代次；未提供时兼容旧调用的存储键。</param>
    /// <returns>封面存储键；无合法封面时返回 null。</returns>
    private string? SaveArtwork(TagLib.IPicture[] pictures, Guid itemId, Guid? attemptId)
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

        // A recovered task uses a new namespace, so late writes cannot replace the
        // artwork produced by the current attempt.
        var storageKey = attemptId is { } attempt
            ? $"artwork/{itemId:N}/{attempt:N}.{extension}"
            : $"artwork/{itemId:N}.{extension}";
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
