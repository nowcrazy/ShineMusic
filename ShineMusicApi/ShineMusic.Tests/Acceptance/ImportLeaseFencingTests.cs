using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using ShineMusic.Service.Media;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class ImportLeaseFencingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Private_completion_rejects_expired_or_replaced_attempt_before_creating_results(bool replaced)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        InitializeCatalog(f);
        var (item, attempt) = await ClaimedItemAsync(f);
        item.IsUserUpload = true;
        var newerAttempt = Invalidate(f, item, replaced);
        var workflow = new UserLibraryWorkflowService(f.Db, Paths(f), f.Storage,
            Options.Create(new LibraryWorkflowOptions()), NullLogger<UserLibraryWorkflowService>.Instance);

        await Assert.ThrowsAsync<MediaLeaseLostException>(() => workflow.CompleteUploadAsync(item,
            Guid.NewGuid(), Path.Combine(f.Root, item.SourceStorageKey), attempt, default));

        var current = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(newerAttempt, current.AttemptId);
        Assert.Equal(LibraryImportItemStatuses.Validating, current.Status);
        Assert.Null(current.PublishedTrackId);
        Assert.Null(current.OriginalStorageKey);
        Assert.Equal(0, f.Db.Queryable<Track>().Count());
        Assert.Equal(0, f.Db.Queryable<MediaAsset>().Count());
        Assert.Equal(0, f.Db.Queryable<LibraryWorkflowReview>().Count());
        Assert.True(File.Exists(Path.Combine(f.Root, item.SourceStorageKey)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Publication_final_transaction_rejects_stale_attempt_for_new_and_reused_tracks(bool reuse, bool replaced)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        InitializeCatalog(f);
        var (item, attempt) = await ClaimedItemAsync(f);
        item.Status = LibraryImportItemStatuses.ReadyToPublish;
        item.IsMetadataReviewed = true;
        await f.Db.Updateable(item).ExecuteCommandAsync();
        Guid? existingId = null;
        if (reuse)
        {
            var track = new Track { Title = item.Title!, DurationMs = item.DurationMs, Isrc = "lease-isrc",
                PublishedAt = DateTimeOffset.UtcNow };
            var artist = new Artist { Name = "Lease artist" };
            await f.Db.Insertable(track).ExecuteCommandAsync();
            await f.Db.Insertable(artist).ExecuteCommandAsync();
            await f.Db.Insertable(new TrackArtist { TrackId = track.Id, ArtistId = artist.Id }).ExecuteCommandAsync();
            await f.Db.Insertable(new MediaAsset { TrackId = track.Id, StorageKey = "existing.mp3", ContentHash = "existing",
                Codec = "mp3", Container = "mp3", BitrateKbps = 192, IsPublished = true, IsPrimary = true }).ExecuteCommandAsync();
            await File.WriteAllTextAsync(Path.Combine(f.Root, "existing.mp3"), "existing audio");
            item.Isrc = track.Isrc;
            await f.Db.Updateable(item).ExecuteCommandAsync();
            await f.Db.Insertable(new ExternalDownloadTask { SourceProvider = "lease", ProviderTrackId = "lease",
                ImportItemId = item.Id, ExpectedTitle = item.Title, ExpectedArtistsJson = item.ArtistsJson,
                ExpectedDurationMs = item.DurationMs }).ExecuteCommandAsync();
            existingId = track.Id;
        }
        var newerAttempt = Invalidate(f, item, replaced);

        await Assert.ThrowsAsync<MediaLeaseLostException>(() => Publisher(f).PublishAsync(item,
            new ImportPublishContext(Guid.Empty, "stale-publication", false, attempt), false, null, default));

        var current = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(newerAttempt, current.AttemptId);
        Assert.Equal(LibraryImportItemStatuses.ReadyToPublish, current.Status);
        Assert.Null(current.PublishedTrackId);
        Assert.Equal(reuse ? 1 : 0, f.Db.Queryable<Track>().Count());
        Assert.Equal(reuse ? 1 : 0, f.Db.Queryable<MediaAsset>().Count());
        Assert.Equal(0, f.Db.Queryable<AdminAuditEntry>().Count());
        Assert.True(File.Exists(Path.Combine(f.Root, item.SourceStorageKey)));
        if (existingId.HasValue)
        {
            Assert.True(f.Db.Queryable<MediaAsset>().Single(asset => asset.TrackId == existingId).IsPrimary);
            Assert.Equal("existing audio", await File.ReadAllTextAsync(Path.Combine(f.Root, "existing.mp3")));
        }
    }

    [Fact]
    public async Task Publication_preserves_heartbeats_newer_than_the_processing_snapshot()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        InitializeCatalog(f);
        var (item, attempt) = await ClaimedItemAsync(f);
        item.Status = LibraryImportItemStatuses.ReadyToPublish;
        item.IsMetadataReviewed = true;
        await f.Db.Updateable(item).ExecuteCommandAsync();
        var renewedUntil = DateTime.UtcNow.AddMinutes(20);
        var heartbeat = DateTime.UtcNow.AddSeconds(1);
        await f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.LeaseUntilUtc == renewedUntil)
            .SetColumns(row => row.LastHeartbeatAtUtc == heartbeat).Where(row => row.Id == item.Id).ExecuteCommandAsync();

        var published = await Publisher(f).PublishAsync(item,
            new ImportPublishContext(Guid.Empty, "owned-publication", false, attempt), false, null, default);

        Assert.True(published.Succeeded, published.Error?.Message);
        var current = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(LibraryImportItemStatuses.Published, current.Status);
        Assert.Equal(attempt, current.AttemptId);
        Assert.Equal(item.WorkerId, current.WorkerId);
        Assert.True(current.LeaseUntilUtc >= renewedUntil.AddSeconds(-1));
        Assert.True(current.LastHeartbeatAtUtc >= heartbeat.AddSeconds(-1));
        Assert.Equal(1, f.Db.Queryable<Track>().Count());
        Assert.Equal(1, f.Db.Queryable<MediaAsset>().Count());
        Assert.False(File.Exists(Path.Combine(f.Root, item.SourceStorageKey)));
    }

    [Fact]
    public async Task Private_completion_recreates_original_if_cleanup_removed_it_while_waiting_for_mutation_lock()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        InitializeCatalog(f);
        var (item, attempt) = await ClaimedItemAsync(f);
        item.IsUserUpload = true;
        var source = Path.Combine(f.Root, item.SourceStorageKey);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))).ToLowerInvariant();
        var original = Path.Combine(f.Root, "original", hash + ".mp3");
        var removed = false;
        f.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (removed || !sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("library_import_items", StringComparison.OrdinalIgnoreCase)) return;
            Assert.True(File.Exists(original));
            File.Delete(original);
            removed = true;
        };
        var workflow = new UserLibraryWorkflowService(f.Db, Paths(f), f.Storage,
            Options.Create(new LibraryWorkflowOptions()), NullLogger<UserLibraryWorkflowService>.Instance);

        await workflow.CompleteUploadAsync(item, Guid.NewGuid(), source, attempt, default);

        Assert.True(removed);
        Assert.Equal(LibraryImportItemStatuses.Published, f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id).Status);
        var asset = Assert.Single(f.Db.Queryable<MediaAsset>().ToList());
        Assert.True(File.Exists(Path.Combine(f.Root, asset.StorageKey)));
        Assert.Equal(hash, asset.ContentHash);
        Assert.Equal(1, f.Db.Queryable<Track>().Count());
        Assert.Equal(1, f.Db.Queryable<UserLibraryItem>().Count());
    }

    [Fact]
    public async Task Private_completion_rolls_back_track_asset_and_references_if_fenced_final_save_fails()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        InitializeCatalog(f);
        var (item, attempt) = await ClaimedItemAsync(f);
        item.IsUserUpload = true;
        f.Db.Ado.ExecuteCommand("CREATE TRIGGER reject_import_completion BEFORE UPDATE ON library_import_items WHEN NEW.status = 'published' BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        var workflow = new UserLibraryWorkflowService(f.Db, Paths(f), f.Storage,
            Options.Create(new LibraryWorkflowOptions()), NullLogger<UserLibraryWorkflowService>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => workflow.CompleteUploadAsync(item, Guid.NewGuid(),
            Path.Combine(f.Root, item.SourceStorageKey), attempt, default));

        Assert.Equal(LibraryImportItemStatuses.Validating, f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id).Status);
        Assert.Equal(0, f.Db.Queryable<Track>().Count());
        Assert.Equal(0, f.Db.Queryable<MediaAsset>().Count());
        Assert.Equal(0, f.Db.Queryable<UserLibraryItem>().Count());
        Assert.True(File.Exists(Path.Combine(f.Root, item.SourceStorageKey)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleted_import_cannot_be_restored_by_late_private_completion_or_publication(bool publication)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        InitializeCatalog(f);
        var (item, attempt) = await ClaimedItemAsync(f);
        item.Status = publication ? LibraryImportItemStatuses.ReadyToPublish : LibraryImportItemStatuses.ParsingMetadata;
        var expectedStatus = item.Status;
        item.IsMetadataReviewed = publication;
        await f.Db.Updateable(item).ExecuteCommandAsync();
        await f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.IsDeleted == true)
            .Where(row => row.Id == item.Id).ExecuteCommandAsync();
        if (publication)
            await Assert.ThrowsAsync<MediaLeaseLostException>(() => Publisher(f).PublishAsync(item,
                new ImportPublishContext(Guid.Empty, "deleted-publication", false, attempt), false, null, default));
        else
        {
            var workflow = new UserLibraryWorkflowService(f.Db, Paths(f), f.Storage,
                Options.Create(new LibraryWorkflowOptions()), NullLogger<UserLibraryWorkflowService>.Instance);
            await Assert.ThrowsAsync<MediaLeaseLostException>(() => workflow.CompleteUploadAsync(item,
                Guid.NewGuid(), Path.Combine(f.Root, item.SourceStorageKey), attempt, default));
        }
        var deleted = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(expectedStatus, deleted.Status);
        Assert.Equal(0, f.Db.Queryable<Track>().Count());
        Assert.Equal(0, f.Db.Queryable<MediaAsset>().Count());
        Assert.True(File.Exists(Path.Combine(f.Root, item.SourceStorageKey)));
    }

    [Fact]
    public async Task Cancelled_auto_publication_keeps_expiring_owner_so_media_recovery_can_find_it()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        InitializeCatalog(f);
        var (item, attempt) = await ClaimedItemAsync(f);
        await f.Lease().ReleaseImportAsync(item.Id, attempt, default);
        var old = DateTime.UtcNow.AddMinutes(-10);
        await f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.Status == LibraryImportItemStatuses.PendingReview)
            .SetColumns(row => row.IsMetadataReviewed == false).SetColumns(row => row.AutoScore == 95)
            .SetColumns(row => row.UpdatedAtUtc == old).Where(row => row.Id == item.Id).ExecuteCommandAsync();
        using var cancellation = new CancellationTokenSource();
        f.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("external_download_tasks", StringComparison.OrdinalIgnoreCase)) cancellation.Cancel();
        };
        var sweep = new LibraryImportRetrySweepJob(f.Db, f.Bus, Publisher(f), Options.Create(f.Options),
            Options.Create(new LibraryImportOptions { AutoPublishEnabled = true, AutoPublishThreshold = 90 }),
            NullLogger<LibraryImportRetrySweepJob>.Instance,
            new StorageGovernanceService(Paths(f), NullLogger<StorageGovernanceService>.Instance), f.Lease());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep.SweepAsync(cancellation.Token));
        f.Db.Aop.OnLogExecuting = null;

        var interrupted = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(LibraryImportItemStatuses.ReadyToPublish, interrupted.Status);
        Assert.NotNull(interrupted.WorkerId);
        Assert.NotNull(interrupted.LeaseUntilUtc);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.LeaseUntilUtc == expired)
            .Where(row => row.Id == item.Id).ExecuteCommandAsync(CancellationToken.None);
        var recovery = new MediaQueueRecoveryJob(f.Db, f.Bus, Options.Create(new MediaLeaseOptions()),
            NullLogger<MediaQueueRecoveryJob>.Instance);
        await recovery.RecoverAsync(false, default);

        var recovered = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(LibraryImportItemStatuses.Uploaded, recovered.Status);
        Assert.Null(recovered.WorkerId);
        Assert.Null(recovered.AttemptId);
        Assert.Contains(f.Bus.Messages, message => message.Id == item.Id);
    }

    [Theory]
    [InlineData(LibraryImportItemStatuses.Validating)]
    [InlineData(LibraryImportItemStatuses.ParsingMetadata)]
    [InlineData(LibraryImportItemStatuses.Transcoding)]
    public async Task Retry_sweep_does_not_reset_long_running_intermediate_stage(string status)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        InitializeCatalog(f);
        var (item, _) = await ClaimedItemAsync(f);
        var old = DateTime.UtcNow.AddHours(-1);
        await f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.Status == status)
            .SetColumns(row => row.UpdatedAtUtc == old).Where(row => row.Id == item.Id).ExecuteCommandAsync();
        var sweep = new LibraryImportRetrySweepJob(f.Db, f.Bus, Publisher(f), Options.Create(f.Options),
            Options.Create(new LibraryImportOptions()), NullLogger<LibraryImportRetrySweepJob>.Instance,
            new StorageGovernanceService(Paths(f), NullLogger<StorageGovernanceService>.Instance), f.Lease());

        await sweep.SweepAsync(default);

        var current = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id);
        Assert.Equal(status, current.Status);
        Assert.Equal(item.AttemptId, current.AttemptId);
        Assert.DoesNotContain(f.Bus.Messages, message => message.Id == item.Id);
    }

    private static Guid? Invalidate(SourceDownloadAcceptanceFixture f, LibraryImportItem item, bool replaced)
    {
        if (!replaced)
        {
            var expired = DateTime.UtcNow.AddMinutes(-1);
            f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.LeaseUntilUtc == expired)
                .Where(row => row.Id == item.Id).ExecuteCommand();
            return item.AttemptId;
        }
        var nextAttempt = Guid.NewGuid();
        f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.AttemptId == nextAttempt)
            .SetColumns(row => row.WorkerId == "new-worker").Where(row => row.Id == item.Id).ExecuteCommand();
        return nextAttempt;
    }

    private static async Task<(LibraryImportItem Item, Guid Attempt)> ClaimedItemAsync(SourceDownloadAcceptanceFixture f)
    {
        var batch = new LibraryImportBatch { CreatedByUserId = Guid.NewGuid() };
        await f.Db.Insertable(batch).ExecuteCommandAsync();
        var item = new LibraryImportItem { BatchId = batch.Id, DisplayName = "lease.mp3", SourceStorageKey = "uploads/lease.mp3",
            Title = "Lease song", ArtistsJson = "[\"Lease artist\"]", DurationMs = 200000, ContentHash = "lease-hash",
            Codec = "mp3", Container = "mp3", BitrateKbps = 192, TranscodedStorageKey = "uploads/lease.mp3",
            TranscodeStatus = "succeeded", TranscodeBitrateKbps = 192 };
        await f.Db.Insertable(item).ExecuteCommandAsync();
        Directory.CreateDirectory(Path.Combine(f.Root, "uploads"));
        var bytes = new byte[4096];
        Encoding.ASCII.GetBytes("ID3").CopyTo(bytes, 0);
        await File.WriteAllBytesAsync(Path.Combine(f.Root, item.SourceStorageKey), bytes);
        var claim = await f.Lease().TryClaimImportAsync(item.Id, default);
        Assert.True(claim.Acquired);
        return (f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == item.Id), claim.AttemptId);
    }

    private static LibraryStoragePaths Paths(SourceDownloadAcceptanceFixture f) => new(
        Options.Create(new StorageOptions { LibraryRoot = f.Root }), new TestEnvironment { ContentRootPath = f.Root });

    private static ImportPublishService Publisher(SourceDownloadAcceptanceFixture f) => new(f.Db,
        Options.Create(new SongRequestFulfillmentOptions()), f.Storage, Paths(f),
        NullLogger<ImportPublishService>.Instance, null!, new ConfigurationBuilder().Build());

    private static void InitializeCatalog(SourceDownloadAcceptanceFixture f) => f.Db.CodeFirst.InitTables(
        typeof(Track), typeof(Artwork), typeof(Artist), typeof(TrackArtist), typeof(Album), typeof(AlbumArtist),
        typeof(Genre), typeof(TrackGenre), typeof(Lyric), typeof(AdminAuditEntry), typeof(UserLibraryItem), typeof(LibraryWorkflowReview));
}
