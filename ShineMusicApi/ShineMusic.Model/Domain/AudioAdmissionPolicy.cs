namespace ShineMusic.Model.Domain;

/// <summary>
/// 原始音频的统一质量准入；不代替身份、完整性或可解码性验证。
/// 码率必须来自原文件探测。VBR 使用全曲平均音频码率，缺失时拒绝；
/// 不得传入目标转码码率或以改容器、上采样后的参数证明来源质量。
/// </summary>
public static class AudioAdmissionPolicy
{
    /// <summary>MP3/AAC 的最低实测码率（kbps）。</summary>
    public const int MinimumLossyBitrateKbps = 192;

    /// <summary>仅根据真实编码认可无损，不能根据容器或扩展名推断。</summary>
    public static bool IsLosslessCodec(string? codec) => codec?.Trim().ToLowerInvariant() is
        "flac" or "alac" or "wavpack" or "ape" or "tta" or "mlp" or "truehd"
        or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_s16be" or "pcm_s24be";

    /// <summary>判断原始探测参数是否合格；返回 false 时可用 GetRejectionReason 获取原因。</summary>
    public static bool IsAcceptable(string? codec, string? container, int? bitrateKbps) =>
        GetRejectionReason(codec, container, bitrateKbps) is null;

    /// <summary>返回稳定错误码；null 表示质量合格。未知编码始终保守拒绝。</summary>
    public static string? GetRejectionReason(string? codec, string? container, int? bitrateKbps)
    {
        var encoding = codec?.Trim().ToLowerInvariant();
        if (IsLosslessCodec(encoding)) return null;
        if (encoding is not ("mp3" or "aac")) return "unsupported_codec";
        if (container?.Trim().ToLowerInvariant() is "flac" or "wav" or "aiff" or "aif" or "wv" or "ape")
            return "encoding_mismatch";
        if (bitrateKbps is null or <= 0) return "unknown_bitrate";
        return bitrateKbps < MinimumLossyBitrateKbps ? "bitrate_below_minimum" : null;
    }
}
