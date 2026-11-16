// 职责：V2.0 Phase 1 调度骨架的「故障隔离 + 超时预算」测试（提案 §10.1 第一轮验收标准的前两项）。
// 单源失败不影响其他源、单源超时不拖垮整体搜索——这里用真实 Task / 计时器验证，不用 mock 时间。

using System.Diagnostics;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;
using Xunit;

namespace ShineMusic.Tests.SourceProviders;

public sealed class ProviderOrchestratorTests
{
    private static DownloadRequest Request() =>
        new("track-1", AudioQualityLevel.Lossless, Path.GetTempPath(), "v2-test");

    [Fact]
    public async Task One_provider_exception_does_not_affect_the_others()
    {
        var broken = new FakeV2Provider("broken")
        {
            OnSearch = _ => throw new InvalidOperationException("boom"),
        };
        var (_, health, orchestrator) = FrameworkFixtures.Build(
            FrameworkFixtures.Options(),
            new FakeV2Provider("first"), broken, new FakeV2Provider("last"));

        var results = await orchestrator.SearchAsync("周杰伦 晴天");

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { "first", "broken", "last" }, results.Select(item => item.ProviderName).ToArray());
        Assert.Equal(2, results.Count(item => item.Success));
        var failure = results.Single(item => !item.Success);
        Assert.Equal("broken", failure.ProviderName);
        Assert.Equal(ProviderOrchestrator.ErrorProviderFailed, failure.ErrorCode);
        Assert.Equal("boom", failure.ErrorMessage);
        Assert.Equal(ProviderStatus.Degraded, health.Snapshot("broken", true).Status);
        Assert.Equal(1, health.Snapshot("broken", true).TotalFailures);
        Assert.Equal(ProviderStatus.Available, health.Snapshot("first", true).Status);
    }

    [Fact]
    public async Task Uncooperative_provider_timeout_does_not_delay_other_providers()
    {
        // 故意**忽略** CancellationToken：如果调度层依赖 provider 配合，这一条会挂满 3 秒。
        static async Task<IReadOnlyList<MusicTrack>> Hanging(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
            return [FrameworkFixtures.Track("hanging")];
        }

        var hanging = new FakeV2Provider("hanging") { OnSearch = Hanging };
        var (_, health, orchestrator) = FrameworkFixtures.Build(
            FrameworkFixtures.Options(), new FakeV2Provider("fast"), hanging);

        var stopwatch = Stopwatch.StartNew();
        var results = await orchestrator.SearchAsync("晴天");
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2.5),
            $"超时源拖住了整轮搜索：耗时 {stopwatch.ElapsedMilliseconds}ms");
        Assert.True(results.Single(item => item.ProviderName == "fast").Success);
        var timeout = results.Single(item => item.ProviderName == "hanging");
        Assert.False(timeout.Success);
        Assert.Equal(ProviderOrchestrator.ErrorProviderTimeout, timeout.ErrorCode);
        Assert.Equal(ProviderOrchestrator.ErrorProviderTimeout, health.Snapshot("hanging", true).LastErrorCode);
    }

    [Fact]
    public async Task Total_budget_caps_slow_providers_and_keeps_completed_results()
    {
        var options = FrameworkFixtures.Options(configure =>
        {
            configure.ProviderFramework.Search.TotalBudgetSeconds = 1;
            configure.ProviderFramework.Search.ProviderTimeoutSeconds = 5;
        });
        var slow = new FakeV2Provider("slow")
        {
            OnSearch = async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                return [FrameworkFixtures.Track("slow")];
            },
        };
        var (_, _, orchestrator) = FrameworkFixtures.Build(
            options, new FakeV2Provider("fast"), slow);

        var stopwatch = Stopwatch.StartNew();
        var results = await orchestrator.SearchAsync("晴天");
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2.5),
            $"总预算未封顶：耗时 {stopwatch.ElapsedMilliseconds}ms");
        Assert.True(results.Single(item => item.ProviderName == "fast").Success);
        Assert.Equal(ProviderOrchestrator.ErrorBudgetExhausted,
            results.Single(item => item.ProviderName == "slow").ErrorCode);
    }

    [Fact]
    public async Task MaxConcurrentProviders_limits_parallelism()
    {
        var options = FrameworkFixtures.Options(configure =>
            configure.ProviderFramework.Search.MaxConcurrentProviders = 2);
        var sync = new object();
        var current = 0;
        var peak = 0;
        var providers = Enumerable.Range(0, 4).Select(index => new FakeV2Provider($"p{index}")
        {
            OnSearch = async token =>
            {
                lock (sync)
                {
                    current++;
                    peak = Math.Max(peak, current);
                }
                await Task.Delay(60, token);
                lock (sync)
                {
                    current--;
                }
                return new[] { FrameworkFixtures.Track($"p{index}") };
            },
        }).ToArray();
        var (_, _, orchestrator) = FrameworkFixtures.Build(options, providers.Cast<IMusicProvider>().ToArray());

        var results = await orchestrator.SearchAsync("晴天");

        Assert.Equal(4, results.Count);
        Assert.All(results, item => Assert.True(item.Success));
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Config_disabled_provider_is_never_invoked()
    {
        var disabled = new FakeV2Provider("off", configEnabled: false)
        {
            OnSearch = _ => throw new InvalidOperationException("must not be called"),
        };
        var (_, _, orchestrator) = FrameworkFixtures.Build(
            FrameworkFixtures.Options(), disabled, new FakeV2Provider("on"));

        var results = await orchestrator.SearchAsync("晴天");

        Assert.Single(results);
        Assert.Equal("on", results[0].ProviderName);
        Assert.True(results[0].Success);
    }

    [Fact]
    public async Task InvokeAsync_returns_structured_failure_for_unknown_or_disabled_provider()
    {
        var (_, _, orchestrator) = FrameworkFixtures.Build(
            FrameworkFixtures.Options(), new FakeV2Provider("on"));

        var result = await orchestrator.InvokeAsync<IMusicSearchProvider, IReadOnlyList<MusicTrack>>(
            "missing", (provider, token) => provider.SearchAsync("晴天", token));

        Assert.False(result.Success);
        Assert.Equal(ProviderOrchestrator.ErrorNotAvailable, result.ErrorCode);
    }

    [Fact]
    public async Task Null_provider_result_is_converged_instead_of_reported_as_success()
    {
        var provider = new FakeV2Provider("p")
        {
            OnSearch = _ => Task.FromResult<IReadOnlyList<MusicTrack>>(null!),
        };
        var (_, health, orchestrator) = FrameworkFixtures.Build(FrameworkFixtures.Options(), provider);

        var result = await orchestrator.InvokeAsync<IMusicSearchProvider, IReadOnlyList<MusicTrack>>(
            "p", (instance, token) => instance.SearchAsync("晴天", token));

        Assert.False(result.Success);
        Assert.Equal(ProviderOrchestrator.ErrorEmptyResult, result.ErrorCode);
        Assert.Equal(1, health.Snapshot("p", true).TotalFailures);
    }

    [Fact]
    public async Task DownloadAsync_separates_call_success_from_business_failure()
    {
        var provider = new FakeV2Provider("p")
        {
            OnDownload = _ => Task.FromResult(
                DownloadResult.Failed("p", "track-1", "quality_not_met", "128K 不允许自动入库")),
        };
        var (_, health, orchestrator) = FrameworkFixtures.Build(FrameworkFixtures.Options(), provider);

        var result = await orchestrator.DownloadAsync("p", Request());

        Assert.True(result.Success, "调用本身成功（异常已收敛），不应伪装成调用失败");
        Assert.NotNull(result.Value);
        Assert.False(result.Value.Success, "平台明确回报资源不可用");
        Assert.Equal("quality_not_met", result.Value.ErrorCode);
        Assert.Equal(1, health.Snapshot("p", true).TotalFailures);
    }

    [Fact]
    public async Task External_cancellation_is_not_counted_as_a_provider_failure()
    {
        var provider = new FakeV2Provider("p")
        {
            OnSearch = async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                return [FrameworkFixtures.Track("p")];
            },
        };
        var (_, health, orchestrator) = FrameworkFixtures.Build(FrameworkFixtures.Options(), provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await orchestrator.InvokeAsync<IMusicSearchProvider, IReadOnlyList<MusicTrack>>(
            "p", (instance, token) => instance.SearchAsync("晴天", token),
            cancellationToken: cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(ProviderOrchestrator.ErrorCancelled, result.ErrorCode);
        Assert.Equal(0, health.Snapshot("p", true).TotalFailures);
    }
}
