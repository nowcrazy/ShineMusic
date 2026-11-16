// 职责：Phase 6 验收测试——ProviderHealthTracker 自动熔断恢复。
// 验收标准：
//   - 音源熔断可自动恢复探测（连续失败5次→Open，HalfOpen探测成功→Closed）
//   - 配置禁用不允许健康检查擅自启用（configEnabled=false 时 Snapshot 返回 ConfigDisabled）
//   - 单次成功重置连续失败计数

using Xunit;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;

namespace ShineMusic.Tests.Rules;

public class ProviderHealthMonitorTests
{
    [Fact]
    public void Fresh_provider_is_closed_with_unknown_status()
    {
        var tracker = new ProviderHealthTracker();
        var snap = tracker.Snapshot("netease", configEnabled: true);

        Assert.True(snap.IsEnabled);
        Assert.Equal(ProviderCircuitState.Closed, snap.CircuitState);
        Assert.Equal(ProviderStatus.Unknown, snap.Status);
    }

    // ─────────────────────── 自动熔断（Closed → Open） ───────────────────────

    [Fact]
    public void Five_consecutive_failures_trigger_open()
    {
        // 验收：连续失败5次 → 自动熔断 Closed→Open
        var tracker = new ProviderHealthTracker();

        for (var i = 0; i < 5; i++)
            tracker.RecordFailure("qq", "timeout", TimeSpan.FromSeconds(1));

        var snap = tracker.Snapshot("qq", configEnabled: true);
        Assert.Equal(ProviderCircuitState.Open, snap.CircuitState);
        Assert.Equal(ProviderStatus.Unavailable, snap.Status);
        Assert.Equal(5, snap.ConsecutiveFailures);
    }

    [Fact]
    public void Four_failures_do_not_trigger_open()
    {
        var tracker = new ProviderHealthTracker();

        for (var i = 0; i < 4; i++)
            tracker.RecordFailure("qq", "timeout", TimeSpan.FromSeconds(1));

        var snap = tracker.Snapshot("qq", configEnabled: true);
        Assert.Equal(ProviderCircuitState.Closed, snap.CircuitState);
    }

    [Fact]
    public void Success_resets_consecutive_failures()
    {
        var tracker = new ProviderHealthTracker();

        for (var i = 0; i < 4; i++)
            tracker.RecordFailure("qq", "timeout", TimeSpan.FromSeconds(1));

        tracker.RecordSuccess("qq", TimeSpan.FromSeconds(2));

        var snap = tracker.Snapshot("qq", configEnabled: true);
        Assert.Equal(ProviderCircuitState.Closed, snap.CircuitState);
        Assert.Equal(0, snap.ConsecutiveFailures);
    }

    // ─────────────────────── 自动恢复（HalfOpen → Closed） ───────────────────────

    [Fact]
    public void Half_open_success_restores_closed()
    {
        // 验收：熔断可自动恢复探测——HalfOpen 探测成功 → Closed
        var tracker = new ProviderHealthTracker();

        for (var i = 0; i < 5; i++)
            tracker.RecordFailure("kuwo", "timeout", TimeSpan.FromSeconds(1));

        tracker.TrySetCircuitState("kuwo", ProviderCircuitState.HalfOpen, configEnabled: true);
        tracker.RecordSuccess("kuwo", TimeSpan.FromSeconds(2));

        var snap = tracker.Snapshot("kuwo", configEnabled: true);
        Assert.Equal(ProviderCircuitState.Closed, snap.CircuitState);
        Assert.Equal(0, snap.ConsecutiveFailures);
    }

    [Fact]
    public void Half_open_failure_reopens_circuit()
    {
        var tracker = new ProviderHealthTracker();

        for (var i = 0; i < 5; i++)
            tracker.RecordFailure("kuwo", "timeout", TimeSpan.FromSeconds(1));

        tracker.TrySetCircuitState("kuwo", ProviderCircuitState.HalfOpen, configEnabled: true);
        tracker.RecordFailure("kuwo", "timeout", TimeSpan.FromSeconds(1));

        var snap = tracker.Snapshot("kuwo", configEnabled: true);
        // 探测失败立即回到 Open，重新开始冷却。
        Assert.Equal(ProviderCircuitState.Open, snap.CircuitState);
    }

    // ─────────────────────── 配置禁用是终止态 ───────────────────────

    [Fact]
    public void Config_disabled_snapshot_returns_config_disabled()
    {
        // 验收：配置禁用不允许健康检查擅自启用
        var tracker = new ProviderHealthTracker();

        tracker.RecordSuccess("kugou", TimeSpan.FromSeconds(1), configEnabled: true);

        var snap = tracker.Snapshot("kugou", configEnabled: false);
        Assert.False(snap.IsEnabled);
        Assert.Equal(ProviderCircuitState.ConfigDisabled, snap.CircuitState);
        Assert.Equal(ProviderStatus.Unavailable, snap.Status);
    }

    [Fact]
    public void Config_disabled_ignores_record_success()
    {
        var tracker = new ProviderHealthTracker();

        tracker.RecordSuccess("kugou", TimeSpan.FromSeconds(1), configEnabled: false);

        var snap = tracker.Snapshot("kugou", configEnabled: false);
        Assert.Equal(0, snap.TotalCalls);
    }

    [Fact]
    public void Config_disabled_ignores_record_failure()
    {
        var tracker = new ProviderHealthTracker();

        tracker.RecordFailure("kugou", "timeout", TimeSpan.FromSeconds(1), configEnabled: false);

        var snap = tracker.Snapshot("kugou", configEnabled: false);
        Assert.Equal(0, snap.TotalCalls);
    }

    [Fact]
    public void Config_disabled_rejects_try_set_circuit_state()
    {
        var tracker = new ProviderHealthTracker();

        var ok = tracker.TrySetCircuitState("kugou", ProviderCircuitState.Closed, configEnabled: false);

        Assert.False(ok);
    }

    // ─────────────────────── 统计数据 ───────────────────────

    [Fact]
    public void Total_calls_and_failures_are_tracked()
    {
        var tracker = new ProviderHealthTracker();

        tracker.RecordSuccess("netease", TimeSpan.FromMilliseconds(500));
        tracker.RecordSuccess("netease", TimeSpan.FromMilliseconds(800));
        tracker.RecordFailure("netease", "timeout", TimeSpan.FromSeconds(2));

        var snap = tracker.Snapshot("netease", configEnabled: true);
        Assert.Equal(3, snap.TotalCalls);
        Assert.Equal(1, snap.TotalFailures);
        Assert.Equal(1, snap.ConsecutiveFailures);
    }

    [Fact]
    public void Success_rate_calculated_correctly()
    {
        var tracker = new ProviderHealthTracker();

        tracker.RecordSuccess("netease", TimeSpan.FromMilliseconds(500));
        tracker.RecordSuccess("netease", TimeSpan.FromMilliseconds(800));
        tracker.RecordFailure("netease", "timeout", TimeSpan.FromSeconds(2));
        tracker.RecordFailure("netease", "timeout", TimeSpan.FromSeconds(2));

        var snap = tracker.Snapshot("netease", configEnabled: true);
        Assert.Equal(4, snap.TotalCalls);
        Assert.Equal(2, snap.TotalFailures);
        Assert.Equal(0.5, snap.SuccessRate, 2);
    }

    [Fact]
    public void Reset_clears_state()
    {
        var tracker = new ProviderHealthTracker();

        for (var i = 0; i < 5; i++)
            tracker.RecordFailure("qq", "timeout", TimeSpan.FromSeconds(1));

        tracker.Reset("qq");

        var snap = tracker.Snapshot("qq", configEnabled: true);
        Assert.Equal(0, snap.TotalCalls);
        Assert.Equal(ProviderCircuitState.Closed, snap.CircuitState);
    }
}
