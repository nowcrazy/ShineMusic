// 职责：网易云音乐 → V2 Provider 适配（提案 §10 Phase 1「兼容现有 Netease 实现」）。
//
// 能力矩阵来自 2026-11-12 逐源实测（docs/音源巡检结果.json / 音源巡检与音质结论.md），
// 是**静态声明**而非实时探测结果；Phase 5 的 HealthMonitor 才会把它变成动态状态。
// 网易云没有 ISourceFileDownloader，只有直链解析 + IQualityLadderProvider 分档候选：
// 因此 DownloadAsync 明确返回 DownloadStage.Resolved，绝不谎称音频文件已落地。

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>网易云音乐 V2 Provider（薄适配，不重写协议）。</summary>
public sealed class NeteaseProvider : LegacySourceProviderAdapter
{
    /// <summary>资源不可下载的业务错误码。</summary>
    public const string ErrorUnavailable = "netease_resource_unavailable";

    private static readonly IReadOnlyList<ProviderCapabilityDescriptor> Capabilities =
    [
        new(ProviderCapability.Search, ProviderStatus.Available,
            "官方 /api/search/get 免登录可用；实测中文曲库覆盖良好。"),
        new(ProviderCapability.Metadata, ProviderStatus.Available,
            "官方 /api/v3/song/detail 可用。"),
        new(ProviderCapability.Resolve, ProviderStatus.Available,
            "/api/song/enhance/player/url 解析 320k 直链；VIP 曲经 gdstudio 回退。"),
        new(ProviderCapability.Preview, ProviderStatus.Unknown,
            "未对官方试听资源做独立探测，调度层不得按 Preview 能力使用。"),
        new(ProviderCapability.Download320, ProviderStatus.Available,
            "官方 320000 档；文件落地沿用既有 SourceDownloadJob 的 HttpClient 下载路径。"),
        new(ProviderCapability.DownloadLossless, ProviderStatus.Degraded,
            "按歌曲验证：仅 VIP / 第三方 gdstudio 回退可得无损，且需逐档实测 URL 有效性。"),
        new(ProviderCapability.Authentication, ProviderStatus.Degraded,
            "Cookie 可选；匿名态可用，登录态覆盖 VIP 曲。"),
        new(ProviderCapability.HealthCheck, ProviderStatus.Unknown,
            "Phase 5 才有独立健康检查端点。"),
    ];

    /// <summary>构造：从 DI 传入既有 provider 实例。</summary>
    /// <param name="legacy">既有 NeteaseSourceProvider 实例。</param>
    /// <param name="options">SourceDownload 配置。</param>
    public NeteaseProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
        : base(legacy, options)
    {
        if (!string.Equals(legacy.Provider, "netease", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"NeteaseProvider 需要 provider=netease，实际为 {legacy.Provider}。", nameof(legacy));
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
            candidates = Legacy is ILazySourceDownloadProvider
                ? new[] { await Legacy.ResolveDownloadAsync(request.ProviderTrackId, cancellationToken).ConfigureAwait(false) }
                : Legacy is IQualityLadderProvider ladder
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
                "未解析出可用下载直链。", stopwatch.ElapsedMilliseconds);

        // 只做解析，不落文件。质量等级是**声明值**，真实编码/时长/SHA256 校验留 Phase 3。
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
