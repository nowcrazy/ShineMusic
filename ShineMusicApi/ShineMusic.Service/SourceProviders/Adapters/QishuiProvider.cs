// 职责：汽水音乐（抖音音乐）→ V2 Provider 适配（Phase 4 恢复国内音源）。
//
// 能力矩阵来自音源巡检结论：汽水匿名接口对 VIP/付费曲目只下发 30~60 秒试听片段。
// Cookie 登录态可获得更完整授权信息。
// Phase 3 AudioValidator 负责拦截试听片段（时长 < 30s 或偏差 > 25%），适配层不做质量判断。

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>汽水音乐（抖音音乐）V2 Provider（薄适配，不重写协议）。</summary>
public sealed class QishuiProvider : LegacySourceProviderAdapter
{
    /// <summary>资源不可下载的业务错误码。</summary>
    public const string ErrorUnavailable = "qishui_resource_unavailable";

    private static readonly IReadOnlyList<ProviderCapabilityDescriptor> Capabilities =
    [
        new(ProviderCapability.Search, ProviderStatus.Degraded,
            "luna/search 可用；需 Android 客户端 UA 与完整设备参数。"),
        new(ProviderCapability.Metadata, ProviderStatus.Degraded,
            "seo_track 详情可用；分享页 SEO 数据源无需签名。"),
        new(ProviderCapability.Resolve, ProviderStatus.Degraded,
            "直链解析可用；匿名态 VIP 曲仅 30~60s 试听片段。"),
        new(ProviderCapability.Preview, ProviderStatus.Degraded,
            "匿名态 VIP 曲返回试听片段；AudioValidator 时长检测可拦截。"),
        new(ProviderCapability.Download320, ProviderStatus.Degraded,
            "匿名态非 VIP 曲可下载完整版本；VIP 曲仅试听。"),
        new(ProviderCapability.DownloadLossless, ProviderStatus.Unavailable,
            "汽水音乐不提供无损音质。"),
        new(ProviderCapability.Authentication, ProviderStatus.Degraded,
            "Cookie 可选；登录态可获取更完整授权信息。"),
        new(ProviderCapability.HealthCheck, ProviderStatus.Unknown,
            "Phase 5 才有独立健康检查端点。"),
    ];

    /// <summary>构造：从 DI 传入既有 provider 实例。</summary>
    /// <param name="legacy">既有 QishuiSourceProvider 实例。</param>
    /// <param name="options">SourceDownload 配置。</param>
    public QishuiProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
        : base(legacy, options)
    {
        if (!string.Equals(legacy.Provider, "qishui", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"QishuiProvider 需要 provider=qishui，实际为 {legacy.Provider}。", nameof(legacy));
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

        SourceDownloadInfo info;
        try
        {
            info = await Legacy.ResolveDownloadAsync(request.ProviderTrackId, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceUnavailableException ex)
        {
            stopwatch.Stop();
            return DownloadResult.Failed(ProviderName, request.ProviderTrackId, ErrorUnavailable,
                ex.Message, stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            return DownloadResult.Failed(ProviderName, request.ProviderTrackId, ErrorUnavailable,
                "未解析出可用下载直链。", stopwatch.ElapsedMilliseconds);

        return new DownloadResult
        {
            Stage = DownloadStage.Resolved,
            ProviderName = ProviderName,
            ProviderTrackId = request.ProviderTrackId,
            DownloadUrl = info.DownloadUrl,
            Format = MapFormat(info.Container),
            Quality = MapQuality(info),
            FileSize = 0,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }
}
