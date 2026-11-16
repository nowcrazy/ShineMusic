// 职责：酷狗音乐 → V2 Provider 适配（Phase 4 恢复国内音源）。
//
// 能力矩阵来自音源巡检结论：酷狗匿名态仅 128k，且搜索命中 Live/黑胶版本。
// 已有 KugouRateLimitHandler（令牌桶 2 QPS）防止触发 errcode:1002 封禁。
// Cookie 登录态可取 VIP 资源。
//
// 酷狗没有 ISourceFileDownloader 也没有 IQualityLadderProvider，只有单档直链解析。

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>酷狗音乐 V2 Provider（薄适配，不重写协议）。</summary>
public sealed class KugouProvider : LegacySourceProviderAdapter
{
    /// <summary>资源不可下载的业务错误码。</summary>
    public const string ErrorUnavailable = "kugou_resource_unavailable";

    private static readonly IReadOnlyList<ProviderCapabilityDescriptor> Capabilities =
    [
        new(ProviderCapability.Search, ProviderStatus.Degraded,
            "移动端搜索 API 可用；但搜索结果常命中 Live/黑胶版本，需身份匹配器过滤。"),
        new(ProviderCapability.Metadata, ProviderStatus.Degraded,
            "getSongInfo 可用；需移动端 UA。"),
        new(ProviderCapability.Resolve, ProviderStatus.Degraded,
            "sharefs 直链解析；匿名态仅 128k。"),
        new(ProviderCapability.Preview, ProviderStatus.Unknown,
            "未做独立试听能力探测。"),
        new(ProviderCapability.Download320, ProviderStatus.Degraded,
            "Cookie 登录态可至 320k；匿名态仅 128k。"),
        new(ProviderCapability.DownloadLossless, ProviderStatus.Degraded,
            "Cookie 登录态可取无损；匿名态不可用。"),
        new(ProviderCapability.Authentication, ProviderStatus.Degraded,
            "Cookie 可选；匿名态严重受限。"),
        new(ProviderCapability.HealthCheck, ProviderStatus.Unknown,
            "Phase 5 才有独立健康检查端点。"),
    ];

    /// <summary>构造：从 DI 传入既有 provider 实例。</summary>
    /// <param name="legacy">既有 KugouSourceProvider 实例。</param>
    /// <param name="options">SourceDownload 配置。</param>
    public KugouProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
        : base(legacy, options)
    {
        if (!string.Equals(legacy.Provider, "kugou", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"KugouProvider 需要 provider=kugou，实际为 {legacy.Provider}。", nameof(legacy));
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
