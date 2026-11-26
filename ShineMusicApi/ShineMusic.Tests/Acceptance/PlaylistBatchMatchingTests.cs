using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class PlaylistBatchMatchingTests
{
    private static PlaylistImportService Service(SourceDownloadAcceptanceFixture fixture) => new(
        fixture.Db, [], fixture.Legacy, fixture.Aggregator(),
        new SourceDownloadService(fixture.Db, fixture.Legacy, fixture.Bus, fixture.Access()),
        new PlaylistParseCache(), Options.Create(fixture.Options), NullLogger<PlaylistImportService>.Instance);

    private static Task<IReadOnlyList<Guid?>> Match(
        PlaylistImportService service, IReadOnlyList<ParsedPlaylistTrack> tracks, CancellationToken cancellationToken = default) =>
        (Task<IReadOnlyList<Guid?>>)typeof(PlaylistImportService)
            .GetMethod("MatchLibraryBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [tracks, cancellationToken])!;

    private static void Init(SourceDownloadAcceptanceFixture fixture) =>
        fixture.Db.CodeFirst.InitTables<Track, Album, Artist, TrackArtist>();

    private static Track Seed(
        SourceDownloadAcceptanceFixture fixture, string title, string artist = "Singer", string? album = null,
        long? durationMs = 120000, bool published = true)
    {
        Guid? albumId = null;
        if (album is not null)
        {
            var albumRow = new Album { Title = album };
            fixture.Db.Insertable(albumRow).ExecuteCommand();
            albumId = albumRow.Id;
        }
        var artistRow = new Artist { Name = artist };
        var track = new Track
        {
            Title = title,
            AlbumId = albumId,
            DurationMs = durationMs,
            PublishedAt = published ? DateTimeOffset.UtcNow : null
        };
        fixture.Db.Insertable(artistRow).ExecuteCommand();
        fixture.Db.Insertable(track).ExecuteCommand();
        fixture.Db.Insertable(new TrackArtist { TrackId = track.Id, ArtistId = artistRow.Id }).ExecuteCommand();
        return track;
    }

    private static ParsedPlaylistTrack Input(
        string title, IReadOnlyList<string>? artists = null, string? album = null, int? duration = 120000) =>
        new(title, artists ?? ["Singer"], album, duration, null, null);

    [Fact]
    public async Task Batch_preserves_title_artist_version_and_published_filters_in_input_order()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        Init(fixture);
        var studio = Seed(fixture, "Song");
        var live = Seed(fixture, "Song (Live)");
        var exactArtist = Seed(fixture, "Other", "G.E.M. 邓紫棋");
        Seed(fixture, "Private", published: false);
        Seed(fixture, "two  spaces");
        var matches = await Match(Service(fixture),
        [
            Input(" song "),
            Input("Song (Live)"),
            Input("Other", ["G.E.M.邓紫棋"]),
            Input("Song", ["Singer Extra"]),
            Input("Private"),
            Input("two spaces"),
            Input("Song", []),
            Input(" "),
            Input("Song")
        ]);
        Assert.Equal(new Guid?[] { studio.Id, live.Id, exactArtist.Id, null, null, null, null, null, studio.Id }, matches);
    }

    [Fact]
    public async Task Batch_preserves_album_preference_fallback_and_any_exact_artist_overlap()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        Init(fixture);
        var first = Seed(fixture, "Song", album: "First", durationMs: 121000);
        var second = Seed(fixture, "Song", album: "Second", durationMs: 120000);
        var matches = await Match(Service(fixture),
        [
            Input("Song", album: " FIRST! "),
            Input("Song", album: "Missing"),
            Input("Song", ["Wrong", "Singer"], "First"),
            Input("Song", ["Wrong"], "First")
        ]);
        Assert.Equal(new Guid?[] { first.Id, second.Id, first.Id, null }, matches);
    }

    [Fact]
    public async Task Batch_keeps_strict_duration_window_missing_duration_and_unique_candidate_rules()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        Init(fixture);
        var unique = Seed(fixture, "Unique", durationMs: null);
        Seed(fixture, "Ambiguous", durationMs: 120000);
        Seed(fixture, "Ambiguous", durationMs: 121000);
        var inside = Seed(fixture, "Inside", durationMs: 124999);
        Seed(fixture, "Boundary", durationMs: 125000);
        Seed(fixture, "AlbumFirst", album: "Chosen", durationMs: 125000);
        Seed(fixture, "AlbumFirst", album: "Other", durationMs: 120000);
        var matches = await Match(Service(fixture),
        [
            Input("Unique", duration: null),
            Input("Unique"),
            Input("Ambiguous", duration: null),
            Input("Inside"),
            Input("Boundary"),
            Input("AlbumFirst", album: "Chosen")
        ]);
        Assert.Equal(new Guid?[] { unique.Id, null, null, inside.Id, null, null }, matches);
    }

    [Fact]
    public async Task Batch_returns_empty_or_invalid_input_without_catalog_queries()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var service = Service(fixture);
        // No catalog tables exist: invalid identities must not try querying them.
        Assert.Empty(await Match(service, []));
        Assert.Equal(new Guid?[] { null, null }, await Match(service, [Input(" "), Input("Song", [])]));
    }

    [Fact]
    public async Task Batch_deduplicates_titles_and_chunks_all_candidate_association_queries()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        Init(fixture);
        var tracks = Enumerable.Range(0, 1001).Select(index => new Track
        {
            Title = "Song " + index,
            AlbumId = Guid.NewGuid(),
            DurationMs = 120000,
            PublishedAt = DateTimeOffset.UtcNow
        }).ToArray();
        var albums = tracks.Select(track => new Album { Id = track.AlbumId!.Value, Title = "Album" }).ToArray();
        var artist = new Artist { Name = "Singer" };
        fixture.Db.Insertable(artist).ExecuteCommand();
        foreach (var batch in tracks.Chunk(200)) fixture.Db.Insertable(batch).ExecuteCommand();
        foreach (var batch in albums.Chunk(200)) fixture.Db.Insertable(batch).ExecuteCommand();
        foreach (var batch in tracks.Select(track => new TrackArtist { TrackId = track.Id, ArtistId = artist.Id }).Chunk(200))
            fixture.Db.Insertable(batch).ExecuteCommand();
        var service = Service(fixture);
        var selects = 0;
        fixture.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) selects++;
        };
        var inputs = tracks.Select(track => Input(track.Title)).Concat([Input(tracks[0].Title)]).ToArray();
        var matches = await Match(service, inputs);
        Assert.Equal(tracks.Select(track => (Guid?)track.Id).Concat([tracks[0].Id]), matches);
        // 3 title chunks + 3 album chunks + 3 artist chunks, independent of
        // repeated input occurrences and without per-song database roundtrips.
        Assert.Equal(9, selects);
    }
}
