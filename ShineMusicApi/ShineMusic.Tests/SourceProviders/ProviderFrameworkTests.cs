// 职责：V2.0 Phase 1 的 Registry / 健康状态 / DI / 适配层测试。
// 全部离线：不连 DB / MQ / 网络，不依赖真实音源凭据。

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Adapters;
using ShineMusic.Service.SourceProviders.Engine;
using Xunit;

namespace ShineMusic.Tests.SourceProviders;

public sealed class ProviderFrameworkTests
{
    private static IOptions<SourceDownloadOptions> Wrap(SourceDownloadOptions options) =>
        Microsoft.Extensions.Options.Options.Create(options);

    private static DownloadRequest Request(string trackId = "track-1") =>
        new(trackId, AudioQualityLevel.Lossless, Path.GetTempPath(), "v2-test");

    // ─────────────────────── Registry：发现 / 能力索引 / 优先级 ───────────────────────

    [Fact]
    public void Registry_orders_providers_by_configured_priority()
    {
        var options = FrameworkFixtures.Options(configure =>
        {
            configure.ProviderFramework.Providers["alpha"] = new SourceProviderPolicyOptions { Priority = 10 };
            configure.ProviderFramework.Providers["beta"] = new SourceProviderPolicyOptions { Priority = 100 };
        });
        // 注册顺序刻意与优先级相反，证明排序来自配置而不是 DI 顺序。
        var (registry, _, _) = FrameworkFixtures.Build(
            options, new FakeV2Provider("alpha"), new FakeV2Provider("beta"));

        Assert.Equal(new[] { "beta", "alpha" }, registry.List().Select(item => item.ProviderName).ToArray());
    }

    [Fact]
    public void Priority_ties_follow_di_registration_order()
    {
        var (registry, _, _) = FrameworkFixtures.Build(
            FrameworkFixtures.Options(),
            new FakeV2Provider("alpha"), new FakeV2Provider("beta"));

        Assert.Equal(new[] { "alpha", "beta" }, registry.List().Select(item => item.ProviderName).ToArray());
        Assert.Equal(new[] { "alpha", "beta" },
            registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search)
                .Select(item => item.ProviderName).ToArray());
    }

    [Fact]
    public void Resolve_excludes_providers_whose_capability_is_not_declared_dispatchable()
    {
        var downloadOnly = new FakeV2Provider("download-only",
            capabilities: FrameworkFixtures.Capabilities((ProviderCapability.Download320, ProviderStatus.Available)));
        var unknownSearch = new FakeV2Provider("unknown-search",
            capabilities: FrameworkFixtures.Capabilities((ProviderCapability.Search, ProviderStatus.Unknown)));
        var healthy = new FakeV2Provider("healthy");

        var (registry, _, _) = FrameworkFixtures.Build(
            FrameworkFixtures.Options(), downloadOnly, unknownSearch, healthy);

        var resolved = registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search);
        Assert.Single(resolved);
        Assert.Equal("healthy", resolved[0].ProviderName);
    }

    [Fact]
    public void Unknown_health_check_capability_is_never_dispatched()
    {
        var provider = new FakeV2Provider("p", capabilities: FrameworkFixtures.Capabilities(
            (ProviderCapability.Search, ProviderStatus.Available),
            (ProviderCapability.HealthCheck, ProviderStatus.Unknown)));
        var (registry, _, _) = FrameworkFixtures.Build(FrameworkFixtures.Options(), provider);

        Assert.Single(registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search));
        Assert.Empty(registry.Resolve<IMusicSearchProvider>(ProviderCapability.HealthCheck));
    }

    [Fact]
    public void SearchEnabled_and_DownloadEnabled_gates_are_independent()
    {
        var options = FrameworkFixtures.Options(configure =>
            configure.ProviderFramework.Providers["split"] = new SourceProviderPolicyOptions
            {
                SearchEnabled = false,
                DownloadEnabled = true,
            });
        var (registry, _, _) = FrameworkFixtures.Build(options, new FakeV2Provider("split"));

        Assert.Empty(registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search));
        Assert.Single(registry.Resolve<IMusicDownloadProvider>(ProviderCapability.Download320));
    }

    [Fact]
    public void Config_disabled_provider_never_reappears_after_health_success()
    {
        var health = new ProviderHealthTracker();
        health.RecordSuccess("off", TimeSpan.FromMilliseconds(10));
        Assert.True(health.TrySetCircuitState("off", ProviderCircuitState.Closed, true));

        var options = FrameworkFixtures.Options(configure =>
            configure.ProviderFramework.Providers["off"] = new SourceProviderPolicyOptions { Enabled = false });
        var registry = new ProviderRegistry([new FakeV2Provider("off")], Wrap(options), health);

        Assert.Empty(registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search));
        Assert.Null(registry.Find("off"));

        var snapshot = registry.Health("off");
        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsEnabled);
        Assert.Equal(ProviderStatus.Unavailable, snapshot.Status);
        Assert.Equal(ProviderCircuitState.ConfigDisabled, snapshot.CircuitState);
        Assert.False(health.TrySetCircuitState("off", ProviderCircuitState.Closed, false));
        health.RecordSuccess("off", TimeSpan.Zero, configEnabled: false);
        Assert.Equal(ProviderCircuitState.ConfigDisabled, health.Snapshot("off", false).CircuitState);
    }

    [Fact]
    public void Open_circuit_blocks_dispatch_but_keeps_descriptor_visible()
    {
        var health = new ProviderHealthTracker();
        Assert.True(health.TrySetCircuitState("p", ProviderCircuitState.Open, true));
        var registry = new ProviderRegistry([new FakeV2Provider("p")], Wrap(FrameworkFixtures.Options()), health);

        Assert.Single(registry.List());
        Assert.Empty(registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search));
        Assert.Equal(ProviderStatus.Unavailable, registry.Health("p")!.Status);
    }

    // ─────────────────────── 运行时健康：与配置维度分离 ───────────────────────

    [Fact]
    public void Health_tracker_accumulates_success_rate_latency_and_consecutive_failures()
    {
        var tracker = new ProviderHealthTracker();
        Assert.Equal(ProviderStatus.Unknown, tracker.Snapshot("p", true).Status);

        tracker.RecordSuccess("p", TimeSpan.FromMilliseconds(100));
        tracker.RecordSuccess("p", TimeSpan.FromMilliseconds(300));
        tracker.RecordFailure("p", "boom", TimeSpan.FromMilliseconds(200));

        var snapshot = tracker.Snapshot("p", true);
        Assert.Equal(3, snapshot.TotalCalls);
        Assert.Equal(1, snapshot.TotalFailures);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Equal(2d / 3, snapshot.SuccessRate, 3);
        Assert.Equal(200, snapshot.AverageLatency.TotalMilliseconds);
        Assert.Equal(ProviderStatus.Degraded, snapshot.Status);
        Assert.Equal("boom", snapshot.LastErrorCode);
        Assert.NotNull(snapshot.LastCheckTime);

        tracker.RecordSuccess("p", TimeSpan.Zero);
        Assert.Equal(0, tracker.Snapshot("p", true).ConsecutiveFailures);
        tracker.Reset("p");
        Assert.Equal(ProviderStatus.Unknown, tracker.Snapshot("p", true).Status);
    }

    // ─────────────────────── DI：新增音源只改一处 ───────────────────────

    [Fact]
    public void AddMusicProvider_preserves_registration_order_and_shares_singleton()
    {
        var services = new ServiceCollection();
        services.AddMusicProvider<FakeDiProviderA>();
        services.AddMusicProvider<FakeDiProviderB>();
        using var serviceProvider = services.BuildServiceProvider();

        var providers = serviceProvider.GetServices<IMusicProvider>().ToArray();
        Assert.Equal(new[] { "a", "b" }, providers.Select(item => item.ProviderName).ToArray());
        Assert.Same(serviceProvider.GetRequiredService<FakeDiProviderA>(), providers[0]);
        Assert.Same(serviceProvider.GetRequiredService<FakeDiProviderB>(), providers[1]);
    }

    [Fact]
    public void Api_module_registers_all_eleven_adapters_and_framework_core()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Wrap(FrameworkFixtures.Options()));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("netease"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("migu"));
        // Phase 4：新增 5 个国内音源 V2 适配器。
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("kuwo"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("qq"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("kugou"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("qianqian"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("qishui"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("audius"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("jamendo"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("bilibili"));
        services.AddSingleton<IMusicSourceProvider>(new FakeLegacyProvider("ytmusic"));
        services.AddShineMusicProviderFrameworkModule();
        using var serviceProvider = services.BuildServiceProvider();

        var providers = serviceProvider.GetServices<IMusicProvider>().ToArray();
        Assert.Equal(11, providers.Length);
        Assert.IsType<NeteaseProvider>(providers[0]);
        Assert.IsType<MiguProvider>(providers[1]);
        Assert.IsType<KuwoProvider>(providers[2]);
        Assert.IsType<QqProvider>(providers[3]);
        Assert.IsType<KugouProvider>(providers[4]);
        Assert.IsType<QianqianProvider>(providers[5]);
        Assert.IsType<QishuiProvider>(providers[6]);
        Assert.IsType<AudiusProvider>(providers[7]);
        Assert.IsType<JamendoProvider>(providers[8]);
        Assert.IsType<BilibiliProvider>(providers[9]);
        Assert.IsType<YouTubeMusicProvider>(providers[10]);
        Assert.NotNull(serviceProvider.GetRequiredService<ProviderRegistry>());
        Assert.NotNull(serviceProvider.GetRequiredService<ProviderOrchestrator>());
        Assert.Same(serviceProvider.GetRequiredService<ProviderHealthTracker>(),
            serviceProvider.GetRequiredService<ProviderHealthTracker>());
    }

    // ─────────────────────── 适配层：委托老实现、不制造第二条身份链 ───────────────────────

    [Fact]
    public async Task Netease_adapter_maps_search_result_and_reuses_single_track_identity()
    {
        var legacy = new FakeLegacyProvider("netease");
        var options = FrameworkFixtures.Options(configure =>
            configure.ProviderFramework.Search.MaxResultsPerProvider = 7);
        var adapter = new NeteaseProvider(legacy, Wrap(options));

        var results = await adapter.SearchAsync("周杰伦 晴天");
        var track = Assert.Single(results);

        Assert.Equal("netease", track.ProviderName);
        Assert.Equal("track-1", track.ProviderTrackId);
        Assert.Equal("晴天", track.Identity.Title);
        Assert.Equal(new[] { "周杰伦" }, track.Identity.Artists.ToArray());
        Assert.Equal(269000, track.Identity.DurationMs);
        Assert.Equal("叶惠美", track.Identity.AlbumTitle);
        Assert.Equal("https://example.test/song", track.SourcePageUrl);
        Assert.Equal(AudioQualityLevel.High320K, track.DeclaredQualityLevel());
        Assert.Equal(7, legacy.LastSearchLimit);
        Assert.Equal(1, legacy.SearchCallCount);
    }

    [Fact]
    public async Task Netease_adapter_returns_resolved_only_and_never_claims_a_file()
    {
        var legacy = new FakeLegacyProvider("netease")
        {
            Candidates = [FrameworkFixtures.Info()],
        };
        var adapter = new NeteaseProvider(legacy, Wrap(FrameworkFixtures.Options()));

        var result = await adapter.DownloadAsync(Request());

        Assert.True(result.Success);
        Assert.Equal(DownloadStage.Resolved, result.Stage);
        Assert.False(result.HasAudioFile);
        Assert.Null(result.FilePath);
        Assert.Equal("https://example.test/audio.flac", result.DownloadUrl);
        Assert.Equal(AudioFormat.Flac, result.Format);
        Assert.Equal(AudioQualityLevel.Lossless, result.Quality);
    }

    [Fact]
    public async Task Netease_adapter_maps_unavailable_exception_to_structured_failure()
    {
        var legacy = new FakeLegacyProvider("netease")
        {
            ResolveException = new SourceUnavailableException("VIP 曲无可用直链"),
        };
        var adapter = new NeteaseProvider(legacy, Wrap(FrameworkFixtures.Options()));

        var result = await adapter.DownloadAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(NeteaseProvider.ErrorUnavailable, result.ErrorCode);
        Assert.Contains("VIP", result.ErrorMessage);
    }

    [Fact]
    public async Task Migu_adapter_returns_downloaded_file_with_real_size()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[256]);
            var legacy = new FakeLegacyProvider("migu") { DownloadedFilePath = path };
            var adapter = new MiguProvider(legacy, Wrap(FrameworkFixtures.Options()));

            var result = await adapter.DownloadAsync(Request());

            Assert.True(result.Success);
            Assert.Equal(DownloadStage.Downloaded, result.Stage);
            Assert.True(result.HasAudioFile);
            Assert.Equal(path, result.FilePath);
            Assert.Equal(256, result.FileSize);
            Assert.Equal(AudioFormat.Flac, result.Format);
            Assert.Equal(AudioQualityLevel.Lossless, result.Quality);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Migu_adapter_without_downloader_returns_structured_failure()
    {
        var adapter = new MiguProvider(new FakePlainLegacyProvider(), Wrap(FrameworkFixtures.Options()));

        var result = await adapter.DownloadAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(MiguProvider.ErrorDownloaderMissing, result.ErrorCode);
    }

    [Fact]
    public void Adapters_report_config_disabled_state_from_legacy_provider()
    {
        var legacy = new FakeLegacyProvider("netease", enabled: false);
        var adapter = new NeteaseProvider(legacy, Wrap(FrameworkFixtures.Options()));

        Assert.False(adapter.IsConfigEnabled);
        Assert.NotNull(adapter.ConfigDisabledReason);
    }

    [Fact]
    public void Adapters_reject_being_bound_to_the_wrong_platform()
    {
        Assert.Throws<ArgumentException>(() =>
            new NeteaseProvider(new FakeLegacyProvider("migu"), Wrap(FrameworkFixtures.Options())));
        Assert.Throws<ArgumentException>(() =>
            new MiguProvider(new FakeLegacyProvider("netease"), Wrap(FrameworkFixtures.Options())));
    }

    [Fact]
    public void Capability_matrices_are_honest_about_unverified_capabilities()
    {
        var netease = new NeteaseProvider(new FakeLegacyProvider("netease"), Wrap(FrameworkFixtures.Options()));
        var migu = new MiguProvider(new FakeLegacyProvider("migu"), Wrap(FrameworkFixtures.Options()));

        foreach (var provider in new LegacySourceProviderAdapter[] { netease, migu })
        {
            Assert.Equal(ProviderStatus.Degraded,
                provider.DeclaredCapabilities.Single(item => item.Capability == ProviderCapability.DownloadLossless).Status);
            Assert.Equal(ProviderStatus.Unknown,
                provider.DeclaredCapabilities.Single(item => item.Capability == ProviderCapability.HealthCheck).Status);
            Assert.Equal(ProviderStatus.Unknown,
                provider.DeclaredCapabilities.Single(item => item.Capability == ProviderCapability.Preview).Status);
        }
    }

    [Theory]
    [InlineData("audius", false)]
    [InlineData("jamendo", false)]
    [InlineData("bilibili", true)]
    [InlineData("ytmusic", true)]
    public async Task Additional_adapters_delegate_search_metadata_and_the_correct_download_path(string name, bool selfDownload)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[32]);
            var legacy = new FakeLegacyProvider(name)
            {
                SearchResults = [FrameworkFixtures.Result(name)],
                DetailResult = FrameworkFixtures.Result(name),
                DownloadedFilePath = path,
                SingleDownloadInfo = FrameworkFixtures.Info(),
            };
            var adapter = AdditionalAdapter(name, legacy);
            Assert.Equal(name, Assert.Single(await adapter.SearchAsync("query")).ProviderName);
            Assert.Equal(name, (await adapter.GetTrackAsync("track-1"))!.ProviderName);
            Assert.Equal(1, legacy.SearchCallCount);
            foreach (var capability in new[] { ProviderCapability.Search, ProviderCapability.Metadata, ProviderCapability.Resolve })
                Assert.Contains(adapter.DeclaredCapabilities, item => item.Capability == capability && item.IsDispatchable);
            if (selfDownload)
                Assert.DoesNotContain(adapter.DeclaredCapabilities, item =>
                    item.Capability is ProviderCapability.Download320 or ProviderCapability.DownloadLossless);
            var result = await adapter.DownloadAsync(Request());
            Assert.Equal(selfDownload ? DownloadStage.Downloaded : DownloadStage.Resolved, result.Stage);
            Assert.Null(result.Probed);
            Assert.Equal(selfDownload, result.HasAudioFile);
            Assert.Equal(selfDownload ? path : null, result.FilePath);
            Assert.Equal(selfDownload ? 32L : 0L, result.FileSize);
            Assert.Equal(selfDownload ? null : "https://example.test/audio.flac", result.DownloadUrl);
            // 同一个替身同时实现两种下载入口，阶段断言锁定适配器实际选择的路径。
            legacy.ResolveException = new SourceUnavailableException("unavailable");
            var failed = await adapter.DownloadAsync(Request());
            Assert.False(failed.Success);
            Assert.Equal($"{name}_resource_unavailable", failed.ErrorCode);
            Assert.Throws<ArgumentException>(() => AdditionalAdapter(name, new FakeLegacyProvider("wrong")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LegacySourceProviderAdapter AdditionalAdapter(string name, IMusicSourceProvider legacy) => name switch
    {
        "audius" => new AudiusProvider(legacy, Wrap(FrameworkFixtures.Options())),
        "jamendo" => new JamendoProvider(legacy, Wrap(FrameworkFixtures.Options())),
        "bilibili" => new BilibiliProvider(legacy, Wrap(FrameworkFixtures.Options())),
        "ytmusic" => new YouTubeMusicProvider(legacy, Wrap(FrameworkFixtures.Options())),
        _ => throw new ArgumentException("Unknown test provider", nameof(name)),
    };

    [Fact]
    public void Metadata_catalog_covers_all_fields_and_preserves_entry_point_semantics()
    {
        Assert.Equal(11, ProviderMetadataCatalog.ProviderNames.Count());
        foreach (var provider in ProviderMetadataCatalog.ProviderNames)
        {
            var fields = ProviderMetadataCatalog.GetFields(provider);
            Assert.Equal(Enum.GetValues<ProviderMetadataField>(), fields.Select(item => item.Field));
            foreach (var field in fields.Where(item => item.Field is ProviderMetadataField.Quality or ProviderMetadataField.IsLosslessAvailable))
            {
                Assert.Contains("不代表真实", field.Search.Meaning);
                Assert.Contains("不代表真实", field.Detail.Meaning);
            }
        }
        Assert.Empty(ProviderMetadataCatalog.GetFields("unknown"));
        foreach (var field in new[] { ProviderMetadataField.AlbumTitle, ProviderMetadataField.CoverUrl })
        {
            var descriptor = CatalogField("KUGOU", field);
            Assert.True(descriptor.Search.Supported);
            Assert.False(descriptor.Detail.Supported);
        }
        Assert.False(CatalogField("kuwo", ProviderMetadataField.CoverUrl).Search.Supported);
        Assert.True(CatalogField("kuwo", ProviderMetadataField.CoverUrl).Detail.Supported);
        Assert.NotEqual(CatalogField("netease", ProviderMetadataField.Artists).Search.Source,
            CatalogField("netease", ProviderMetadataField.Artists).Detail.Source);
        Assert.Contains("收费描述", CatalogField("netease", ProviderMetadataField.License).Detail.Source);
        Assert.Contains("UP主", CatalogField("bilibili", ProviderMetadataField.Artists).Detail.Source);
        Assert.Contains("视频发布年", CatalogField("bilibili", ProviderMetadataField.ReleaseYear).Search.Source);
        Assert.False(CatalogField("bilibili", ProviderMetadataField.AlbumTitle).Detail.Supported);
        Assert.Contains("uploader", CatalogField("ytmusic", ProviderMetadataField.Artists).Detail.Source);
        Assert.Contains("歌单", CatalogField("audius", ProviderMetadataField.AlbumTitle).Search.Source);
        Assert.Contains("固定", CatalogField("ytmusic", ProviderMetadataField.Quality).Detail.Source);
    }

    private static ProviderMetadataFieldDescriptor CatalogField(string name, ProviderMetadataField field) =>
        ProviderMetadataCatalog.GetFields(name).Single(item => item.Field == field);

    [Fact]
    public void New_provider_layer_defines_no_shadow_identity_matcher()
    {
        // 与 SingleDecisionPointTests 同口径：V2 适配层不得重新定义 TitleMatches/ArtistsMatch/NormalizeTitle。
        var guarded = new[] { "TitleMatches", "ArtistsMatch", "NormalizeTitle" };
        var offenders = typeof(NeteaseProvider).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("ShineMusic.Service.SourceProviders", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => guarded.Contains(method.Name, StringComparer.Ordinal))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }
}
