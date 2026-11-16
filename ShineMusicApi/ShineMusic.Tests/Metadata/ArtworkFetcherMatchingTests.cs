using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ShineMusic.Service;
using Xunit;

namespace ShineMusic.Tests.Metadata;

public sealed class ArtworkFetcherMatchingTests
{
    private static object Track(string title = "晴天", string artist = "周杰伦", string album = "叶惠美", long? duration = 269000, string? artwork = null)
        => new { trackName = title, artistName = artist, collectionName = album, trackTimeMillis = duration,
            releaseDate = "2003-07-31T12:00:00Z", artworkUrl100 = artwork, trackId = 123L };

    [Fact]
    public async Task WrongFirstThenRight_PreservesDateAndTitleWithoutWritingArtwork()
    {
        var factory = new FakeFactory(Track(artist: "其他歌手", artwork: "https://invalid/image"), Track());
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var result = await Create(factory).FetchMatchedAsync("晴天", ["周杰伦"], "叶惠美", 269000, "art", output);
        Assert.True(result.Success);
        Assert.Equal("2003-07-31", result.ReleaseDate);
        Assert.Equal(2003, result.ReleaseYear);
        Assert.Equal("晴天", result.TrackTitle);
        Assert.Null(result.StorageKey);
        Assert.False(Directory.Exists(output));
        Assert.Single(factory.Requests);
        Assert.Contains("limit=20", factory.Requests[0]);
    }

    [Theory]
    [InlineData("晴天 (Live)", "叶惠美", 269000L)]
    [InlineData("晴天", "叶惠美 (Deluxe)", 269000L)]
    [InlineData("晴天", "叶惠美", null)]
    [InlineData("晴天", "叶惠美", 280000L)]
    public async Task UnconfirmedIdentityOrAlbum_IsRejectedBeforeArtwork(string title, string album, long? duration)
    {
        var factory = new FakeFactory(Track(title: title, album: album, duration: duration, artwork: "https://invalid/image"));
        var result = await Create(factory).FetchMatchedAsync("晴天", ["周杰伦"], "叶惠美", 269000, "art", "unused");
        Assert.False(result.Success);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task DifferentAlbumsWithoutExpectedAlbum_AreAmbiguous()
    {
        var factory = new FakeFactory(Track(artwork: "https://invalid/image"), Track(album: "精选集"));
        var result = await Create(factory).FetchMatchedAsync("晴天", ["周杰伦"], null, null, "art", "unused");
        Assert.False(result.Success);
        Assert.Equal("metadata_ambiguous", result.Error);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task MissingArtistsAndTitleOnly_DoNotUseNetwork()
    {
        var factory = new FakeFactory();
        var fetcher = Create(factory);
        var missing = await fetcher.FetchMatchedAsync("晴天", [" "], null, null, "art", "unused");
        var titleOnly = await fetcher.FetchByTitleAsync("晴天", "art", "unused");
        Assert.Equal("identity_insufficient", missing.Error);
        Assert.Equal("identity_insufficient", titleOnly.Error);
        Assert.False(missing.Success);
        Assert.False(titleOnly.Success);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task LegacySongMiss_DoesNotFallBackToAlbum()
    {
        var factory = new FakeFactory(Track(title: "其他歌曲"));
        var result = await Create(factory).FetchAsync("周杰伦", "叶惠美", "晴天", "art", "unused");
        Assert.False(result.Success);
        Assert.Single(factory.Requests);
        Assert.Contains("entity=song", factory.Requests[0]);
    }

    [Fact]
    public async Task SourceTimeout_ReturnsUnavailable()
    {
        var fetcher = new ArtworkFetcher(new CancelledFactory(), NullLogger<ArtworkFetcher>.Instance);
        var result = await fetcher.FetchMatchedAsync("晴天", ["周杰伦"], "叶惠美", 269000, "art", "unused");
        Assert.False(result.Success);
        Assert.Equal("metadata_source_unavailable", result.Error);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fetcher = new ArtworkFetcher(new CancelledFactory(), NullLogger<ArtworkFetcher>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fetcher.FetchMatchedAsync("晴天", ["周杰伦"], "叶惠美", 269000, "art", "unused", cancellation.Token));
    }

    private sealed class CancelledFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new CancelledHandler());
        private sealed class CancelledHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated transport timeout"));
        }
    }

    private static ArtworkFetcher Create(FakeFactory factory) => new(factory, NullLogger<ArtworkFetcher>.Instance);

    private sealed class FakeFactory(params object[] tracks) : IHttpClientFactory
    {
        public List<string> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this, tracks));

        private sealed class Handler(FakeFactory owner, object[] tracks) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(request.RequestUri!.ToString());
                Assert.Equal("itunes.apple.com", request.RequestUri.Host);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { results = tracks }))
                });
            }
        }
    }
}
