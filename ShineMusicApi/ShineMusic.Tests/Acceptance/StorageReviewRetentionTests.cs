using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class StorageReviewRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_original_survives_while_a_pending_review_retains_its_snapshot(bool expiryPassed)
    {
        using var f = new Fixture();
        var track = f.Track();
        var old = f.Asset(track.Id, retired: true);
        var current = f.Asset(track.Id);
        f.Review(old.StorageKey, expiryPassed: expiryPassed);

        await f.Service.CleanupRetiredAsync(f.Db, default);

        Assert.True(f.Exists(old.StorageKey));
        Assert.True(f.Db.Queryable<MediaAsset>().Any(asset => asset.Id == old.Id));
        Assert.True(f.Exists(current.StorageKey));
        Assert.True(f.Db.Queryable<MediaAsset>().Single(asset => asset.Id == current.Id).IsPrimary);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("expired")]
    public async Task A_resolved_review_releases_an_otherwise_unreferenced_retired_original(string status)
    {
        using var f = new Fixture();
        var original = f.Asset(f.Track().Id, retired: true);
        var review = f.Review(original.StorageKey);
        await f.Service.CleanupRetiredAsync(f.Db, default);
        Assert.True(f.Exists(original.StorageKey));
        review.Status = status;
        f.Db.Updateable(review).ExecuteCommand();

        await f.Service.CleanupRetiredAsync(f.Db, default);

        Assert.False(f.Exists(original.StorageKey));
        Assert.False(f.Db.Queryable<MediaAsset>().Any(asset => asset.Id == original.Id));
        Assert.True(f.Db.Queryable<LibraryWorkflowReview>().Any(row => row.Id == review.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_only_original_is_referenced_in_audit_and_preserved_by_cleanup(bool expiryPassed)
    {
        using var f = new Fixture();
        var key = f.File();
        f.Review(key, expiryPassed: expiryPassed);

        var audit = await f.Service.AuditAsync(f.Db, default);
        var cleanup = await f.Service.CleanupAsync(f.Db, default);

        Assert.Empty(audit.Orphans);
        Assert.Empty(audit.Missing);
        Assert.Equal(1, audit.ReferencedFileCount);
        var snapshot = Assert.Single(audit.Groups.SelectMany(group => group.Files));
        Assert.Equal(key, snapshot.StorageKey);
        Assert.Equal("review", snapshot.Role);
        Assert.True(snapshot.OnDisk);
        Assert.Equal(0, cleanup.DeletedOrphanCount);
        Assert.True(f.Exists(key));
    }

    [Fact]
    public async Task Missing_review_only_original_is_reported_with_its_review_reference()
    {
        using var f = new Fixture();
        const string key = "original/missing.flac";
        f.Review(key);

        var audit = await f.Service.AuditAsync(f.Db, default);

        var missing = Assert.Single(audit.Missing);
        Assert.Equal(key, missing.StorageKey);
        Assert.Contains(missing.ReferencedBy, owner => owner.Contains("审核", StringComparison.Ordinal));
        Assert.False(Assert.Single(audit.Groups.SelectMany(group => group.Files)).OnDisk);
    }

    [Fact]
    public async Task Resolved_review_only_original_is_reclaimed_after_the_existing_orphan_ttl()
    {
        using var f = new Fixture();
        var key = f.File(old: false);
        f.Review(key, status: "rejected");

        var recent = await f.Service.CleanupAsync(f.Db, default);

        Assert.Equal(0, recent.DeletedOrphanCount);
        Assert.True(f.Exists(key));
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(f.LibraryRoot, key), DateTime.UtcNow.AddHours(-48));

        var aged = await f.Service.CleanupAsync(f.Db, default);

        Assert.Equal(1, aged.DeletedOrphanCount);
        Assert.Equal(Fixture.Bytes.Length, aged.DeletedBytes);
        Assert.False(f.Exists(key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_track_deletion_preserves_its_pending_review_snapshot(bool expiryPassed)
    {
        using var f = new Fixture();
        var track = f.Track();
        var asset = f.Asset(track.Id);
        var review = f.Review(asset.StorageKey, expiryPassed: expiryPassed);

        var result = await f.Service.DeleteTrackAsync(f.Db, track.Id, default);

        Assert.NotNull(result);
        Assert.Equal(0, result.DeletedFileCount);
        Assert.False(f.Db.Queryable<Track>().Any(row => row.Id == track.Id));
        Assert.False(f.Db.Queryable<MediaAsset>().Any(row => row.Id == asset.Id));
        Assert.True(f.Exists(asset.StorageKey));
        Assert.Empty((await f.Service.AuditAsync(f.Db, default)).Orphans);
        review.Status = "expired";
        f.Db.Updateable(review).ExecuteCommand();

        var cleanup = await f.Service.CleanupAsync(f.Db, default);

        Assert.Equal(1, cleanup.DeletedOrphanCount);
        Assert.False(f.Exists(asset.StorageKey));
    }

    [Fact]
    public async Task Physical_track_deletion_preserves_another_tracks_shared_original()
    {
        using var f = new Fixture();
        var deleted = f.Track();
        var retained = f.Track();
        var asset = f.Asset(deleted.Id);
        f.Asset(retained.Id, key: asset.StorageKey);

        var result = await f.Service.DeleteTrackAsync(f.Db, deleted.Id, default);

        Assert.NotNull(result);
        Assert.Equal(0, result.DeletedFileCount);
        Assert.True(f.Exists(asset.StorageKey));
        Assert.True(f.Db.Queryable<MediaAsset>().Any(row => row.TrackId == retained.Id));
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly byte[] Bytes = [1, 2, 3, 4, 5, 6];
        private string Root { get; } = Directory.CreateTempSubdirectory("shine-review-retention-").FullName;
        internal string LibraryRoot => Path.Combine(Root, "library");
        internal SqlSugarClient Db { get; }
        internal StorageGovernanceService Service { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(LibraryRoot);
            Db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={Path.Combine(Root, "retention.db")};Pooling=False",
                DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute,
                ConfigureExternalServices = new ConfigureExternalServices
                {
                    EntityNameService = (_, entity) => entity.DbTableName = JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                    EntityService = (property, column) => column.DbColumnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
                }
            });
            Db.CodeFirst.InitTables(typeof(Track), typeof(MediaAsset), typeof(Artwork), typeof(Album), typeof(Artist),
                typeof(TrackArtist), typeof(TrackGenre), typeof(EditorialCollection), typeof(LibraryWorkflowReview),
                typeof(TrackComment), typeof(TrackCommentLike), typeof(Favorite), typeof(UserPlaylistTrack),
                typeof(UserTrackFeedback), typeof(DailyRecommendationItem), typeof(Lyric), typeof(UserLibraryItem), typeof(CollectionTrack));
            var paths = new LibraryStoragePaths(Options.Create(new StorageOptions { LibraryRoot = LibraryRoot }),
                new TestEnvironment { ContentRootPath = Root });
            Service = new StorageGovernanceService(paths, NullLogger<StorageGovernanceService>.Instance);
        }

        internal Track Track()
        {
            var track = new Track { Title = Guid.NewGuid().ToString(), OwnerUserId = Guid.NewGuid() };
            Db.Insertable(track).ExecuteCommand();
            return track;
        }

        internal MediaAsset Asset(Guid trackId, bool retired = false, string? key = null)
        {
            var asset = new MediaAsset
            {
                TrackId = trackId, StorageKey = key ?? File(), Container = "flac", Codec = "flac",
                ContentHash = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(),
                IsOriginal = true, IsPublished = !retired, IsPrimary = !retired,
                RetainUntilUtc = retired ? DateTime.UtcNow.AddHours(-1) : null
            };
            Db.Insertable(asset).ExecuteCommand();
            return asset;
        }

        internal string File(bool old = true)
        {
            var key = $"original/{Guid.NewGuid():N}.flac";
            var path = Path.Combine(LibraryRoot, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, Bytes);
            if (old) System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-48));
            return key;
        }

        internal LibraryWorkflowReview Review(string key, bool expiryPassed = false, string status = "pending")
        {
            var review = new LibraryWorkflowReview
            {
                Kind = "public", OwnerUserId = Guid.NewGuid(), ImportItemId = Guid.NewGuid(), OriginalStorageKey = key,
                ContentHash = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(), Status = status,
                ExpiresAtUtc = expiryPassed ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddDays(1)
            };
            Db.Insertable(review).ExecuteCommand();
            return review;
        }

        internal bool Exists(string key) => System.IO.File.Exists(Path.Combine(LibraryRoot, key));
        public void Dispose() { Db.Dispose(); Directory.Delete(Root, recursive: true); }
    }
}
