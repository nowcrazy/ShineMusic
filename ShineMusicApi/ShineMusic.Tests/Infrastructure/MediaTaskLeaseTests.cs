using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service.Media;
using ShineMusic.Service.Messaging;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

/// <summary>用独立 SQLite 连接验证生产租约认领、心跳和恢复的 fencing 边界。</summary>
public sealed class MediaTaskLeaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_does_not_reset_active_lease_even_when_last_business_update_is_old(bool import)
    {
        using var f = new LeaseFixture();
        var id = f.Seed(import);
        var claim = await LeaseFixture.ClaimAsync(f.First, import, id);
        Assert.True(claim.Acquired);
        var old = DateTime.UtcNow.AddDays(-2);
        if (import)
            f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.UpdatedAtUtc == old)
                .Where(row => row.Id == id).ExecuteCommand();
        else
            f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.UpdatedAtUtc == old)
                .Where(row => row.Id == id).ExecuteCommand();
        var before = f.Snapshot(import, id);

        await f.Recovery.RecoverAsync(includeQueued: true, default);

        Assert.Equal(before, f.Snapshot(import, id));
        Assert.Empty(f.Bus.Messages);
    }

    [Theory]
    [InlineData(LibraryImportItemStatuses.Validating, false)]
    [InlineData(LibraryImportItemStatuses.ParsingMetadata, false)]
    [InlineData(LibraryImportItemStatuses.Transcoding, false)]
    [InlineData(LibraryImportItemStatuses.ReadyToPublish, false)]
    [InlineData(LibraryImportItemStatuses.Validating, true)]
    [InlineData(LibraryImportItemStatuses.ParsingMetadata, true)]
    [InlineData(LibraryImportItemStatuses.Transcoding, true)]
    [InlineData(LibraryImportItemStatuses.ReadyToPublish, true)]
    public async Task Recovery_requeues_only_expired_or_legacy_import_attempts(string status, bool legacy)
    {
        using var f = new LeaseFixture();
        var id = f.Seed(true, status);
        f.SetOwner(true, id, legacy ? null : DateTime.UtcNow.AddMinutes(-1));

        await f.Recovery.RecoverAsync(includeQueued: false, default);

        var row = f.Import(id);
        Assert.Equal(LibraryImportItemStatuses.Uploaded, row.Status);
        Assert.Equal("queued", row.TranscodeStatus);
        Assert.Null(row.WorkerId);
        Assert.Null(row.AttemptId);
        Assert.Null(row.LeaseUntilUtc);
        Assert.Null(row.LastHeartbeatAtUtc);
        Assert.Equal((QueueNames.Imports, id), Assert.Single(f.Bus.Messages));
        await f.Recovery.RecoverAsync(includeQueued: false, default);
        Assert.Single(f.Bus.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_requeues_expired_or_legacy_download_attempts(bool legacy)
    {
        using var f = new LeaseFixture();
        var id = f.Seed(false, ExternalDownloadTaskStatuses.Downloading);
        f.SetOwner(false, id, legacy ? null : DateTime.UtcNow.AddMinutes(-1));
        var retry = DateTime.UtcNow.AddHours(1);
        f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.NextRetryAtUtc == retry)
            .Where(row => row.Id == id).ExecuteCommand();

        await f.Recovery.RecoverAsync(includeQueued: false, default);

        var row = f.Download(id);
        Assert.Equal(ExternalDownloadTaskStatuses.Queued, row.Status);
        Assert.Null(row.NextRetryAtUtc);
        Assert.Null(row.WorkerId);
        Assert.Null(row.AttemptId);
        Assert.Null(row.LeaseUntilUtc);
        Assert.Null(row.LastHeartbeatAtUtc);
        Assert.Equal((QueueNames.Downloads, id), Assert.Single(f.Bus.Messages));
        await f.Recovery.RecoverAsync(includeQueued: false, default);
        Assert.Single(f.Bus.Messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_workers_claiming_the_same_task_have_exactly_one_winner(bool import)
    {
        using var f = new LeaseFixture();
        var id = f.Seed(import);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await start.Task; return await LeaseFixture.ClaimAsync(f.First, import, id); });
        var second = Task.Run(async () => { await start.Task; return await LeaseFixture.ClaimAsync(f.Second, import, id); });
        start.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Acquired);
        var winner = results[0].Acquired ? f.First : f.Second;
        var attempt = results.Single(result => result.Acquired).AttemptId;
        var row = f.Snapshot(import, id);
        Assert.Equal(winner.Lease.WorkerId, row.WorkerId);
        Assert.Equal(attempt, row.AttemptId);
        Assert.True(row.LeaseUntilUtc > DateTime.UtcNow);
        Assert.NotNull(row.LastHeartbeatAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_task_reset_to_queued_cannot_be_claimed_while_its_existing_lease_is_active(bool import)
    {
        using var f = new LeaseFixture();
        var id = f.Seed(import);
        var owner = await LeaseFixture.ClaimAsync(f.First, import, id);
        Assert.True(owner.Acquired);
        if (import)
            f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.Status == LibraryImportItemStatuses.Uploaded)
                .Where(row => row.Id == id).ExecuteCommand();
        else
            f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.Status == ExternalDownloadTaskStatuses.Queued)
                .Where(row => row.Id == id).ExecuteCommand();
        var before = f.Snapshot(import, id);

        var claimant = await LeaseFixture.ClaimAsync(f.Second, import, id);

        Assert.False(claimant.Acquired);
        Assert.Equal(before, f.Snapshot(import, id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saving_a_stale_entity_preserves_renewed_lease_and_heartbeat(bool import)
    {
        using var f = new LeaseFixture();
        var id = f.Seed(import);
        var claim = await LeaseFixture.ClaimAsync(f.First, import, id);
        f.SetLeaseTimes(import, id, DateTime.UtcNow.AddSeconds(10), DateTime.UtcNow.AddMinutes(-1));
        var staleImport = import ? f.Import(id) : null;
        var staleDownload = import ? null : f.Download(id);
        var before = f.Snapshot(import, id);

        Assert.True(await LeaseFixture.RenewAsync(f.First, import, id, claim.AttemptId));
        var renewed = f.Snapshot(import, id);
        Assert.True(renewed.LeaseUntilUtc > before.LeaseUntilUtc);
        Assert.True(renewed.LastHeartbeatAtUtc > before.LastHeartbeatAtUtc);
        if (import)
        {
            staleImport!.Title = "saved business result";
            await f.First.Lease.UpdateOwnedAsync(staleImport, claim.AttemptId, default);
        }
        else
        {
            staleDownload!.Title = "saved business result";
            await f.First.Lease.UpdateOwnedAsync(staleDownload, claim.AttemptId, default);
        }

        var saved = f.Snapshot(import, id);
        Assert.Equal("saved business result", saved.Title);
        Assert.Equal(renewed.WorkerId, saved.WorkerId);
        Assert.Equal(renewed.AttemptId, saved.AttemptId);
        Assert.Equal(renewed.LeaseUntilUtc, saved.LeaseUntilUtc);
        Assert.Equal(renewed.LastHeartbeatAtUtc, saved.LastHeartbeatAtUtc);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Late_attempt_cannot_save_renew_or_release_a_reclaimed_task(bool import, bool sameWorker)
    {
        using var f = new LeaseFixture();
        var id = f.Seed(import);
        var oldClaim = await LeaseFixture.ClaimAsync(f.First, import, id);
        var staleImport = import ? f.Import(id) : null;
        var staleDownload = import ? null : f.Download(id);
        f.SetLeaseTimes(import, id, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(-2));
        await f.Recovery.RecoverAsync(includeQueued: false, default);
        var nextWorker = sameWorker ? f.First : f.Second;
        var newClaim = await LeaseFixture.ClaimAsync(nextWorker, import, id);
        Assert.True(newClaim.Acquired);
        Assert.NotEqual(oldClaim.AttemptId, newClaim.AttemptId);
        var newAttempt = f.Snapshot(import, id);

        if (import)
        {
            staleImport!.Title = "late import result";
            staleImport.Status = LibraryImportItemStatuses.Published;
            await Assert.ThrowsAsync<MediaLeaseLostException>(() =>
                f.First.Lease.UpdateOwnedAsync(staleImport, oldClaim.AttemptId, default));
        }
        else
        {
            staleDownload!.Title = "late download result";
            staleDownload.Status = ExternalDownloadTaskStatuses.Completed;
            await Assert.ThrowsAsync<MediaLeaseLostException>(() =>
                f.First.Lease.UpdateOwnedAsync(staleDownload, oldClaim.AttemptId, default));
        }
        Assert.False(await LeaseFixture.RenewAsync(f.First, import, id, oldClaim.AttemptId));
        await LeaseFixture.ReleaseAsync(f.First, import, id, oldClaim.AttemptId);

        Assert.Equal(newAttempt, f.Snapshot(import, id));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task An_expired_or_missing_lease_cannot_renew_or_save_before_recovery_runs(bool import, bool missingLease)
    {
        using var f = new LeaseFixture();
        var id = f.Seed(import);
        var claim = await LeaseFixture.ClaimAsync(f.First, import, id);
        f.SetLeaseTimes(import, id, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(-2));
        if (missingLease)
        {
            if (import)
                f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.LeaseUntilUtc == (DateTime?)null)
                    .Where(row => row.Id == id).ExecuteCommand();
            else
                f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.LeaseUntilUtc == (DateTime?)null)
                    .Where(row => row.Id == id).ExecuteCommand();
        }
        var before = f.Snapshot(import, id);

        Assert.False(await LeaseFixture.RenewAsync(f.First, import, id, claim.AttemptId));
        if (import)
        {
            var row = f.Import(id);
            row.Title = "expired result";
            await Assert.ThrowsAsync<MediaLeaseLostException>(() =>
                f.First.Lease.UpdateOwnedAsync(row, claim.AttemptId, default));
        }
        else
        {
            var row = f.Download(id);
            row.Title = "expired result";
            await Assert.ThrowsAsync<MediaLeaseLostException>(() =>
                f.First.Lease.UpdateOwnedAsync(row, claim.AttemptId, default));
        }
        Assert.Equal(before, f.Snapshot(import, id));
    }

    [Fact]
    public async Task Publication_claim_moves_pending_review_to_ready_and_can_renew()
    {
        using var f = new LeaseFixture();
        var id = f.Seed(true, LibraryImportItemStatuses.PendingReview);

        var claim = await f.First.Lease.TryClaimImportPublicationAsync(id, default);

        Assert.True(claim.Acquired);
        var row = f.Import(id);
        Assert.Equal(LibraryImportItemStatuses.ReadyToPublish, row.Status);
        Assert.True(row.IsMetadataReviewed);
        Assert.Equal(f.First.Lease.WorkerId, row.WorkerId);
        Assert.Equal(claim.AttemptId, row.AttemptId);
        Assert.True(row.LeaseUntilUtc > DateTime.UtcNow);
        Assert.True(await f.First.Lease.RenewImportAsync(id, claim.AttemptId, default));
        Assert.False((await f.Second.Lease.TryClaimImportPublicationAsync(id, default)).Acquired);
        Assert.False((await f.Second.Lease.TryClaimImportAsync(id, default)).Acquired);
        await f.Recovery.RecoverAsync(includeQueued: true, default);
        Assert.Equal(LibraryImportItemStatuses.ReadyToPublish, f.Import(id).Status);
        Assert.Empty(f.Bus.Messages);
    }

    [Fact]
    public async Task Heartbeats_renew_in_independent_scopes_and_cancel_both_jobs_after_attempt_ownership_changes()
    {
        using var f = new LeaseFixture(heartbeatSeconds: 5);
        var importId = f.Seed(true);
        var downloadId = f.Seed(false);
        var importClaim = await LeaseFixture.ClaimAsync(f.First, true, importId);
        var downloadClaim = await LeaseFixture.ClaimAsync(f.First, false, downloadId);
        Assert.True(importClaim.Acquired);
        Assert.True(downloadClaim.Acquired);
        var importBefore = f.Snapshot(true, importId);
        var downloadBefore = f.Snapshot(false, downloadId);
        Assert.Equal(1, f.First.CreatedClientCount);
        var deadline = DateTime.UtcNow.AddSeconds(18);
        await using var importHeartbeat = f.First.Lease.StartImportHeartbeat(importId, importClaim.AttemptId, default);
        await using var downloadHeartbeat = f.First.Lease.StartExternalDownloadHeartbeat(downloadId, downloadClaim.AttemptId, default);

        await WaitUntilAsync(() =>
            f.Import(importId).LastHeartbeatAtUtc > importBefore.LastHeartbeatAtUtc
            && f.Download(downloadId).LastHeartbeatAtUtc > downloadBefore.LastHeartbeatAtUtc,
            deadline, "Both real heartbeat loops should renew their task before the deadline.");

        Assert.True(f.First.CreatedClientCount >= 3, "Each heartbeat should resolve a fresh scoped database client.");
        Assert.True(f.Import(importId).LeaseUntilUtc > importBefore.LeaseUntilUtc);
        Assert.True(f.Download(downloadId).LeaseUntilUtc > downloadBefore.LeaseUntilUtc);
        Assert.False(importHeartbeat.Lost);
        Assert.False(downloadHeartbeat.Lost);
        Assert.False(importHeartbeat.Token.IsCancellationRequested);
        Assert.False(downloadHeartbeat.Token.IsCancellationRequested);
        var nextImportAttempt = Guid.NewGuid();
        var nextDownloadAttempt = Guid.NewGuid();
        f.Db.Updateable<LibraryImportItem>().SetColumns(row => row.AttemptId == nextImportAttempt)
            .Where(row => row.Id == importId).ExecuteCommand();
        f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.AttemptId == nextDownloadAttempt)
            .Where(row => row.Id == downloadId).ExecuteCommand();
        var importReclaimed = f.Snapshot(true, importId);
        var downloadReclaimed = f.Snapshot(false, downloadId);

        await WaitUntilAsync(() => importHeartbeat.Lost && downloadHeartbeat.Lost
            && importHeartbeat.Token.IsCancellationRequested && downloadHeartbeat.Token.IsCancellationRequested,
            deadline, "Both heartbeat loops should detect changed AttemptId and cancel their work token.");

        Assert.Equal(importReclaimed, f.Snapshot(true, importId));
        Assert.Equal(downloadReclaimed, f.Snapshot(false, downloadId));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, DateTime deadline, string failure)
    {
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Startup_recovery_delivers_queued_work_but_preserves_future_retry_and_unowned_ready_items()
    {
        using var f = new LeaseFixture();
        var itemId = f.Seed(true);
        var downloadId = f.Seed(false);
        var futureId = f.Seed(false);
        var readyId = f.Seed(true, LibraryImportItemStatuses.ReadyToPublish);
        var later = DateTime.UtcNow.AddHours(1);
        f.Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.NextRetryAtUtc == later)
            .Where(row => row.Id == futureId).ExecuteCommand();

        await f.Recovery.RecoverAsync(includeQueued: true, default);

        Assert.Equal(2, f.Bus.Messages.Count);
        Assert.Contains((QueueNames.Imports, itemId), f.Bus.Messages);
        Assert.Contains((QueueNames.Downloads, downloadId), f.Bus.Messages);
        Assert.Equal(later, f.Download(futureId).NextRetryAtUtc);
        Assert.Equal(LibraryImportItemStatuses.ReadyToPublish, f.Import(readyId).Status);
        Assert.Null(f.Import(readyId).WorkerId);
    }

    private sealed class LeaseFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "shine-media-lease-" + Guid.NewGuid().ToString("N"));
        private readonly string _connectionString;
        private readonly MediaLeaseOptions _options;
        public SqlSugarClient Db { get; }
        public TestWorker First { get; }
        public TestWorker Second { get; }
        public LeaseBus Bus { get; } = new();
        public MediaQueueRecoveryJob Recovery { get; }

        public LeaseFixture(int heartbeatSeconds = 15)
        {
            Directory.CreateDirectory(_root);
            _options = new MediaLeaseOptions { HeartbeatSeconds = heartbeatSeconds };
            _connectionString = $"Data Source={Path.Combine(_root, "leases.db")};Pooling=False;Default Timeout=10";
            Db = NewClient();
            Db.CodeFirst.InitTables<LibraryImportItem, ExternalDownloadTask, MessageOutboxEntry>();
            Db.CodeFirst.InitTables<MessageDeadLetter>();
            First = NewWorker();
            Second = NewWorker();
            Recovery = new MediaQueueRecoveryJob(Db, Bus, Options.Create(_options),
                NullLogger<MediaQueueRecoveryJob>.Instance);
        }

        private SqlSugarClient NewClient() => new(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.DbTableName = JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                EntityService = (property, column) => column.DbColumnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
            }
        });

        private TestWorker NewWorker()
        {
            var createdClients = 0;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new MediaWorkerIdentity());
            services.AddSingleton(Options.Create(_options));
            services.AddScoped<ISqlSugarClient>(_ =>
            {
                Interlocked.Increment(ref createdClients);
                return NewClient();
            });
            services.AddScoped<MediaTaskLeaseService>();
            return new TestWorker(services.BuildServiceProvider(), () => Volatile.Read(ref createdClients));
        }

        public Guid Seed(bool import, string? status = null)
        {
            if (import)
            {
                var row = new LibraryImportItem
                {
                    DisplayName = "lease-test.flac", SourceStorageKey = "lease-test.flac", ContentHash = Guid.NewGuid().ToString("N"),
                    Status = status ?? LibraryImportItemStatuses.Uploaded, BatchId = Guid.NewGuid(), TranscodeStatus = "processing"
                };
                Db.Insertable(row).ExecuteCommand();
                return row.Id;
            }
            var task = new ExternalDownloadTask
            {
                SourceProvider = "test", ProviderTrackId = Guid.NewGuid().ToString("N"),
                Status = status ?? ExternalDownloadTaskStatuses.Queued, CreatedByUserId = Guid.NewGuid()
            };
            Db.Insertable(task).ExecuteCommand();
            return task.Id;
        }

        public void SetOwner(bool import, Guid id, DateTime? leaseUntil)
        {
            var workerId = First.Lease.WorkerId;
            var attempt = Guid.NewGuid();
            var heartbeat = DateTime.UtcNow.AddMinutes(-2);
            if (import)
                Db.Updateable<LibraryImportItem>().SetColumns(row => row.WorkerId == workerId)
                    .SetColumns(row => row.AttemptId == attempt).SetColumns(row => row.LeaseUntilUtc == leaseUntil)
                    .SetColumns(row => row.LastHeartbeatAtUtc == heartbeat).Where(row => row.Id == id).ExecuteCommand();
            else
                Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.WorkerId == workerId)
                    .SetColumns(row => row.AttemptId == attempt).SetColumns(row => row.LeaseUntilUtc == leaseUntil)
                    .SetColumns(row => row.LastHeartbeatAtUtc == heartbeat).Where(row => row.Id == id).ExecuteCommand();
        }

        public void SetLeaseTimes(bool import, Guid id, DateTime leaseUntil, DateTime heartbeat)
        {
            if (import)
                Db.Updateable<LibraryImportItem>().SetColumns(row => row.LeaseUntilUtc == leaseUntil)
                    .SetColumns(row => row.LastHeartbeatAtUtc == heartbeat).Where(row => row.Id == id).ExecuteCommand();
            else
                Db.Updateable<ExternalDownloadTask>().SetColumns(row => row.LeaseUntilUtc == leaseUntil)
                    .SetColumns(row => row.LastHeartbeatAtUtc == heartbeat).Where(row => row.Id == id).ExecuteCommand();
        }

        public LibraryImportItem Import(Guid id) => Db.Queryable<LibraryImportItem>().Single(row => row.Id == id);
        public ExternalDownloadTask Download(Guid id) => Db.Queryable<ExternalDownloadTask>().Single(row => row.Id == id);
        public LeaseSnapshot Snapshot(bool import, Guid id)
        {
            if (import)
            {
                var row = Import(id);
                return new(row.Status, row.Title, row.WorkerId, row.AttemptId, row.LeaseUntilUtc, row.LastHeartbeatAtUtc, row.UpdatedAtUtc);
            }
            var task = Download(id);
            return new(task.Status, task.Title, task.WorkerId, task.AttemptId, task.LeaseUntilUtc, task.LastHeartbeatAtUtc, task.UpdatedAtUtc);
        }

        public static Task<(bool Acquired, Guid AttemptId)> ClaimAsync(TestWorker worker, bool import, Guid id) => import
            ? worker.Lease.TryClaimImportAsync(id, default)
            : worker.Lease.TryClaimExternalDownloadAsync(id, default);

        public static Task<bool> RenewAsync(TestWorker worker, bool import, Guid id, Guid attempt) => import
            ? worker.Lease.RenewImportAsync(id, attempt, default)
            : worker.Lease.RenewExternalDownloadAsync(id, attempt, default);

        public static Task ReleaseAsync(TestWorker worker, bool import, Guid id, Guid attempt) => import
            ? worker.Lease.ReleaseImportAsync(id, attempt, default)
            : worker.Lease.ReleaseExternalDownloadAsync(id, attempt, default);

        public void Dispose()
        {
            First.Dispose();
            Second.Dispose();
            Db.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestWorker : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        private readonly Func<int> _createdClientCount;
        public MediaTaskLeaseService Lease { get; }
        public int CreatedClientCount => _createdClientCount();
        public TestWorker(ServiceProvider provider, Func<int> createdClientCount)
        {
            _provider = provider;
            _createdClientCount = createdClientCount;
            _scope = provider.CreateScope();
            Lease = _scope.ServiceProvider.GetRequiredService<MediaTaskLeaseService>();
        }
        public void Dispose() { _scope.Dispose(); _provider.Dispose(); }
    }

    private sealed class LeaseBus : IRabbitMessageBus
    {
        public List<(string Queue, Guid Id)> Messages { get; } = [];
        public Task PublishAsync(string queue, Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add((queue, id));
            return Task.CompletedTask;
        }
    }

    private sealed record LeaseSnapshot(string Status, string? Title, string? WorkerId, Guid? AttemptId,
        DateTime? LeaseUntilUtc, DateTime? LastHeartbeatAtUtc, DateTime UpdatedAtUtc);
}
