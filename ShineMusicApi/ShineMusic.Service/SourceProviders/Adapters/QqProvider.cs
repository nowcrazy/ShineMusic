// 职责：QQ音乐 → V2 Provider 适配（Phase 4 恢复国内音源）。
//
// 能力矩阵来自音源巡检结论（docs/音源巡检与音质结论.md）：
// QQ 音乐匿名态 vkey 返回空 / purl 为空，仅 128k m4a；Cookie 登录态可至 320k/无损。
// 当前配置默认禁用（QqSourceOptions.Enabled=false），需配置 Cookie 后手动启用。
//
// QQ 音乐没有 ISourceFileDownloader，只有直链解析；DownloadAsync 返回 DownloadStage.Resolved。
// Phase 3 QualityGate 负责拦截低于 320K 的候选，适配层不做质量判断。

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>QQ音乐 V2 Provider（薄适配，不重写协议）。</summary>
public sealed class QqProvider : LegacySourceProviderAdapter
{
    /// <summary>资源不可下载的业务错误码。</summary>
    public const string ErrorUnavailable = "qq_resource_unavailable";

    private static readonly IReadOnlyList<ProviderCapabilityDescriptor> Capabilities =
    [
        new(ProviderCapability.Search, ProviderStatus.Degraded,
            "c.y.qq.com 搜索可用；匿名态可搜索但 VIP 曲下载受限。"),
        new(ProviderCapability.Metadata, ProviderStatus.Degraded,
            "曲目详情可用；匿名态部分字段缺失。"),
        new(ProviderCapability.Resolve, ProviderStatus.Degraded,
            "u.y.qq.com vkey 解析；匿名态 purl 常为空，需 Cookie 登录态。"),
        new(ProviderCapability.Preview, ProviderStatus.Unknown,
            "未做独立试听能力探测。"),
        new(ProviderCapability.Download320, ProviderStatus.Degraded,
            "Cookie 登录态可至 320k m4a；匿名态仅 128k。"),
        new(ProviderCapability.DownloadLossless, ProviderStatus.Degraded,
            "Cookie 登录态可取无损 flac；匿名态不可用。"),
        new(ProviderCapability.Authentication, ProviderStatus.Degraded,
            "Cookie 可选；匿名态严重受限。"),
        new(ProviderCapability.HealthCheck, ProviderStatus.Unknown,
            "Phase 5 才有独立健康检查端点。"),
    ];

    /// <summary>构造：从 DI 传入既有 provider 实例。</summary>
    /// <param name="legacy">既有 QqSourceProvider 实例。</param>
    /// <param name="options">SourceDownload 配置。</param>
    public QqProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
        : base(legacy, options)
    {
        if (!string.Equals(legacy.Provider, "qq", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"QqProvider 需要 provider=qq，实际为 {legacy.Provider}。", nameof(legacy));
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
                "未解析出可用下载直链（匿名态 purl 常为空，需配置 Cookie）。", stopwatch.ElapsedMilliseconds);

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
