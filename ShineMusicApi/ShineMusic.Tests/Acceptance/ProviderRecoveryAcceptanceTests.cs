using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;
using ShineMusic.Tests.SourceProviders;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Trait("Category", "SourceDownloadAcceptance")]
public sealed class ProviderRecoveryAcceptanceTests
{
    private static DownloadRequest Request() =>
        new("track-1", AudioQualityLevel.Lossless, Path.GetTempPath(), "recovery-acceptance");

    private sealed class Rig : IDisposable
    {
        public readonly ManualClock Clock = new();
        public readonly ProviderHealthTracker Health;
        public readonly ProviderRegistry Registry;
        public readonly ProviderOrchestrator Orchestrator;
        public readonly ProviderHealthMonitor Monitor;

        public Rig(SourceDownloadOptions options, params IMusicProvider[] providers)
        {
            Health = new ProviderHealthTracker(Clock);
            var wrapped = Options.Create(options);
            Registry = new ProviderRegistry(providers, wrapped, Health);
            Orchestrator = new ProviderOrchestrator(Registry, Health, wrapped,
                NullLogger<ProviderOrchestrator>.Instance);
            Monitor = new ProviderHealthMonitor(Health, Registry, wrapped,
                NullLogger<ProviderHealthMonitor>.Instance);
        }

        public ProviderHealth Snapshot(string name = "p") => Registry.Health(name)!;
        public Task Poll() => Monitor.CheckAllProvidersAsync(CancellationToken.None);
        public void Dispose() => Monitor.Dispose();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(2)]
    [InlineData(7)]
    public async Task Consecutive_business_failures_open_at_default_or_custom_threshold_and_block_requests(int threshold)
    {
        var calls = 0;
        var provider = new FakeV2Provider("p")
        {
            OnDownload = _ =>
            {
                calls++;
                return Task.FromResult(DownloadResult.Failed("p", "track-1", "upstream_failed", "retry"));
            },
        };
        var options = FrameworkFixtures.Options();
        if (threshold != 5)
            options.ProviderFramework.HealthCheck.FailureThreshold = threshold;
        using var rig = new Rig(options, provider);
        for (var i = 1; i <= threshold; i++)
        {
            var result = await rig.Orchestrator.DownloadAsync("p", Request());
            Assert.True(result.Success);
            Assert.False(result.Value!.Success);
            Assert.Equal(i, rig.Snapshot().ConsecutiveFailures);
            Assert.Equal(i, rig.Snapshot().TotalCalls);
            Assert.Equal(i, rig.Snapshot().TotalFailures);
            Assert.Equal(i == threshold ? ProviderCircuitState.Open : ProviderCircuitState.Closed,
                rig.Snapshot().CircuitState);
        }
        Assert.Null(rig.Registry.Find("p"));
        Assert.Empty(rig.Registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search));
        Assert.Equal(ProviderOrchestrator.ErrorNotAvailable,
            (await rig.Orchestrator.DownloadAsync("p", Request())).ErrorCode);
        Assert.Equal(threshold, calls);
    }

    [Theory]
    [InlineData("auth_failed")]
    [InlineData("authentication_required")]
    [InlineData("no_license")]
    [InlineData("no_copyright")]
    [InlineData("track_not_found")]
    [InlineData("resource_not_found")]
    [InlineData("source_duplicate")]
    [InlineData("AUTH_FAILED")]
    public async Task Non_retriable_business_errors_neither_trip_nor_reset_existing_failures(string error)
    {
        var provider = new FakeV2Provider("p")
        {
            OnDownload = _ => Task.FromResult(DownloadResult.Failed("p", "track-1", error, "business rejection")),
        };
        using var rig = new Rig(FrameworkFixtures.Options(), provider);
        rig.Health.RecordFailure("p", "timeout", TimeSpan.Zero);
        for (var i = 0; i < 8; i++)
        {
            var result = await rig.Orchestrator.DownloadAsync("p", Request());
            Assert.True(result.Success);
            Assert.False(result.Value!.Success);
            await rig.Poll();
        }
        Assert.Equal(ProviderCircuitState.Closed, rig.Snapshot().CircuitState);
        Assert.Equal(1, rig.Snapshot().ConsecutiveFailures);
        Assert.Equal(1, rig.Snapshot().TotalFailures);
        Assert.NotNull(rig.Registry.Find("p"));
    }

    [Fact]
    public async Task Monitor_and_dispatch_recover_then_escalate_cooldown_with_a_cap()
    {
        var options = FrameworkFixtures.Options(o =>
        {
            o.ProviderFramework.HealthCheck.FailureThreshold = 1;
            o.ProviderFramework.HealthCheck.RecoverySeconds = 10;
            o.ProviderFramework.HealthCheck.MaxRecoverySeconds = 25;
        });
        var provider = new FakeV2Provider("p")
        {
            OnDownload = _ => Task.FromResult(DownloadResult.Failed("p", "track-1", "timeout", "retry")),
        };
        using var rig = new Rig(options, provider);
        await rig.Orchestrator.DownloadAsync("p", Request());
        foreach (var cooldown in new[] { 10, 20, 25, 25 })
        {
            Assert.Equal(ProviderCircuitState.Open, rig.Snapshot().CircuitState);
            rig.Clock.Advance(TimeSpan.FromSeconds(cooldown - 1));
            await rig.Poll();
            Assert.Equal(ProviderCircuitState.Open, rig.Snapshot().CircuitState);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            await rig.Poll();
            Assert.Equal(ProviderCircuitState.HalfOpen, rig.Snapshot().CircuitState);
            Assert.NotNull(rig.Registry.Find("p"));
            await rig.Orchestrator.DownloadAsync("p", Request());
            Assert.Equal(ProviderCircuitState.Open, rig.Snapshot().CircuitState);
        }
        rig.Clock.Advance(TimeSpan.FromSeconds(25));
        await rig.Poll();
        provider.OnDownload = null;
        Assert.True((await rig.Orchestrator.DownloadAsync("p", Request())).Value!.Success);
        Assert.Equal(ProviderCircuitState.Closed, rig.Snapshot().CircuitState);
        Assert.Equal(0, rig.Snapshot().ConsecutiveFailures);
        // Successful recovery also resets the next cooldown to the base duration.
        provider.OnDownload = _ => throw new InvalidOperationException("down again");
        await rig.Orchestrator.DownloadAsync("p", Request());
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.Poll();
        Assert.Equal(ProviderCircuitState.HalfOpen, rig.Snapshot().CircuitState);
    }

    [Fact]
    public async Task Hosted_monitor_timer_uses_tracker_clock_and_never_enables_disabled_sources()
    {
        var options = FrameworkFixtures.Options(o =>
        {
            o.ProviderFramework.HealthCheck.Enabled = true;
            o.ProviderFramework.HealthCheck.IntervalMinutes = 1;
            o.ProviderFramework.HealthCheck.RecoverySeconds = 60;
        });
        var disabledCalls = 0;
        var disabled = new FakeV2Provider("off", configEnabled: false)
        {
            OnSearch = _ => { disabledCalls++; throw new InvalidOperationException("disabled"); },
        };
        using var rig = new Rig(options, new FakeV2Provider("p"), disabled);
        for (var i = 0; i < 5; i++)
        {
            rig.Health.RecordFailure("p", "timeout", TimeSpan.Zero);
            rig.Health.RecordFailure("off", "timeout", TimeSpan.Zero);
        }
        await rig.Monitor.StartAsync(CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(ProviderCircuitState.Open, rig.Snapshot().CircuitState);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ProviderCircuitState.HalfOpen, rig.Snapshot().CircuitState);
        Assert.Equal(ProviderCircuitState.ConfigDisabled, rig.Snapshot("off").CircuitState);
        Assert.Null(rig.Registry.Find("off"));
        var results = await rig.Orchestrator.SearchAsync("晴天");
        Assert.Single(results);
        Assert.True(results[0].Success);
        Assert.Equal(ProviderCircuitState.Closed, rig.Snapshot().CircuitState);
        Assert.Equal(0, disabledCalls);
        await rig.Monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Faults_and_uncooperative_timeout_are_isolated_from_healthy_sources()
    {
        var never = new TaskCompletionSource<IReadOnlyList<MusicTrack>>();
        using var rig = new Rig(FrameworkFixtures.Options(), new FakeV2Provider("fast"),
            new FakeV2Provider("broken") { OnSearch = _ => throw new InvalidOperationException("boom") },
            new FakeV2Provider("hung") { OnSearch = _ => never.Task });
        var watch = Stopwatch.StartNew();
        var results = await rig.Orchestrator.SearchAsync("晴天").WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(results.Single(r => r.ProviderName == "fast").Success);
        Assert.Equal(ProviderOrchestrator.ErrorProviderFailed, results.Single(r => r.ProviderName == "broken").ErrorCode);
        Assert.Equal(ProviderOrchestrator.ErrorProviderTimeout, results.Single(r => r.ProviderName == "hung").ErrorCode);
        Assert.Equal(1, rig.Snapshot("broken").TotalFailures);
        Assert.Equal(1, rig.Snapshot("hung").TotalFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Budget_or_external_cancellation_preserves_completed_results_without_penalizing_sources(bool external)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<IReadOnlyList<MusicTrack>>();
        var queuedCalls = 0;
        var options = FrameworkFixtures.Options(o =>
        {
            o.ProviderFramework.Search.TotalBudgetSeconds = 1;
            o.ProviderFramework.Search.ProviderTimeoutSeconds = 10;
            o.ProviderFramework.Search.MaxConcurrentProviders = 1;
        });
        using var rig = new Rig(options, new FakeV2Provider("fast"),
            new FakeV2Provider("hung") { OnSearch = _ => { entered.SetResult(); return never.Task; } },
            new FakeV2Provider("queued") { OnSearch = _ => { queuedCalls++; return never.Task; } });
        using var cancel = new CancellationTokenSource();
        var search = rig.Orchestrator.SearchAsync("晴天", cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (external)
            cancel.Cancel();
        var results = await search.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(results.Single(r => r.ProviderName == "fast").Success);
        Assert.All(results.Where(r => r.ProviderName != "fast"), r =>
            Assert.Equal(external ? ProviderOrchestrator.ErrorCancelled : ProviderOrchestrator.ErrorBudgetExhausted, r.ErrorCode));
        Assert.Equal(0, queuedCalls);
        Assert.Equal(0, rig.Snapshot("hung").TotalFailures);
        Assert.Equal(0, rig.Snapshot("queued").TotalCalls);
    }

    // Deliberately small deterministic clock: no package change and no real cooldown waits.
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray())
                timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                _period = period;
                return true;
            }
            public void Fire()
            {
                if (_disposed || _due is not { } due || due > clock._now) return;
                _due = _period > TimeSpan.Zero ? clock._now + _period : null;
                callback(state);
            }
            public void Dispose() { _disposed = true; _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
