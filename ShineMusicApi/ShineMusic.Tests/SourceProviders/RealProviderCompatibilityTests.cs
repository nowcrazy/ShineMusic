using System.Net;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Adapters;
using ShineMusic.Service.SourceProviders.Engine;
using Xunit;

namespace ShineMusic.Tests.SourceProviders;

public sealed class RealProviderCompatibilityTests
{
    [Theory]
    [InlineData("""{"data":[{"name":"晴天","album":{"name":"叶惠美","mid":"album-1","time_public":"2003-07-31"},"singer":[{"name":"周杰伦"}],"interval":269}]}""")]
    [InlineData("""{"data":[{"songname":"晴天","albumname":"叶惠美","albummid":"album-1","year":2003,"singer":[{"name":"周杰伦"}],"interval":269}]}""")]
    public async Task Qq_metadata_supports_current_and_legacy_schema(string json)
    {
        using var http = new ResponseFactory(json);
        var source = new QqSourceProvider(http, Options.Create(new SourceDownloadOptions()));
        var track = await source.GetTrackAsync("song-1", default);
        Assert.NotNull(track);
        Assert.Equal("晴天", track.Title);
        Assert.Equal("叶惠美", track.AlbumTitle);
        Assert.Equal(2003, track.ReleaseYear);
        Assert.Equal(269000, track.DurationMs);
        Assert.Equal(["周杰伦"], track.Artists);
        Assert.Contains("album-1", track.CoverUrl);
    }

    [Theory]
    [InlineData("M500sample.mp3", "mp3", 128, false, "standard")]
    [InlineData("M800sample.mp3", "mp3", 320, false, "high")]
    [InlineData("F000sample.flac", "flac", null, true, "lossless")]
    [InlineData("C400sample.m4a", "m4a", null, false, "standard")]
    [InlineData("unknown", "", null, false, "standard")]
    public async Task Qq_resource_labels_do_not_promote_low_or_unknown_quality(
        string filename, string container, int? bitrate, bool lossless, string quality)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            req_0 = new { data = new { midurlinfo = new[] { new { purl = "audio", filename } } } }
        });
        using var http = new ResponseFactory(json);
        var source = new QqSourceProvider(http, Options.Create(new SourceDownloadOptions()));
        var resource = await source.ResolveDownloadAsync("song-1", default);
        Assert.Equal(container, resource.Container);
        Assert.Equal(bitrate, resource.BitrateKbps);
        Assert.Equal(lossless, resource.IsLossless);
        Assert.Equal(quality, resource.Quality);
    }

    [Fact]
    public async Task Kugou_search_uses_the_verified_request_fingerprint()
    {
        using var http = new ResponseFactory("""{"data":{"lists":[]}}""");
        var source = new KugouSourceProvider(http, Options.Create(new SourceDownloadOptions()));
        await source.SearchAsync("晴天 周杰伦", 3, default);
        Assert.Contains("Android", http.UserAgent);
        Assert.Equal("https://y.qq.com/", http.Referrer);
    }

    [Fact]
    public void Registry_applies_real_evidence_without_turning_unknown_into_downloadable()
    {
        var options = Options.Create(FrameworkFixtures.Options());
        var providers = new IMusicProvider[]
        {
            new QqProvider(new FakeLegacyProvider("qq"), options),
            new AudiusProvider(new FakeLegacyProvider("audius"), options),
            new JamendoProvider(new FakeLegacyProvider("jamendo"), options),
            new QishuiProvider(new FakeLegacyProvider("qishui"), options)
        };
        var registry = new ProviderRegistry(providers, options, new ProviderHealthTracker());
        Assert.True(registry.IsConfigured("qq", ProviderCapability.Metadata));
        Assert.False(registry.IsConfigured("qq", ProviderCapability.Resolve));
        Assert.True(registry.IsConfigured("audius", ProviderCapability.Download320));
        Assert.False(registry.IsConfigured("audius", ProviderCapability.DownloadLossless));
        Assert.True(registry.IsConfigured("jamendo", ProviderCapability.DownloadLossless));
        Assert.False(registry.IsConfigured("qishui", ProviderCapability.Metadata));
        Assert.Contains("三曲", registry.List().Single(p => p.ProviderName == "qq")
            .Capabilities.Single(c => c.Capability == ProviderCapability.Metadata).Note);
    }

    private sealed class ResponseFactory(string json) : HttpMessageHandler, IHttpClientFactory
    {
        public string UserAgent { get; private set; } = "";
        public string? Referrer { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            Referrer = request.Headers.Referrer?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
