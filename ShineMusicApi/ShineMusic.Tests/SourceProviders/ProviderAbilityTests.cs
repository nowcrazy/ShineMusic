using Microsoft.Extensions.Logging.Abstractions;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;
using Xunit;

namespace ShineMusic.Tests.SourceProviders;

public sealed class ProviderAbilityTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Three_ability_switches_are_independent(bool metadata, bool search, bool download)
    {
        var options = FrameworkFixtures.Options(value => value.ProviderFramework.Providers["p"] = new()
        {
            MetadataEnabled = metadata, SearchEnabled = search, DownloadEnabled = download
        });
        var (registry, _, orchestrator) = FrameworkFixtures.Build(options, new FakeV2Provider("p"));
        Assert.Equal(metadata, registry.Resolve<IMusicMetadataProvider>(ProviderCapability.Metadata).Count == 1);
        Assert.Equal(search, registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search).Count == 1);
        Assert.Equal(download, registry.Resolve<IMusicDownloadProvider>(ProviderCapability.Resolve).Count == 1);
        var detail = await orchestrator.InvokeAsync<IMusicMetadataProvider, MusicTrack>("p",
            async (provider, token) => (await provider.GetTrackAsync("track-1", token))!);
        Assert.Equal(metadata, detail.Success);
        var resource = await orchestrator.DownloadAsync("p", new("track-1", AudioQualityLevel.Lossless, Path.GetTempPath(), "test"));
        Assert.Equal(download, resource.Success);
    }

    [Fact]
    public async Task Metadata_only_source_returns_details_but_cannot_enter_download_pipeline()
    {
        var options = FrameworkFixtures.Options(value => value.ProviderFramework.Providers["netease"] = new()
        {
            MetadataEnabled = true, SearchEnabled = false, DownloadEnabled = false
        });
        var (registry, health, orchestrator) = FrameworkFixtures.Build(options, new FakeV2Provider("netease"));
        var access = new ProviderAccessService(registry, orchestrator, health, [new FakeLegacyProvider("netease")],
            NullLogger<ProviderAccessService>.Instance);
        var detail = await access.GetMetadataAsync("netease", "track-1", default);
        Assert.NotNull(detail);
        Assert.Equal("晴天", detail.Title);
        Assert.False(access.IsDownloadConfigured("netease"));
        Assert.Throws<SourceUnavailableException>(() => access.RequireDownloadProvider("netease"));
        Assert.Empty(await orchestrator.SearchAsync("晴天"));
    }

    [Fact]
    public async Task Disabled_metadata_is_not_called_or_counted_as_provider_failure()
    {
        var options = FrameworkFixtures.Options(value => value.ProviderFramework.Providers["p"] = new()
        {
            MetadataEnabled = false
        });
        var (registry, health, orchestrator) = FrameworkFixtures.Build(options, new FakeV2Provider("p"));
        var access = new ProviderAccessService(registry, orchestrator, health, [], NullLogger<ProviderAccessService>.Instance);
        Assert.Null(await access.GetMetadataAsync("p", "track-1", default));
        Assert.Equal(0, health.Snapshot("p", true).TotalCalls);
    }

    [Fact]
    public void Catalog_exposes_independent_switches_and_search_detail_field_differences()
    {
        var options = FrameworkFixtures.Options(value => value.ProviderFramework.Providers["kugou"] = new()
        {
            MetadataEnabled = true, SearchEnabled = false, DownloadEnabled = false
        });
        var (registry, _, _) = FrameworkFixtures.Build(options, new FakeV2Provider("kugou"));
        var item = Assert.Single(new SourceProvidersController().List(registry));
        Assert.True(item.Metadata.Enabled);
        Assert.False(item.Search.Enabled);
        Assert.False(item.Download.Enabled);
        var album = Assert.Single(item.MetadataFields, field => field.Field == "AlbumTitle");
        Assert.True(album.Search.Supported);
        Assert.False(album.Detail.Supported);
    }
}
