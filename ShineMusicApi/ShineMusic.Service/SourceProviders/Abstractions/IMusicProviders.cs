// 职责：V2.0 搜索 / 元数据 / 下载三组能力接口（提案 §3）。
//
// 「以后新增音乐源，只需要实现对应接口并注册，不需要修改 SearchAggregator 或 DownloadCoordinator」：
// 因此三组接口彼此独立，允许一个平台只实现搜索、另一个只实现下载。
// IMusicProvider 是所有能力接口的公共基接口，Registry 只依赖它做发现，不认识任何具体平台。

namespace ShineMusic.Service.SourceProviders.Abstractions;

/// <summary>
/// 所有 V2 Provider 的公共契约：名称、平台自述能力、配置启用状态。
/// 注意 <see cref="IsConfigEnabled"/> 属于**配置维度**，运行时健康状态不得改写它。
/// </summary>
public interface IMusicProvider
{
    /// <summary>平台标识（netease / migu ...），与配置节、任务行的 Provider 字段一致。</summary>
    string ProviderName { get; }

    /// <summary>平台自述的能力矩阵（静态声明，Phase 5 之前不做运行期动态改写）。</summary>
    IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities { get; }

    /// <summary>是否应用仓库保存的真实平台巡检证据；自定义实现默认只使用自身声明。</summary>
    bool UsesVerifiedCapabilities => false;

    /// <summary>配置层是否启用；false 时 Registry 绝不调度，健康探测也不得自动启用。</summary>
    bool IsConfigEnabled { get; }

    /// <summary>配置禁用的原因（可用时为 null），写入 ProviderDescriptor 供审核页展示。</summary>
    string? ConfigDisabledReason { get; }
}

/// <summary>搜索能力（提案 §3.1）。</summary>
public interface IMusicSearchProvider : IMusicProvider
{
    /// <summary>按关键词搜索，返回统一曲目模型；异常由 Orchestrator 收敛，实现方无需吞异常。</summary>
    /// <param name="keyword">搜索关键词。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<MusicTrack>> SearchAsync(string keyword, CancellationToken cancellationToken = default);
}

/// <summary>元数据能力（提案 §3.2）。</summary>
public interface IMusicMetadataProvider : IMusicProvider
{
    /// <summary>按平台曲目 Id 取详情；不存在返回 null。</summary>
    /// <param name="trackId">平台曲目 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MusicTrack?> GetTrackAsync(string trackId, CancellationToken cancellationToken = default);
}

/// <summary>下载 / 资源解析能力（提案 §3.3）。返回结构化结果而非仅 URL。</summary>
public interface IMusicDownloadProvider : IMusicProvider
{
    /// <summary>下载或解析资源。只拿到直链时返回 <see cref="DownloadStage.Resolved"/>，不谎称文件已落地。</summary>
    /// <param name="request">下载请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default);
}
