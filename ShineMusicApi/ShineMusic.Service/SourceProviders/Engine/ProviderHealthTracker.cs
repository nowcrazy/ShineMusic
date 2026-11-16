// 职责：Provider 运行时健康状态管理（提案 §6 / §11「三个维度独立存储」）。
//
// Phase 5 更新：RecordFailure 增加自动熔断判定——连续失败达阈值时自动 Closed→Open。
// 自动冷却恢复（Open→HalfOpen→Closed）由 ProviderHealthMonitor 定时巡检驱动，
// 本类只负责「失败累计 + 触发熔断」这一同步路径，不做延时转移（避免引入定时器）。
//
// 不变式（测试锁定）：配置禁用是终止态，RecordSuccess / TrySetCircuitState 都不能把它改回可用。

using System.Collections.Concurrent;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Engine;

/// <summary>进程内、线程安全的 Provider 健康记录器。不持久化、不连库。</summary>
public sealed class ProviderHealthTracker
{
    private sealed class State
    {
        public readonly object Gate = new();
        public int TotalCalls;
        public int TotalFailures;
        public int ConsecutiveFailures;
        public double TotalLatencyMs;
        public DateTimeOffset? LastCheckTime;
        public string? LastErrorCode;
        public ProviderCircuitState Circuit = ProviderCircuitState.Closed;
        public DateTimeOffset? CircuitSince;
        public double RecoverySeconds;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    internal TimeProvider Clock => _timeProvider;
    private double _recoverySeconds = 60;
    private double _maxRecoverySeconds = 300;

    internal void ConfigureRecovery(int threshold, int recoverySeconds, int maxRecoverySeconds)
    {
        SetFailureThreshold(threshold);
        _recoverySeconds = Math.Max(1, recoverySeconds);
        _maxRecoverySeconds = Math.Max(_recoverySeconds, maxRecoverySeconds);
    }

    // Keep cooldown state under the same lock as call outcomes; polling cannot lose a probe failure.
    internal void CheckRecovery(string providerName, bool configEnabled)
    {
        if (!configEnabled)
            return;
        var state = For(providerName);
        lock (state.Gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (state.CircuitSince is not { } since)
                return;
            if (state.Circuit == ProviderCircuitState.Open
                && (now - since).TotalSeconds >= state.RecoverySeconds)
            {
                state.Circuit = ProviderCircuitState.HalfOpen;
                state.CircuitSince = now;
            }
            else if (state.Circuit == ProviderCircuitState.HalfOpen
                     && (now - since).TotalSeconds >= _maxRecoverySeconds)
            {
                Open(state, now, escalate: true);
            }
        }
    }

    private void Open(State state, DateTimeOffset now, bool escalate)
    {
        state.RecoverySeconds = escalate
            ? Math.Min(_maxRecoverySeconds, Math.Max(_recoverySeconds, state.RecoverySeconds) * 2)
            : _recoverySeconds;
        state.Circuit = ProviderCircuitState.Open;
        state.CircuitSince = now;
    }

    /// <summary>使用系统时钟。</summary>
    public ProviderHealthTracker() : this(TimeProvider.System)
    {
    }

    /// <summary>注入时钟（测试可用 FakeTimeProvider 做确定性断言）。</summary>
    /// <param name="timeProvider">时间提供者。</param>
    public ProviderHealthTracker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    private State For(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return _states.GetOrAdd(providerName, _ => new State());
    }

    /// <summary>记录一次成功调用。</summary>
    /// <param name="providerName">平台标识。</param>
    /// <param name="latency">本次耗时。</param>
    /// <param name="configEnabled">配置层是否启用；false 时不改写运行时状态。</param>
    public void RecordSuccess(string providerName, TimeSpan latency, bool configEnabled = true)
    {
        if (!configEnabled)
            return;

        var state = For(providerName);
        lock (state.Gate)
        {
            state.TotalCalls++;
            state.ConsecutiveFailures = 0;
            state.TotalLatencyMs += Math.Max(0, latency.TotalMilliseconds);
            state.LastCheckTime = _timeProvider.GetUtcNow();
            state.LastErrorCode = null;

            // Phase 5：HalfOpen 探测成功 → 自动恢复 Closed。
            // Open 状态不会因为成功调用而恢复（Open 期间不会有正常调用到达此处）。
            if (state.Circuit == ProviderCircuitState.HalfOpen)
            {
                state.Circuit = ProviderCircuitState.Closed;
                state.CircuitSince = null;
                state.RecoverySeconds = 0;
            }
        }
    }

    /// <summary>
    /// 连续失败阈值；达到后自动触发 Closed→Open 熔断。
    /// 默认 5（与 SourceProviderHealthCheckOptions.FailureThreshold 一致），
    /// 由 ProviderHealthMonitor 在启动时通过 <see cref="SetFailureThreshold"/> 设置。
    /// </summary>
    private int _failureThreshold = 5;

    /// <summary>设置连续失败阈值（由 ProviderHealthMonitor 启动时调用）。</summary>
    /// <param name="threshold">阈值。</param>
    internal void SetFailureThreshold(int threshold) =>
        _failureThreshold = Math.Max(1, threshold);

    /// <summary>记录一次失败调用（异常、超时或结构化失败结果）。</summary>
    /// <param name="providerName">平台标识。</param>
    /// <param name="errorCode">机器可读错误码。</param>
    /// <param name="latency">本次耗时。</param>
    /// <param name="configEnabled">配置层是否启用；false 时不改写运行时状态。</param>
    public void RecordFailure(string providerName, string errorCode, TimeSpan latency, bool configEnabled = true)
    {
        if (!configEnabled || !SourceDownload.ProviderHealthMonitor.ShouldCountAsFailure(errorCode))
            return;

        var state = For(providerName);
        lock (state.Gate)
        {
            state.TotalCalls++;
            state.TotalFailures++;
            state.ConsecutiveFailures++;
            state.TotalLatencyMs += Math.Max(0, latency.TotalMilliseconds);
            state.LastCheckTime = _timeProvider.GetUtcNow();
            state.LastErrorCode = errorCode;

            if (state.Circuit == ProviderCircuitState.HalfOpen)
                Open(state, _timeProvider.GetUtcNow(), escalate: true);
            else if (state.Circuit == ProviderCircuitState.Closed
                     && state.ConsecutiveFailures >= _failureThreshold)
                Open(state, _timeProvider.GetUtcNow(), escalate: false);
        }
    }

    /// <summary>
    /// 设置熔断状态。Phase 5 的健康监控器会调用它做自动转移；
    /// 配置禁用时只允许保持 <see cref="ProviderCircuitState.ConfigDisabled"/>，返回 false。
    /// </summary>
    /// <param name="providerName">平台标识。</param>
    /// <param name="state">目标熔断状态。</param>
    /// <param name="configEnabled">配置层是否启用。</param>
    /// <returns>是否成功设置。</returns>
    public bool TrySetCircuitState(string providerName, ProviderCircuitState state, bool configEnabled)
    {
        if (!configEnabled)
            return false;

        var entry = For(providerName);
        lock (entry.Gate)
        {
            entry.Circuit = state;
            entry.CircuitSince = _timeProvider.GetUtcNow();
            if (state == ProviderCircuitState.Open)
                entry.RecoverySeconds = _recoverySeconds;
        }
        return true;
    }

    /// <summary>清空某平台的运行时统计（不改动配置）。</summary>
    /// <param name="providerName">平台标识。</param>
    public void Reset(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        _states.TryRemove(providerName, out _);
    }

    /// <summary>
    /// 生成健康快照。配置禁用时强制返回 <c>IsEnabled=false / Status=Unavailable / ConfigDisabled</c>，
    /// 与运行时统计完全解耦——这是「配置禁用不允许健康检查擅自启用」的落地点。
    /// </summary>
    /// <param name="providerName">平台标识。</param>
    /// <param name="configEnabled">配置层是否启用。</param>
    public ProviderHealth Snapshot(string providerName, bool configEnabled)
    {
        var entry = For(providerName);
        int totalCalls, totalFailures, consecutiveFailures;
        double totalLatencyMs;
        DateTimeOffset? lastCheck;
        string? lastErrorCode;
        ProviderCircuitState circuit;

        lock (entry.Gate)
        {
            totalCalls = entry.TotalCalls;
            totalFailures = entry.TotalFailures;
            consecutiveFailures = entry.ConsecutiveFailures;
            totalLatencyMs = entry.TotalLatencyMs;
            lastCheck = entry.LastCheckTime;
            lastErrorCode = entry.LastErrorCode;
            circuit = entry.Circuit;
        }

        if (!configEnabled)
            return new ProviderHealth(providerName, false, ProviderStatus.Unavailable,
                ProviderCircuitState.ConfigDisabled, lastCheck, consecutiveFailures,
                SuccessRate(totalCalls, totalFailures), AverageLatency(totalCalls, totalLatencyMs),
                totalCalls, totalFailures, lastErrorCode);

        var status = circuit switch
        {
            ProviderCircuitState.Open => ProviderStatus.Unavailable,
            ProviderCircuitState.HalfOpen => ProviderStatus.Degraded,
            _ when totalCalls == 0 => ProviderStatus.Unknown,
            _ when consecutiveFailures > 0 => ProviderStatus.Degraded,
            _ => SuccessRate(totalCalls, totalFailures) >= 0.8
                ? ProviderStatus.Available
                : ProviderStatus.Degraded,
        };

        return new ProviderHealth(providerName, true, status, circuit, lastCheck,
            consecutiveFailures, SuccessRate(totalCalls, totalFailures),
            AverageLatency(totalCalls, totalLatencyMs), totalCalls, totalFailures, lastErrorCode);
    }

    private static double SuccessRate(int totalCalls, int totalFailures) =>
        totalCalls == 0 ? 0d : (double)(totalCalls - totalFailures) / totalCalls;

    private static TimeSpan AverageLatency(int totalCalls, double totalLatencyMs) =>
        totalCalls == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(totalLatencyMs / totalCalls);
}
