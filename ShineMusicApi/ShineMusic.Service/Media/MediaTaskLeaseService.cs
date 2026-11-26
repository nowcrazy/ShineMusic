using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using SqlSugar;

namespace ShineMusic.Service.Media;

/// <summary>
/// 本进程的媒体 Worker 身份。每个进程只创建一个身份，任务租约和心跳都使用该值。
/// </summary>
public sealed class MediaWorkerIdentity
{
    public string Id { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
}

/// <summary>媒体任务已被另一个执行实例接管。</summary>
public sealed class MediaLeaseLostException(string taskType, Guid taskId) : InvalidOperationException(
    $"The {taskType} task {taskId} lease is no longer owned by this worker.");

/// <summary>
/// 下载任务和导入任务共用的租约/fencing 服务。
/// 所有认领都用数据库条件更新完成；所有执行中写入都必须带 WorkerId + AttemptId 条件。
/// </summary>
public sealed class MediaTaskLeaseService(
    ISqlSugarClient db,
    IServiceScopeFactory scopeFactory,
    MediaWorkerIdentity identity,
    IOptions<MediaLeaseOptions> options,
    ILogger<MediaTaskLeaseService> logger)
{
    private readonly MediaLeaseOptions _options = options.Value;

    public string WorkerId => identity.Id;

    public async Task<(bool Acquired, Guid AttemptId)> TryClaimExternalDownloadAsync(
        Guid taskId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var workerId = WorkerId;
        var attemptId = Guid.NewGuid();
        var leaseUntil = now.AddSeconds(Math.Clamp(_options.LeaseSeconds, 30, 3600));
        var affected = await db.Updateable<ExternalDownloadTask>()
            .SetColumns(task => task.Status == ExternalDownloadTaskStatuses.Downloading)
            .SetColumns(task => task.WorkerId == workerId)
            .SetColumns(task => task.AttemptId == attemptId)
            .SetColumns(task => task.LeaseUntilUtc == leaseUntil)
            .SetColumns(task => task.LastHeartbeatAtUtc == now)
            .SetColumns(task => task.UpdatedAtUtc == now)
            .Where(task => task.Id == taskId
                && (task.Status == ExternalDownloadTaskStatuses.Queued
                    || (task.Status == ExternalDownloadTaskStatuses.Downloading
                        && (task.LeaseUntilUtc == null || task.LeaseUntilUtc <= now)))
                && (task.LeaseUntilUtc == null || task.LeaseUntilUtc <= now)
                && (task.NextRetryAtUtc == null || task.NextRetryAtUtc <= now))
            .ExecuteCommandAsync(cancellationToken);
        return (affected == 1, attemptId);
    }

    public async Task<(bool Acquired, Guid AttemptId)> TryClaimImportAsync(
        Guid itemId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var workerId = WorkerId;
        var attemptId = Guid.NewGuid();
        var leaseUntil = now.AddSeconds(Math.Clamp(_options.LeaseSeconds, 30, 3600));
        var affected = await db.Updateable<LibraryImportItem>()
            .SetColumns(item => item.Status == LibraryImportItemStatuses.Validating)
            .SetColumns(item => item.WorkerId == workerId)
            .SetColumns(item => item.AttemptId == attemptId)
            .SetColumns(item => item.LeaseUntilUtc == leaseUntil)
            .SetColumns(item => item.LastHeartbeatAtUtc == now)
            .SetColumns(item => item.UpdatedAtUtc == now)
            .Where(item => item.Id == itemId && !item.IsDeleted && item.Status == LibraryImportItemStatuses.Uploaded
                && (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
            .ExecuteCommandAsync(cancellationToken);
        return (affected == 1, attemptId);
    }

    public async Task<(bool Acquired, Guid AttemptId)> TryClaimImportPublicationAsync(
        Guid itemId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var workerId = WorkerId;
        var attemptId = Guid.NewGuid();
        var leaseUntil = now.AddSeconds(Math.Clamp(_options.LeaseSeconds, 30, 3600));
        var affected = await db.Updateable<LibraryImportItem>()
            .SetColumns(item => item.Status == LibraryImportItemStatuses.ReadyToPublish)
            .SetColumns(item => item.IsMetadataReviewed == true)
            .SetColumns(item => item.WorkerId == workerId)
            .SetColumns(item => item.AttemptId == attemptId)
            .SetColumns(item => item.LeaseUntilUtc == leaseUntil)
            .SetColumns(item => item.LastHeartbeatAtUtc == now)
            .SetColumns(item => item.UpdatedAtUtc == now)
            .Where(item => item.Id == itemId && !item.IsDeleted && item.Status == LibraryImportItemStatuses.PendingReview
                && (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
            .ExecuteCommandAsync(cancellationToken);
        return (affected == 1, attemptId);
    }

    public async Task UpdateOwnedAsync(ExternalDownloadTask task, Guid attemptId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var workerId = WorkerId;
        var affected = await db.Updateable(task)
            .IgnoreColumns(row => new { row.WorkerId, row.AttemptId, row.LeaseUntilUtc, row.LastHeartbeatAtUtc })
            .Where(row => row.Id == task.Id && row.WorkerId == workerId && row.AttemptId == attemptId
                && row.LeaseUntilUtc != null && row.LeaseUntilUtc > now)
            .ExecuteCommandAsync(cancellationToken);
        if (affected != 1)
            throw new MediaLeaseLostException("download", task.Id);
    }

    public async Task UpdateOwnedAsync(LibraryImportItem item, Guid attemptId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var workerId = WorkerId;
        var affected = await db.Updateable(item)
            .IgnoreColumns(row => new { row.WorkerId, row.AttemptId, row.LeaseUntilUtc, row.LastHeartbeatAtUtc, row.IsDeleted })
            .Where(row => row.Id == item.Id && !row.IsDeleted && row.WorkerId == workerId && row.AttemptId == attemptId
                && row.LeaseUntilUtc != null && row.LeaseUntilUtc > now)
            .ExecuteCommandAsync(cancellationToken);
        if (affected != 1)
            throw new MediaLeaseLostException("import", item.Id);
    }

    public Task ReleaseExternalDownloadAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken)
    {
        var workerId = WorkerId;
        return db.Updateable<ExternalDownloadTask>()
            .SetColumns(task => task.WorkerId == null)
            .SetColumns(task => task.LeaseUntilUtc == (DateTime?)null)
            .SetColumns(task => task.LastHeartbeatAtUtc == (DateTime?)null)
            .Where(task => task.Id == taskId && task.WorkerId == workerId && task.AttemptId == attemptId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public Task ReleaseImportAsync(Guid itemId, Guid attemptId, CancellationToken cancellationToken)
    {
        var workerId = WorkerId;
        return db.Updateable<LibraryImportItem>()
            .SetColumns(item => item.WorkerId == null)
            .SetColumns(item => item.LeaseUntilUtc == (DateTime?)null)
            .SetColumns(item => item.LastHeartbeatAtUtc == (DateTime?)null)
            .Where(item => item.Id == itemId && item.WorkerId == workerId && item.AttemptId == attemptId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public Task<bool> RenewExternalDownloadAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken)
        => RenewExternalDownloadCoreAsync(taskId, attemptId, cancellationToken);

    public Task<bool> RenewImportAsync(Guid itemId, Guid attemptId, CancellationToken cancellationToken)
        => RenewImportCoreAsync(itemId, attemptId, cancellationToken);

    private async Task<bool> RenewExternalDownloadCoreAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var workerId = WorkerId;
        var leaseUntil = now.AddSeconds(Math.Clamp(_options.LeaseSeconds, 30, 3600));
        var affected = await db.Updateable<ExternalDownloadTask>()
            .SetColumns(task => task.LeaseUntilUtc == leaseUntil)
            .SetColumns(task => task.LastHeartbeatAtUtc == now)
            .SetColumns(task => task.UpdatedAtUtc == now)
            .Where(task => task.Id == taskId
                && task.WorkerId == workerId
                && task.AttemptId == attemptId
                && task.LeaseUntilUtc != null && task.LeaseUntilUtc > now)
            .ExecuteCommandAsync(cancellationToken);
        return affected == 1;
    }

    private async Task<bool> RenewImportCoreAsync(Guid itemId, Guid attemptId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var workerId = WorkerId;
        var leaseUntil = now.AddSeconds(Math.Clamp(_options.LeaseSeconds, 30, 3600));
        var affected = await db.Updateable<LibraryImportItem>()
            .SetColumns(item => item.LeaseUntilUtc == leaseUntil)
            .SetColumns(item => item.LastHeartbeatAtUtc == now)
            .SetColumns(item => item.UpdatedAtUtc == now)
            .Where(item => item.Id == itemId
                && item.WorkerId == workerId
                && item.AttemptId == attemptId
                && item.LeaseUntilUtc != null && item.LeaseUntilUtc > now)
            .ExecuteCommandAsync(cancellationToken);
        return affected == 1;
    }

    public LeaseHeartbeat StartExternalDownloadHeartbeat(Guid taskId, Guid attemptId, CancellationToken cancellationToken)
        => new(scopeFactory, taskId, attemptId, false, _options, logger, cancellationToken);

    public LeaseHeartbeat StartImportHeartbeat(Guid itemId, Guid attemptId, CancellationToken cancellationToken)
        => new(scopeFactory, itemId, attemptId, true, _options, logger, cancellationToken);

    /// <summary>周期性续租；心跳失败会取消工作令牌，调用方随后只能走失租约分支退出。</summary>
    public sealed class LeaseHeartbeat : IAsyncDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly Guid _taskId;
        private readonly Guid _attemptId;
        private readonly bool _isImport;
        private readonly MediaLeaseOptions _options;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _linkedCts;
        private readonly Task _loop;
        private int _lost;

        internal LeaseHeartbeat(
            IServiceScopeFactory scopeFactory,
            Guid taskId,
            Guid attemptId,
            bool isImport,
            MediaLeaseOptions options,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            _scopeFactory = scopeFactory;
            _taskId = taskId;
            _attemptId = attemptId;
            _isImport = isImport;
            _options = options;
            _logger = logger;
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = RunAsync(_linkedCts.Token);
        }

        public CancellationToken Token => _linkedCts.Token;
        public bool Lost => Volatile.Read(ref _lost) != 0;

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.HeartbeatSeconds, 5, 300)));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var lease = scope.ServiceProvider.GetRequiredService<MediaTaskLeaseService>();
                    var renewed = _isImport
                        ? await lease.RenewImportAsync(_taskId, _attemptId, cancellationToken)
                        : await lease.RenewExternalDownloadAsync(_taskId, _attemptId, cancellationToken);
                    if (renewed) continue;

                    Interlocked.Exchange(ref _lost, 1);
                    _logger.LogWarning("Media task {TaskId} lease lost for attempt {AttemptId}; stopping this execution.", _taskId, _attemptId);
                    _linkedCts.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Parent cancellation or lease loss; caller observes Token/Lost.
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _lost, 1);
                _logger.LogError(exception, "Media task {TaskId} heartbeat failed; stopping this execution.", _taskId);
                _linkedCts.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _linkedCts.Cancel();
            try { await _loop; } catch (OperationCanceledException) { }
            _linkedCts.Dispose();
        }
    }
}
