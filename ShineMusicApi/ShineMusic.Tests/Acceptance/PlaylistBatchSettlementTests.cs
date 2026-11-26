using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class PlaylistBatchSettlementTests
{
    private static PlaylistImportService Service(SourceDownloadAcceptanceFixture fixture, ISqlSugarClient? client = null)
    {
        client ??= fixture.Db;
        return new(client, [], fixture.Legacy, fixture.Aggregator(),
            new SourceDownloadService(client, fixture.Legacy, fixture.Bus, fixture.Access()),
            new PlaylistParseCache(), Options.Create(fixture.Options), NullLogger<PlaylistImportService>.Instance);
    }

    private static UserPlaylist Playlist(SourceDownloadAcceptanceFixture fixture)
    {
        fixture.Db.CodeFirst.InitTables<UserPlaylist, UserPlaylistPendingTrack, UserPlaylistTrack>();
        var playlist = new UserPlaylist { Name = "batch settlement", UserId = Guid.NewGuid() };
        fixture.Db.Insertable(playlist).ExecuteCommand();
        return playlist;
    }

    private static UserPlaylistPendingTrack Pending(Guid playlistId, int order, string status = "downloading", Guid? taskId = null) =>
        new()
        {
            PlaylistId = playlistId, Title = "pending " + order, ArtistsJson = "[]", SourcePlatform = "test",
            SortOrder = order, Status = status, DownloadTaskId = taskId,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            // Match the fractional precision normally round-tripped by the app. SQLite's
            // bulk literal insert and DateTime parameter bind format whole seconds differently.
            UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc)
        };

    private static ExternalDownloadTask TaskRow(string status, Guid? importId = null) => new()
    {
        SourceProvider = "test", ProviderTrackId = Guid.NewGuid().ToString("N"),
        Status = status, ImportItemId = importId, CreatedByUserId = Guid.NewGuid()
    };

    private static LibraryImportItem Import(string status, Guid? publishedTrackId = null) => new()
    {
        DisplayName = "settlement.flac", SourceStorageKey = "settlement.flac", ContentHash = Guid.NewGuid().ToString("N"),
        Status = status, PublishedTrackId = publishedTrackId
    };

    private static UserPlaylistPendingTrack SeedPublished(SourceDownloadAcceptanceFixture fixture, Guid playlistId, int order, Guid trackId)
    {
        var import = Import(LibraryImportItemStatuses.Published, trackId);
        var task = TaskRow(ExternalDownloadTaskStatuses.Completed, import.Id);
        var pending = Pending(playlistId, order, taskId: task.Id);
        fixture.Db.Insertable(import).ExecuteCommand();
        fixture.Db.Insertable(task).ExecuteCommand();
        fixture.Db.Insertable(pending).ExecuteCommand();
        return pending;
    }

    [Fact]
    public async Task Published_rows_use_batched_reads_and_preserve_first_occurrence_and_existing_link_position()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        var trackIds = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        fixture.Db.Insertable(new UserPlaylistTrack
        {
            PlaylistId = playlist.Id, TrackId = trackIds[0], SortOrder = 999, AddedAtUtc = DateTime.UtcNow
        }).ExecuteCommand();
        // Reversed physical insertion order must not change which duplicate occurrence supplies SortOrder.
        foreach (var order in Enumerable.Range(0, 120).Reverse())
            SeedPublished(fixture, playlist.Id, order, trackIds[order % trackIds.Length]);
        var selects = 0;
        fixture.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) selects++;
        };
        await Service(fixture).ResolvePendingAsync(playlist.Id, default);
        fixture.Db.Aop.OnLogExecuting = null;

        Assert.InRange(selects, 1, 5); // pending, tasks, import outcomes and existing links, independent of 120 rows.
        Assert.Empty(fixture.Db.Queryable<UserPlaylistPendingTrack>().ToList());
        var links = fixture.Db.Queryable<UserPlaylistTrack>().ToList();
        Assert.Equal(trackIds.Length, links.Count);
        Assert.Equal(999, Assert.Single(links, link => link.TrackId == trackIds[0]).SortOrder);
        for (var index = 1; index < trackIds.Length; index++)
            Assert.Equal(index, Assert.Single(links, link => link.TrackId == trackIds[index]).SortOrder);
    }

    [Fact]
    public async Task Duplicate_download_failure_rematches_in_one_batch_before_promotion()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        fixture.Db.CodeFirst.InitTables<Track, Artist, TrackArtist, Album>();
        var track = new Track { Title = "shared song", DurationMs = 120000, PublishedAt = DateTimeOffset.UtcNow };
        var artist = new Artist { Name = "shared artist" };
        fixture.Db.Insertable(track).ExecuteCommand();
        fixture.Db.Insertable(artist).ExecuteCommand();
        fixture.Db.Insertable(new TrackArtist { TrackId = track.Id, ArtistId = artist.Id }).ExecuteCommand();
        for (var order = 0; order < 30; order++)
        {
            var task = TaskRow(ExternalDownloadTaskStatuses.Failed);
            task.ErrorCode = ErrorCodes.SourceDuplicate;
            fixture.Db.Insertable(task).ExecuteCommand();
            var pending = Pending(playlist.Id, order, "failed", task.Id);
            pending.Title = track.Title;
            pending.ArtistsJson = "[\"shared artist\"]";
            pending.DurationMs = 120000;
            fixture.Db.Insertable(pending).ExecuteCommand();
        }
        var selects = 0;
        fixture.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) selects++;
        };
        await Service(fixture).ResolvePendingAsync(playlist.Id, default);
        fixture.Db.Aop.OnLogExecuting = null;
        Assert.InRange(selects, 1, 7);
        Assert.Empty(fixture.Db.Queryable<UserPlaylistPendingTrack>().ToList());
        var link = Assert.Single(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
        Assert.Equal(track.Id, link.TrackId);
        Assert.Equal(0, link.SortOrder);
    }

    [Fact]
    public async Task Rematch_keeps_active_downloads_and_review_while_requeuing_other_unmatched_rows()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        var active = TaskRow(ExternalDownloadTaskStatuses.Queued);
        var reviewImport = Import(LibraryImportItemStatuses.PendingReview);
        var review = TaskRow(ExternalDownloadTaskStatuses.Completed, reviewImport.Id);
        var rejectedImport = Import(LibraryImportItemStatuses.Rejected);
        var rejected = TaskRow(ExternalDownloadTaskStatuses.Completed, rejectedImport.Id);
        fixture.Db.Insertable(new[] { active, review, rejected }).ExecuteCommand();
        fixture.Db.Insertable(new[] { reviewImport, rejectedImport }).ExecuteCommand();
        var rows = new[]
        {
            Pending(playlist.Id, 0, "downloading", active.Id), Pending(playlist.Id, 1, "needs_review", review.Id),
            Pending(playlist.Id, 2, "rejected", rejected.Id), Pending(playlist.Id, 3, "skipped"),
            Pending(playlist.Id, 4, "no_source")
        };
        fixture.Db.Insertable(rows).ExecuteCommand();
        var result = await Service(fixture).RematchPendingAsync(playlist.Id, default);
        Assert.Equal(5, result.Total);
        Assert.Equal(0, result.Promoted);
        Assert.Equal(3, result.Retried);
        Assert.Equal(2, result.Unchanged);
        var saved = fixture.Db.Queryable<UserPlaylistPendingTrack>().OrderBy(item => item.SortOrder).ToList();
        Assert.Equal("downloading", saved[0].Status);
        Assert.Equal("needs_review", saved[1].Status);
        Assert.All(saved.Skip(2), row => { Assert.Equal("pending", row.Status); Assert.Null(row.DownloadTaskId); });
        Assert.Empty(fixture.Bus.Messages);
    }

    [Fact]
    public async Task Requeue_fence_does_not_overwrite_a_task_attached_after_the_matching_snapshot()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        var pending = Pending(playlist.Id, 0, "no_source");
        fixture.Db.Insertable(pending).ExecuteCommand();
        var newTask = Guid.NewGuid();
        var changed = false;
        fixture.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (changed || !sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("user_playlist_pending_tracks", StringComparison.OrdinalIgnoreCase)) return;
            changed = true;
            using var concurrent = new SqlSugarClient(fixture.Db.CurrentConnectionConfig);
            concurrent.Updateable<UserPlaylistPendingTrack>()
                .SetColumns(item => item.Status == "downloading")
                .SetColumns(item => item.DownloadTaskId == newTask)
                .SetColumns(item => item.UpdatedAtUtc == pending.UpdatedAtUtc.AddSeconds(5))
                .Where(item => item.Id == pending.Id).ExecuteCommand();
        };
        var result = await Service(fixture).RematchPendingAsync(playlist.Id, default);
        fixture.Db.Aop.OnLogExecuting = null;
        Assert.True(changed);
        Assert.Equal(0, result.Retried);
        Assert.Equal(1, result.Unchanged);
        var saved = fixture.Db.Queryable<UserPlaylistPendingTrack>().Single(item => item.Id == pending.Id);
        Assert.Equal("downloading", saved.Status);
        Assert.Equal(newTask, saved.DownloadTaskId);
    }

    [Fact]
    public async Task Promotion_fence_rejects_an_edited_pending_and_does_not_create_its_link()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        var pending = SeedPublished(fixture, playlist.Id, 0, Guid.NewGuid());
        var changed = false;
        fixture.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            // Pending rows have already been read; change them before the import snapshot read,
            // while no promotion write transaction has begun (SQLite starts an immediate transaction).
            if (changed || !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("library_import_items", StringComparison.OrdinalIgnoreCase)) return;
            changed = true;
            using var concurrent = new SqlSugarClient(fixture.Db.CurrentConnectionConfig);
            concurrent.Updateable<UserPlaylistPendingTrack>()
                .SetColumns(item => item.Status == "skipped")
                .SetColumns(item => item.UpdatedAtUtc == pending.UpdatedAtUtc.AddSeconds(5))
                .Where(item => item.Id == pending.Id).ExecuteCommand();
        };
        await Service(fixture).ResolvePendingAsync(playlist.Id, default);
        fixture.Db.Aop.OnLogExecuting = null;
        Assert.True(changed);
        Assert.Equal("skipped", fixture.Db.Queryable<UserPlaylistPendingTrack>().Single(item => item.Id == pending.Id).Status);
        Assert.Empty(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
    }

    [Fact]
    public async Task Failed_link_insert_rolls_back_all_claimed_pending_deletions()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        var first = SeedPublished(fixture, playlist.Id, 0, Guid.NewGuid());
        var second = SeedPublished(fixture, playlist.Id, 1, Guid.NewGuid());
        fixture.Db.Ado.ExecuteCommand("CREATE TRIGGER fail_settlement_link BEFORE INSERT ON user_playlist_tracks BEGIN SELECT RAISE(ABORT, 'injected link failure'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => Service(fixture).ResolvePendingAsync(playlist.Id, default));
        var remaining = fixture.Db.Queryable<UserPlaylistPendingTrack>().ToList();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, item => item.Id == first.Id);
        Assert.Contains(remaining, item => item.Id == second.Id);
        Assert.Empty(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
    }

    [Fact]
    public async Task Concurrent_settlers_do_not_duplicate_links_or_drop_pending_rows()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        var trackId = Guid.NewGuid();
        SeedPublished(fixture, playlist.Id, 0, trackId);
        SeedPublished(fixture, playlist.Id, 1, trackId);
        using var secondClient = new SqlSugarClient(fixture.Db.CurrentConnectionConfig);
        await Task.WhenAll(
            Task.Run(() => Service(fixture).ResolvePendingAsync(playlist.Id, default)),
            Task.Run(() => Service(fixture, secondClient).ResolvePendingAsync(playlist.Id, default)))
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(fixture.Db.Queryable<UserPlaylistPendingTrack>().ToList());
        var link = Assert.Single(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
        Assert.Equal(trackId, link.TrackId);
        Assert.Equal(0, link.SortOrder);
    }

    [Fact]
    public async Task A_deleted_playlist_does_not_acquire_orphan_links()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        Playlist(fixture);
        var missingPlaylist = Guid.NewGuid();
        var pending = SeedPublished(fixture, missingPlaylist, 0, Guid.NewGuid());
        await Service(fixture).ResolvePendingAsync(missingPlaylist, default);
        Assert.Equal(pending.Id, Assert.Single(fixture.Db.Queryable<UserPlaylistPendingTrack>().ToList()).Id);
        Assert.Empty(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
    }

    [Fact]
    public async Task Requeue_updates_multiple_fenced_chunks_with_each_rows_own_snapshot()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        var rows = Enumerable.Range(0, 205).Select(index =>
        {
            var row = Pending(playlist.Id, index, index % 2 == 0 ? "no_source" : "skipped",
                index % 3 == 0 ? Guid.NewGuid() : null);
            row.UpdatedAtUtc = row.UpdatedAtUtc.AddSeconds(index);
            return row;
        }).ToArray();
        foreach (var batch in rows.Chunk(50)) fixture.Db.Insertable(batch).ExecuteCommand();
        var updates = 0;
        fixture.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("user_playlist_pending_tracks", StringComparison.OrdinalIgnoreCase)) updates++;
        };
        var result = await Service(fixture).RematchPendingAsync(playlist.Id, default);
        fixture.Db.Aop.OnLogExecuting = null;

        Assert.Equal(rows.Length, result.Total);
        Assert.Equal(rows.Length, result.Retried);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(3, updates);
        var saved = fixture.Db.Queryable<UserPlaylistPendingTrack>().ToList();
        Assert.Equal(rows.Length, saved.Count);
        Assert.All(saved, row =>
        {
            Assert.Equal("pending", row.Status);
            Assert.Null(row.DownloadTaskId);
        });
    }

    [Fact]
    public async Task Settlement_inside_existing_transaction_leaves_commit_and_rollback_to_the_caller()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        var playlist = Playlist(fixture);
        var pending = SeedPublished(fixture, playlist.Id, 0, Guid.NewGuid());
        await fixture.Db.Ado.BeginTranAsync();
        try
        {
            await Service(fixture).ResolvePendingAsync(playlist.Id, default);
            Assert.NotNull(fixture.Db.Ado.Transaction);
            Assert.Empty(fixture.Db.Queryable<UserPlaylistPendingTrack>().ToList());
            Assert.Single(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
            await fixture.Db.Ado.RollbackTranAsync();
        }
        finally
        {
            if (fixture.Db.Ado.Transaction is not null) await fixture.Db.Ado.RollbackTranAsync();
        }

        Assert.Equal(pending.Id, Assert.Single(fixture.Db.Queryable<UserPlaylistPendingTrack>().ToList()).Id);
        Assert.Empty(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
    }
}
