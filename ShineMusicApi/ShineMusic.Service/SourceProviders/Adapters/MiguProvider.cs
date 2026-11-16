// 职责：咪咕音乐 → V2 Provider 适配（提案 §10 Phase 1「兼容现有 Migu 实现」）。
//
// 咪咕自带 ISourceFileDownloader（curl 解密 + SQ→HQ→PQ 逐档实测降档），
// 因此 DownloadAsync 能真正把音频文件落地，返回 DownloadStage.Downloaded。
// 注意：逐档降档是既有行为，Phase 1 原样保留、不改 MiguSourceProvider 内部；
// 「PQ 128K 不得自动入库」属于 Phase 3 QualityGate 的职责，不在适配层偷偷改判。

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>咪咕音乐 V2 Provider（薄适配，不重写协议）。</summary>
public sealed class MiguProvider : LegacySourceProviderAdapter
{
    /// <summary>资源不可下载的业务错误码。</summary>
    public const string ErrorUnavailable = "migu_resource_unavailable";

    /// <summary>既有 provider 未实现自下载能力的错误码（装配错误）。</summary>
    public const string ErrorDownloaderMissing = "migu_downloader_unavailable";

    private static readonly IReadOnlyList<ProviderCapabilityDescriptor> Capabilities =
    [
        new(ProviderCapability.Search, ProviderStatus.Available,
            "运营商曲库可用；实测能命中腾讯独家原曲。"),
        new(ProviderCapability.Metadata, ProviderStatus.Available,
            "曲目详情可用。"),
        new(ProviderCapability.Resolve, ProviderStatus.Available,
            "h5/v2.4 加密响应内置解密，可解析 SQ/HQ/PQ 直链。"),
        new(ProviderCapability.Preview, ProviderStatus.Unknown,
            "未做独立试听能力探测，调度层不得按 Preview 能力使用。"),
        new(ProviderCapability.Download320, ProviderStatus.Available,
            "HQ=320k；SQ 声明但 URL 404 时既有实现会逐档实测降档。"),
        new(ProviderCapability.DownloadLossless, ProviderStatus.Degraded,
            "实测正版《晴天》SQ FLAC 935.1kbps；但偶有 SQ 声明与 404 URL，必须逐档实测。"),
        new(ProviderCapability.Authentication, ProviderStatus.Degraded,
            "Cookie 可选；匿名态可用。"),
        new(ProviderCapability.HealthCheck, ProviderStatus.Unknown,
            "Phase 5 才有独立健康检查端点。"),
    ];

    /// <summary>构造：从 DI 传入既有 provider 实例。</summary>
    /// <param name="legacy">既有 MiguSourceProvider 实例。</param>
    /// <param name="options">SourceDownload 配置。</param>
    public MiguProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
        : base(legacy, options)
    {
        if (!string.Equals(legacy.Provider, "migu", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"MiguProvider 需要 provider=migu，实际为 {legacy.Provider}。", nameof(legacy));
    }

    /// <inheritdoc />
    public override IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities => Capabilities;

    /// <inheritdoc />
    public override async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Legacy is not ISourceFileDownloader downloader)
            return DownloadResult.Failed(ProviderName, request.ProviderTrackId, ErrorDownloaderMissing,
                "既有咪咕 provider 未实现 ISourceFileDownloader，无法交付音频文件。");

        var stopwatch = Stopwatch.StartNew();
        SourceFileDownload file;
        try
        {
            file = await downloader.DownloadToFileAsync(
                request.ProviderTrackId, request.OutputDirectory, request.FileBaseName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SourceUnavailableException ex)
        {
            stopwatch.Stop();
            return DownloadResult.Failed(ProviderName, request.ProviderTrackId, ErrorUnavailable,
                ex.Message, stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();
        long size = 0;
        try
        {
            size = new FileInfo(file.FilePath).Length;
        }
        catch (IOException)
        {
            // 文件体积只是追溯信息，取不到不应让一次成功下载变成失败。
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new DownloadResult
        {
            Stage = DownloadStage.Downloaded,
            ProviderName = ProviderName,
            ProviderTrackId = request.ProviderTrackId,
            FilePath = file.FilePath,
            Format = MapFormat(file.Container),
            Quality = MapQuality(file.Quality),
            FileSize = size,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }
}
