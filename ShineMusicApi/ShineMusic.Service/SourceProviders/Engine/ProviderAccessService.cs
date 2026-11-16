using Microsoft.Extensions.Logging;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Engine;

/// <summary>生产链路的能力入口：元数据独立调度，旧下载流水线共用能力准入和健康记录。</summary>
public sealed class ProviderAccessService(
    ProviderRegistry registry,
    ProviderOrchestrator orchestrator,
    ProviderHealthTracker health,
    IEnumerable<IMusicSourceProvider> legacyProviders,
    ILogger<ProviderAccessService> logger)
{
    private sealed record MetadataLookup(MusicTrack? Track);

    /// <summary>是否配置了音频资源解析/下载能力，不受临时熔断影响。</summary>
    public bool IsDownloadConfigured(string providerName) =>
        registry.IsConfigured(providerName, ProviderCapability.Resolve);

    /// <summary>下载执行时重新检查开关、能力、健康，防止排队后关闭的源继续下载。</summary>
    public IMusicSourceProvider RequireDownloadProvider(string providerName)
    {
        if (registry.Find<IMusicDownloadProvider>(providerName, ProviderCapability.Resolve) is null)
            throw new SourceUnavailableException($"音源 '{providerName}' 的下载能力未启用、未实现或已熔断。");
        return legacyProviders.FirstOrDefault(provider =>
            string.Equals(provider.Provider, providerName, StringComparison.OrdinalIgnoreCase) && provider.IsEnabled)
            ?? throw new SourceUnavailableException($"音源 '{providerName}' 未接入文件下载流水线。");
    }

    /// <summary>按 ID 读取元数据；禁用、失败或不存在时返回 null，可由调用方使用已有快照。</summary>
    public async Task<SourceSearchResult?> GetMetadataAsync(
        string providerName, string trackId, CancellationToken cancellationToken)
    {
        var result = await orchestrator.InvokeAsync<IMusicMetadataProvider, MetadataLookup>(
            providerName,
            async (provider, token) => new MetadataLookup(await provider.GetTrackAsync(trackId, token).ConfigureAwait(false)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success)
            logger.LogDebug("Metadata lookup skipped/failed for {Provider}: {ErrorCode}", providerName, result.ErrorCode);
        return result.Value?.Track?.ToLegacy();
    }

    /// <summary>成功指音频下载及质量校验通过，不将 DB/MQ 移交失败误计为音源故障。</summary>
    public void RecordDownloadSuccess(string providerName, TimeSpan elapsed) =>
        health.RecordSuccess(providerName, elapsed);

    /// <summary>仅真实下载尝试参与健康统计；调用方排除取消、策略禁用与移交失败。</summary>
    public void RecordDownloadFailure(string providerName, string errorCode, TimeSpan elapsed) =>
        health.RecordFailure(providerName, errorCode, elapsed);
}
