using System.Net;
using System.Text.Json;
using ShineMusic.Service.PlaylistImport;
using Xunit;

namespace ShineMusic.Tests.PlaylistImport;

public sealed class PlaylistParserReliabilityTests
{
    private const string Url = "https://music.163.com/playlist?id=19723756";
    private static object Song(int id) => new { id, name = "Track " + id, ar = new[] { new { name = "Artist" } } };

    [Fact]
    public async Task Html_failure_falls_back_and_restores_source_order()
    {
        using var factory = new Factory(request => request.RequestUri!.AbsolutePath == "/playlist"
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json(new { playlist = new { trackCount = 2, trackIds = new[] { new { id = 2 }, new { id = 1 } }, tracks = new[] { Song(1), Song(2) } } }));
        var result = await new NeteasePlaylistParser(factory).ParseAsync(Url, default);
        Assert.Equal(new[] { "2", "1" }, result.Tracks.Select(t => t.SourceTrackId));
        Assert.Equal(2, result.ExpectedTrackCount);
        Assert.Equal(2, factory.Calls);
    }

    [Fact]
    public async Task Detail_batches_are_bounded_and_missing_details_fail()
    {
        var batches = 0;
        using var factory = new Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/playlist") return Json(new { });
            if (request.RequestUri.AbsolutePath.Contains("playlist/detail"))
                return Json(new { playlist = new { trackCount = 101, trackIds = Enumerable.Range(1, 101).Select(id => new { id }), tracks = Array.Empty<object>() } });
            Assert.True(request.RequestUri.AbsoluteUri.Length < 3000);
            batches++;
            return Json(new { songs = Array.Empty<object>() });
        });
        await Assert.ThrowsAsync<PlaylistParseException>(() => new NeteasePlaylistParser(factory).ParseAsync(Url, default));
        Assert.Equal(3, batches);
    }

    [Theory]
    [InlineData(2, 1, "Title", "mid", "Artist")]
    [InlineData(1, 1, "", "mid", "Artist")]
    [InlineData(1, 1, "Title", "", "Artist")]
    [InlineData(1, 1, "Title", "mid", "")]
    public async Task Qq_rejects_partial_page_and_missing_identity(int total, int current, string title, string mid, string artist)
    {
        using var factory = new Factory(_ => Json(new { cdlist = new[] { new { songnum = total, cur_song_num = current,
            songlist = new[] { new { songname = title, songmid = mid, singer = new[] { new { name = artist } } } } } } }));
        await Assert.ThrowsAsync<PlaylistParseException>(() => new QQMusicPlaylistParser(factory).ParseAsync("https://y.qq.com/n/ryqq/playlist/12345", default));
    }

    [Fact]
    public async Task Caller_cancellation_does_not_trigger_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var factory = new Factory(_ => { cancellation.Cancel(); throw new TaskCanceledException(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NeteasePlaylistParser(factory).ParseAsync(Url, cancellation.Token));
        Assert.Equal(1, factory.Calls);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(response(request));
        }
    }
}
