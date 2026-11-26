// 职责：媒体探测与转码的领域端口。
// 用例层只认这个接口，不直接依赖 ffmpeg 子进程、进程参数或临时文件布局。

namespace ShineMusic.Model.Ports;

/// <summary>
/// ffprobe 探测结果（领域侧的归一化模型，不含第三方字段）。
/// 后三个字段为 V2 Phase 3 AudioValidator 追加，带默认值以保全既有 5 参数构造。
/// </summary>
public sealed record MediaProbe(
    string Container,
    string Codec,
    int? BitrateKbps,
    long? DurationMs,
    string FormatNames,
    int? SampleRateHz = null,
    int? BitsPerSample = null,
    int? Channels = null,
    string? CodecProfile = null);

/// <summary>音频处理端口。</summary>
public interface IMediaTranscoder
{
    /// <summary>探测源文件的容器、编码、码率与时长。</summary>
    Task<MediaProbe> ProbeAsync(string sourcePath, CancellationToken cancellationToken);

    /// <summary>统一转码为 AAC/m4a 播放产物。</summary>
    Task TranscodeToAacAsync(string sourcePath, string destinationPath, int bitrateKbps, CancellationToken cancellationToken);
}
