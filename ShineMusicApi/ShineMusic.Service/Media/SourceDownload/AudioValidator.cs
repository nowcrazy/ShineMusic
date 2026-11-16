// 职责：Phase 3 音质验证器——用 ffprobe 真实编码检测，防止有损转无损伪装和试听片段入库。
//
// 背景：DeliveryContractVerifier 只做魔数（首 16 字节）+ 码率声明值校验，抓不到以下场景：
//   1) MP3 被重命名为 .flac（有损转无损伪装，魔数 fLaC 头可伪造或转码后头正确但内容有损）
//   2) 45 秒试听片段被当成完整歌曲（时长与元数据不符但魔数和码率都正常）
//   3) 中段被替换且长度不变的篡改（需要 SHA-256 全量校验）
//
// 本类复用已有 FfmpegMediaService.ProbeAsync（已返回 Container/Codec/BitrateKbps/DurationMs/
// SampleRateHz/BitsPerSample/Channels），不重复实现 ffprobe 调用。
// SHA-256 期望值来自流式下载；未传实际值时重新读取落地文件计算，校验写入后的完整性。

using System.Security.Cryptography;
using ShineMusic.Model.Ports;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceDownload;

/// <summary>音质验证结果。</summary>
/// <param name="Accepted">是否通过验证。</param>
/// <param name="Quality">通过时的实测音质信息；未通过时为 null。</param>
/// <param name="ErrorCode">未通过时的机器可读错误码。</param>
/// <param name="Reason">未通过时的可读原因。</param>
public sealed record AudioValidationResult(
    bool Accepted,
    AudioQuality? Quality,
    string? ErrorCode,
    string? Reason)
{
    /// <summary>构造通过结果。</summary>
    public static AudioValidationResult Passed(AudioQuality quality) =>
        new(true, quality, null, null);

    /// <summary>构造拒绝结果。</summary>
    public static AudioValidationResult Rejected(string errorCode, string reason) =>
        new(false, null, errorCode, reason);
}

/// <summary>
/// Phase 3 音质验证器：基于 ffprobe 真实编码检测，补齐 DeliveryContractVerifier 缺失的编码级验证。
/// 检测项：有损转无损伪装、试听片段、SHA-256 完整性（可选）。
/// </summary>
public sealed class AudioValidator
{
    /// <summary>有损编码集合——如果文件容器声称无损但实际编码属于这些，判定为有损转无损伪装。</summary>
    private static readonly HashSet<string> LossyCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "aac", "ac3", "eac3", "opus", "vorbis", "wma", "wmapro",
    };

    /// <summary>无损编码集合。</summary>
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "alac", "wavpack", "ape", "tta", "mlp", "truehd", "pcm_s16le",
        "pcm_s24le", "pcm_s32le", "pcm_s24be", "pcm_s16be",
    };

    /// <summary>无损容器集合——对这些容器做有损编码检测。</summary>
    private static readonly HashSet<string> LosslessContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "wav", "aiff", "aif", "wv", "ape",
    };

    private readonly IMediaTranscoder _mediaService;
    private readonly ILogger _logger;

    /// <summary>构造验证器。</summary>
    /// <param name="mediaService">ffprobe 媒体服务（DI 单例）。</param>
    /// <param name="logger">日志。</param>
    public AudioValidator(FfmpegMediaService mediaService, ILogger logger)
        : this(new MediaTranscoderAdapter(mediaService), logger)
    {
    }

    /// <summary>通过媒体端口执行探测，供下载链路与隔离测试共用。</summary>
    public AudioValidator(IMediaTranscoder mediaService, ILogger logger)
    {
        _mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 验证下载的音频文件：ffprobe 真实编码检测 + 试听片段检测 + 无损伪装检测。
    /// </summary>
    /// <param name="filePath">已落地的音频文件绝对路径。</param>
    /// <param name="expectedDurationMs">期望时长（毫秒），来自详情接口或任务快照；null 时跳过时长校验。</param>
    /// <param name="expectedSha256">期望的 SHA-256 哈希；null 时跳过哈希校验。</param>
    /// <param name="actualSha256">实际 SHA-256；未提供且有期望值时从落地文件计算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>验证结果：通过时携带实测 AudioQuality，未通过时携带错误码和原因。</returns>
    public async Task<AudioValidationResult> ValidateAsync(
        string filePath,
        long? expectedDurationMs = null,
        string? expectedSha256 = null,
        string? actualSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            return AudioValidationResult.Rejected("file_not_found", $"音频文件不存在：{filePath}");

        MediaProbe probed;
        try
        {
            probed = await _mediaService.ProbeAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "AudioValidator: ffprobe 探测失败 {FilePath}", filePath);
            // 探测失败不直接拒绝（与既有 EvaluateDownloadedAudioAsync 的降级哲学一致），
            // 但返回 Unknown 质量让评分闸门决定。
            return AudioValidationResult.Rejected("probe_failed",
                $"ffprobe 探测失败：{exception.Message}");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256) && string.IsNullOrWhiteSpace(actualSha256))
        {
            await using var stream = File.OpenRead(filePath);
            actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        return Evaluate(new ProbedMedia(probed.Container, probed.Codec, probed.BitrateKbps,
            probed.DurationMs, probed.FormatNames, probed.SampleRateHz, probed.BitsPerSample, probed.Channels),
            expectedDurationMs, expectedSha256, actualSha256, new FileInfo(filePath).Length);
    }

    /// <summary>
    /// 纯函数：对已探测的 ProbedMedia 做编码级验证判定，不依赖文件系统或 ffprobe。
    /// 测试可直接调用此方法验证判定逻辑。
    /// </summary>
    /// <param name="probed">ffprobe 探测结果。</param>
    /// <param name="expectedDurationMs">期望时长（毫秒）；null 时跳过时长校验。</param>
    /// <param name="expectedSha256">期望的 SHA-256；null 时跳过哈希校验。</param>
    /// <param name="actualSha256">实际的 SHA-256；null 时跳过哈希校验。</param>
    /// <param name="fileSizeBytes">文件大小（字节），用于回填 AudioQuality。</param>
    /// <returns>验证结果。</returns>
    public static AudioValidationResult Evaluate(
        ProbedMedia probed,
        long? expectedDurationMs = null,
        string? expectedSha256 = null,
        string? actualSha256 = null,
        long fileSizeBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(probed);

        // —— 1) 有损转无损伪装检测 ——
        var container = probed.Container?.ToLowerInvariant() ?? string.Empty;
        var codec = probed.Codec?.ToLowerInvariant() ?? string.Empty;

        if (LosslessContainers.Contains(container) && LossyCodecs.Contains(codec))
        {
            return AudioValidationResult.Rejected("encoding_mismatch",
                $"容器 {container} 声称无损但实际编码为 {codec}（有损转无损伪装）。");
        }

        // —— 2) 试听片段检测 ——
        if (expectedDurationMs is > 0 && probed.DurationMs is > 0)
        {
            var actual = probed.DurationMs.Value;
            var expected = expectedDurationMs.Value;
            var deviation = Math.Abs(actual - expected);

            if (actual < 30_000)
                return AudioValidationResult.Rejected("preview_fragment",
                    $"下载文件仅 {actual / 1000.0:0.#}s，判定为试听片段/占位音频。");

            if (deviation > 15_000 && deviation > expected * 0.25)
                return AudioValidationResult.Rejected("preview_fragment",
                    $"下载文件时长与曲目不符（实测 {actual / 1000.0:0.#}s，期望 {expected / 1000.0:0.#}s），判定音源返回了占位资源。");
        }
        else if (probed.DurationMs is > 0 && probed.DurationMs < 30_000)
        {
            return AudioValidationResult.Rejected("preview_fragment",
                $"下载文件仅 {probed.DurationMs.Value / 1000.0:0.#}s，判定为试听片段/占位音频。");
        }

        // —— 3) SHA-256 完整性校验 ——
        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !string.IsNullOrWhiteSpace(actualSha256)
            && !string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            return AudioValidationResult.Rejected("hash_mismatch",
                "SHA-256 校验失败：文件可能在传输过程中被篡改。");
        }

        // —— 通过：映射为 AudioQuality ——
        var format = MapAudioFormat(probed.Container ?? string.Empty, probed.Codec ?? string.Empty);
        var level = DetermineQualityLevel(probed, format);
        var quality = new AudioQuality(
            Format: format,
            Level: level,
            Codec: probed.Codec,
            BitrateKbps: probed.BitrateKbps,
            SampleRateHz: probed.SampleRateHz,
            BitsPerSample: probed.BitsPerSample,
            Channels: probed.Channels,
            DurationMs: probed.DurationMs,
            FileSizeBytes: fileSizeBytes);
        return AudioValidationResult.Passed(quality);
    }

    /// <summary>根据容器和编码推断音频格式枚举。</summary>
    private static AudioFormat MapAudioFormat(string container, string codec)
    {
        var c = container?.ToLowerInvariant() ?? string.Empty;
        if (c is "flac") return AudioFormat.Flac;
        if (c is "mp3") return AudioFormat.Mp3;
        if (c is "m4a" or "mp4" or "mov") return AudioFormat.M4a;
        if (c is "ogg" or "oga") return AudioFormat.Ogg;
        if (c is "wav") return AudioFormat.Wav;
        // alac 在 m4a 容器中
        if (string.Equals(codec, "alac", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Alac;
        return AudioFormat.Unknown;
    }

    /// <summary>
    /// 根据 ffprobe 实测结果推断质量等级。
    /// 无损编码（flac/alac/wavpack 等）且位深 &gt; 0 → Lossless；
    /// 码率 &gt;= 320kbps → High320K；
    /// 码率 &lt; 320kbps → Below320K；
    /// 未知 → Unknown。
    /// </summary>
    private static AudioQualityLevel DetermineQualityLevel(ProbedMedia probed, AudioFormat format)
    {
        // 无损编码判定：codec 在无损白名单中
        if (LosslessCodecs.Contains(probed.Codec ?? string.Empty))
            return AudioQualityLevel.Lossless;

        // ALAC 在 m4a 容器中
        if (format == AudioFormat.Alac)
            return AudioQualityLevel.Lossless;

        // FLAC 容器 + flac codec
        if (format == AudioFormat.Flac)
            return AudioQualityLevel.Lossless;

        // WAV 容器（PCM 编码天然无损）
        if (format == AudioFormat.Wav)
            return AudioQualityLevel.Lossless;

        // 有损编码按码率分级
        if (probed.BitrateKbps is >= 320)
            return AudioQualityLevel.High320K;
        if (probed.BitrateKbps is > 0 and < 320)
            return AudioQualityLevel.Below320K;

        return AudioQualityLevel.Unknown;
    }
}
