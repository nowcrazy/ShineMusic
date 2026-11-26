using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class PlaylistBatchImportTests
{
    [Theory]
    [InlineData(false, "skipped")]
    [InlineData(true, "pending")]
    public async Task Import_counts_matched_occurrences_but_stores_first_position_and_preserves_every_unmatched_occurrence(bool downloadMissing, string expectedStatus)
    {
        using var f = PlaylistBatchTestData.Fixture();
        var catalog = PlaylistBatchTestData.SeedCatalog(f, ["First song", "Second song"]);
        var parsed = new ParsedPlaylist("重复与顺序", null, null,
        [
            PlaylistBatchTestData.Input("Missing repeat", "same-missing"),
            PlaylistBatchTestData.Input("First song", "first-1"),
            PlaylistBatchTestData.Input("First song", "first-2"),
            PlaylistBatchTestData.Input("Missing repeat", "same-missing"),
            PlaylistBatchTestData.Input("Second song", "second-1"),
            PlaylistBatchTestData.Input("Other missing", "other-missing"),
            PlaylistBatchTestData.Input("Second song", "second-2")
        ]);
        var service = PlaylistBatchTestData.Service(f, parsed);
        var preview = await service.ParsePreviewAsync(PlaylistBatchTestData.Url, Guid.Empty, default);
        Assert.Equal(new Guid?[] { null, catalog[0].Id, catalog[0].Id, null, catalog[1].Id, null, catalog[1].Id },
            preview.Tracks.Select(track => track.MatchedTrackId));
        var result = await service.ImportAsync(PlaylistBatchTestData.Url, null, Guid.Empty, downloadMissing, default);
        Assert.Equal(7, result.Total);
        Assert.Equal(4, result.Matched);
        Assert.Equal(3, result.Pending);
        var links = f.Db.Queryable<UserPlaylistTrack>().Where(row => row.PlaylistId == result.PlaylistId).OrderBy(row => row.SortOrder).ToList();
        Assert.Equal(new[] { catalog[0].Id, catalog[1].Id }, links.Select(row => row.TrackId));
        Assert.Equal(new[] { 1, 4 }, links.Select(row => row.SortOrder));
        var pending = f.Db.Queryable<UserPlaylistPendingTrack>().Where(row => row.PlaylistId == result.PlaylistId).OrderBy(row => row.SortOrder).ToList();
        Assert.Equal(new[] { 0, 3, 5 }, pending.Select(row => row.SortOrder));
        Assert.Equal(new[] { "same-missing", "same-missing", "other-missing" }, pending.Select(row => row.SourceTrackId));
        Assert.All(pending, row => Assert.Equal(expectedStatus, row.Status));
        var playlist = f.Db.Queryable<UserPlaylist>().Single(row => row.Id == result.PlaylistId);
        using var snapshot = JsonDocument.Parse(playlist.ImportSnapshotJson!);
        Assert.Equal(7, snapshot.RootElement.GetProperty("Tracks").GetArrayLength());
        Assert.Empty(f.Bus.Messages);
        Assert.Equal(0, f.Db.Queryable<ExternalDownloadTask>().Count());
    }

    [Fact]
    public async Task Selected_queue_sorts_input_indices_counts_duplicate_matches_and_keeps_unmatched_source_order()
    {
        using var f = PlaylistBatchTestData.Fixture();
        PlaylistBatchTestData.SeedCatalog(f, ["Known"]);
        var parsed = new ParsedPlaylist("选择性入队", null, null,
        [
            PlaylistBatchTestData.Input("Missing", "source-0"),
            PlaylistBatchTestData.Input("Unselected", "source-1"),
            PlaylistBatchTestData.Input("Known", "source-2"),
            PlaylistBatchTestData.Input("Known", "source-3"),
            PlaylistBatchTestData.Input("Unselected", "source-4"),
            PlaylistBatchTestData.Input("Missing", "source-5")
        ]);
        var service = PlaylistBatchTestData.Service(f, parsed);
        var preview = await service.ParsePreviewAsync(PlaylistBatchTestData.Url, Guid.Empty, default);
        var result = await service.EnqueueDownloadsAsync(PlaylistBatchTestData.Url, Guid.Empty, default, [5, 3, 0, 2], preview.SnapshotId);
        Assert.Equal(4, result.Total);
        Assert.Equal(2, result.Matched);
        Assert.Equal(2, result.Queued);
        Assert.Equal(new[] { "source-0", "source-5" }, f.Db.Ado.SqlQuery<string>(
            "SELECT source_track_id FROM playlist_download_queue_items ORDER BY rowid"));
        Assert.Equal(0, f.Db.Queryable<UserPlaylist>().Count());
        Assert.Empty(f.Bus.Messages);
    }

    [Theory]
    [InlineData(new int[] { })]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 0, 0 })]
    public async Task Invalid_selected_indices_do_not_write_a_partial_batch(int[] selected)
    {
        using var f = PlaylistBatchTestData.Fixture();
        var parsed = new ParsedPlaylist("invalid", null, null, [PlaylistBatchTestData.Input("Missing", "0"), PlaylistBatchTestData.Input("Other", "1")]);
        var service = PlaylistBatchTestData.Service(f, parsed);
        var preview = await service.ParsePreviewAsync(PlaylistBatchTestData.Url, Guid.Empty, default);
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(PlaylistBatchTestData.Url, Guid.Empty, default, selected, preview.SnapshotId));
        Assert.Equal(0, f.Db.Queryable<PlaylistDownloadQueueItem>().Count());
    }

    [Fact]
    public async Task Selected_queue_requires_the_same_user_url_and_unchanged_preview_snapshot()
    {
        using var f = PlaylistBatchTestData.Fixture();
        var parsed = new ParsedPlaylist("snapshot", null, null, [PlaylistBatchTestData.Input("Missing", "0")]);
        var cache = new PlaylistParseCache();
        var service = PlaylistBatchTestData.Service(f, parsed, cache);
        var preview = await service.ParsePreviewAsync(PlaylistBatchTestData.Url, Guid.Empty, default);
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(PlaylistBatchTestData.Url, Guid.NewGuid(), default, [0], preview.SnapshotId));
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync("https://example.test/other", Guid.Empty, default, [0], preview.SnapshotId));
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(PlaylistBatchTestData.Url, Guid.Empty, default, [0], null));
        cache.Set(Guid.Empty, PlaylistBatchTestData.Url, "batch-test", parsed with { Tracks = [PlaylistBatchTestData.Input("Changed", "0")] });
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(PlaylistBatchTestData.Url, Guid.Empty, default, [0], preview.SnapshotId));
        Assert.Equal(0, f.Db.Queryable<PlaylistDownloadQueueItem>().Count());
    }

    [Fact]
    public async Task Failure_after_one_pending_bulk_chunk_rolls_back_playlist_links_and_all_pending_rows()
    {
        using var f = PlaylistBatchTestData.Fixture();
        PlaylistBatchTestData.SeedCatalog(f, ["Known"]);
        var inputs = new[] { PlaylistBatchTestData.Input("Known", "known") }
            .Concat(Enumerable.Range(0, 1200).Select(index => new ParsedPlaylistTrack("Missing " + index, [], null, null, index.ToString(), null))).ToArray();
        var service = PlaylistBatchTestData.Service(f, new ParsedPlaylist("rollback", null, null, inputs));
        var pendingInserts = 0;
        var sawCommittedChunkInsideTransaction = false;
        f.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("user_playlist_pending_tracks", StringComparison.OrdinalIgnoreCase)) return;
            pendingInserts++;
            if (pendingInserts != 2) return;
            Assert.NotNull(f.Db.Ado.Transaction);
            sawCommittedChunkInsideTransaction = f.Db.Queryable<UserPlaylistPendingTrack>().Count() > 0;
            throw new InvalidOperationException("injected second pending batch failure");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => service.ImportAsync(PlaylistBatchTestData.Url, null, Guid.Empty, true, default));
        f.Db.Aop.OnLogExecuting = null;
        Assert.True(sawCommittedChunkInsideTransaction, "The failure must occur after a previous bulk chunk has actually written rows in the same transaction.");
        Assert.True(pendingInserts >= 2);
        Assert.Equal(0, f.Db.Queryable<UserPlaylist>().Count());
        Assert.Equal(0, f.Db.Queryable<UserPlaylistTrack>().Count());
        Assert.Equal(0, f.Db.Queryable<UserPlaylistPendingTrack>().Count());
        Assert.Null(f.Db.Ado.Transaction);
    }

    [Fact]
    public async Task Failure_after_one_queue_bulk_chunk_rolls_back_all_queue_rows()
    {
        using var f = PlaylistBatchTestData.Fixture();
        var parsed = new ParsedPlaylist("queue rollback", null, null,
            Enumerable.Range(0, 120).Select(index => new ParsedPlaylistTrack("Missing " + index, [], null, null, index.ToString(), null)).ToArray());
        var service = PlaylistBatchTestData.Service(f, parsed);
        var inserts = 0;
        var sawFirstChunk = false;
        f.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("playlist_download_queue_items", StringComparison.OrdinalIgnoreCase)) return;
            if (++inserts != 2) return;
            Assert.NotNull(f.Db.Ado.Transaction);
            sawFirstChunk = f.Db.Queryable<PlaylistDownloadQueueItem>().Count() > 0;
            throw new InvalidOperationException("injected second queue batch failure");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => service.EnqueueDownloadsAsync(PlaylistBatchTestData.Url, Guid.Empty, default));
        f.Db.Aop.OnLogExecuting = null;
        Assert.True(sawFirstChunk);
        Assert.Equal(0, f.Db.Queryable<PlaylistDownloadQueueItem>().Count());
        Assert.Null(f.Db.Ado.Transaction);
    }
}

internal static class PlaylistBatchTestData
{
    public const string Url = "https://example.test/batch-playlist";
    public const string ArtistName = "Batch Artist";
    public const string AlbumName = "Batch Album";

    public static SourceDownloadAcceptanceFixture Fixture()
    {
        var fixture = new SourceDownloadAcceptanceFixture();
        fixture.Db.CodeFirst.InitTables<Track, Artist, Album, TrackArtist>();
        fixture.Db.CodeFirst.InitTables<UserPlaylist, UserPlaylistTrack, UserPlaylistPendingTrack, PlaylistDownloadQueueItem>();
        fixture.SearchProviders.Clear();
        fixture.Legacy.Clear();
        return fixture;
    }

    public static PlaylistImportService Service(SourceDownloadAcceptanceFixture fixture, ParsedPlaylist parsed,
        PlaylistParseCache? cache = null, ILogger<PlaylistImportService>? logger = null)
    {
        cache ??= new PlaylistParseCache();
        cache.Set(Guid.Empty, Url, "batch-test", parsed);
        return new PlaylistImportService(fixture.Db, [new Parser(parsed)], fixture.Legacy, fixture.Aggregator(),
            new SourceDownloadService(fixture.Db, fixture.Legacy, fixture.Bus, fixture.Access()), cache,
            Options.Create(fixture.Options), logger ?? NullLogger<PlaylistImportService>.Instance);
    }

    public static ParsedPlaylistTrack Input(string title, string sourceId) => new(title, [ArtistName], AlbumName, 180_000, sourceId, "https://example.test/track/" + sourceId);

    public static IReadOnlyList<Track> SeedCatalog(SourceDownloadAcceptanceFixture fixture, IReadOnlyList<string> titles)
    {
        var artist = new Artist { Name = ArtistName };
        var album = new Album { Title = AlbumName };
        fixture.Db.Insertable(artist).ExecuteCommand();
        fixture.Db.Insertable(album).ExecuteCommand();
        var tracks = titles.Select(title => new Track { Title = title, AlbumId = album.Id, DurationMs = 180_000, PublishedAt = DateTimeOffset.UtcNow }).ToArray();
        foreach (var chunk in tracks.Chunk(50))
        {
            fixture.Db.Insertable(chunk).ExecuteCommand();
            fixture.Db.Insertable(chunk.Select(track => new TrackArtist { TrackId = track.Id, ArtistId = artist.Id }).ToArray()).ExecuteCommand();
        }
        return tracks;
    }

    private sealed class Parser(ParsedPlaylist playlist) : IPlaylistParser
    {
        public string Platform => "batch-test";
        public bool CanHandle(string url) => true;
        public Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken) => Task.FromResult(playlist);
    }
}
