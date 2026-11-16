// 职责：酷我音乐 → V2 Provider 适配（Phase 4 恢复国内音源）。
//
// 能力矩阵来自音源巡检结论：酷我匿名态 purl 为空 / 11s 占位，封顶 128k。
// Cookie 登录态可取 VIP/无损资源。
//
// 酷我实现了 IQualityLadderProvider（多档候选：无损→320K→128K），
// DownloadAsync 走候选链解析首个有效直链，返回 DownloadStage.Resolved。

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>酷我音乐 V2 Provider（薄适配，不重写协议）。</summary>
public sealed class KuwoProvider : LegacySourceProviderAdapter
{
    /// <summary>资源不可下载的业务错误码。</summary>
    public const string ErrorUnavailable = "kuwo_resource_unavailable";

    private static readonly IReadOnlyList<ProviderCapabilityDescriptor> Capabilities =
    [
        new(ProviderCapability.Search, ProviderStatus.Degraded,
            "搜索 API 可用；匿名态可搜索但下载受限。"),
        new(ProviderCapability.Metadata, ProviderStatus.Degraded,
            "曲目详情可用。"),
        new(ProviderCapability.Resolve, ProviderStatus.Degraded,
            "多档候选解析（无损→320K→128K）；匿名态常返回 11s 占位。"),
        new(ProviderCapability.Preview, ProviderStatus.Unknown,
            "未做独立试听能力探测。"),
        new(ProviderCapability.Download320, ProviderStatus.Degraded,
            "Cookie 登录态可至 320k；匿名态仅 128k 且常为占位。"),
        new(ProviderCapability.DownloadLossless, ProviderStatus.Degraded,
            "Cookie 登录态可取无损；匿名态不可用。"),
        new(ProviderCapability.Authentication, ProviderStatus.Degraded,
            "Cookie 可选；匿名态严重受限。"),
        new(ProviderCapability.HealthCheck, ProviderStatus.Unknown,
            "Phase 5 才有独立健康检查端点。"),
    ];

    /// <summary>构造：从 DI 传入既有 provider 实例。</summary>
    /// <param name="legacy">既有 KuwoSourceProvider 实例。</param>
    /// <param name="options">SourceDownload 配置。</param>
    public KuwoProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
        : base(legacy, options)
    {
        if (!string.Equals(legacy.Provider, "kuwo", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"KuwoProvider 需要 provider=kuwo，实际为 {legacy.Provider}。", nameof(legacy));
    }

    /// <inheritdoc />
    public override IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities => Capabilities;

    /// <inheritdoc />
    public override async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();

        IReadOnlyList<SourceDownloadInfo> candidates;
        try
        {
            candidates = Legacy is IQualityLadderProvider ladder
                ? await ladder.ResolveDownloadCandidatesAsync(request.ProviderTrackId, cancellationToken).ConfigureAwait(false)
                : new[] { await Legacy.ResolveDownloadAsync(request.ProviderTrackId, cancellationToken).ConfigureAwait(false) };
        }
        catch (SourceUnavailableException ex)
        {
            stopwatch.Stop();
            return DownloadResult.Failed(ProviderName, request.ProviderTrackId, ErrorUnavailable,
                ex.Message, stopwatch.ElapsedMilliseconds);
        }

        var chosen = candidates.Count > 0 ? candidates[0] : null;
        stopwatch.Stop();
        if (chosen is null || string.IsNullOrWhiteSpace(chosen.DownloadUrl))
            return DownloadResult.Failed(ProviderName, request.ProviderTrackId, ErrorUnavailable,
                "未解析出可用下载直链（匿名态常为 11s 占位，需配置 Cookie）。", stopwatch.ElapsedMilliseconds);

        return new DownloadResult
        {
            Stage = DownloadStage.Resolved,
            ProviderName = ProviderName,
            ProviderTrackId = request.ProviderTrackId,
            DownloadUrl = chosen.DownloadUrl,
            Format = MapFormat(chosen.Container),
            Quality = MapQuality(chosen),
            FileSize = 0,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }
}
