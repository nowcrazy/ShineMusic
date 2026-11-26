using System.Text;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service.Messaging;

/// <summary>失败消息先落库后确认；人工重放同事务记录 outbox 与审计。</summary>
public sealed class MessageDeadLetterStore(ISqlSugarClient db)
{
    private const int MaxRetainedBodyBytes = 4096;

    /// <summary>消息体最多保存 4096 字节并标记截断。调用方仅能在此方法成功后确认原 broker 消息。</summary>
    public Task<int> RecordAsync(string queue, ReadOnlyMemory<byte> body, string reason, CancellationToken cancellationToken)
    {
        if (!MessageOutbox.IsKnownQueue(queue))
            throw new ArgumentException("Only media queues can be retained as dead letters.", nameof(queue));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var retainedBody = body.Length <= MaxRetainedBodyBytes ? body : body[..MaxRetainedBodyBytes];
        // Prefix the truncation marker so even a long reason cannot hide that the retained body is incomplete.
        var failureReason = body.Length <= MaxRetainedBodyBytes
            ? reason
            : $"body_truncated({body.Length}->{MaxRetainedBodyBytes}); {reason}";
        return db.Insertable(new MessageDeadLetter
        {
            QueueName = queue,
            TaskId = body.Length <= MaxRetainedBodyBytes && TryParseTaskId(body.Span, out var taskId) ? taskId : null,
            BodyBase64 = Convert.ToBase64String(retainedBody.Span),
            FailureReason = failureReason.Length <= 1000 ? failureReason : failureReason[..1000]
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<MessageDeadLetterListDto> ListAsync(string? queue, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Queryable<MessageDeadLetter>();
        if (queue is not null) query = query.Where(row => row.QueueName == queue);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(row => row.FailedAtUtc).OrderBy(row => row.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var downloads = rows.Where(row => row.QueueName == QueueNames.Downloads && TryReadTaskId(row, out _))
            .Select(row => { TryReadTaskId(row, out var id); return id; }).Distinct().ToArray();
        var imports = rows.Where(row => row.QueueName == QueueNames.Imports && TryReadTaskId(row, out _))
            .Select(row => { TryReadTaskId(row, out var id); return id; }).Distinct().ToArray();
        var waitingDownloads = downloads.Length == 0 ? [] : await db.Queryable<ExternalDownloadTask>()
            .Where(task => downloads.Contains(task.Id) && task.Status == ExternalDownloadTaskStatuses.Queued
                && (task.NextRetryAtUtc == null || task.NextRetryAtUtc <= now)
                && (task.LeaseUntilUtc == null || task.LeaseUntilUtc <= now))
            .Select(task => task.Id).ToListAsync(cancellationToken);
        var waitingImports = imports.Length == 0 ? [] : await db.Queryable<LibraryImportItem>()
            .Where(item => imports.Contains(item.Id) && !item.IsDeleted && item.Status == LibraryImportItemStatuses.Uploaded
                && (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
            .Select(item => item.Id).ToListAsync(cancellationToken);
        return new(rows.Select(row => ToDto(row, now, row.QueueName == QueueNames.Downloads
            ? waitingDownloads : waitingImports)).ToList(), total, page, pageSize);
    }

    /// <summary>仅重放已知队列中未被工作进程认领的排队任务。失效租约的处理中状态先由 recovery 恢复。</summary>
    public async Task<DeadLetterReplayResult> ReplayAsync(Guid id, HttpContext context, CancellationToken cancellationToken)
    {
        var row = await db.Queryable<MessageDeadLetter>().FirstAsync(item => item.Id == id, cancellationToken);
        if (row is null) return new(DeadLetterReplayStatus.NotFound);
        if (!TryReadTaskId(row, out var taskId)) return new(DeadLetterReplayStatus.InvalidMessage);
        if (row.ReplayedAtUtc is not null) return new(DeadLetterReplayStatus.Conflict);

        var now = DateTime.UtcNow;
        var leaseId = Guid.NewGuid();
        var leaseUntil = now.AddMinutes(2);
        await db.Ado.BeginTranAsync();
        try
        {
            var changed = await db.Updateable<MessageDeadLetter>()
                .SetColumns(item => item.ReplayLeaseId == leaseId)
                .SetColumns(item => item.ReplayLeaseUntilUtc == leaseUntil)
                .Where(item => item.Id == id && item.ReplayedAtUtc == null
                    && (item.ReplayLeaseUntilUtc == null || item.ReplayLeaseUntilUtc <= now))
                .ExecuteCommandAsync(cancellationToken);
            if (changed != 1 || !await LockWaitingTaskAsync(row.QueueName, taskId, now, cancellationToken))
            {
                await db.Ado.RollbackTranAsync();
                return new(DeadLetterReplayStatus.Conflict);
            }
            var outboxId = await MessageOutbox.EnqueueAsync(db, row.QueueName, taskId, cancellationToken);
            await db.Updateable<MessageDeadLetter>()
                .SetColumns(item => item.ReplayCount == item.ReplayCount + 1)
                .SetColumns(item => item.LastReplayAtUtc == now)
                .SetColumns(item => item.ReplayedAtUtc == now)
                .SetColumns(item => item.ReplayOutboxId == outboxId)
                .SetColumns(item => item.ReplayLeaseId == (Guid?)null)
                .SetColumns(item => item.ReplayLeaseUntilUtc == (DateTime?)null)
                .SetColumns(item => item.LastReplayError == (string?)null)
                .Where(item => item.Id == id && item.ReplayLeaseId == leaseId)
                .ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(context, db, "message.dead_letter_replay_scheduled", "message_dead_letter", id,
                new { row.QueueName, taskId, outboxId });
            await db.Ado.CommitTranAsync();
            return new(DeadLetterReplayStatus.Scheduled, outboxId);
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<FailedMessageOutboxListDto> ListFailedOutboxAsync(string? queue, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Queryable<MessageOutboxEntry>().Where(row => row.Status == "failed");
        if (queue is not null) query = query.Where(row => row.QueueName == queue);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.AvailableAtUtc).OrderBy(row => row.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(row => new FailedMessageOutboxDto(row.Id, row.QueueName, row.TaskId,
            row.Attempts, ToUtcOffset(row.CreatedAtUtc)!.Value, ToUtcOffset(row.AvailableAtUtc)!.Value, row.LastError))
            .ToList(), total, page, pageSize);
    }

    /// <summary>仅 failed 且无投递租约的记录可重试；不删除任何 sent 历史。</summary>
    public async Task<DeadLetterReplayResult> RetryOutboxAsync(Guid id, HttpContext context, CancellationToken cancellationToken)
    {
        var row = await db.Queryable<MessageOutboxEntry>().FirstAsync(item => item.Id == id, cancellationToken);
        if (row is null) return new(DeadLetterReplayStatus.NotFound);
        if (!MessageOutbox.IsKnownQueue(row.QueueName)) return new(DeadLetterReplayStatus.InvalidMessage);
        var now = DateTime.UtcNow;
        await db.Ado.BeginTranAsync();
        try
        {
            var changed = await db.Updateable<MessageOutboxEntry>()
                .SetColumns(item => item.Status == "pending")
                .SetColumns(item => item.Attempts == 0)
                .SetColumns(item => item.AvailableAtUtc == now)
                .SetColumns(item => item.ClaimId == (Guid?)null)
                .SetColumns(item => item.LeaseUntilUtc == (DateTime?)null)
                .SetColumns(item => item.LastError == (string?)null)
                .Where(item => item.Id == id && item.Status == "failed" && item.Attempts == row.Attempts
                    && (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
                .ExecuteCommandAsync(cancellationToken);
            if (changed != 1 || !await LockWaitingTaskAsync(row.QueueName, row.TaskId, now, cancellationToken))
            {
                await db.Ado.RollbackTranAsync();
                return new(DeadLetterReplayStatus.Conflict);
            }
            AdminAudit.Add(context, db, "message.outbox_retry_scheduled", "message_outbox", id,
                new { row.QueueName, row.TaskId, previousAttempts = row.Attempts, previousError = row.LastError });
            await db.Ado.CommitTranAsync();
            return new(DeadLetterReplayStatus.Scheduled, id);
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private async Task<bool> LockWaitingTaskAsync(string queue, Guid taskId, DateTime now, CancellationToken cancellationToken)
    {
        // A conditional self-update locks the row until outbox and audit commit. It cannot reset a live worker.
        var changed = queue == QueueNames.Downloads
            ? await db.Updateable<ExternalDownloadTask>()
                .SetColumns(task => task.UpdatedAtUtc == task.UpdatedAtUtc)
                .Where(task => task.Id == taskId && task.Status == ExternalDownloadTaskStatuses.Queued
                    && (task.NextRetryAtUtc == null || task.NextRetryAtUtc <= now)
                    && (task.LeaseUntilUtc == null || task.LeaseUntilUtc <= now))
                .ExecuteCommandAsync(cancellationToken)
            : await db.Updateable<LibraryImportItem>()
                .SetColumns(item => item.UpdatedAtUtc == item.UpdatedAtUtc)
                .Where(item => item.Id == taskId && !item.IsDeleted && item.Status == LibraryImportItemStatuses.Uploaded
                    && (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
                .ExecuteCommandAsync(cancellationToken);
        return changed == 1;
    }

    private static MessageDeadLetterDto ToDto(MessageDeadLetter row, DateTime now, IReadOnlyList<Guid> waitingTaskIds) => new(
        row.Id, row.QueueName, row.TaskId, row.BodyBase64, row.FailureReason, ToUtcOffset(row.FailedAtUtc)!.Value,
        row.ReplayCount, ToUtcOffset(row.LastReplayAtUtc), ToUtcOffset(row.ReplayedAtUtc), row.ReplayOutboxId,
        ToUtcOffset(row.ReplayLeaseUntilUtc), row.LastReplayError,
        row.ReplayedAtUtc is null && (row.ReplayLeaseUntilUtc is null || row.ReplayLeaseUntilUtc <= now)
            && TryReadTaskId(row, out var taskId) && waitingTaskIds.Contains(taskId));

    private static DateTimeOffset? ToUtcOffset(DateTime? value) => value is { } time
        ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null;

    private static bool TryReadTaskId(MessageDeadLetter row, out Guid taskId)
    {
        taskId = Guid.Empty;
        if (!MessageOutbox.IsKnownQueue(row.QueueName) || row.TaskId is null) return false;
        try
        {
            return TryParseTaskId(Convert.FromBase64String(row.BodyBase64), out taskId) && row.TaskId == taskId;
        }
        catch (FormatException) { return false; }
    }

    private static bool TryParseTaskId(ReadOnlySpan<byte> body, out Guid taskId)
    {
        taskId = Guid.Empty;
        // N is exactly 32 ASCII bytes. Do not let Guid's surrounding-whitespace handling turn
        // a truncated or padded malformed body into an apparently valid task trigger.
        return body.Length == 32 && Guid.TryParseExact(Encoding.UTF8.GetString(body), "N", out taskId)
            && taskId != Guid.Empty;
    }
}

public enum DeadLetterReplayStatus { Scheduled, NotFound, InvalidMessage, Conflict }
public sealed record DeadLetterReplayResult(DeadLetterReplayStatus Status, Guid? OutboxId = null);
