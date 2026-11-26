using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Tests.Acceptance;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

[Collection("Source download database acceptance")]
public sealed class MediaLeaseAdminMutationTests
{
    [Theory]
    [InlineData("update")]
    [InlineData("enrich")]
    [InlineData("lyrics")]
    [InlineData("unlink")]
    [InlineData("delete")]
    [InlineData("dismiss")]
    public async Task Administrator_cannot_mutate_an_import_while_its_lease_is_active(string action)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var item = SeedImport(f);
        item.Status = action == "dismiss" ? LibraryImportItemStatuses.Published : LibraryImportItemStatuses.ReadyToPublish;
        item.WorkerId = "active-worker";
        item.AttemptId = Guid.NewGuid();
        item.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
        f.Db.Updateable(item).ExecuteCommand();
        var controller = Controller();
        var fulfillment = Options.Create(new SongRequestFulfillmentOptions());

        var response = action switch
        {
            "update" => await controller.UpdateItem(item.Id, new("changed", null, null, null, null, null, null, null, null), f.Db, fulfillment, default),
            "enrich" => await controller.EnrichItem(item.Id, f.Db, null!, null!, f.Storage, fulfillment, default),
            "lyrics" => await controller.UploadLyricsFile(item.Id, f.Db, fulfillment, default),
            "unlink" => await controller.UnlinkSongRequest(item.Id, f.Db, fulfillment, default),
            "delete" => await controller.DeleteItem(item.Id, f.Db, null!, default),
            _ => await controller.DismissItem(item.Id, f.Db, default)
        };

        Assert.Equal(409, Status(response));
        var unchanged = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(item.Status, unchanged.Status);
        Assert.Equal(item.AttemptId, unchanged.AttemptId);
        Assert.Equal(item.WorkerId, unchanged.WorkerId);
        Assert.False(unchanged.IsDeleted);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Bulk_review_leaves_an_active_worker_item_untouched(string action)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var item = SeedImport(f);
        item.WorkerId = "active-worker";
        item.AttemptId = Guid.NewGuid();
        item.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
        f.Db.Updateable(item).ExecuteCommand();

        var response = await Controller().BulkReviewItems(new([item.Id], action, "reason"), f.Db, null!, default);

        var result = Assert.IsType<BulkReviewResultDto>(Assert.IsAssignableFrom<IValueHttpResult>(response).Value);
        Assert.Equal(0, result.Success);
        Assert.False(Assert.Single(result.Results).Success);
        Assert.Equal(item.AttemptId, f.Db.Queryable<LibraryImportItem>().Single().AttemptId);
        Assert.Equal(item.Status, f.Db.Queryable<LibraryImportItem>().Single().Status);
    }

    [Fact]
    public async Task Slow_enrichment_cannot_restore_its_snapshot_after_worker_publication()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var item = SeedImport(f);
        var publishedTrackId = Guid.NewGuid();
        var newAttempt = Guid.NewGuid();
        var resolver = new CallbackLyricsResolver(() =>
        {
            f.Db.Updateable<LibraryImportItem>()
                .SetColumns(row => row.Status == LibraryImportItemStatuses.Published)
                .SetColumns(row => row.PublishedTrackId == publishedTrackId)
                .SetColumns(row => row.AttemptId == newAttempt)
                .SetColumns(row => row.WorkerId == "new-worker")
                .Where(row => row.Id == item.Id).ExecuteCommand();
        });
        var enricher = new ImportMetadataEnricher(null!, null!, NullLogger<ImportMetadataEnricher>.Instance, null!);

        var response = await Controller().EnrichItem(item.Id, f.Db, resolver, enricher, f.Storage,
            Options.Create(new SongRequestFulfillmentOptions()), default);

        Assert.Equal(409, Status(response));
        var published = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(LibraryImportItemStatuses.Published, published.Status);
        Assert.Equal(publishedTrackId, published.PublishedTrackId);
        Assert.Equal(newAttempt, published.AttemptId);
        Assert.Equal("new-worker", published.WorkerId);
        Assert.Null(published.Lyrics);
    }

    [Fact]
    public async Task Unowned_item_remains_editable_and_revokes_previous_execution_metadata()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<AdminAuditEntry>();
        var item = SeedImport(f);
        item.AttemptId = Guid.NewGuid();
        f.Db.Updateable(item).ExecuteCommand();

        var response = await Controller().UpdateItem(item.Id,
            new("reviewed title", null, null, null, null, null, null, null, null), f.Db,
            Options.Create(new SongRequestFulfillmentOptions()), default);

        Assert.Equal(200, Status(response));
        var saved = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal("reviewed title", saved.Title);
        Assert.Null(saved.AttemptId);
        Assert.Null(saved.WorkerId);
        Assert.Null(saved.LeaseUntilUtc);
        Assert.Single(f.Db.Queryable<AdminAuditEntry>().ToList());
    }

    [Fact]
    public async Task Administrator_edit_revokes_expired_attempt_and_its_old_heartbeat()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Db.CodeFirst.InitTables<AdminAuditEntry>();
        var item = SeedImport(f);
        item.Status = LibraryImportItemStatuses.Uploaded;
        f.Db.Updateable(item).ExecuteCommand();
        var oldLease = f.Lease();
        var claim = await oldLease.TryClaimImportAsync(item.Id, default);
        Assert.True(claim.Acquired);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        f.Db.Updateable<LibraryImportItem>()
            .SetColumns(row => row.Status == LibraryImportItemStatuses.PendingReview)
            .SetColumns(row => row.LeaseUntilUtc == expired)
            .Where(row => row.Id == item.Id).ExecuteCommand();

        var response = await Controller().UpdateItem(item.Id,
            new("reviewed after crash", null, null, null, null, null, null, null, null), f.Db,
            Options.Create(new SongRequestFulfillmentOptions()), default);

        Assert.Equal(200, Status(response));
        var saved = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Null(saved.AttemptId);
        Assert.Null(saved.WorkerId);
        Assert.Null(saved.LeaseUntilUtc);
        Assert.Null(saved.LastHeartbeatAtUtc);
        Assert.False(await oldLease.RenewImportAsync(item.Id, claim.AttemptId, default));
    }

    [Fact]
    public async Task Stale_discard_does_not_delete_files_after_a_concurrent_publication()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var item = SeedImport(f);
        var source = Path.Combine(f.Root, item.SourceStorageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "source still belongs to the published item");
        var publishedTrack = Guid.NewGuid();
        var injected = false;
        f.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (injected || !sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("library_import_items", StringComparison.OrdinalIgnoreCase)) return;
            injected = true;
            using var publisher = new SqlSugarClient(f.Db.CurrentConnectionConfig);
            publisher.Updateable<LibraryImportItem>()
                .SetColumns(row => row.Status == LibraryImportItemStatuses.Published)
                .SetColumns(row => row.PublishedTrackId == publishedTrack)
                .Where(row => row.Id == item.Id).ExecuteCommand();
        };

        var response = await Controller().DeleteItem(item.Id, f.Db, f.Storage, default);

        Assert.Equal(409, Status(response));
        Assert.True(File.Exists(source));
        Assert.Equal(LibraryImportItemStatuses.Published, f.Db.Queryable<LibraryImportItem>().Single().Status);
        Assert.Equal(publishedTrack, f.Db.Queryable<LibraryImportItem>().Single().PublishedTrackId);
    }

    [Fact]
    public async Task Task_creation_reset_cannot_overwrite_a_concurrent_worker_claim()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = f.Seed(ExternalDownloadTaskStatuses.Failed);
        var newAttempt = ClaimOnNextUpdate(f, task.Id);
        var service = new SourceDownloadService(f.Db, f.Legacy, f.Bus, f.Access());

        var result = await service.CreateTasksAsync(new CreateSourceDownloadTasksRequest("qq", [task.ProviderTrackId]),
            Guid.NewGuid(), default);

        Assert.Empty(result.Created);
        Assert.Equal(new[] { task.ProviderTrackId }, result.SkippedDuplicates);
        Assert.Equal(newAttempt, f.Reload(task.Id).AttemptId);
        Assert.Equal(ExternalDownloadTaskStatuses.Downloading, f.Reload(task.Id).Status);
        Assert.Empty(f.Bus.Messages);
    }

    [Fact]
    public async Task Task_creation_reset_revokes_the_expired_attempt_and_heartbeat()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = f.Seed();
        var oldLease = f.Lease();
        var claim = await oldLease.TryClaimExternalDownloadAsync(task.Id, default);
        Assert.True(claim.Acquired);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        f.Db.Updateable<ExternalDownloadTask>()
            .SetColumns(row => row.Status == ExternalDownloadTaskStatuses.Failed)
            .SetColumns(row => row.LeaseUntilUtc == expired)
            .Where(row => row.Id == task.Id).ExecuteCommand();
        var service = new SourceDownloadService(f.Db, f.Legacy, f.Bus, f.Access());

        var result = await service.CreateTasksAsync(new CreateSourceDownloadTasksRequest("qq", [task.ProviderTrackId]),
            Guid.NewGuid(), default);

        Assert.Single(result.Created);
        var reset = f.Reload(task.Id);
        Assert.Equal(ExternalDownloadTaskStatuses.Queued, reset.Status);
        Assert.Null(reset.WorkerId);
        Assert.Null(reset.AttemptId);
        Assert.Null(reset.LeaseUntilUtc);
        Assert.Null(reset.LastHeartbeatAtUtc);
        Assert.False(await oldLease.RenewExternalDownloadAsync(task.Id, claim.AttemptId, default));
    }

    [Fact]
    public async Task Manual_retry_cannot_reset_a_concurrent_worker_claim()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = f.Seed(ExternalDownloadTaskStatuses.Failed);
        var newAttempt = ClaimOnNextUpdate(f, task.Id);
        var controller = new SourceDownloadController { ControllerContext = Context() };

        var response = await controller.RetryTask(task.Id, f.Db, f.Bus, default);

        Assert.Equal(409, Status(response));
        Assert.Equal(newAttempt, f.Reload(task.Id).AttemptId);
        Assert.Equal(ExternalDownloadTaskStatuses.Downloading, f.Reload(task.Id).Status);
        Assert.Empty(f.Bus.Messages);
    }

    private static Guid ClaimOnNextUpdate(SourceDownloadAcceptanceFixture f, Guid taskId)
    {
        var attempt = Guid.NewGuid();
        var injected = false;
        f.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (injected || !sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("external_download_tasks", StringComparison.OrdinalIgnoreCase)) return;
            injected = true;
            using var otherWorker = new SqlSugarClient(f.Db.CurrentConnectionConfig);
            var leaseUntil = DateTime.UtcNow.AddMinutes(1);
            otherWorker.Updateable<ExternalDownloadTask>()
                .SetColumns(row => row.Status == ExternalDownloadTaskStatuses.Downloading)
                .SetColumns(row => row.WorkerId == "replacement-worker")
                .SetColumns(row => row.AttemptId == attempt)
                .SetColumns(row => row.LeaseUntilUtc == leaseUntil)
                .Where(row => row.Id == taskId).ExecuteCommand();
        };
        return attempt;
    }

    private static LibraryImportItem SeedImport(SourceDownloadAcceptanceFixture f)
    {
        var item = new LibraryImportItem
        {
            BatchId = Guid.NewGuid(), Status = LibraryImportItemStatuses.PendingReview,
            DisplayName = "sample.flac", SourceStorageKey = "uploads/sample.flac", ContentHash = Guid.NewGuid().ToString("N"),
            Title = "sample", ArtistsJson = "[\"artist\"]", AlbumTitle = "album", ArtworkStorageKey = "artwork/existing.jpg",
            ReleaseDate = "2026-01-01", ReleaseYear = 2026, TrackNumber = 1, DiscNumber = 1, OriginalGenresJson = "[\"pop\"]"
        };
        f.Db.Insertable(item).ExecuteCommand();
        return item;
    }

    private static LibraryImportController Controller() => new() { ControllerContext = Context() };
    private static ControllerContext Context() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"))
        }
    };
    private static int? Status(IResult response) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode;

    private sealed class CallbackLyricsResolver(Action callback) : ILyricsResolver
    {
        public Task<LyricsMatch?> ResolveAsync(LyricsQuery query, CancellationToken cancellationToken)
        {
            callback();
            return Task.FromResult<LyricsMatch?>(new LyricsMatch("new lyrics", "test", 1, null));
        }
    }
}
