using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class PlaylistImportReliabilityTests
{
    private static PlaylistImportService Service(SourceDownloadAcceptanceFixture f, ParsedPlaylist parsed, PlaylistParseCache? cache = null)
    {
        cache ??= new PlaylistParseCache();
        cache.Set(Guid.Empty, "https://example.test/playlist", "test", parsed);
        return new(f.Db, [new TestParser(parsed)], f.Legacy, f.Aggregator(), new SourceDownloadService(f.Db, f.Legacy, f.Bus, f.Access()),
            cache, Options.Create(f.Options), NullLogger<PlaylistImportService>.Instance);
    }

    private sealed class TestParser(ParsedPlaylist parsed) : IPlaylistParser
    {
        public string Platform => "test";
        public bool CanHandle(string url) => true;
        public Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken) => Task.FromResult(parsed);
    }

    [Fact]
    public async Task Selective_queue_preserves_duplicate_occurrences_and_excludes_unselected_rows()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<PlaylistDownloadQueueItem>();
        var parsed = new ParsedPlaylist("test", null, null,
            [new("重复", [], null, null, "same", null), new("不要下载", [], null, null, "other", null),
             new("重复", [], null, null, "same", null)]);
        var service = Service(f, parsed);
        var preview = await service.ParsePreviewAsync("https://example.test/playlist", Guid.Empty, default);
        var result = await service.EnqueueDownloadsAsync("https://example.test/playlist", Guid.Empty, default, [2, 0], preview.SnapshotId);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Queued);
        Assert.Equal(0, result.Matched);
        var rows = f.Db.Queryable<PlaylistDownloadQueueItem>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("same", row.SourceTrackId));
    }

    [Theory]
    [InlineData(new int[] { })]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 0, 0 })]
    public async Task Explicit_invalid_selection_never_queues_whole_playlist(int[] indices)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<PlaylistDownloadQueueItem>();
        var service = Service(f, new ParsedPlaylist("test", null, null, [new("曲目", [], null, null, "1", null)]));
        var preview = await service.ParsePreviewAsync("https://example.test/playlist", Guid.Empty, default);
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(
            "https://example.test/playlist", Guid.Empty, default, indices, preview.SnapshotId));
        Assert.Equal(0, f.Db.Queryable<PlaylistDownloadQueueItem>().Count());
    }

    [Fact]
    public async Task Selection_requires_same_user_url_and_unchanged_cached_snapshot()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<PlaylistDownloadQueueItem>();
        var parsed = new ParsedPlaylist("test", null, null, [new("曲目", [], null, null, "1", null)]);
        var cache = new PlaylistParseCache();
        var service = Service(f, parsed, cache);
        var preview = await service.ParsePreviewAsync("https://example.test/playlist", Guid.Empty, default);
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(
            "https://example.test/playlist", Guid.NewGuid(), default, [0], preview.SnapshotId));
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(
            "https://example.test/other", Guid.Empty, default, [0], preview.SnapshotId));
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(
            "https://example.test/playlist", Guid.Empty, default, [0]));
        cache.Set(Guid.Empty, "https://example.test/playlist", "test", parsed with { Name = "changed" });
        await Assert.ThrowsAsync<PlaylistParseException>(() => service.EnqueueDownloadsAsync(
            "https://example.test/playlist", Guid.Empty, default, [0], preview.SnapshotId));
        Assert.Equal(0, f.Db.Queryable<PlaylistDownloadQueueItem>().Count());
    }

    [Fact]
    public async Task Legacy_queue_without_selection_still_queues_entire_playlist()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<PlaylistDownloadQueueItem>();
        var service = Service(f, new ParsedPlaylist("test", null, null,
            [new("第一首", [], null, null, "1", null), new("第二首", [], null, null, "2", null)]));
        var result = await service.EnqueueDownloadsAsync("https://example.test/playlist", Guid.Empty, default);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Queued);
        Assert.Equal(2, f.Db.Queryable<PlaylistDownloadQueueItem>().Count());
    }

    [Theory]
    [InlineData("processing_failed", "failed")]
    [InlineData("rejected", "rejected")]
    [InlineData("pending_review", "needs_review")]
    [InlineData("ready_to_publish", "needs_review")]
    public async Task Settlement_distinguishes_processing_failure_from_review(string importStatus, string expected)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<UserPlaylistPendingTrack, UserPlaylistTrack>();
        var task = f.Seed(ExternalDownloadTaskStatuses.Completed);
        var item = new LibraryImportItem { DisplayName = "audio.flac", SourceStorageKey = "audio.flac", ContentHash = "hash", Status = importStatus };
        f.Db.Insertable(item).ExecuteCommand();
        task.ImportItemId = item.Id;
        f.Db.Updateable(task).ExecuteCommand();
        var row = new UserPlaylistPendingTrack { PlaylistId = Guid.NewGuid(), Title = "晴天", ArtistsJson = "[\"周杰伦\"]", SourcePlatform = "test", DownloadTaskId = task.Id, Status = "downloading" };
        f.Db.Insertable(row).ExecuteCommand();
        var service = Service(f, new ParsedPlaylist("test", null, null, []));
        await service.ResolvePendingAsync(row.PlaylistId, default);
        Assert.Equal(expected, f.Db.Queryable<UserPlaylistPendingTrack>().Single(x => x.Id == row.Id).Status);
        if (importStatus == "processing_failed")
        {
            item.Status = LibraryImportItemStatuses.PendingReview;
            f.Db.Updateable(item).ExecuteCommand();
            await service.ResolvePendingAsync(row.PlaylistId, default);
            Assert.Equal("needs_review", f.Db.Queryable<UserPlaylistPendingTrack>().Single(x => x.Id == row.Id).Status);
        }
    }

    [Fact]
    public async Task Import_failure_rolls_back_playlist_and_preceding_rows()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<UserPlaylist, UserPlaylistPendingTrack, UserPlaylistTrack>();
        // 第一条缺少艺人，不查询曲库；第二条查询未创建的Track表，模拟中途数据库异常。
        var parsed = new ParsedPlaylist("test", null, null,
            [new("第一首", [], null, null, "1", null), new("第二首", ["艺人"], null, null, "2", null)]);
        await Assert.ThrowsAnyAsync<Exception>(() => Service(f, parsed).ImportAsync("https://example.test/playlist", null, Guid.Empty, false, default));
        Assert.Equal(0, f.Db.Queryable<UserPlaylist>().Count());
        Assert.Equal(0, f.Db.Queryable<UserPlaylistPendingTrack>().Count());
    }

    [Fact]
    public async Task Import_preserves_all_unmatched_rows_and_source_order()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<UserPlaylist, UserPlaylistPendingTrack, UserPlaylistTrack>();
        var parsed = new ParsedPlaylist("test", null, null,
            Enumerable.Range(1, 3).Select(i => new ParsedPlaylistTrack("缺艺人曲目" + i, [], "专辑", 120000, i.ToString(), "https://example.test/" + i)).ToArray());
        var result = await Service(f, parsed).ImportAsync("https://example.test/playlist", null, Guid.Empty, false, default);
        var rows = f.Db.Queryable<UserPlaylistPendingTrack>().OrderBy(x => x.SortOrder).ToList();
        Assert.Equal(3, result.Total);
        var saved = f.Db.Queryable<UserPlaylist>().Single(p => p.Id == result.PlaylistId);
        using var snapshot = System.Text.Json.JsonDocument.Parse(saved.ImportSnapshotJson!);
        Assert.Equal(3, snapshot.RootElement.GetProperty("Tracks").GetArrayLength());
        Assert.Equal(result.Total, result.Matched + result.Pending);
        Assert.Equal(new[] { "1", "2", "3" }, rows.Select(x => x.SourceTrackId));
        Assert.All(rows, row => Assert.Equal("skipped", row.Status));
    }
}
