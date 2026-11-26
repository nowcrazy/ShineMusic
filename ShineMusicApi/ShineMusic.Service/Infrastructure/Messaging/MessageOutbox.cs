using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service.Messaging;

/// <summary>Transaction-aware outbox writer. The caller's transaction is never committed here.</summary>
public static class MessageOutbox
{
    public static bool IsKnownQueue(string queue) => queue is QueueNames.Downloads or QueueNames.Imports;

    public static async Task<Guid> EnqueueAsync(ISqlSugarClient db, string queue, Guid taskId,
        CancellationToken cancellationToken, DateTime? availableAtUtc = null)
    {
        if (!IsKnownQueue(queue)) throw new ArgumentException("Unknown media queue.", nameof(queue));
        if (db.Ado.Transaction is null)
            throw new InvalidOperationException("Outbox scheduling must share the business transaction.");
        var entry = new MessageOutboxEntry { QueueName = queue, TaskId = taskId,
            AvailableAtUtc = availableAtUtc ?? DateTime.UtcNow };
        await db.Insertable(entry).ExecuteCommandAsync(cancellationToken);
        return entry.Id;
    }

    /// <summary>Run a state mutation and schedule its trigger atomically, joining an existing transaction.</summary>
    public static async Task<Guid> MutateAsync(ISqlSugarClient db, string queue, Guid taskId,
        Func<Task> mutation, CancellationToken cancellationToken, DateTime? availableAtUtc = null)
    {
        var ownsTransaction = db.Ado.Transaction is null;
        if (ownsTransaction) await db.Ado.BeginTranAsync();
        try
        {
            await mutation();
            var id = await EnqueueAsync(db, queue, taskId, cancellationToken, availableAtUtc);
            if (ownsTransaction) await db.Ado.CommitTranAsync();
            return id;
        }
        catch
        {
            if (ownsTransaction) await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public static async Task<Guid?> MutateIfAsync(ISqlSugarClient db, string queue, Guid taskId,
        Func<Task<bool>> mutation, CancellationToken cancellationToken)
    {
        var ownsTransaction = db.Ado.Transaction is null;
        if (ownsTransaction) await db.Ado.BeginTranAsync();
        try
        {
            var changed = await mutation();
            Guid? id = changed ? await EnqueueAsync(db, queue, taskId, cancellationToken) : null;
            if (ownsTransaction) await db.Ado.CommitTranAsync();
            return id;
        }
        catch
        {
            if (ownsTransaction) await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    /// <summary>Bounded legacy-state repair; failed outbox rows require explicit administrator retry.</summary>
    public static async Task<Guid?> EnsureAsync(ISqlSugarClient db, string queue, Guid taskId,
        CancellationToken cancellationToken)
    {
        var hasUnreplayedDeadLetter = await db.Queryable<MessageDeadLetter>()
            .Where(row => row.QueueName == queue && row.TaskId == taskId && row.ReplayedAtUtc == null)
            .AnyAsync(cancellationToken);
        if (hasUnreplayedDeadLetter) return null;

        var recent = DateTime.UtcNow.AddMinutes(-1);
        var exists = await db.Queryable<MessageOutboxEntry>()
            .Where(row => row.QueueName == queue && row.TaskId == taskId
                && (row.Status != "sent" || row.SentAtUtc >= recent))
            .AnyAsync(cancellationToken);
        if (exists) return null;
        // Concurrent repairers may schedule duplicates. Business claims remain the final idempotency boundary.
        return await MutateAsync(db, queue, taskId, () => Task.CompletedTask, cancellationToken);
    }
}
