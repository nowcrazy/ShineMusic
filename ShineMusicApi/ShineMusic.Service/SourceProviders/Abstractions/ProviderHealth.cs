// 职责：V2.0 Provider 运行时健康模型（提案 §6）。
//
// 明确区分两件事，这是提案反复强调、也最容易踩坑的地方：
//   1) 配置禁用：用户主动关闭，健康探测/熔断恢复**永不**允许自动启用；
//   2) 健康熔断：临时故障，冷却后可探测恢复。
// 因此 ProviderHealth 是「运行时」数据，与 ProviderDescriptor.ConfigEnabled（配置维度）、
// DownloadResult（单曲下载质量维度）完全分开存储。

namespace ShineMusic.Service.SourceProviders.Abstractions;

/// <summary>
/// 熔断状态（提案 §6 的 Closed → Open → HalfOpen → Closed）。
/// Phase 1 只提供状态容器与手工设置接口；自动转移与探测调度属 Phase 5。
/// <see cref="ConfigDisabled"/> 是**终止态**，不能由健康状态机转出。
/// </summary>
public enum ProviderCircuitState
{
    /// <summary>正常运行。</summary>
    Closed,

    /// <summary>暂时停止请求。</summary>
    Open,

    /// <summary>允许少量探测请求。</summary>
    HalfOpen,

    /// <summary>配置层禁用（不是故障，不允许自动恢复）。</summary>
    ConfigDisabled,
}

/// <summary>Provider 运行时健康快照（不可变，由 HealthTracker 生成）。</summary>
public sealed record ProviderHealth(
    string ProviderName,
    bool IsEnabled,
    ProviderStatus Status,
    ProviderCircuitState CircuitState,
    DateTimeOffset? LastCheckTime,
    int ConsecutiveFailures,
    double SuccessRate,
    TimeSpan AverageLatency,
    int TotalCalls,
    int TotalFailures,
    string? LastErrorCode);
