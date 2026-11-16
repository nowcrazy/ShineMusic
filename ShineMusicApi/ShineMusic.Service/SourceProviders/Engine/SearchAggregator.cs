// 职责：V2.0 Phase 2 搜索聚合器 —— 四处生产搜索链路的唯一入口（裁决④「四处全部统一收口」）。
//
// 源清单以 V2 ProviderRegistry 为准（用户裁决）：调用方不再自己遍历
// IEnumerable<IMusicSourceProvider>，也不再各自实现并行 / 超时 / 异常收敛，
// 这些能力统一由 ProviderOrchestrator.SearchAsync 提供。
//
// 刻意保留的兼容边界：
//   1) V2 MusicTrack 转回既有 SourceSearchResult（纯字段搬运），下游 DTO / 兜底匹配 / 评分链不动；
//   2) CanDownloadAsync 与 IQualityLadderProvider 尚未 V2 化，结果里带出同名 legacy 实例，
//      调用方按名字取用；纯 V2 provider（无 legacy）时该字段为 null，预判保守放行。
//
// 本类**不做任何曲目身份判定**，匹配一律走 TrackIdentityMatcher.Evaluate（唯一决策点）。

using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Engine;

/// <summary>统一搜索入口：屏蔽 V2 调度细节，向旧链路交付既有 SourceSearchResult 形状。</summary>
public interface ISearchAggregator
{
    /// <summary>V2 Registry 的 provider 快照（含被禁用的，供管理端提示与启动校验使用）。</summary>
    IReadOnlyList<ProviderDescriptor> Providers { get; }

    /// <summary>
    /// 并发搜索 V2 注册且可调度（配置 / 能力 / 健康三维放行）的搜索 provider。
    /// 单源失败、超时、预算耗尽一律收敛为 <see cref="ProviderSearchOutcome"/>，不冒泡。
    /// </summary>
    /// <param name="keyword">搜索关键词。</param>
    /// <param name="providerNames">限定参与的 platform 名；null 表示全部可调度源。</param>
    /// <param name="maxResultsPerProvider">每源最多保留的条目数（对齐旧链路各调用点的 limit）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐源聚合结果。</returns>
    Task<AggregateSearchResult> SearchAsync(
        string keyword,
        IReadOnlyCollection<string>? providerNames = null,
        int maxResultsPerProvider = 50,
        CancellationToken cancellationToken = default);

    /// <summary>按平台名取既有 provider 实例（用于尚未 V2 化的轻量预判能力）。</summary>
    /// <param name="providerName">平台标识。</param>
    /// <returns>找到的既有 provider；纯 V2 provider 返回 null。</returns>
    IMusicSourceProvider? FindLegacyProvider(string providerName);
}

/// <summary>单源的聚合搜索结果。<see cref="ErrorCode"/> 非空表示该源本次未成功返回。</summary>
/// <param name="ProviderName">平台标识。</param>
/// <param name="Results">该源返回的条目（已按调用点的 limit 截断）。</param>
/// <param name="ErrorCode">机器可读错误码（provider_timeout / provider_failed / search_budget_exhausted ...）。</param>
/// <param name="ErrorMessage">错误详情，供管理端 providerErrors / 歌单诊断展示。</param>
/// <param name="Legacy">同名既有 provider 实例；纯 V2 provider 为 null。</param>
public sealed record ProviderSearchOutcome(
    string ProviderName,
    IReadOnlyList<SourceSearchResult> Results,
    string? ErrorCode,
    string? ErrorMessage,
    IMusicSourceProvider? Legacy);

/// <summary>整轮聚合结果，顺序与 Registry 的优先级顺序一致。</summary>
/// <param name="Outcomes">逐源结果。</param>
public sealed record AggregateSearchResult(IReadOnlyList<ProviderSearchOutcome> Outcomes)
{
    /// <summary>全部源 Successful 的结果展平（失败源为空数组，不污染）。</summary>
    public IReadOnlyList<SourceSearchResult> AllResults =>
        Outcomes.SelectMany(outcome => outcome.Results).ToArray();
}

/// <summary>默认聚合器实现：只做调度结果 → 既有模型的转换，不含匹配逻辑。</summary>
public sealed class SearchAggregator : ISearchAggregator
{
    private readonly ProviderOrchestrator _orchestrator;
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<IMusicSourceProvider> _legacyProviders;

    /// <summary>由 DI 构造。</summary>
    /// <param name="orchestrator">能力感知调度器。</param>
    /// <param name="registry">V2 注册表（源清单的唯一事实源）。</param>
    /// <param name="legacyProviders">既有 provider 集合，用于回填 Legacy 字段。</param>
    public SearchAggregator(
        ProviderOrchestrator orchestrator,
        ProviderRegistry registry,
        IEnumerable<IMusicSourceProvider> legacyProviders)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(legacyProviders);
        _legacyProviders = legacyProviders.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderDescriptor> Providers => _registry.List();

    /// <inheritdoc />
    public IMusicSourceProvider? FindLegacyProvider(string providerName) =>
        string.IsNullOrWhiteSpace(providerName)
            ? null
            : _legacyProviders.FirstOrDefault(provider =>
                string.Equals(provider.Provider, providerName, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<AggregateSearchResult> SearchAsync(
        string keyword,
        IReadOnlyCollection<string>? providerNames = null,
        int maxResultsPerProvider = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        IReadOnlySet<string>? filter = providerNames is null
            ? null
            : new HashSet<string>(providerNames, StringComparer.OrdinalIgnoreCase);
        var take = Math.Clamp(maxResultsPerProvider, 1, 50);
        var calls = await _orchestrator.SearchAsync(keyword, filter, cancellationToken).ConfigureAwait(false);

        var outcomes = calls.Select(call =>
        {
            var tracks = call.Success && call.Value is not null
                ? call.Value
                : Array.Empty<MusicTrack>();
            var results = tracks
                .Take(take)
                .Select(track => track.ToLegacy())
                .ToArray();
            return new ProviderSearchOutcome(
                call.ProviderName,
                results,
                call.Success ? null : call.ErrorCode,
                call.Success ? null : call.ErrorMessage,
                FindLegacyProvider(call.ProviderName));
        }).ToArray();

        return new AggregateSearchResult(outcomes);
    }
}

/// <summary>
/// 启动期源清单一致性校验（用户裁决「以 V2 Registry 为准 + 启动校验」）。
/// 生产搜索收口后，「legacy 启用但 V2 没注册」不再是能力增强，而是**静默漏源**：
/// 下载链路还能用这个源，所有搜索链路却都看不到它。这里把它变成 fail-fast。
/// </summary>
public static class SearchProviderParity
{
    /// <summary>找出「legacy 已启用、但 V2 Registry 没有可搜索注册」的平台名。</summary>
    /// <param name="legacyProviders">既有 provider 集合。</param>
    /// <param name="v2Providers">V2 Registry 快照。</param>
    /// <param name="frameworkEnabled">V2 框架总开关；关闭时所有 V2 源视为未配置。</param>
    /// <returns>缺失注册的平台名（去重）。</returns>
    public static IReadOnlyList<string> FindUnregistered(
        IEnumerable<IMusicSourceProvider> legacyProviders,
        IReadOnlyList<ProviderDescriptor> v2Providers,
        bool frameworkEnabled)
    {
        ArgumentNullException.ThrowIfNull(legacyProviders);
        ArgumentNullException.ThrowIfNull(v2Providers);

        var searchable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (frameworkEnabled)
        {
            foreach (var descriptor in v2Providers)
            {
                // 主动关闭搜索、只保留元数据或下载是合法配置；这里仅检查是否漏注册。
                searchable.Add(descriptor.ProviderName);
            }
        }

        return legacyProviders
            .Where(provider => provider.IsEnabled
                // ytmusic 在四处搜索链路里都被显式排除（ytsearch 需代理），不算漏源。
                && !string.Equals(provider.Provider, "ytmusic", StringComparison.OrdinalIgnoreCase)
                && !searchable.Contains(provider.Provider))
            .Select(provider => provider.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>存在漏源时抛出，阻止进程在生产搜索「半瞎」状态下启动。</summary>
    /// <param name="legacyProviders">既有 provider 集合。</param>
    /// <param name="v2Providers">V2 Registry 快照。</param>
    /// <param name="frameworkEnabled">V2 框架总开关。</param>
    /// <exception cref="InvalidOperationException">存在已启用但未注册的音源。</exception>
    public static void ValidateOrThrow(
        IEnumerable<IMusicSourceProvider> legacyProviders,
        IReadOnlyList<ProviderDescriptor> v2Providers,
        bool frameworkEnabled)
    {
        var missing = FindUnregistered(legacyProviders, v2Providers, frameworkEnabled);
        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            $"生产搜索已收口到 V2 ProviderRegistry，但以下已启用音源没有可搜索的 V2 注册：{string.Join("、", missing)}。" +
            "请在 AddShineMusicProviderFrameworkModule() 里 AddMusicProvider<T>() 并补齐 " +
            "SourceDownload:ProviderFramework:Providers 配置，或同步关闭这些音源。" +
            "若只是关闭了 ProviderFramework:Enabled 总开关：V2 已接管生产搜索，不能只关总开关而保留 legacy 启用源。");
    }
}
