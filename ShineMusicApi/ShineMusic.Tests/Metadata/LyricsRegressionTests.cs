using ShineMusic.Service.PlaylistImport;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.Lyrics;
using Xunit;

namespace ShineMusic.Tests.Metadata;

public sealed class LyricsRegressionTests
{
    private static LyricsQuery Query(string title = "爱情陷阱", string artist = "谭咏麟", long duration = 233277) => new(title, [artist], null, duration, null, null);
    private static readonly IOptions<LyricsOptions> OptionsValue = Options.Create(new LyricsOptions { MinScore = 0, ExternalSources = ["lrclib"] });

    [Theory]
    [InlineData("爱情陷阱", "谭咏麟", 233277, "爱情陷阱(Special Club Mix)", 509)]
    [InlineData("Stronger", "Inez", 220373, "Stronger (Jody Den Broeder Club Mix)", 500)]
    [InlineData("风的季节", "soler", 249000, "风的季节 (live)", 120)]
    [InlineData("感谢你曾来过", "周思涵", 250005, "感谢你曾来过 (变速版)", 227)]
    public async Task RealWrongExactCandidate_FallsBackToValidSearch(string title, string artist, long duration, string wrongTitle, double wrongDuration)
    {
        var query = Query(title, artist, duration);
        object Record(int id, string name, double seconds, string text) => new { id, trackName = name, artistName = artist, duration = seconds, syncedLyrics = text };
        var factory = new Factory(request => request.RequestUri!.AbsolutePath.EndsWith("get")
            ? Record(1, wrongTitle, wrongDuration, "[00:10]test")
            : new[] { Record(2, title, duration / 1000d, "[99:00]test"), Record(3, title, duration / 1000d, "[00:10]test") });
        var result = await new LrclibLyricsProvider(factory, OptionsValue).FetchAsync(query, default);
        Assert.Equal("3", result?.ProviderId);
        Assert.Contains(query.Diagnostics, d => d.CandidateId == "1" && d.Reason == "duration_mismatch");
        Assert.Contains(query.Diagnostics, d => d.CandidateId == "2" && d.Reason == "timeline_out_of_bounds");
        Assert.Equal(2, factory.Calls);
    }

    [Theory]
    [InlineData("[00:10][04:00]test", false)]
    [InlineData("[offset:10000]\n[03:50]test", false)]
    [InlineData("[03:99]test", false)]
    [InlineData("[00:bad]test", false)]
    [InlineData("[00:10 test", false)]
    [InlineData("[offset:bad]\n[00:10]test", false)]
    [InlineData("[offset:-20000]\n[00:10]test", false)]
    [InlineData("[00:10][00:20.123]test", true)]
    [InlineData("[offset:-1000]\n[03:59]test", true)]
    [InlineData("[00:10]纯音乐，请欣赏", false)]
    [InlineData("[00:10]暂无歌词", false)]
    public void TimelineAndPlaceholders(string content, bool valid) =>
        Assert.Equal(valid, LyricsValidation.ValidateContent(Query(), "test", "1", content));

    [Theory]
    [InlineData("海阔天空", 239560, "[04:26.53]test")]
    [InlineData("爱情陷阱", 233277, "[08:16.02]test")]
    [InlineData("全部都给你", 187636, "[03:51.99]test")]
    public void RecordedTimelineDefects_AreRejected(string title, long duration, string content)
    {
        var query = Query(title, "artist", duration);
        Assert.False(LyricsValidation.ValidateContent(query, "recorded", "sample", content));
        Assert.Contains(query.Diagnostics, d => d.Reason == "timeline_out_of_bounds");
    }

    [Theory]
    [InlineData(238.277, true)]
    [InlineData(238.278, false)]
    [InlineData(0, false)]
    public void StrictDurationGate(double seconds, bool valid) =>
        Assert.Equal(valid, LyricsValidation.ValidateIdentity(Query(), "test", "1", new("爱情陷阱", "谭咏麟", null, seconds)));

    [Fact]
    public void VersionGateCannotBeOffsetByScore() =>
        Assert.False(LyricsValidation.ValidateIdentity(Query(), "test", "1", new("爱情陷阱 (Live)", "谭咏麟", null, 233.277)));

    [Fact]
    public async Task DirectSourcePrecedesConfiguredExternal_EmbeddedStillFirst()
    {
        var factory = new Factory(_ => new { code = 200, lrc = new { lyric = "[00:10]test" } });
        var external = new Stub("lrclib", (_, _) => throw new InvalidOperationException("must not run"));
        var resolver = new LyricsResolver(OptionsValue, [external, new NeteaseLyricsProvider(factory, OptionsValue)], NullLogger<LyricsResolver>.Instance);
        var query = Query() with { SourceProvider = "netease", SourceTrackId = "328076" };
        Assert.Equal("netease", (await resolver.ResolveAsync(query, default))?.Source);
        Assert.Equal("embedded", (await resolver.ResolveAsync(query with { EmbeddedLyrics = "local" }, default))?.Source);
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData(true, "[00:10]test")]
    [InlineData(false, "[00:10]纯音乐，请欣赏")]
    [InlineData(false, "[99:00]test")]
    public async Task DirectInvalidFallsBack(bool nolyric, string lyric)
    {
        var factory = new Factory(_ => new { code = 200, nolyric, lrc = new { lyric } });
        var external = new Stub("lrclib", (_, _) => Task.FromResult<LyricsMatch?>(new("[00:10]test", "lrclib", .9, "ok")));
        var resolver = new LyricsResolver(OptionsValue, [external, new NeteaseLyricsProvider(factory, OptionsValue)], NullLogger<LyricsResolver>.Instance);
        var query = Query() with { SourceProvider = "netease", SourceTrackId = "328076" };
        Assert.Equal("lrclib", (await resolver.ResolveAsync(query, default))?.Source);
        Assert.Contains(query.Diagnostics, d => d.Source == "netease" && d.CandidateId == "328076");
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var provider = new Stub("lrclib", (_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult<LyricsMatch?>(null); });
        var resolver = new LyricsResolver(OptionsValue, [provider], NullLogger<LyricsResolver>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(Query(), cts.Token));
    }

    [Fact]
    public async Task KugouTriesNextSongAfterInvalidTimeline()
    {
        var query = Query();
        var factory = new Factory(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("mobilecdn")) return new { data = new { info = new[] {
                new { hash = "bad", songname = query.Title, singername = "谭咏麟", duration = 233.277 },
                new { hash = "good", songname = query.Title, singername = "谭咏麟", duration = 233.277 } } } };
            if (url.Contains("krcs")) return new { candidates = new[] { new { id = url.Contains("bad") ? 1 : 2, accesskey = "test" } } };
            return new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url.Contains("id=1&") ? "[99:00]test" : "[00:10]test")) };
        });
        Assert.Equal("2", (await new KuGouLyricsProvider(factory, OptionsValue).FetchAsync(query, default))?.ProviderId);
        Assert.Equal(5, factory.Calls);
    }

    [Fact]
    public async Task MiguRejectsMissingDurationThenTriesNextTimeline()
    {
        var query = Query();
        var factory = new Factory(request => new { songResultData = new { result = new[] {
            new { songId = "missing", name = query.Title, singers = new[] { new { name = "谭咏麟" } }, duration = 0d, lyricUrl = "https://test/missing" },
            new { songId = "bad", name = query.Title, singers = new[] { new { name = "谭咏麟" } }, duration = 233.277, lyricUrl = "https://test/bad" },
            new { songId = "good", name = query.Title, singers = new[] { new { name = "谭咏麟" } }, duration = 233.277, lyricUrl = "https://test/good" } } } });
        // Plain responses are supplied by the handler for the two lyric URLs.
        Assert.Equal("good", (await new MiguLyricsProvider(factory, OptionsValue).FetchAsync(query, default))?.ProviderId);
        Assert.Contains(query.Diagnostics, d => d.CandidateId == "missing" && d.Reason == "duration_unconfirmed");
        Assert.Equal(3, factory.Calls);
    }


    [Fact]
    public void RecordedSevenDurationMismatches_CannotBeAccepted()
    {
        using var playlist = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playlist-900025217.json")));
        var tracks = playlist.RootElement.GetProperty("results")[0].GetProperty("orderedTracks").Deserialize<ParsedPlaylistTrack[]>()!;
        using var records = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lyrics-before.json")));
        var rejected = 0;
        foreach (var record in records.RootElement.EnumerateArray())
        {
            if (!record.TryGetProperty("durationDelta", out var delta) || delta.ValueKind != JsonValueKind.Number || delta.GetDouble() <= 5) continue;
            var track = tracks[record.GetProperty("index").GetInt32() - 1];
            var query = new LyricsQuery(track.Title, track.Artists, track.AlbumTitle, track.DurationMs, null, null);
            Assert.False(LyricsValidation.ValidateIdentity(query, "lrclib", record.GetProperty("providerId").GetString(),
                new(record.GetProperty("candidateTitle").GetString(), record.GetProperty("candidateArtist").GetString(),
                    record.GetProperty("candidateAlbum").GetString(), record.GetProperty("candidateDuration").GetDouble())));
            rejected++;
        }
        Assert.Equal(7, rejected);
    }

    private sealed class Stub(string source, Func<LyricsQuery, CancellationToken, Task<LyricsMatch?>> fetch) : IExternalLyricsProvider
    {
        public string Source => source;
        public Task<LyricsMatch?> FetchAsync(LyricsQuery query, CancellationToken cancellationToken) => fetch(query, cancellationToken);
    }
    private sealed class Factory(Func<HttpRequestMessage, object> response) : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler(request => { Calls++; return response(request); }));
    }
    private sealed class Handler(Func<HttpRequestMessage, object> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                PlainOrJson(request), System.Text.Encoding.UTF8, "application/json") });
        private string PlainOrJson(HttpRequestMessage request)
        {
            var data = response(request);
            return request.RequestUri!.Host == "test" ? (request.RequestUri.AbsolutePath == "/bad" ? "[99:00]test" : "[00:10]test") : JsonSerializer.Serialize(data);
        }
    }
}
