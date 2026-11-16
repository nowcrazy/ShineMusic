// 职责：V2.0 Phase 2「四处搜索统一收口」的行为与结构断言。
//
// 背景：#32 是首次改生产搜索行为。只跑既有 241 个测试即便全绿也证明不了
// 「四处真的走了聚合器」——B 轴老问题（全绿但 bug 活着）。因此这里同时锁两类事实：
//   1) 行为：源清单只来自 V2 Registry、过滤 / 截断 / 失败收敛 / 字段往返不丢；
//   2) 结构：四处调用点的构造函数（或端点参数）必须依赖 ISearchAggregator。
// 全部离线，不连库、不启动 API、不发网络请求。

using Microsoft.Extensions.DependencyInjection;
using ShineMusic.Api.Controllers;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;
using Xunit;

namespace ShineMusic.Tests.SourceProviders;

public sealed class SearchAggregatorTests
{
    private static SearchAggregator BuildAggregator(
        out ProviderRegistry registry,
        out FakeLegacyProvider[] legacyProviders,
        params IMusicProvider[] v2Providers)
    {
        var health = new ProviderHealthTracker();
        var options = Microsoft.Extensions.Options.Options.Create(FrameworkFixtures.Options());
        registry = new ProviderRegistry(v2Providers, options, health);
        var orchestrator = new ProviderOrchestrator(
            registry, health, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderOrchestrator>.Instance);
        legacyProviders =
        [
            new FakeLegacyProvider("netease"),
            new FakeLegacyProvider("migu"),
            new FakeLegacyProvider("qishui"),
        ];
        return new SearchAggregator(orchestrator, registry, legacyProviders);
    }

    [Fact]
    public void MusicTrack_round_trip_preserves_genres_release_year_and_album()
    {
        var source = new SourceSearchResult(
            "netease", "id-7", "晴天", ["周杰伦"], "叶惠美", 269000,
            ["流行", "华语"], "https://example.test/cover", "https://example.test/song",
            "CC-BY", SourceQualities.High, false, 2003);

        var roundTripped = MusicTrack.From(source).ToLegacy();

        Assert.Equal(source, roundTripped);
        Assert.Equal(new[] { "流行", "华语" }, roundTripped.Genres);
        Assert.Equal(2003, roundTripped.ReleaseYear);
        Assert.Equal("叶惠美", roundTripped.AlbumTitle);
    }

    [Fact]
    public async Task Source_list_comes_only_from_v2_registry()
    {
        var aggregator = BuildAggregator(out _, out _, 
            new FakeV2Provider("netease"),
            new FakeV2Provider("migu"),
            new FakeV2Provider("kugou", configEnabled: false));

        var result = await aggregator.SearchAsync("周杰伦 晴天");

        Assert.Equal(new[] { "netease", "migu" }, result.Outcomes.Select(item => item.ProviderName).ToArray());
        Assert.DoesNotContain("qishui", result.Outcomes.Select(item => item.ProviderName));
        Assert.Equal(2, result.AllResults.Count);
    }

    [Fact]
    public async Task Provider_filter_prevents_requests_to_other_sources()
    {
        var netease = new FakeV2Provider("netease");
        var migu = new FakeV2Provider("migu");
        var aggregator = BuildAggregator(out _, out _, netease, migu);

        var result = await aggregator.SearchAsync("晴天", ["netease"], 10);

        Assert.Single(result.Outcomes);
        Assert.Equal("netease", result.Outcomes[0].ProviderName);
    }

    [Fact]
    public async Task Max_results_per_provider_is_applied_after_conversion()
    {
        var many = new FakeV2Provider("netease")
        {
            OnSearch = _ => Task.FromResult<IReadOnlyList<MusicTrack>>(
                Enumerable.Range(0, 7)
                    .Select(index => FrameworkFixtures.Track("netease") with { ProviderTrackId = $"id-{index}" })
                    .ToArray()),
        };
        var aggregator = BuildAggregator(out _, out _, many);

        var result = await aggregator.SearchAsync("晴天", maxResultsPerProvider: 3);

        Assert.Equal(3, result.AllResults.Count);
        Assert.Equal(new[] { "id-0", "id-1", "id-2" },
            result.AllResults.Select(item => item.ProviderTrackId).ToArray());
    }

    [Fact]
    public async Task Failed_provider_becomes_structured_outcome_and_keeps_other_results()
    {
        var broken = new FakeV2Provider("netease")
        {
            OnSearch = _ => throw new InvalidOperationException("boom"),
        };
        var aggregator = BuildAggregator(out _, out var legacy, broken, new FakeV2Provider("migu"));

        var result = await aggregator.SearchAsync("晴天");

        Assert.Equal(2, result.Outcomes.Count);
        var failure = result.Outcomes.Single(item => item.ProviderName == "netease");
        Assert.Equal(ProviderOrchestrator.ErrorProviderFailed, failure.ErrorCode);
        Assert.Empty(failure.Results);
        Assert.Equal("netease", failure.Legacy?.Provider);
        Assert.Single(result.AllResults);
        Assert.Equal("migu", result.AllResults[0].Provider);
        Assert.Equal(0, legacy.Single(item => item.Provider == "qishui").SearchCallCount);
    }

    [Fact]
    public void Legacy_provider_lookup_is_case_insensitive()
    {
        var aggregator = BuildAggregator(out _, out _, new FakeV2Provider("netease"));

        Assert.Equal("netease", aggregator.FindLegacyProvider("NETEASE")?.Provider);
        Assert.Null(aggregator.FindLegacyProvider("unknown"));
    }

    [Fact]
    public void Parity_reports_enabled_legacy_source_without_v2_registration()
    {
        var aggregator = BuildAggregator(out var registry, out var legacy,
            new FakeV2Provider("netease"), new FakeV2Provider("migu"));

        var missing = SearchProviderParity.FindUnregistered(legacy, registry.List(), frameworkEnabled: true);

        Assert.Equal(new[] { "qishui" }, missing);
        Assert.Contains(aggregator.Providers, descriptor =>
            string.Equals(descriptor.ProviderName, "NETEASE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parity_ignores_disabled_legacy_sources()
    {
        BuildAggregator(out var registry, out var legacy,
            new FakeV2Provider("netease"), new FakeV2Provider("migu"));
        legacy.Single(item => item.Provider == "qishui").IsEnabled = false;

        Assert.Empty(SearchProviderParity.FindUnregistered(legacy, registry.List(), frameworkEnabled: true));
    }

    [Fact]
    public void Parity_treats_disabled_framework_as_no_registration()
    {
        BuildAggregator(out var registry, out var legacy,
            new FakeV2Provider("netease"), new FakeV2Provider("migu"));

        var missing = SearchProviderParity.FindUnregistered(legacy, registry.List(), frameworkEnabled: false);

        Assert.Equal(new[] { "netease", "migu", "qishui" }, missing);
    }

    [Fact]
    public void Parity_exempts_ytmusic_which_is_excluded_from_all_search_paths()
    {
        BuildAggregator(out var registry, out var legacy, new FakeV2Provider("netease"));
        var withYtmusic = legacy.Append(new FakeLegacyProvider("ytmusic")).ToArray();

        var missing = SearchProviderParity.FindUnregistered(withYtmusic, registry.List(), frameworkEnabled: true);

        Assert.DoesNotContain("ytmusic", missing);
        Assert.Contains("migu", missing);
    }

    [Fact]
    public void Parity_validate_or_throw_fails_fast_with_source_names()
    {
        BuildAggregator(out var registry, out var legacy, new FakeV2Provider("netease"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            SearchProviderParity.ValidateOrThrow(legacy, registry.List(), frameworkEnabled: true));

        Assert.Contains("migu", error.Message);
        Assert.Contains("qishui", error.Message);
        Assert.Contains("V2", error.Message);
    }

    [Fact]
    public void Parity_allows_registered_metadata_only_provider()
    {
        var options = FrameworkFixtures.Options(value => value.ProviderFramework.Providers["netease"] = new()
        {
            MetadataEnabled = true, SearchEnabled = false, DownloadEnabled = false
        });
        var (registry, _, _) = FrameworkFixtures.Build(options, new FakeV2Provider("netease"));
        Assert.Empty(SearchProviderParity.FindUnregistered([new FakeLegacyProvider("netease")], registry.List(), true));
    }

    [Theory]
    [InlineData(typeof(PlaylistImportService))]
    [InlineData(typeof(SourceDownloadJob))]
    [InlineData(typeof(SourceDownloadFallbackService))]
    public void Production_search_call_sites_depend_on_the_aggregator(Type type)
    {
        var parameters = type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).ToArray();

        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(ISearchAggregator));
    }

    [Fact]
    public void Admin_search_endpoint_uses_the_aggregator_instead_of_legacy_provider_collection()
    {
        var method = typeof(SourceDownloadController).GetMethod("Search");
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(ISearchAggregator));
        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType == typeof(IEnumerable<IMusicSourceProvider>));
    }

    [Fact]
    public void Aggregator_is_registered_in_the_framework_di_module()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEnumerable<IMusicSourceProvider>>(Array.Empty<IMusicSourceProvider>());
        services.AddSingleton(ProviderFrameworkOptionsProvider());
        services.AddShineMusicProviderFrameworkCore();
        services.AddMusicProvider<FakeDiProviderA>();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ISearchAggregator>());
    }

    private static Microsoft.Extensions.Options.IOptions<ShineMusic.Model.Options.SourceDownloadOptions>
        ProviderFrameworkOptionsProvider() =>
        Microsoft.Extensions.Options.Options.Create(FrameworkFixtures.Options());
}
