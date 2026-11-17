using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Domain;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class AssetLifecycleTests
{
    [Theory]
    [InlineData("mp3", "mp3", 192, true)]
    [InlineData("aac", "m4a", 256, true)]
    [InlineData("aac", "aac", 320, true)]
    [InlineData("mp3", "mp3", 128, false)]
    [InlineData("unknown", "flac", 900, false)]
    [InlineData("aac", "flac", 320, false)]
    public void Publication_requires_measured_quality_and_identity(string codec, string container, int bitrate, bool acceptable)
    {
        Assert.Equal(acceptable, AutoPublishPolicy.SourceRejectionReason(true, true, true, codec, bitrate, true, container) is null);
        Assert.NotNull(AutoPublishPolicy.SourceRejectionReason(true, false, true, codec, bitrate, true, container));
        Assert.NotNull(AutoPublishPolicy.SourceRejectionReason(false, true, true, codec, bitrate, true, container));
        Assert.NotNull(AutoPublishPolicy.SourceRejectionReason(true, true, false, codec, bitrate, true, container));
    }

    [Theory]
    [InlineData("mp3", "mp3", false, "audio/mpeg")]
    [InlineData("aac", "m4a", false, "audio/mp4")]
    [InlineData("aac", "aac", false, "audio/aac")]
    [InlineData("flac", "flac", true, "audio/flac")]
    public void Only_required_compatibility_copy(string codec, string container, bool copy, string mime)
    {
        Assert.Equal(copy, PlaybackMediaPolicy.NeedsCompatibilityCopy(new LibraryImportItem { Codec = codec, Container = container }));
        Assert.Equal(mime, PlaybackMediaPolicy.TryGetContentType(container));
    }

    [Theory]
    [InlineData("mp3", 256, true)]
    [InlineData("mp3", 192, false)]
    [InlineData("mp3", 128, false)]
    [InlineData("aac", 320, false)]
    public void Upgrade_requires_same_encoding(string codec, int bitrate, bool upgrade) =>
        Assert.Equal(upgrade, AssetReplacementPolicy.IsUpgrade(new MediaAsset { Codec = "mp3", BitrateKbps = 192 },
            new LibraryImportItem { Codec = codec, Container = codec == "aac" ? "m4a" : codec, BitrateKbps = bitrate }));

    [Fact]
    public async Task Retired_cleanup_preserves_retention_references_and_retries_locked_files()
    {
        using var f = new LifecycleFixture();
        var expired = f.Asset("expired.mp3", DateTime.UtcNow.AddHours(-1));
        var retained = f.Asset("retained.mp3", DateTime.UtcNow.AddHours(24));
        var shared = f.Asset("shared.mp3", DateTime.UtcNow.AddHours(-1));
        f.Db.Insertable(new MediaAsset { StorageKey = shared.StorageKey, ContentHash = "reference", Codec = "mp3", Container = "mp3", IsPublished = true }).ExecuteCommand();
        var locked = f.Asset("locked.mp3", DateTime.UtcNow.AddHours(-1));
        using (var handle = new FileStream(Path.Combine(f.Root, locked.StorageKey), FileMode.Open, FileAccess.Read, FileShare.None))
            await f.Service.CleanupRetiredAsync(f.Db, default);
        Assert.False(File.Exists(Path.Combine(f.Root, expired.StorageKey)));
        Assert.True(File.Exists(Path.Combine(f.Root, retained.StorageKey)));
        Assert.True(File.Exists(Path.Combine(f.Root, shared.StorageKey)));
        if (OperatingSystem.IsWindows()) Assert.True(f.Db.Queryable<MediaAsset>().Any(a => a.Id == locked.Id));
        await f.Service.CleanupRetiredAsync(f.Db, default);
        Assert.False(f.Db.Queryable<MediaAsset>().Any(a => a.Id == locked.Id));
    }

    [Fact]
    public async Task Equal_quality_reuses_track_without_creating_asset_and_missing_source_preserves_old_primary()
    {
        using var f = new LifecycleFixture();
        var current = f.Asset("current.mp3", null, true);
        var track = new Track { Id = current.TrackId, Title = "test" };
        var item = new LibraryImportItem { DisplayName = "test.mp3", SourceStorageKey = "test.mp3", Codec = "mp3", Container = "mp3", BitrateKbps = 192, ContentHash = "new" };
        f.Db.Insertable(item).ExecuteCommand();
        var media = new FfmpegMediaService(Options.Create(new FfmpegOptions()), NullLogger<FfmpegMediaService>.Instance);
        var replacement = new AssetReplacementService(f.Db, f.Paths, media, TimeSpan.FromHours(24));
        var reused = await replacement.ReplaceAsync(track, item, "unused", default);
        Assert.Equal(track.Id, reused.TrackId);
        Assert.Equal(1, f.Db.Queryable<MediaAsset>().Count());
        item.BitrateKbps = 320;
        var failed = await replacement.ReplaceAsync(track, item, Path.Combine(f.Root, "missing.mp3"), default);
        Assert.False(failed.Succeeded);
        Assert.True(f.Db.Queryable<MediaAsset>().Single(a => a.Id == current.Id).IsPrimary);
        Assert.True(File.Exists(Path.Combine(f.Root, current.StorageKey)));
        Assert.Equal(1, f.Db.Queryable<MediaAsset>().Count());
    }

    [Fact]
    public async Task Real_audio_upgrade_keeps_track_id_and_database_failure_rolls_back()
    {
        using var f = new LifecycleFixture();
        var source = Path.Combine(f.Root, "candidate.mp3");
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "libmp3lame", "-b:a", "320k", source }) start.ArgumentList.Add(arg);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        var media = new FfmpegMediaService(Options.Create(new FfmpegOptions()), NullLogger<FfmpegMediaService>.Instance);
        var probe = await media.ProbeAsync(source, default);
        var old = f.Asset("old.mp3", null, true);
        var item = new LibraryImportItem { DisplayName = "candidate.mp3", SourceStorageKey = "candidate.mp3", ContentHash = "candidate", Codec = probe.Codec, Container = probe.Container, BitrateKbps = probe.BitrateKbps };
        f.Db.Insertable(item).ExecuteCommand();
        var service = new AssetReplacementService(f.Db, f.Paths, media, TimeSpan.FromHours(24));
        var track = new Track { Id = old.TrackId, Title = "test" };
        f.Db.Ado.ExecuteCommand("CREATE TRIGGER reject_asset BEFORE INSERT ON media_assets BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        Assert.False((await service.ReplaceAsync(track, item, source, default)).Succeeded);
        Assert.True(f.Db.Queryable<MediaAsset>().Single(a => a.Id == old.Id).IsPrimary);
        Assert.Null(item.PublishedTrackId);
        f.Db.Ado.ExecuteCommand("DROP TRIGGER reject_asset;");
        var result = await service.ReplaceAsync(track, item, source, default);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(track.Id, result.TrackId);
        var active = f.Db.Queryable<MediaAsset>().Single(a => a.IsPrimary && a.IsPublished);
        Assert.Equal(track.Id, active.TrackId);
        var retired = f.Db.Queryable<MediaAsset>().Single(a => a.Id == old.Id);
        Assert.False(retired.IsPublished);
        Assert.InRange(retired.RetainUntilUtc!.Value, DateTime.UtcNow.AddHours(23.9), DateTime.UtcNow.AddHours(24.1));
        Assert.True(File.Exists(Path.Combine(f.Root, old.StorageKey)));
        Assert.Equal(2, f.Db.Queryable<MediaAsset>().Count());
        // Independent connections contend for the same old primary; only one may retire it.
        var old2 = f.Asset("race.mp3", null, true);
        var track2 = new Track { Id = old2.TrackId, Title = "race" };
        var items = Enumerable.Range(0, 2).Select(i => new LibraryImportItem { DisplayName = "candidate.mp3", SourceStorageKey = "candidate.mp3", ContentHash = "race" + i, Codec = probe.Codec, Container = probe.Container, BitrateKbps = probe.BitrateKbps }).ToArray();
        f.Db.Insertable(items).ExecuteCommand();
        using var db2 = new SqlSugarClient(f.Db.CurrentConnectionConfig);
        var second = new AssetReplacementService(db2, f.Paths, media, TimeSpan.FromHours(24));
        var results = await Task.WhenAll(service.ReplaceAsync(track2, items[0], source, default), second.ReplaceAsync(track2, items[1], source, default));
        Assert.Contains(results, r => r.Succeeded);
        Assert.Single(f.Db.Queryable<MediaAsset>().Where(a => a.TrackId == track2.Id && a.IsPrimary && a.IsPublished).ToList());
        Assert.Equal(2, f.Db.Queryable<MediaAsset>().Where(a => a.TrackId == track2.Id).Count());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Full_publication_selects_assets_and_repairs_housekeeping(bool flac, bool failHousekeeping)
    {
        using var f = new LifecycleFixture();
        f.Db.CodeFirst.InitTables(typeof(Track), typeof(Artist), typeof(TrackArtist), typeof(Album), typeof(AlbumArtist), typeof(Genre), typeof(TrackGenre), typeof(Lyric), typeof(LibraryImportBatch), typeof(AdminAuditEntry), typeof(ExternalDownloadTask));
        var quarantine = Path.Combine(f.Root, "quarantine");
        Directory.CreateDirectory(quarantine);
        var key = flac ? "test.flac" : "test.mp3";
        var bytes = new byte[4096];
        System.Text.Encoding.ASCII.GetBytes(flac ? "fLaC" : "ID3").CopyTo(bytes, 0);
        File.WriteAllBytes(Path.Combine(quarantine, key), bytes);
        if (flac)
        {
            var m4a = new byte[4096];
            System.Text.Encoding.ASCII.GetBytes("ftyp").CopyTo(m4a, 4);
            File.WriteAllBytes(Path.Combine(quarantine, "compat.m4a"), m4a);
        }
        var batch = new LibraryImportBatch { CreatedByUserId = Guid.NewGuid() };
        f.Db.Insertable(batch).ExecuteCommand();
        var item = new LibraryImportItem { BatchId = batch.Id, Title = "Test", ArtistsJson = "[\"Artist\"]", DurationMs = 200000,
            DisplayName = key, SourceStorageKey = key, ContentHash = "hash", Container = flac ? "flac" : "mp3", Codec = flac ? "flac" : "mp3",
            BitrateKbps = flac ? 900 : 192, TranscodeBitrateKbps = 192, TranscodedStorageKey = flac ? "compat.m4a" : key,
            TranscodeStatus = "succeeded", IsMetadataReviewed = true, Status = LibraryImportItemStatuses.ReadyToPublish };
        f.Db.Insertable(item).ExecuteCommand();
        var publisher = new ImportPublishService(f.Db, Options.Create(new SongRequestFulfillmentOptions()),
            new QuarantineStoragePaths(Options.Create(new StorageOptions { QuarantineRoot = quarantine }), new TestEnvironment { ContentRootPath = f.Root }),
            f.Paths, NullLogger<ImportPublishService>.Instance,
            new FfmpegMediaService(Options.Create(new FfmpegOptions()), NullLogger<FfmpegMediaService>.Instance),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        if (failHousekeeping) f.Db.Ado.ExecuteCommand("CREATE TRIGGER reject_batch BEFORE UPDATE ON library_import_batches BEGIN SELECT RAISE(ABORT, 'housekeeping'); END;");
        var result = await publisher.PublishAsync(item, new ImportPublishContext(Guid.Empty, "test", false), false, null, default);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(LibraryImportItemStatuses.Published, f.Db.Queryable<LibraryImportItem>().Single(i => i.Id == item.Id).Status);
        Assert.Equal(flac ? 2 : 1, f.Db.Queryable<MediaAsset>().Count());
        Assert.Single(f.Db.Queryable<MediaAsset>().Where(a => a.IsPrimary).ToList());
        Assert.All(f.Db.Queryable<MediaAsset>().ToList(), a => Assert.True(File.Exists(Path.Combine(f.Root, a.StorageKey))));
        if (failHousekeeping)
        {
            Assert.True(File.Exists(Path.Combine(quarantine, key)));
            f.Db.Ado.ExecuteCommand("DROP TRIGGER reject_batch;");
            await publisher.RepairPublicationHousekeepingAsync(default);
        }
        if (!flac && !failHousekeeping)
        {
            f.Db.CodeFirst.InitTables<SongRequestRecord, SongRequestEventRecord, UserNotification>();
            var request = new SongRequestRecord { Title = "Wrong song", NormalizedTitle = "wrong song", ArtistName = "Other", NormalizedArtistName = "other", CreatedByUserId = Guid.NewGuid() };
            f.Db.Insertable(request).ExecuteCommand();
            var track = f.Db.Queryable<Track>().Single(t => t.Id == result.TrackId);
            track.Isrc = "TEST-ISRC";
            f.Db.Updateable(track).ExecuteCommand();
            item.Id = Guid.NewGuid();
            item.Isrc = track.Isrc;
            item.SongRequestId = request.Id;
            item.Status = LibraryImportItemStatuses.ReadyToPublish;
            item.PublishedTrackId = null;
            f.Db.Insertable(item).ExecuteCommand();
            f.Db.Insertable(new ExternalDownloadTask { SourceProvider = "test", ProviderTrackId = "test", ImportItemId = item.Id, Title = "Test", ExpectedTitle = "Test", ExpectedArtistsJson = item.ArtistsJson, ExpectedDurationMs = item.DurationMs }).ExecuteCommand();
            File.WriteAllBytes(Path.Combine(quarantine, key), bytes);
            var mismatch = await publisher.PublishAsync(item, new ImportPublishContext(Guid.Empty, "reuse", false), false, null, default);
            Assert.False(mismatch.Succeeded);
            Assert.Null(f.Db.Queryable<LibraryImportItem>().Single(i => i.Id == item.Id).PublishedTrackId);
            request.Title = "Test";
            request.ArtistName = "Artist";
            f.Db.Updateable(request).ExecuteCommand();
            var reused = await publisher.PublishAsync(item, new ImportPublishContext(Guid.Empty, "reuse", false), false, null, default);
            Assert.True(reused.Succeeded, reused.Error?.Message);
            Assert.Equal(result.TrackId, reused.TrackId);
            Assert.Equal(SongRequestStatuses.Fulfilled, f.Db.Queryable<SongRequestRecord>().Single(r => r.Id == request.Id).Status);
            Assert.Equal(1, f.Db.Queryable<SongRequestEventRecord>().Count());
            Assert.Equal(1, f.Db.Queryable<UserNotification>().Count());
            Assert.Equal(2, f.Db.Queryable<AdminAuditEntry>().Count());
            Assert.Equal(1, f.Db.Queryable<MediaAsset>().Count());
            Assert.Empty(Directory.GetFiles(quarantine));
        }
        Assert.NotNull(f.Db.Queryable<LibraryImportBatch>().Single(b => b.Id == batch.Id).CompletedAtUtc);
        Assert.Empty(Directory.GetFiles(quarantine));
    }

    [Fact]
    public async Task Retired_cleanup_is_bounded_to_one_hundred()
    {
        using var f = new LifecycleFixture();
        for (var i = 0; i < 101; i++) f.Asset($"expired-{i}.mp3", DateTime.UtcNow.AddDays(-1));
        await f.Service.CleanupRetiredAsync(f.Db, default);
        Assert.Equal(1, f.Db.Queryable<MediaAsset>().Count());
        await f.Service.CleanupRetiredAsync(f.Db, default);
        Assert.Equal(0, f.Db.Queryable<MediaAsset>().Count());
        for (var i = 0; i < 10; i++) f.Asset($"concurrent-{i}.mp3", DateTime.UtcNow.AddDays(-1));
        var active = f.Asset("active.mp3", null, true);
        using var second = new SqlSugarClient(f.Db.CurrentConnectionConfig);
        await Task.WhenAll(Task.Run(() => f.Service.CleanupRetiredAsync(f.Db, default)),
            Task.Run(() => f.Service.CleanupRetiredAsync(second, default)));
        Assert.Equal(1, f.Db.Queryable<MediaAsset>().Count());
        Assert.True(File.Exists(Path.Combine(f.Root, active.StorageKey)));
    }

    [Fact]
    public async Task Reuse_callback_failure_rolls_back_item_and_callback_writes()
    {
        using var f = new LifecycleFixture();
        var old = f.Asset("reuse.mp3", null, true);
        var item = new LibraryImportItem { DisplayName = "test.mp3", SourceStorageKey = "test.mp3", ContentHash = "test", Codec = "mp3", Container = "mp3", BitrateKbps = 192 };
        f.Db.Insertable(item).ExecuteCommand();
        var service = new AssetReplacementService(f.Db, f.Paths, null!, TimeSpan.FromHours(24));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceAsync(new Track { Id = old.TrackId, Title = "test" }, item, "unused", default,
            async ct =>
            {
                await f.Db.Updateable<MediaAsset>().SetColumns(a => a.BitrateKbps == 123).Where(a => a.Id == old.Id).ExecuteCommandAsync(ct);
                throw new InvalidOperationException("callback failure");
            }));
        Assert.Equal(192, f.Db.Queryable<MediaAsset>().Single(a => a.Id == old.Id).BitrateKbps);
        Assert.Null(f.Db.Queryable<LibraryImportItem>().Single(i => i.Id == item.Id).PublishedTrackId);
    }

    private sealed class LifecycleFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(), "asset-tests-" + Guid.NewGuid().ToString("N"));
        public SqlSugarClient Db { get; }
        public LibraryStoragePaths Paths { get; }
        public StorageGovernanceService Service { get; }
        public LifecycleFixture()
        {
            Directory.CreateDirectory(Root);
            Db = new SqlSugarClient(new ConnectionConfig { ConnectionString = $"Data Source={Path.Combine(Root, "test.db")};Pooling=False", DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute,
                ConfigureExternalServices = new ConfigureExternalServices
                {
                    EntityNameService = (_, entity) => entity.DbTableName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                    EntityService = (property, column) => column.DbColumnName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
                } });
            Db.CodeFirst.InitTables<MediaAsset, Artwork, LibraryImportItem>();
            Paths = new LibraryStoragePaths(Options.Create(new StorageOptions { LibraryRoot = Root }), new TestEnvironment { ContentRootPath = Root });
            Service = new StorageGovernanceService(Paths, NullLogger<StorageGovernanceService>.Instance);
        }
        public MediaAsset Asset(string key, DateTime? until, bool active = false)
        {
            var asset = new MediaAsset { TrackId = Guid.NewGuid(), StorageKey = key, ContentHash = key, Container = "mp3", Codec = "mp3", BitrateKbps = 192, RetainUntilUtc = until, IsPublished = active, IsPrimary = active };
            File.WriteAllText(Path.Combine(Root, key), "test audio placeholder");
            Db.Insertable(asset).ExecuteCommand();
            return asset;
        }
        public void Dispose() { Db.Dispose(); Directory.Delete(Root, true); }
    }
}
