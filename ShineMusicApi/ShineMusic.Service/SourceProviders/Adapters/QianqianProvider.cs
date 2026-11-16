// 职责：千千音乐（百度音乐）→ V2 Provider 适配（Phase 4 恢复国内音源）。
//
// 能力矩阵来自音源巡检结论：千千公开 API 免登录，中文老歌覆盖；
// 但实测高码率 FLAC 命中《等晴天》等错误曲目（搜索命中翻唱/错曲）。
// 需身份匹配器 + AudioValidator 双重过滤。
//
// 千千没有 ISourceFileDownloader 也没有 IQualityLadderProvider，只有单档直链解析。

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>千千音乐（百度音乐）V2 Provider（薄适配，不重写协议）。</summary>
public sealed class QianqianProvider : LegacySourceProviderAdapter
{
    /// <summary>资源不可下载的业务错误码。</summary>
    public const string ErrorUnavailable = "qianqian_resource_unavailable";

    private static readonly IReadOnlyList<ProviderCapabilityDescriptor> Capabilities =
    [
        new(ProviderCapability.Search, ProviderStatus.Degraded,
            "91q.com 公开 API 免登录可搜索；但实测高码率结果常命中翻唱/错曲，需身份匹配器过滤。"),
        new(ProviderCapability.Metadata, ProviderStatus.Degraded,
            "曲目详情可用；需浏览器 UA + Referer + from:web 头。"),
        new(ProviderCapability.Resolve, ProviderStatus.Degraded,
            "直链解析可用；高码率 FLAC 命中率低。"),
        new(ProviderCapability.Preview, ProviderStatus.Unknown,
            "未做独立试听能力探测。"),
        new(ProviderCapability.Download320, ProviderStatus.Degraded,
            "320k 可用但需逐曲验证身份（实测有错曲命中）。"),
        new(ProviderCapability.DownloadLossless, ProviderStatus.Degraded,
            "FLAC 声明可用但实测常为错曲；必须经 AudioValidator 编码验证 + 身份匹配。"),
        new(ProviderCapability.Authentication, ProviderStatus.Available,
            "公开 API 免登录。"),
        new(ProviderCapability.HealthCheck, ProviderStatus.Unknown,
            "Phase 5 才有独立健康检查端点。"),
    ];

    /// <summary>构造：从 DI 传入既有 provider 实例。</summary>
    /// <param name="legacy">既有 QianqianSourceProvider 实例。</param>
    /// <param name="options">SourceDownload 配置。</param>
    public QianqianProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
        : base(legacy, options)
    {
        if (!string.Equals(legacy.Provider, "qianqian", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"QianqianProvider 需要 provider=qianqian，实际为 {legacy.Provider}。", nameof(legacy));
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
