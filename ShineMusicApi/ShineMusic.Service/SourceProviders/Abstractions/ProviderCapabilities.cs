// 职责：V2.0 Provider 能力模型（提案 §1.1 / §11）。
//
// 设计取舍：一个音乐平台的「搜索 / 元数据 / 解析 / 下载」可用性并不一致，
// 因此不再用单个 Enabled 描述整个音源；每项能力独立声明状态。
// 能力状态（Available/Degraded/Unavailable/Unknown）与配置启用、运行时健康、单曲下载质量
// 是三个互相独立的维度，见提案 §11 最后一条架构原则——本文件只承载「能力描述」。
//
// 边界：Phase 1 的能力矩阵是**静态声明**（来自适配层对现有 provider 的实测结论），
// 运行期动态探测、熔断状态机与自动恢复属 Phase 5（ProviderHealthMonitor）。

namespace ShineMusic.Service.SourceProviders.Abstractions;

/// <summary>Provider 可提供的能力位。一个平台可同时具备多项，也可只具备其中一项。</summary>
[Flags]
public enum ProviderCapability
{
    /// <summary>无能力。</summary>
    None = 0,

    /// <summary>歌曲搜索。</summary>
    Search = 1 << 0,

    /// <summary>歌曲元数据（详情）获取。</summary>
    Metadata = 1 << 1,

    /// <summary>歌曲资源解析（取得直链 / 分档候选）。</summary>
    Resolve = 1 << 2,

    /// <summary>试听资源（正式曲库不应使用）。</summary>
    Preview = 1 << 3,

    /// <summary>320K 高码率下载。</summary>
    Download320 = 1 << 4,

    /// <summary>无损（FLAC 等）下载。</summary>
    DownloadLossless = 1 << 5,

    /// <summary>登录凭据状态。</summary>
    Authentication = 1 << 6,

    /// <summary>接口健康检查。</summary>
    HealthCheck = 1 << 7,
}

/// <summary>
/// 单项能力的状态。<see cref="Unknown"/> 表示尚未检测，调度层必须保守地**不**自动使用，
/// 避免「没验证过」被当成「可用」（质量优先）。
/// </summary>
public enum ProviderStatus
{
    /// <summary>可用。</summary>
    Available,

    /// <summary>部分可用 / 按歌曲或按凭据而定。</summary>
    Degraded,

    /// <summary>不可用。</summary>
    Unavailable,

    /// <summary>尚未检测。</summary>
    Unknown,
}

/// <summary>单项能力的声明。<paramref name="Note"/> 记录判定依据，供审核与后续复测追溯。</summary>
public sealed record ProviderCapabilityDescriptor(
    ProviderCapability Capability,
    ProviderStatus Status,
    string? Note = null)
{
    /// <summary>该能力是否允许被自动调度（Unknown 与 Unavailable 一律不放行）。</summary>
    public bool IsDispatchable => Status is ProviderStatus.Available or ProviderStatus.Degraded;
}

/// <summary>
/// Registry 对外暴露的 provider 快照：把「平台自述能力」与「配置维度」拼在一起。
/// 运行时健康请查 <see cref="ProviderHealth"/>，不要写进本类型。
/// </summary>
public sealed record ProviderDescriptor(
    string ProviderName,
    int Priority,
    bool ConfigEnabled,
    bool SearchEnabled,
    bool DownloadEnabled,
    IReadOnlyList<ProviderCapabilityDescriptor> Capabilities,
    string? DisabledReason = null,
    bool MetadataEnabled = true)
{
    /// <summary>未声明的能力按 Unknown 处理（不放行）。</summary>
    public ProviderStatus StateOf(ProviderCapability capability) =>
        Capabilities.FirstOrDefault(item => item.Capability == capability)?.Status
        ?? ProviderStatus.Unknown;

    /// <summary>能力是否声明为可调度（不含配置/健康过滤，过滤由 Registry 完成）。</summary>
    public bool Declares(ProviderCapability capability) =>
        Capabilities.Any(item => item.Capability == capability && item.IsDispatchable);
}
