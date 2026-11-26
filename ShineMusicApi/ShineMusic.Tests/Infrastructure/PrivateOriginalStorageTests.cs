using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using ShineMusic.Tests.Acceptance;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

public sealed class PrivateOriginalStorageTests
{
    [Fact]
    public async Task Usage_counts_distinct_retained_originals_per_owner_including_recycle_retired_and_reviews()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        var shared = f.Asset(owner, [1, 2, 3]);
        f.Review(owner, shared.StorageKey, shared.ContentHash);
        f.Asset(owner, [4, 5, 6, 7], recycled: true);
        f.Asset(owner, [8, 9], retired: true);
        f.Review(owner, f.File("review.flac", [10, 11, 12, 13, 14]), Hash([10, 11, 12, 13, 14]));
        f.Asset(Guid.NewGuid(), [15, 16, 17]);
        f.Asset(owner, [18, 19, 20], published: true);

        var result = await f.Workflow(100).GetPrivateOriginalStorageAsync(owner, default);

        Assert.Equal(14, result.UsedBytes);
        Assert.Equal(4, result.OriginalCount);
        Assert.Equal(86, result.RemainingBytes);
    }

    [Fact]
    public async Task Legacy_hashes_are_measured_from_actual_files_and_same_path_is_not_charged_twice()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        var first = f.Asset(owner, [1, 2, 3], hash: "legacy");
        f.Asset(owner, [4, 5, 6, 7], hash: "legacy");
        f.Review(owner, first.StorageKey, "unknown");

        var result = await f.Workflow(0).GetPrivateOriginalStorageAsync(owner, default);

        Assert.Equal(7, result.UsedBytes);
        Assert.Equal(2, result.OriginalCount);
        Assert.Null(result.RemainingBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_unsafe_historical_files_do_not_silently_become_zero_usage(bool unsafeKey)
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        var asset = f.Asset(owner, [1, 2, 3]);
        if (unsafeKey) { asset.StorageKey = "../outside.flac"; f.Db.Updateable(asset).ExecuteCommand(); }
        else System.IO.File.Delete(Path.Combine(f.Root, asset.StorageKey));

        var error = await Assert.ThrowsAsync<MediaProcessingException>(() => f.Workflow(10).GetPrivateOriginalStorageAsync(owner, default));

        Assert.Equal(ErrorCodes.PrivateStorageUsageUnavailable, error.Code);
        Assert.DoesNotContain(f.Root, error.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<MediaProcessingException>(() =>
            PrivateOriginalStorageMeter.EnsureCapacityAsync(f.Db, f.Paths, owner, Hash([8]), 1, 10, default));
    }

    [Fact]
    public async Task Shared_disk_content_is_still_charged_to_each_owner_but_own_duplicate_is_free_after_limit_reduction()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var asset = f.Asset(owner, [1, 2, 3, 4]);

        await PrivateOriginalStorageMeter.EnsureCapacityAsync(f.Db, f.Paths, owner, asset.ContentHash, 4, 1, default);
        var error = await Assert.ThrowsAsync<MediaProcessingException>(() =>
            PrivateOriginalStorageMeter.EnsureCapacityAsync(f.Db, f.Paths, other, asset.ContentHash, 4, 3, default));

        Assert.Equal(ErrorCodes.PrivateStorageQuotaExceeded, error.Code);
        await PrivateOriginalStorageMeter.EnsureCapacityAsync(f.Db, f.Paths, other, asset.ContentHash, 4, 4, default);
    }

    [Fact]
    public async Task Exact_boundary_is_allowed_and_one_byte_more_is_rejected()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        f.Asset(owner, [1, 2, 3]);
        await PrivateOriginalStorageMeter.EnsureCapacityAsync(f.Db, f.Paths, owner, Hash([9]), 5, 8, default);
        var error = await Assert.ThrowsAsync<MediaProcessingException>(() =>
            PrivateOriginalStorageMeter.EnsureCapacityAsync(f.Db, f.Paths, owner, Hash([9]), 6, 8, default));
        Assert.Equal(ErrorCodes.PrivateStorageQuotaExceeded, error.Code);
    }

    [Fact]
    public async Task A_retained_hash_with_a_conflicting_disk_size_cannot_make_a_new_allocation_free()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        var asset = f.Asset(owner, [1, 2, 3]);

        var error = await Assert.ThrowsAsync<MediaProcessingException>(() =>
            PrivateOriginalStorageMeter.EnsureCapacityAsync(f.Db, f.Paths, owner, asset.ContentHash, 4, 3, default));

        Assert.Equal(ErrorCodes.PrivateStorageUsageUnavailable, error.Code);
    }

    [Fact]
    public async Task Failed_final_save_restores_in_memory_completion_fields_before_the_caller_saves_failure()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        var upload = f.Item([1, 2, 3, 4], "rollback");
        upload.Item.TranscodeStatus = "queued";
        // Fail the actual final write without depending on SqlSugar's log formatting.
        // The execution guard only updates the timestamp, so it does not trip this trigger.
        f.Db.Ado.ExecuteCommand("""
            CREATE TRIGGER fail_upload_final_save BEFORE UPDATE ON library_import_items
            WHEN NEW.status = 'published'
            BEGIN SELECT RAISE(ABORT, 'Synthetic final import save failure.'); END;
            """);
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => f.Workflow(5).CompleteUploadAsync(
                upload.Item, owner, upload.Path, upload.Item.AttemptId!.Value, default));
            Assert.Contains("Synthetic final import save failure.", failure.ToString(), StringComparison.Ordinal);
        }
        finally { f.Db.Ado.ExecuteCommand("DROP TRIGGER fail_upload_final_save"); }

        Assert.Null(upload.Item.PublishedTrackId);
        Assert.Null(upload.Item.OriginalStorageKey);
        Assert.Equal(LibraryImportItemStatuses.ParsingMetadata, upload.Item.Status);
        Assert.Equal("queued", upload.Item.TranscodeStatus);
        Assert.Equal(0, f.Db.Queryable<Track>().Count());
        Assert.Equal(0, f.Db.Queryable<MediaAsset>().Count());
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "original")));
        // LibraryImportJob saves this same object when recording the failed attempt.
        upload.Item.Status = LibraryImportItemStatuses.ProcessingFailed;
        f.Db.Updateable(upload.Item).ExecuteCommand();
        var failed = f.Db.Queryable<LibraryImportItem>().Single(item => item.Id == upload.Item.Id);
        Assert.Null(failed.PublishedTrackId);
        Assert.Null(failed.OriginalStorageKey);
        Assert.Equal("queued", failed.TranscodeStatus);
    }

    [Fact]
    public async Task Concurrent_finalizations_only_commit_one_allocation_and_remove_rejected_unreferenced_copy()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        var first = f.Item([1, 2, 3, 4], "first");
        var second = f.Item([5, 6, 7, 8], "second");
        using var otherConnection = new SqlSugarClient(f.Db.CurrentConnectionConfig);
        var worker1 = f.Workflow(5);
        var worker2 = f.Workflow(5, otherConnection);
        static async Task<string> Complete(UserLibraryWorkflowService worker, (LibraryImportItem Item, string Path) upload, Guid user)
        {
            try { await worker.CompleteUploadAsync(upload.Item, user, upload.Path, upload.Item.AttemptId!.Value, default); return "passed"; }
            catch (MediaProcessingException error) { return error.Code; }
        }

        var outcomes = await Task.WhenAll(Complete(worker1, first, owner), Complete(worker2, second, owner));

        Assert.Equal(1, outcomes.Count(result => result == "passed"));
        Assert.Equal(1, outcomes.Count(result => result == ErrorCodes.PrivateStorageQuotaExceeded));
        Assert.Equal(1, f.Db.Queryable<Track>().Count());
        Assert.Equal(1, f.Db.Queryable<MediaAsset>().Count());
        Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "original")));
        Assert.Equal(4, (await worker1.GetPrivateOriginalStorageAsync(owner, default)).UsedBytes);
        var rejected = first.Item.OriginalStorageKey is null ? first : second;
        Assert.Null(rejected.Item.OriginalStorageKey);
        Assert.Null(f.Db.Queryable<LibraryImportItem>().Single(item => item.Id == rejected.Item.Id).PublishedTrackId);
    }

    [Fact]
    public async Task Default_unlimited_does_not_block_new_upload_when_an_old_original_needs_repair()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid();
        var broken = f.Asset(owner, [1]);
        System.IO.File.Delete(Path.Combine(f.Root, broken.StorageKey));
        var upload = f.Item([2, 3, 4], "new");

        await f.Workflow(0).CompleteUploadAsync(upload.Item, owner, upload.Path, upload.Item.AttemptId!.Value, default);

        Assert.NotNull(upload.Item.PublishedTrackId);
        Assert.Equal(LibraryImportItemStatuses.Published, upload.Item.Status);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("shine-private-quota-").FullName;
        public SqlSugarClient Db { get; }
        public LibraryStoragePaths Paths { get; }
        private readonly QuarantineStoragePaths quarantine;
        public Fixture()
        {
            Db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={Path.Combine(Root, "quota.db")};Pooling=False", DbType = DbType.Sqlite,
                IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute,
                ConfigureExternalServices = new ConfigureExternalServices
                {
                    EntityNameService = (_, entity) => entity.DbTableName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                    EntityService = (property, column) => column.DbColumnName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
                }
            });
            Db.CodeFirst.InitTables(typeof(Track), typeof(MediaAsset), typeof(Artist), typeof(Album), typeof(Genre),
                typeof(TrackArtist), typeof(TrackGenre), typeof(UserLibraryItem), typeof(LibraryWorkflowReview),
                typeof(LibraryImportBatch), typeof(LibraryImportItem), typeof(UserNotification));
            var configuration = Options.Create(new StorageOptions { LibraryRoot = Root, QuarantineRoot = Path.Combine(Root, "quarantine") });
            var environment = new TestEnvironment { ContentRootPath = Root };
            Paths = new(configuration, environment);
            quarantine = new(configuration, environment);
        }
        public UserLibraryWorkflowService Workflow(long limit, ISqlSugarClient? connection = null) => new(connection ?? Db, Paths, quarantine,
            Options.Create(new LibraryWorkflowOptions { MaxPrivateOriginalBytesPerUser = limit }), NullLogger<UserLibraryWorkflowService>.Instance);
        public string File(string key, byte[] bytes)
        {
            var path = Path.Combine(Root, key); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, bytes); return key;
        }
        public MediaAsset Asset(Guid owner, byte[] bytes, bool recycled = false, bool retired = false, bool published = false, string? hash = null)
        {
            var track = new Track { Title = Guid.NewGuid().ToString(), OwnerUserId = owner,
                IsRecycled = recycled, PublishedAt = published ? DateTimeOffset.UtcNow : null };
            Db.Insertable(track).ExecuteCommand();
            var asset = new MediaAsset { TrackId = track.Id, StorageKey = File($"original/{Guid.NewGuid():N}.flac", bytes),
                Container = "flac", Codec = "flac", ContentHash = hash ?? Hash(bytes), IsOriginal = true,
                IsPublished = !retired, IsPrimary = !retired, RetainUntilUtc = retired ? DateTime.UtcNow : null };
            Db.Insertable(asset).ExecuteCommand(); return asset;
        }
        public void Review(Guid owner, string key, string hash) => Db.Insertable(new LibraryWorkflowReview
        { OwnerUserId = owner, Kind = "public", OriginalStorageKey = key, ContentHash = hash, ExpiresAtUtc = DateTime.UtcNow.AddDays(1) }).ExecuteCommand();
        public (LibraryImportItem Item, string Path) Item(byte[] bytes, string title)
        {
            var batch = new LibraryImportBatch { CreatedByUserId = Guid.NewGuid() }; Db.Insertable(batch).ExecuteCommand();
            var source = Path.Combine(Root, File($"quarantine/{Guid.NewGuid():N}.flac", bytes));
            var item = new LibraryImportItem { BatchId = batch.Id, DisplayName = title + ".flac", Title = title,
                SourceStorageKey = "unused", ContentHash = "untrusted", SizeBytes = 1, Container = "flac", Codec = "flac",
                IsUserUpload = true, Status = LibraryImportItemStatuses.ParsingMetadata, WorkerId = "quota-worker", AttemptId = Guid.NewGuid(),
                LeaseUntilUtc = DateTime.UtcNow.AddMinutes(5) };
            Db.Insertable(item).ExecuteCommand(); return (item, source);
        }
        public void Dispose() { Db.Dispose(); Directory.Delete(Root, true); }
    }
}
