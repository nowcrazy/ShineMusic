using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Ports;
using ShineMusic.Service.Messaging;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Tests.SourceProviders;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[CollectionDefinition("Source download database acceptance", DisableParallelization = true)]
public sealed class SourceDownloadDatabaseCollection { }

[Collection("Source download database acceptance")]
[Trait("Category", "SourceDownloadAcceptance")]
public sealed class SourceDownloadAcceptanceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Single_selection_discovers_only_enabled_exact_fallbacks(bool downloadEnabled)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Legacy.Add(new LadderProvider("netease"));
        f.Options.ProviderFramework.Providers["netease"] = new() { DownloadEnabled = downloadEnabled };
        var calls = 0;
        f.SearchProviders.Add(new FakeV2Provider("netease") { OnSearch = _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<MusicTrack>>([
                FrameworkFixtures.Track("netease"), FrameworkFixtures.Track("netease"),
                FrameworkFixtures.Track("netease", "晴天 (Live)"), FrameworkFixtures.Track("netease", "错误歌曲")]);
        }});
        var selected = new ShineMusic.Model.Contracts.SourceSearchItemDto("qq", "selected", "晴天",
            ["周杰伦"], "叶惠美", 269000, [], null, null, null, SourceQualities.Standard, false);
        var service = new SourceDownloadService(f.Db, f.Legacy, f.Bus, f.Access(), f.Aggregator());
        var result = await service.CreateTasksAsync(new ShineMusic.Model.Contracts.CreateSourceDownloadTasksRequest(
            "qq", ["selected"], new Dictionary<string, ShineMusic.Model.Contracts.SourceSearchItemDto> { ["selected"] = selected }),
            Guid.NewGuid(), default);
        var task = Assert.Single(result.Created);
        Assert.Equal("selected", task.ProviderTrackId);
        Assert.Equal(selected.Title, task.ExpectedTitle);
        Assert.Equal(selected.AlbumTitle, task.ExpectedAlbumTitle);
        Assert.Equal(selected.DurationMs, task.ExpectedDurationMs);
        Assert.Equal(downloadEnabled ? 1 : 0, calls);
        if (downloadEnabled)
        {
            var candidates = System.Text.Json.JsonSerializer.Deserialize<ShineMusic.Model.Contracts.SourceSearchItemDto[]>(task.CandidateProvidersJson!);
            Assert.Equal(2, candidates!.Length);
            Assert.Equal("selected", candidates[0].ProviderTrackId);
            Assert.All(candidates, c => Assert.Equal("晴天", c.Title));
        }
        else Assert.Null(task.CandidateProvidersJson);
    }

    [Theory]
    [InlineData("晴天 (Live)", "叶惠美", 269000)]
    [InlineData("晴天", "另一专辑", 269000)]
    [InlineData("晴天", "叶惠美", 275000)]
    public async Task Explicit_wrong_fallback_does_not_replace_original(string title, string album, long duration)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Legacy.Add(new LadderProvider("netease"));
        var selected = new ShineMusic.Model.Contracts.SourceSearchItemDto("qq", "selected", "晴天",
            ["周杰伦"], "叶惠美", 269000, [], null, null, null, SourceQualities.Standard, false);
        var wrong = selected with { Provider = "netease", Title = title, AlbumTitle = album,
            DurationMs = duration, Quality = SourceQualities.Lossless };
        var service = new SourceDownloadService(f.Db, f.Legacy, f.Bus, f.Access());
        var result = await service.CreateTasksAsync(new ShineMusic.Model.Contracts.CreateSourceDownloadTasksRequest(
            "qq", ["selected"], Candidates: [selected, wrong]), Guid.NewGuid(), default);
        var task = Assert.Single(result.Created);
        Assert.Equal("qq", task.SourceProvider);
        Assert.Equal(selected.Title, task.ExpectedTitle);
        Assert.Equal(selected.AlbumTitle, task.ExpectedAlbumTitle);
        Assert.Equal(selected.DurationMs, task.ExpectedDurationMs);
        Assert.Null(task.CandidateProvidersJson);
    }

    [Fact]
    public async Task Creation_preserves_original_identity_separately_from_provider_details()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var expected = new ShineMusic.Model.Contracts.SourceExpectedTrackDto("原始目标", ["原始艺人"], "原始专辑", 280000);
        var service = new SourceDownloadService(f.Db, f.Legacy, f.Bus, f.Access());
        var result = await service.CreateTasksAsync(new ShineMusic.Model.Contracts.CreateSourceDownloadTasksRequest(
            "qq", ["track-1"], ExpectedTrack: expected), Guid.NewGuid(), default);
        var task = f.Reload(Assert.Single(result.Created).Id);
        Assert.Equal("晴天", task.Title);
        Assert.Equal(expected.Title, task.ExpectedTitle);
        Assert.Equal(expected.DurationMs, task.ExpectedDurationMs);
        Assert.Equal(expected.AlbumTitle, task.ExpectedAlbumTitle);
        Assert.Equal(expected.Artists, System.Text.Json.JsonSerializer.Deserialize<string[]>(task.ExpectedArtistsJson!));
    }

    [Fact]
    public async Task Switching_to_metadata_disabled_source_uses_its_candidate_snapshot()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Options.ProviderFramework.Providers["netease"] = new() { MetadataEnabled = false };
        f.Legacy.Add(new LadderProvider("netease"));
        f.Media.Probe = _ => ScriptedMedia.Flac(11000);
        var task = f.Seed();
        task.ExpectedTitle = "原歌单标题";
        task.ExpectedArtistsJson = "[\"原歌单艺人\"]";
        task.ExpectedDurationMs = 269000;
        task.ExpectedAlbumTitle = "原歌单专辑";
        var candidate = new ShineMusic.Model.Contracts.SourceSearchItemDto("netease", "next-id", "晴天",
            ["周杰伦"], "目标专辑", 300000, ["流行"], "https://example.test/next.jpg",
            "https://example.test/next", null, SourceQualities.High, false, 2003);
        task.CandidateProvidersJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            candidate with { Provider = "qq", ProviderTrackId = task.ProviderTrackId }, candidate
        });
        task.AttemptIndex = 0;
        f.Db.Updateable(task).ExecuteCommand();
        await f.Job().ProcessAsync(task.Id, default);
        var switched = f.Reload(task.Id);
        Assert.Equal("netease", switched.SourceProvider);
        Assert.Equal(1, switched.AttemptIndex);
        Assert.Equal(candidate.DurationMs, switched.DurationMs);
        Assert.Equal(candidate.AlbumTitle, switched.AlbumTitle);
        Assert.Equal(candidate.CoverUrl, switched.CoverUrl);
        Assert.Equal("原歌单标题", switched.ExpectedTitle);
        Assert.Equal("[\"原歌单艺人\"]", switched.ExpectedArtistsJson);
        Assert.Equal(269000, switched.ExpectedDurationMs);
        Assert.Equal("原歌单专辑", switched.ExpectedAlbumTitle);
    }

    [Fact]
    public async Task Disabled_download_cannot_fetch_audio_or_create_import()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Options.ProviderFramework.Providers["qq"] = new() { DownloadEnabled = false };
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        Assert.Empty(f.Http.Requests);
        AssertRejected(f, task.Id);
    }

    [Fact]
    public async Task Metadata_disabled_download_uses_existing_snapshot_and_can_import()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Options.ProviderFramework.Providers["qq"] = new() { MetadataEnabled = false, SearchEnabled = false };
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        AssertImported(f, task.Id);
    }

    [Fact]
    public async Task Import_message_failure_retains_committed_media_and_task_link()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Bus.FailImports = true;
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        var linked = f.Reload(task.Id);
        Assert.Equal(ExternalDownloadTaskStatuses.ImportQueued, linked.Status);
        var item = f.Db.Queryable<LibraryImportItem>().Single(row => row.Id == linked.ImportItemId);
        var outbox = f.Db.Queryable<MessageOutboxEntry>().Single(row => row.TaskId == item.Id);
        Assert.Equal("pending", outbox.Status);
        Assert.Equal(1, outbox.Attempts);
        Assert.True(File.Exists(Path.Combine(f.Root, item.SourceStorageKey)));
        Assert.Equal(LibraryImportItemStatuses.Uploaded, item.Status);
        f.Bus.FailImports = false;
        item.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        await f.Db.Updateable(item).ExecuteCommandAsync();
        f.Db.CodeFirst.InitTables<MediaAsset, Artwork>();
        var publisher = new ShineMusic.Service.ImportPublishService(f.Db,
            Microsoft.Extensions.Options.Options.Create(new ShineMusic.Model.Options.SongRequestFulfillmentOptions()), null!, null!,
            NullLogger<ShineMusic.Service.ImportPublishService>.Instance, null!, null!);
        var sweep = new ShineMusic.Service.LibraryImportRetrySweepJob(f.Db, f.Bus, publisher,
            Microsoft.Extensions.Options.Options.Create(f.Options),
            Microsoft.Extensions.Options.Options.Create(new ShineMusic.Model.Options.LibraryImportOptions()),
            NullLogger<ShineMusic.Service.LibraryImportRetrySweepJob>.Instance,
            new ShineMusic.Service.StorageGovernanceService(
                new ShineMusic.Repository.Data.LibraryStoragePaths(
                    Microsoft.Extensions.Options.Options.Create(new ShineMusic.Model.Options.StorageOptions { LibraryRoot = f.Root }),
                    new TestEnvironment { ContentRootPath = f.Root }),
                NullLogger<ShineMusic.Service.StorageGovernanceService>.Instance), f.Lease());
        await sweep.SweepAsync(default);
        Assert.Empty(f.Bus.Messages);
        Assert.Single(f.Db.Queryable<MessageOutboxEntry>().ToList());
        // Compensation must not bypass the durable retry budget/backoff. Make it due, then let the dispatcher retry.
        await f.Db.Updateable<MessageOutboxEntry>().SetColumns(row => row.AvailableAtUtc == DateTime.UtcNow.AddMinutes(-1))
            .Where(row => row.Id == outbox.Id).ExecuteCommandAsync();
        await new MessageOutboxDispatcher(f.Db, f.Bus, NullLogger<MessageOutboxDispatcher>.Instance).DispatchAsync(default);
        Assert.Contains(f.Bus.Messages, message => message.Queue == QueueNames.Imports && message.Id == item.Id);
        Assert.Equal("sent", f.Db.Queryable<MessageOutboxEntry>().Single(row => row.Id == outbox.Id).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A01_A02_Probed_quality_and_metadata_survive_validation_and_import_handoff(bool lossless)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var probe = lossless ? ScriptedMedia.Flac() : ScriptedMedia.Mp3();
        f.Media.Probe = _ => probe;
        f.Primary.Candidates = [LadderProvider.Candidate("audio", lossless, lossless ? 935 : 320)];
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        var item = AssertImported(f, task.Id);
        var path = Path.Combine(f.Root, item.SourceStorageKey);
        var validated = await new AudioValidator(f.Media, NullLogger.Instance).ValidateAsync(path, 269000);
        Assert.True(validated.Accepted);
        var quality = Assert.IsType<AudioQuality>(validated.Quality);
        Assert.Equal(lossless ? AudioQualityLevel.Lossless : AudioQualityLevel.High320K, quality.Level);
        Assert.Equal(probe.Codec, quality.Codec);
        Assert.Equal(probe.BitrateKbps, quality.BitrateKbps);
        Assert.Equal(44100, quality.SampleRateHz);
        Assert.Equal(probe.BitsPerSample, quality.BitsPerSample);
        Assert.Equal(2, quality.Channels);
        Assert.Equal(269000, quality.DurationMs);
        Assert.Equal(4096, quality.FileSizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), item.ContentHash);
    }

    [Theory]
    [InlineData("flac", "mp3")]
    [InlineData("wav", "aac")]
    public async Task A03_Mismatched_codec_cannot_create_import(string container, string codec)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Media.Probe = _ => new MediaProbe(container, codec, 320, 269000, container, 44100, null, 2);
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        AssertRejected(f, task.Id);
    }

    [Theory]
    [InlineData(11000, 269000)]
    [InlineData(45000, 269000)]
    [InlineData(60000, 60000)]
    public async Task A04_Preview_including_consistently_mislabelled_source_is_cleaned(long actual, long declared)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Media.Probe = _ => ScriptedMedia.Flac(actual);
        f.SearchProviders.Add(new FakeV2Provider("netease"));
        var task = f.Seed(duration: declared);
        await f.Job().ProcessAsync(task.Id, default);
        AssertRejected(f, task.Id);
    }

    [Fact]
    public async Task A04_Short_complete_song_matching_cross_source_is_not_rejected()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Media.Probe = _ => ScriptedMedia.Flac(60000);
        f.SearchProviders.Add(new FakeV2Provider("netease")
        {
            OnSearch = _ => Task.FromResult<IReadOnlyList<MusicTrack>>(
                [FrameworkFixtures.Track("netease") with { Identity = new TrackIdentity("晴天", ["周杰伦"], 60000) }])
        });
        var task = f.Seed(duration: 60000);
        await f.Job().ProcessAsync(task.Id, default);
        AssertImported(f, task.Id);
    }

    [Fact]
    public async Task A05_File_hash_match_passes_and_tampering_is_rejected()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var path = Path.Combine(f.Root, "hash.flac");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var validator = new AudioValidator(f.Media, NullLogger.Instance);
        Assert.True((await validator.ValidateAsync(path, 269000, expected, expected.ToLowerInvariant())).Accepted);
        await File.WriteAllBytesAsync(path, new byte[4096].Select(_ => (byte)1).ToArray());
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var rejected = await validator.ValidateAsync(path, 269000, expected, actual);
        Assert.False(rejected.Accepted);
        Assert.Equal("hash_mismatch", rejected.ErrorCode);
        Assert.Null(rejected.Quality);
        Assert.Equal(0, f.Db.Queryable<LibraryImportItem>().Count());
        Assert.Empty(f.Bus.Messages);
    }

    [Fact]
    public async Task A05_Tampered_download_never_reaches_import_queue()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Media.Probe = path =>
        {
            File.WriteAllBytes(path, Enumerable.Repeat((byte)1, 4096).ToArray());
            return ScriptedMedia.Flac();
        };
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        AssertRejected(f, task.Id);
        Assert.Contains("SHA-256", f.Reload(task.Id).ErrorMessage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A06_Sq_network_or_validation_failure_attempts_hq_in_order(bool networkFailure)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Options.ProviderFramework.QualityPolicy.PreferredQuality = "Lossless";
        f.Primary.Candidates = [LadderProvider.Candidate("sq", true, 935), LadderProvider.Candidate("hq", false, 320)];
        if (networkFailure)
            f.Http.Respond = path => path.Contains("sq") ? new(HttpStatusCode.ServiceUnavailable) :
                new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) };
        f.Media.Probe = path => path.EndsWith(".flac") ? ScriptedMedia.Flac(11000) : ScriptedMedia.Mp3();
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        AssertImported(f, task.Id);
        // Transient HTTP 503 exhausts two in-attempt recoveries before falling back;
        // a completed file that fails media validation must fall back immediately.
        var expectedRequests = networkFailure
            ? new[] { "/sq.flac", "/sq.flac", "/sq.flac", "/hq.mp3" }
            : new[] { "/sq.flac", "/hq.mp3" };
        Assert.Equal(expectedRequests, f.Http.Requests);
        Assert.Equal(SourceQualities.High, f.Reload(task.Id).AudioQualityTier);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "sourcedl"), $"{task.Id:N}.*.flac"));
    }

    [Fact]
    public async Task Active_download_lease_ignores_duplicate_delivery()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = f.Seed(ExternalDownloadTaskStatuses.Downloading);
        task.WorkerId = "another-worker";
        task.AttemptId = Guid.NewGuid();
        task.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
        f.Db.Updateable(task).ExecuteCommand();

        await f.Job().ProcessAsync(task.Id, default);

        Assert.Empty(f.Http.Requests);
        Assert.Empty(f.Bus.Messages);
        var unchanged = f.Reload(task.Id);
        Assert.Equal(task.WorkerId, unchanged.WorkerId);
        Assert.Equal(task.AttemptId, unchanged.AttemptId);
        Assert.Equal(ExternalDownloadTaskStatuses.Downloading, unchanged.Status);
    }

    [Fact]
    public async Task Lost_download_attempt_cannot_commit_or_delete_new_attempt_files()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = f.Seed();
        var newAttempt = Guid.NewGuid();
        var retainedPath = Path.Combine(f.Root, "sourcedl", $"{task.Id:N}.{newAttempt:N}.flac");
        f.Media.Probe = _ =>
        {
            // 模拟旧下载校验期间租约被回收并由新 Worker 接管。
            var current = f.Reload(task.Id);
            current.WorkerId = "replacement-worker";
            current.AttemptId = newAttempt;
            current.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
            current.ErrorMessage = "new attempt owns this task";
            f.Db.Updateable(current).ExecuteCommand();
            File.WriteAllText(retainedPath, "new attempt output");
            return ScriptedMedia.Flac();
        };

        await f.Job().ProcessAsync(task.Id, default);

        var ownedByReplacement = f.Reload(task.Id);
        Assert.Equal(newAttempt, ownedByReplacement.AttemptId);
        Assert.Equal("replacement-worker", ownedByReplacement.WorkerId);
        Assert.Equal("new attempt owns this task", ownedByReplacement.ErrorMessage);
        Assert.Equal(ExternalDownloadTaskStatuses.Downloading, ownedByReplacement.Status);
        Assert.Empty(f.Db.Queryable<LibraryImportItem>().ToList());
        Assert.Empty(f.Db.Queryable<LibraryImportBatch>().ToList());
        Assert.Empty(f.Bus.Messages);
        Assert.Equal("new attempt output", File.ReadAllText(retainedPath));
        Assert.Equal(new[] { retainedPath }, Directory.GetFiles(Path.Combine(f.Root, "sourcedl")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A07_Actual_low_bitrate_cannot_bypass_policy_using_high_quality_label(bool allowLossy)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Options.ProviderFramework.QualityPolicy.AllowLossyFallback = allowLossy;
        f.Media.Probe = _ => ScriptedMedia.Mp3(128);
        f.Primary.Candidates = [LadderProvider.Candidate("claimed-hq", false, 320)];
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        AssertRejected(f, task.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A07_Subminimum_candidate_is_never_downloaded(bool allowLossy)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Options.ProviderFramework.QualityPolicy.AllowLossyFallback = allowLossy;
        f.Primary.Candidates = [LadderProvider.Candidate("sq", true, 935), LadderProvider.Candidate("hq", false, 320),
            LadderProvider.Candidate("pq", false, 128)];
        f.Media.Probe = path => path.EndsWith(".flac") ? ScriptedMedia.Flac(11000) :
            f.Http.Requests.Last().Contains("hq") ? ScriptedMedia.Mp3(320, 11000) : ScriptedMedia.Mp3(128);
        var task = f.Seed();
        await f.Job().ProcessAsync(task.Id, default);
        Assert.Equal(new[] { "/hq.mp3", "/sq.flac" }, f.Http.Requests);
        AssertRejected(f, task.Id);
    }

    [Theory]
    [InlineData("晴天", "周杰伦", "migu")]
    [InlineData("等晴天", "周杰伦", "netease")]
    [InlineData("晴天 (Live)", "周杰伦", "netease")]
    [InlineData("晴天", "其他歌手", "netease")]
    public async Task A08_Fallback_uses_priority_and_only_unambiguous_identity(string title, string artist, string expectedProvider)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var calls = new List<string>();
        foreach (var name in new[] { "netease", "migu", "kugou", "off" })
        {
            f.Legacy.Add(new LadderProvider(name) { IsEnabled = name != "off" });
            f.SearchProviders.Add(new FakeV2Provider(name, configEnabled: name != "off")
            {
                OnSearch = _ =>
                {
                    calls.Add(name);
                    var track = FrameworkFixtures.Track(name);
                    if (name == "migu") track = track with { Identity = new TrackIdentity(title, [artist], 269000) };
                    return Task.FromResult<IReadOnlyList<MusicTrack>>([track]);
                }
            });
        }
        f.SearchProviders[0].OnSearch = _ => throw new InvalidOperationException("原源不得参与换源");
        var task = f.Seed(ExternalDownloadTaskStatuses.Failed);
        var result = await f.Fallback().SwitchSourceAsync(task.Id, Guid.NewGuid(), null, default);
        Assert.True(result.Success, result.FailureMessage);
        var created = f.Db.Queryable<ExternalDownloadTask>().ToList().Single(t => t.Id != task.Id);
        Assert.Equal(expectedProvider, created.SourceProvider);
        Assert.Equal(ExternalDownloadTaskStatuses.Queued, created.Status);
        Assert.Equal(ExternalDownloadTaskStatuses.Failed, f.Reload(task.Id).Status);
        Assert.DoesNotContain("qq", calls);
        Assert.DoesNotContain("off", calls);
        Assert.Contains(f.Bus.Messages, m => m.Queue == QueueNames.Downloads && m.Id == created.Id);
    }

    private static LibraryImportItem AssertImported(SourceDownloadAcceptanceFixture f, Guid id)
    {
        var task = f.Reload(id);
        Assert.True(task.Status == ExternalDownloadTaskStatuses.ImportQueued, $"{task.Status}: {task.ErrorCode} {task.ErrorMessage}");
        var item = Assert.Single(f.Db.Queryable<LibraryImportItem>().ToList());
        Assert.Equal(item.Id, task.ImportItemId);
        Assert.True(File.Exists(Path.Combine(f.Root, item.SourceStorageKey)));
        Assert.Contains(f.Bus.Messages, m => m.Queue == QueueNames.Imports && m.Id == item.Id);
        return item;
    }

    private static void AssertRejected(SourceDownloadAcceptanceFixture f, Guid id)
    {
        var task = f.Reload(id);
        Assert.Equal(ExternalDownloadTaskStatuses.Failed, task.Status);
        Assert.Null(task.ImportItemId);
        Assert.Equal(0, f.Db.Queryable<LibraryImportItem>().Count());
        Assert.Equal(0, f.Db.Queryable<LibraryImportBatch>().Count());
        Assert.Empty(f.Bus.Messages);
        var directory = Path.Combine(f.Root, "sourcedl");
        Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
    }
}
