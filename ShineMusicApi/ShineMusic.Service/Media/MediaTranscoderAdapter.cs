// 职责：把已有的 FfmpegMediaService 适配到领域端口 IMediaTranscoder。
// 适配器只做模型映射，不改变探测/转码行为；用例层从此不直接依赖具体类。

using ShineMusic.Model.Ports;

namespace ShineMusic.Service;

/// <summary>FFmpeg 实现的媒体处理端口适配器。</summary>
public sealed class MediaTranscoderAdapter(FfmpegMediaService inner) : IMediaTranscoder
{
    /// <inheritdoc />
    public async Task<MediaProbe> ProbeAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var probed = await inner.ProbeAsync(sourcePath, cancellationToken);
        return new MediaProbe(
            probed.Container,
            probed.Codec,
            probed.BitrateKbps,
            probed.DurationMs,
            probed.FormatNames);
    }

    /// <inheritdoc />
    public Task TranscodeToAacAsync(string sourcePath, string destinationPath, int bitrateKbps, CancellationToken cancellationToken) =>
        inner.TranscodeToAacAsync(sourcePath, destinationPath, bitrateKbps, cancellationToken);
}
