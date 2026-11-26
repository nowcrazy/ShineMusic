using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

/// <summary>
/// 音源配置的「安全默认值」回归锁（2026-11-12 逐源实测结论）。
/// 这些断言防的不是配置写错，而是「配置节缺失 / 新环境未覆盖」时静默回退到低质量链路：
/// 只有 netease / migu 默认启用，Migu 默认 HQ，VIP 回退保持 320 优先，自动发布阈值必须高于低质量文件的评分上限。
/// </summary>
public sealed class SourceDownloadSafeDefaultsTests
{
    private static SourceDownloadOptions Defaults() => new();

    [Fact]
    public void Only_the_two_verified_high_quality_sources_are_enabled_by_default()
    {
        var options = Defaults();

        Assert.True(options.Netease.Enabled, "netease 实测可交付 320k / gdstudio FLAC");
        Assert.True(options.Migu.Enabled, "migu 实测可交付正版 HQ 320k / SQ FLAC");

        Assert.False(options.Audius.Enabled);
        Assert.False(options.Jamendo.Enabled);
        Assert.False(options.Kuwo.Enabled);
        Assert.False(options.Kugou.Enabled);
        Assert.False(options.Qishui.Enabled);
        Assert.False(options.Qq.Enabled);
        Assert.False(options.Qianqian.Enabled);
        Assert.False(options.Bilibili.Enabled);
        Assert.False(options.YtDlp.Enabled);
    }

    [Fact]
    public void Migu_defaults_to_320_tier()
    {
        Assert.Equal("HQ", Defaults().Migu.ToneFlag);
    }

    [Fact]
    public void Vip_fallback_default_chain_contains_only_the_verified_endpoint()
    {
        var endpoints = Defaults().Netease.VipFallback.Endpoints;

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(VipFallbackEndpointTypes.GdStudio, endpoint.Type);
        Assert.Equal(["320", "999"], endpoint.Levels);
        Assert.DoesNotContain(endpoints, e => e.Type == VipFallbackEndpointTypes.HaiTangW);
        Assert.DoesNotContain(endpoints, e => e.Type == VipFallbackEndpointTypes.NextMusic);
        Assert.Equal(VipFallbackEndpointTypes.GdStudio, new NeteaseVipFallbackEndpoint().Type);
    }

    [Fact]
    public void Auto_publish_threshold_stays_above_the_low_quality_score_ceiling()
    {
        Assert.True(Defaults().AutoPublishThreshold > SourceScoreCalculator.LowQualityAutoScoreCeiling,
            "阈值一旦降到低质量评分上限以下，128k 音频就能跳过人工审核自动发布");
    }

    [Fact]
    public void Configured_resolvers_and_levels_replace_the_initialized_defaults()
    {
        using var provider = BuildOptionsProvider(new Dictionary<string, string?>
        {
            ["SourceDownload:Netease:VipFallback:Endpoints:0:Id"] = "custom",
            ["SourceDownload:Netease:VipFallback:Endpoints:0:Url"] = "https://resolver.example/audio",
            ["SourceDownload:Netease:VipFallback:Endpoints:0:Levels:0"] = "256",
            ["SourceDownload:Netease:VipFallback:Endpoints:0:Enabled"] = "false"
        });

        var endpoint = Assert.Single(provider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value.Netease.VipFallback.Endpoints);
        Assert.Equal("custom", endpoint.Id);
        Assert.False(endpoint.Enabled);
        Assert.Equal(["256"], endpoint.Levels);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Explicitly_empty_resolver_list_does_not_restore_the_default_service(string value)
    {
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"SourceDownload\":{\"Netease\":{\"VipFallback\":{\"Endpoints\":" + value + "}}}}"));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var options = configuration.GetSection("SourceDownload").Get<SourceDownloadOptions>()!;

        SourceDownloadOptionsBinding.ReplaceConfiguredNeteaseEndpoints(configuration, options);

        Assert.Empty(options.Netease.VipFallback.Endpoints);
    }

    [Fact]
    public void A_128k_file_with_perfect_metadata_still_cannot_auto_publish()
    {
        // 把元数据、封面、歌词全部拉满，只把码率压到 128k：
        // 这是低质量文件能拿到的最高分，必须仍低于默认自动发布阈值。
        var item = new LibraryImportItem
        {
            DisplayName = "test.mp3",
            SourceStorageKey = "sourcedl/test.mp3",
            ContentHash = "hash",
            Title = "晴天",
            ArtistsJson = "[\"周杰伦\"]",
            AlbumTitle = "叶惠美",
            GenresJson = "[\"流行\"]",
            DurationMs = 269000,
            ArtworkStorageKey = "artwork/cover.jpg",
            LyricsConfidence = 1.0,
            Container = "mp3",
            Codec = "mp3",
            BitrateKbps = 128
        };

        var result = SourceScoreCalculator.Calculate(item);

        Assert.Equal(0, result.Quality.Score);
        Assert.True(result.Total <= SourceScoreCalculator.LowQualityAutoScoreCeiling);
        Assert.True(result.Total < Defaults().AutoPublishThreshold);
    }

    [Fact]
    public void Startup_validation_accepts_the_safe_defaults_when_config_section_is_missing()
    {
        // 模拟「新部署漏配 SourceDownload 整节」：代码默认值必须仍能通过启动校验并处于安全状态。
        using var provider = BuildOptionsProvider(new Dictionary<string, string?>());
        var options = provider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value;

        Assert.True(options.Migu.Enabled);
        Assert.True(options.Netease.Enabled);
        Assert.Equal("HQ", options.Migu.ToneFlag);
        Assert.False(options.Qq.Enabled);
    }

    [Theory]
    [InlineData("SourceDownload:AutoPublishThreshold", "60")]
    [InlineData("SourceDownload:Migu:ToneFlag", "FLAC")]
    // 权重合计必须为 100：改一项不补另一项 → 启动期拒绝。
    [InlineData("SourceDownload:ScoreWeights:Identity", "41")]
    [InlineData("SourceDownload:ScoreWeights:Lyrics", "-1")]
    public void Startup_validation_rejects_a_config_that_would_silently_reopen_low_quality(string key, string value)
    {
        using var provider = BuildOptionsProvider(new Dictionary<string, string?> { [key] = value });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value);
    }

    [Fact]
    public void Appsettings_and_code_defaults_agree_on_quality_policy()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShineMusic.Api", "appsettings.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory!.FullName, "ShineMusic.Api", "appsettings.json"))
            .Build();
        var configured = configuration.GetSection("SourceDownload").Get<SourceDownloadOptions>()!;
        var policy = configured.ProviderFramework.QualityPolicy;
        Assert.Equal("320K", policy.PreferredQuality);
        Assert.Equal("192K", policy.MinimumQuality);
        Assert.True(policy.AllowLossyFallback);
        Assert.False(policy.AllowPreview);
        Assert.True(policy.RequireFullTrack);
        Assert.Equal(Defaults().ProviderFramework.QualityPolicy.MinimumQuality, policy.MinimumQuality);
        Assert.Equal(Defaults().Migu.ToneFlag, configured.Migu.ToneFlag);
        Assert.Equal(Defaults().Netease.VipFallback.Endpoints[0].Levels, configured.Netease.VipFallback.Endpoints[0].Levels);
    }

    private static ServiceProvider BuildOptionsProvider(IDictionary<string, string?> overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(overrides)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // BindConfiguration 在解析 IOptions 时会向容器索取 IConfiguration，测试容器需显式注册。
        services.AddSingleton<IConfiguration>(configuration);
        services.AddShineMusicOptions(configuration, new TestHostEnvironment());
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "ShineMusic.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
