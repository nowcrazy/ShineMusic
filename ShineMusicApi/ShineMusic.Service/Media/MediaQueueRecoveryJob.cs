using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service.Messaging;
using ShineMusic.Service.SourceDownload;
using SqlSugar;

namespace ShineMusic.Service.Media;

/// <summary>由媒体调度方恢复已过期租约；绝不重置仍持有有效租约的工作。</summary>
public sealed class MediaQueueRecoveryJob(
    ISqlSugarClient db,
    IRabbitMessageBus messageBus,
    IOptions<MediaLeaseOptions> options,
    ILogger<MediaQueueRecoveryJob> logger,
    QuarantineStoragePaths? quarantinePaths = null)
{
    public async Task RecoverAsync(bool includeQueued, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var limit = Math.Clamp(options.Value.RecoveryBatchSize, 1, 1000);
        var expiredItems = await db.Queryable<LibraryImportItem>()
            .Where(item => !item.IsDeleted && (item.Status == LibraryImportItemStatuses.Validating
                    || item.Status == LibraryImportItemStatuses.ParsingMetadata
                    || item.Status == LibraryImportItemStatuses.Transcoding
                    || (item.Status == LibraryImportItemStatuses.ReadyToPublish && item.WorkerId != null))
                && (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
            .OrderBy(item => item.UpdatedAtUtc)
            .Take(limit)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var recoveredImports = 0;
        foreach (var itemId in expiredItems)
        {
            // The second predicate is essential: a heartbeat or another recoverer may have won since SELECT.
            var outboxId = await RecoverAndScheduleAsync(QueueNames.Imports, itemId, () => db.Updateable<LibraryImportItem>()
                .SetColumns(item => item.Status == LibraryImportItemStatuses.Uploaded)
                .SetColumns(item => item.TranscodeStatus == "queued")
                .SetColumns(item => item.WorkerId == (string?)null)
                .SetColumns(item => item.AttemptId == (Guid?)null)
                .SetColumns(item => item.LeaseUntilUtc == (DateTime?)null)
                .SetColumns(item => item.LastHeartbeatAtUtc == (DateTime?)null)
                .SetColumns(item => item.UpdatedAtUtc == now)
                .Where(item => item.Id == itemId && !item.IsDeleted
                    && (item.Status == LibraryImportItemStatuses.Validating
                        || item.Status == LibraryImportItemStatuses.ParsingMetadata
                        || item.Status == LibraryImportItemStatuses.Transcoding
                        || (item.Status == LibraryImportItemStatuses.ReadyToPublish && item.WorkerId != null))
                    && (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
                .ExecuteCommandAsync(cancellationToken), cancellationToken);
            if (outboxId is null) continue;
            recoveredImports++;
            await MessageOutboxDispatcher.TryDeliverAsync(db, messageBus, outboxId.Value, cancellationToken);
        }

        var expiredDownloads = await db.Queryable<ExternalDownloadTask>()
            .Where(task => task.Status == ExternalDownloadTaskStatuses.Downloading
                && (task.LeaseUntilUtc == null || task.LeaseUntilUtc <= now))
            .OrderBy(task => task.UpdatedAtUtc)
            .Take(limit)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
        var recoveredDownloads = 0;
        foreach (var taskId in expiredDownloads)
        {
            var outboxId = await RecoverAndScheduleAsync(QueueNames.Downloads, taskId, () => db.Updateable<ExternalDownloadTask>()
                .SetColumns(task => task.Status == ExternalDownloadTaskStatuses.Queued)
                .SetColumns(task => task.NextRetryAtUtc == (DateTime?)null)
                .SetColumns(task => task.WorkerId == (string?)null)
                .SetColumns(task => task.AttemptId == (Guid?)null)
                .SetColumns(task => task.LeaseUntilUtc == (DateTime?)null)
                .SetColumns(task => task.LastHeartbeatAtUtc == (DateTime?)null)
                .SetColumns(task => task.UpdatedAtUtc == now)
                .Where(task => task.Id == taskId && task.Status == ExternalDownloadTaskStatuses.Downloading
                    && (task.LeaseUntilUtc == null || task.LeaseUntilUtc <= now))
                .ExecuteCommandAsync(cancellationToken), cancellationToken);
            if (outboxId is null) continue;
            recoveredDownloads++;
            await MessageOutboxDispatcher.TryDeliverAsync(db, messageBus, outboxId.Value, cancellationToken);
        }

        if (includeQueued)
        {
            // Startup delivery is bounded too; regular retry sweeps recover any remaining queued rows.
            var importIds = await db.Queryable<LibraryImportItem>()
                .Where(item => !item.IsDeleted && item.Status == LibraryImportItemStatuses.Uploaded)
                .OrderBy(item => item.UpdatedAtUtc).Take(limit).Select(item => item.Id)
                .ToListAsync(cancellationToken);
            foreach (var itemId in importIds.Except(expiredItems))
            {
                var outboxId = await MessageOutbox.EnsureAsync(db, QueueNames.Imports, itemId, cancellationToken);
                if (outboxId.HasValue)
                    await MessageOutboxDispatcher.TryDeliverAsync(db, messageBus, outboxId.Value, cancellationToken);
            }
            var downloadIds = await db.Queryable<ExternalDownloadTask>()
                .Where(task => task.Status == ExternalDownloadTaskStatuses.Queued
                    && (task.NextRetryAtUtc == null || task.NextRetryAtUtc <= now))
                .OrderBy(task => task.UpdatedAtUtc).Take(limit).Select(task => task.Id)
                .ToListAsync(cancellationToken);
            foreach (var taskId in downloadIds.Except(expiredDownloads))
            {
                var outboxId = await MessageOutbox.EnsureAsync(db, QueueNames.Downloads, taskId, cancellationToken);
                if (outboxId.HasValue)
                    await MessageOutboxDispatcher.TryDeliverAsync(db, messageBus, outboxId.Value, cancellationToken);
            }
        }

        if (recoveredImports + recoveredDownloads > 0)
            logger.LogInformation("Recovered expired media leases (imports={Imports}, downloads={Downloads}).",
                recoveredImports, recoveredDownloads);
        if (quarantinePaths is not null)
            await DownloadCheckpointStore.SweepAsync(db, quarantinePaths, cancellationToken);
    }

    private async Task<Guid?> RecoverAndScheduleAsync(string queue, Guid taskId, Func<Task<int>> recover,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = db.Ado.Transaction is null;
        if (ownsTransaction) await db.Ado.BeginTranAsync();
        try
        {
            var changed = await recover();
            Guid? outboxId = changed == 1
                ? await MessageOutbox.EnqueueAsync(db, queue, taskId, cancellationToken)
                : null;
            if (ownsTransaction) await db.Ado.CommitTranAsync();
            return outboxId;
        }
        catch
        {
            if (ownsTransaction) await db.Ado.RollbackTranAsync();
            throw;
        }
    }
}
