using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service.Media;
using ShineMusic.Service.SourceDownload;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class SourceDownloadCheckpointTests
{
    private static readonly byte[] Audio = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromSeconds(15);
    private static SourceDownloadInfo Candidate => LadderProvider.Candidate("sq", true, 935) with
        { ResolverId = "verified-source", ResourceId = "qq:track-1:lossless", DownloadUrl = "https://example.test/sq.flac?token=secret" };

    [Fact]
    public async Task A_new_worker_resumes_an_immutable_prefix_after_host_shutdown_and_queue_recovery()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = await InterruptAsync(f);
        var previousAttempt = task.AttemptId;
        var point = DownloadCheckpointStore.Parse(task.DownloadCheckpointJson)!;
        Assert.Equal(3, point.WrittenBytes);
        Assert.DoesNotContain("secret", task.DownloadCheckpointJson!);
        Assert.DoesNotContain("example.test", task.DownloadCheckpointJson!);
        // The old live file may be truncated or changed by a dying process. The published snapshot is independent.
        var oldLive = Directory.GetFiles(Path.Combine(f.Root, "sourcedl"), "*.flac").Single();
        await File.WriteAllBytesAsync(oldLive, [99, 98]);
        await new MediaQueueRecoveryJob(f.Db, f.Bus, Options.Create(new MediaLeaseOptions()),
            NullLogger<MediaQueueRecoveryJob>.Instance, f.Storage).RecoverAsync(includeQueued: true, default);
        Assert.Equal(ExternalDownloadTaskStatuses.Queued, f.Reload(task.Id).Status);

        using var nextWorker = Worker(f);
        using var http = new InspectingHttp(request => Partial(request, Audio));
        await Job(f, http, nextWorker).ProcessAsync(task.Id, default);

        Assert.Equal(["bytes=3-"], http.Ranges);
        Assert.Equal(["\"v1\""], http.IfRanges);
        var completed = f.Reload(task.Id);
        Assert.NotEqual(previousAttempt, completed.AttemptId);
        Assert.Equal(ExternalDownloadTaskStatuses.ImportQueued, completed.Status);
        Assert.Null(completed.DownloadCheckpointJson);
        var item = f.Db.Queryable<LibraryImportItem>().Single();
        Assert.True(f.Storage.TryResolve(item.SourceStorageKey, out var path));
        Assert.Equal(Audio, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, DownloadCheckpointStore.DirectoryKey), "*.part"));
    }

    [Theory]
    [InlineData("corrupt-prefix")]
    [InlineData("missing-prefix")]
    [InlineData("malformed-json")]
    [InlineData("expired")]
    [InlineData("changed-resource")]
    [InlineData("changed-origin")]
    [InlineData("changed-query")]
    public async Task Invalid_or_changed_checkpoints_are_discarded_and_never_spliced(string change)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = await InterruptAsync(f);
        var path = Directory.GetFiles(Path.Combine(f.Root, DownloadCheckpointStore.DirectoryKey), "*.part").Single();
        if (change == "corrupt-prefix") await File.WriteAllBytesAsync(path, [9, 9, 9]);
        if (change == "missing-prefix") File.Delete(path);
        if (change == "malformed-json") task.DownloadCheckpointJson = "{half-written";
        if (change == "expired") task.DownloadCheckpointJson = JsonSerializer.Serialize(
            DownloadCheckpointStore.Parse(task.DownloadCheckpointJson)! with { CreatedAtUtc = DateTime.UtcNow.AddDays(-2) });
        f.Db.Updateable(task).ExecuteCommand();
        f.Primary.Candidates = [change switch
        {
            "changed-resource" => Candidate with { ResourceId = "different-recording" },
            "changed-origin" => Candidate with { DownloadUrl = "https://another.example.test/sq.flac" },
            "changed-query" => Candidate with { DownloadUrl = "https://example.test/sq.flac?token=changed" },
            _ => Candidate
        }];
        using var worker = Worker(f);
        using var http = new InspectingHttp(_ => Full(Audio));

        await Job(f, http, worker).ProcessAsync(task.Id, default);

        Assert.Equal([null], http.Ranges);
        Assert.Equal(ExternalDownloadTaskStatuses.ImportQueued, f.Reload(task.Id).Status);
        Assert.Null(f.Reload(task.Id).DownloadCheckpointJson);
        var item = f.Db.Queryable<LibraryImportItem>().Single();
        Assert.True(f.Storage.TryResolve(item.SourceStorageKey, out var output));
        Assert.Equal(Audio, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task A_changed_etag_replaces_the_checkpoint_prefix_with_a_full_new_representation()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = await InterruptAsync(f);
        byte[] replacement = Audio.Reverse().ToArray();
        using var worker = Worker(f);
        using var http = new InspectingHttp(_ => Full(replacement, "\"v2\""));

        await Job(f, http, worker).ProcessAsync(task.Id, default);

        Assert.Equal(["bytes=3-"], http.Ranges);
        Assert.Null(f.Reload(task.Id).DownloadCheckpointJson);
        var item = f.Db.Queryable<LibraryImportItem>().Single();
        Assert.True(f.Storage.TryResolve(item.SourceStorageKey, out var output));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task Exhausted_network_recoveries_keep_a_checkpoint_across_the_durable_retry_queue()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Primary.Candidates = [Candidate];
        var task = f.Seed();
        var calls = 0;
        using var failedHttp = new InspectingHttp(_ => ++calls == 1
            ? PrefixResponse(new FailingPrefixBody(Audio))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Job(f, failedHttp, f.Lease()).ProcessAsync(task.Id, default);
        var failed = f.Reload(task.Id);
        Assert.Equal(ExternalDownloadTaskStatuses.Queued, failed.Status);
        Assert.Equal(1, failed.AutoRetryCount);
        Assert.NotNull(failed.DownloadCheckpointJson);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "sourcedl"), "*.flac"));
        f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.NextRetryAtUtc == DateTime.UtcNow.AddSeconds(-1))
            .Where(row => row.Id == task.Id).ExecuteCommand();
        using var worker = Worker(f);
        using var succeedingHttp = new InspectingHttp(request => Partial(request, Audio));

        await Job(f, succeedingHttp, worker).ProcessAsync(task.Id, default);

        Assert.Equal(["bytes=3-"], succeedingHttp.Ranges);
        Assert.Equal(ExternalDownloadTaskStatuses.ImportQueued, f.Reload(task.Id).Status);
        Assert.Null(f.Reload(task.Id).DownloadCheckpointJson);
    }

    [Fact]
    public async Task Old_worker_cannot_publish_or_clear_after_lease_transfer_and_successor_snapshot_survives()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = f.Seed(ExternalDownloadTaskStatuses.Downloading);
        task.WorkerId = "old-worker";
        task.AttemptId = Guid.NewGuid();
        task.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(2);
        f.Db.Updateable(task).ExecuteCommand();
        var old = new DownloadCheckpointStore(f.Db, f.Storage, task, task.AttemptId.Value, task.WorkerId);
        var oldPath = LivePath(f, task.Id, task.AttemptId.Value);
        await using (var file = new FileStream(oldPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            await file.WriteAsync(Audio.AsMemory(0, 3));
            await old.PublishAsync(file, Candidate, "\"v1\"", Audio.Length, 3, Audio.Length, default);
        }
        var nextAttempt = Guid.NewGuid();
        f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.WorkerId == "new-worker")
            .SetColumns(row => row.AttemptId == nextAttempt).Where(row => row.Id == task.Id).ExecuteCommand();
        var successor = f.Reload(task.Id);
        var next = new DownloadCheckpointStore(f.Db, f.Storage, successor, nextAttempt, "new-worker");
        var nextPath = LivePath(f, task.Id, nextAttempt);
        Assert.NotNull(await next.RestoreAsync(Candidate, nextPath, Audio.Length, default));
        // A separate store instance models a fresh process publishing the successor's own checkpoint.
        var nextPublisher = new DownloadCheckpointStore(f.Db, f.Storage, successor, nextAttempt, "new-worker");
        await using (var file = new FileStream(nextPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Position = 3;
            await file.WriteAsync(Audio.AsMemory(3, 1));
            await nextPublisher.PublishAsync(file, Candidate, "\"v1\"", Audio.Length, 4, Audio.Length, default);
        }
        var pointer = f.Reload(task.Id).DownloadCheckpointJson;
        await Assert.ThrowsAsync<MediaLeaseLostException>(() => old.ClearAsync(default));
        var latePublisher = new DownloadCheckpointStore(f.Db, f.Storage, task, task.AttemptId.Value, "old-worker");
        await using (var file = new FileStream(oldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Position = 3;
            await file.WriteAsync(Audio.AsMemory(3, 1));
            await Assert.ThrowsAsync<MediaLeaseLostException>(() => latePublisher.PublishAsync(file, Candidate,
                "\"v1\"", Audio.Length, 4, Audio.Length, default));
        }
        Assert.Equal(pointer, f.Reload(task.Id).DownloadCheckpointJson);
        Assert.Equal(nextAttempt, DownloadCheckpointStore.Parse(pointer)!.OwnerAttemptId);
        Assert.Equal(Audio[..4], await File.ReadAllBytesAsync(nextPath));
        Assert.Single(Directory.GetFiles(Path.Combine(f.Root, DownloadCheckpointStore.DirectoryKey), "*.part"));
    }

    [Fact]
    public async Task Lease_lost_old_job_cannot_remove_a_successor_import_file()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = f.Seed();
        f.Primary.Candidates = [Candidate];
        using var oldWorker = Worker(f);
        using var newWorker = Worker(f);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scenario = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var oldHttp = new InspectingHttp(_ => PrefixResponse(new PausingBody(Audio, paused, continueOld)));
        using var newHttp = new InspectingHttp(request => Partial(request, Audio));
        var runningOld = Job(f, oldHttp, oldWorker).ProcessAsync(task.Id, scenario.Token);
        Task? runningNew = null;
        try
        {
            await paused.Task.WaitAsync(SynchronizationTimeout, scenario.Token);
            var oldAttempt = f.Reload(task.Id).AttemptId;
            f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.LeaseUntilUtc == DateTime.UtcNow.AddMinutes(-1))
                .Where(row => row.Id == task.Id).ExecuteCommand();
            runningNew = Job(f, newHttp, newWorker).ProcessAsync(task.Id, scenario.Token);
            await runningNew.WaitAsync(SynchronizationTimeout, scenario.Token);
            var item = f.Db.Queryable<LibraryImportItem>().Single();
            Assert.True(f.Storage.TryResolve(item.SourceStorageKey, out var imported));
            var nextAttempt = f.Reload(task.Id).AttemptId;
            continueOld.TrySetResult();
            await runningOld.WaitAsync(SynchronizationTimeout, scenario.Token);

            Assert.NotEqual(oldAttempt, nextAttempt);
            Assert.Equal(ExternalDownloadTaskStatuses.ImportQueued, f.Reload(task.Id).Status);
            Assert.Equal(nextAttempt, f.Reload(task.Id).AttemptId);
            Assert.True(File.Exists(imported));
            Assert.Equal(Audio, await File.ReadAllBytesAsync(imported));
        }
        finally
        {
            // A failed takeover or assertion must still release the suspended old worker.
            continueOld.TrySetResult();
            scenario.Cancel();
            try { await Task.WhenAll(runningOld, runningNew ?? Task.CompletedTask).WaitAsync(SynchronizationTimeout); }
            catch (OperationCanceledException) when (scenario.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task Checkpoint_quota_and_size_limits_never_prevent_a_normal_transfer_or_accept_oversized_prefixes()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = f.Seed(ExternalDownloadTaskStatuses.Downloading);
        task.WorkerId = "worker"; task.AttemptId = Guid.NewGuid(); task.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
        f.Db.Updateable(task).ExecuteCommand();
        var store = new DownloadCheckpointStore(f.Db, f.Storage, task, task.AttemptId.Value, "worker", maximumStoreBytes: 2);
        await using var file = new FileStream(LivePath(f, task.Id, task.AttemptId.Value), FileMode.Create, FileAccess.ReadWrite);
        await file.WriteAsync(Audio.AsMemory(0, 3));

        await store.PublishAsync(file, Candidate, "\"v1\"", Audio.Length, 3, Audio.Length, default);

        Assert.Null(f.Reload(task.Id).DownloadCheckpointJson);
        Assert.Equal(3, file.Position);
        var small = new DownloadCheckpointStore(f.Db, f.Storage, task, task.AttemptId.Value, "worker", maximumCheckpointBytes: 2);
        await small.PublishAsync(file, Candidate, "\"v1\"", Audio.Length, 3, Audio.Length, default);
        Assert.Null(f.Reload(task.Id).DownloadCheckpointJson);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("quota-lock")]
    public async Task Unavailable_checkpoint_storage_does_not_fail_an_otherwise_valid_download(string obstruction)
    {
        using var f = new SourceDownloadAcceptanceFixture();
        f.Primary.Candidates = [Candidate];
        var task = f.Seed();
        var checkpointDirectory = Path.Combine(f.Root, DownloadCheckpointStore.DirectoryKey);
        if (obstruction == "directory") await File.WriteAllTextAsync(checkpointDirectory, "occupied");
        else Directory.CreateDirectory(Path.Combine(checkpointDirectory, "quota.lock"));
        using var http = new InspectingHttp(_ => PrefixResponse(new ChunkedBody(Audio)));

        await Job(f, http, f.Lease()).ProcessAsync(task.Id, default);

        Assert.Equal(ExternalDownloadTaskStatuses.ImportQueued, f.Reload(task.Id).Status);
        Assert.Null(f.Reload(task.Id).DownloadCheckpointJson);
        Assert.Equal([null], http.Ranges);
        var item = f.Db.Queryable<LibraryImportItem>().Single();
        Assert.True(f.Storage.TryResolve(item.SourceStorageKey, out var output));
        Assert.Equal(Audio, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task Terminal_and_expired_orphan_checkpoints_are_cleaned_while_active_pointer_is_protected()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var task = await InterruptAsync(f);
        var directory = Path.Combine(f.Root, DownloadCheckpointStore.DirectoryKey);
        var pointPath = Directory.GetFiles(directory, "*.part").Single();
        task.Status = ExternalDownloadTaskStatuses.Failed;
        f.Db.Updateable(task).ExecuteCommand();
        var orphan = Path.Combine(directory, $"{Guid.NewGuid():N}.{Guid.NewGuid():N}.{Guid.NewGuid():N}.part.tmp");
        await File.WriteAllBytesAsync(orphan, [1, 2, 3]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));

        await DownloadCheckpointStore.SweepAsync(f.Db, f.Storage, default);

        Assert.Null(f.Reload(task.Id).DownloadCheckpointJson);
        Assert.False(File.Exists(pointPath));
        Assert.False(File.Exists(orphan));

        var active = await InterruptAsync(f);
        var snapshot = DownloadCheckpointStore.Parse(active.DownloadCheckpointJson)! with { CreatedAtUtc = DateTime.UtcNow.AddDays(-2) };
        active.DownloadCheckpointJson = JsonSerializer.Serialize(snapshot);
        active.WorkerId = "active"; active.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
        f.Db.Updateable(active).ExecuteCommand();
        var protectedPath = Directory.GetFiles(directory, "*.part").Single();
        File.SetLastWriteTimeUtc(protectedPath, DateTime.UtcNow.AddDays(-2));
        await DownloadCheckpointStore.SweepAsync(f.Db, f.Storage, default);
        Assert.NotNull(f.Reload(active.Id).DownloadCheckpointJson);
        Assert.True(File.Exists(protectedPath));
    }

    [Fact]
    public async Task Forged_cross_task_metadata_cannot_delete_another_tasks_snapshot_or_write_outside_attempt()
    {
        using var f = new SourceDownloadAcceptanceFixture();
        var victim = await InterruptAsync(f);
        var victimPath = Directory.GetFiles(Path.Combine(f.Root, DownloadCheckpointStore.DirectoryKey), "*.part").Single();
        var other = f.Seed(ExternalDownloadTaskStatuses.Downloading);
        other.WorkerId = "other"; other.AttemptId = Guid.NewGuid(); other.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
        other.DownloadCheckpointJson = victim.DownloadCheckpointJson;
        f.Db.Updateable(other).ExecuteCommand();
        var store = new DownloadCheckpointStore(f.Db, f.Storage, other, other.AttemptId.Value, "other");

        Assert.Null(await store.RestoreAsync(Candidate, LivePath(f, other.Id, other.AttemptId.Value), Audio.Length, default));
        Assert.True(File.Exists(victimPath));
        Assert.NotNull(f.Reload(victim.Id).DownloadCheckpointJson);
        await Assert.ThrowsAsync<SourceUnavailableException>(() => store.RestoreAsync(Candidate,
            Path.Combine(f.Root, "outside-attempt.flac"), Audio.Length, default));
        Assert.False(File.Exists(Path.Combine(f.Root, "outside-attempt.flac")));
    }

    private static async Task<ExternalDownloadTask> InterruptAsync(SourceDownloadAcceptanceFixture f)
    {
        f.Primary.Candidates = [Candidate];
        var task = f.Seed();
        using var cancellation = new CancellationTokenSource();
        using var http = new InspectingHttp(_ => PrefixResponse(new CancellingBody(Audio, cancellation)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Job(f, http, f.Lease()).ProcessAsync(task.Id, cancellation.Token));
        var interrupted = f.Reload(task.Id);
        Assert.NotNull(interrupted.DownloadCheckpointJson);
        return interrupted;
    }

    private static SourceDownloadJob Job(SourceDownloadAcceptanceFixture f, IHttpClientFactory http, MediaTaskLeaseService lease) =>
        new(f.Db, f.Aggregator(), f.Bus, http, Options.Create(f.Options), f.Storage, f.Media,
            NullLogger<SourceDownloadJob>.Instance, f.Access(), lease);

    // Match production scoped DI: transactional and fencing writes share one client.
    private static SourceDownloadJob Job(SourceDownloadAcceptanceFixture f, IHttpClientFactory http, IServiceProvider worker) =>
        new(worker.GetRequiredService<ISqlSugarClient>(), f.Aggregator(), f.Bus, http, Options.Create(f.Options), f.Storage, f.Media,
            NullLogger<SourceDownloadJob>.Instance, f.Access(), worker.GetRequiredService<MediaTaskLeaseService>());

    private static ServiceProvider Worker(SourceDownloadAcceptanceFixture f)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<MediaWorkerIdentity>();
        services.AddSingleton(Options.Create(new MediaLeaseOptions()));
        services.AddScoped<ISqlSugarClient>(_ => new SqlSugarClient(f.Db.CurrentConnectionConfig));
        services.AddScoped<MediaTaskLeaseService>();
        return services.BuildServiceProvider();
    }

    private static string LivePath(SourceDownloadAcceptanceFixture f, Guid task, Guid attempt)
    {
        var directory = Directory.CreateDirectory(Path.Combine(f.Root, "sourcedl")).FullName;
        return Path.Combine(directory, $"{task:N}.{attempt:N}.flac");
    }

    private static HttpResponseMessage Full(byte[] bytes, string tag = "\"v1\"") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), Headers = { ETag = EntityTagHeaderValue.Parse(tag) } };

    private static HttpResponseMessage Partial(HttpRequestMessage request, byte[] bytes)
    {
        var from = request.Headers.Range!.Ranges.Single().From!.Value;
        var response = Full(bytes[(int)from..]);
        response.StatusCode = HttpStatusCode.PartialContent;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
        return response;
    }

    private static HttpResponseMessage PrefixResponse(Stream stream) =>
        new(HttpStatusCode.OK) { Content = new PrefixContent(stream), Headers = { ETag = EntityTagHeaderValue.Parse("\"v1\"") } };

    private sealed class InspectingHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string?> Ranges { get; } = [];
        public List<string?> IfRanges { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Ranges.Add(request.Headers.Range?.ToString()); IfRanges.Add(request.Headers.IfRange?.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class PrefixContent(Stream body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = Audio.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => body.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(body);
        protected override void Dispose(bool disposing) { if (disposing) body.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CancellingBody(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return base.ReadAsync(buffer[..3], cancellationToken);
        }
    }

    private sealed class PausingBody(byte[] bytes, TaskCompletionSource paused, TaskCompletionSource resume) : MemoryStream(bytes)
    {
        private bool waited;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0) return await base.ReadAsync(buffer[..3], cancellationToken);
            if (!waited)
            {
                waited = true;
                paused.TrySetResult();
                await resume.Task.WaitAsync(SynchronizationTimeout, cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ChunkedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(Position == 0 ? buffer[..3] : buffer, cancellationToken);
    }

    private sealed class FailingPrefixBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) throw new IOException("interrupted network response");
            return base.ReadAsync(buffer[..3], cancellationToken);
        }
    }
}
