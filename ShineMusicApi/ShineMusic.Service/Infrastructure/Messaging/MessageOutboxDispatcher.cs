using Microsoft.Extensions.Logging;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service.Messaging;

/// <summary>At-least-once delivery with a fenced lease and bounded exponential retry.</summary>
public sealed class MessageOutboxDispatcher(ISqlSugarClient db, IRabbitMessageBus bus,
    ILogger<MessageOutboxDispatcher> logger)
{
    public const int MaxAttempts = 8;
    private static readonly TimeSpan PublishWatchdog = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan SentEntryRetention = TimeSpan.FromDays(30);

    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var ids = await db.Queryable<MessageOutboxEntry>()
            .Where(row => row.Status == "pending" && row.AvailableAtUtc <= now
                && (row.LeaseUntilUtc == null || row.LeaseUntilUtc <= now))
            .OrderBy(row => row.AvailableAtUtc).OrderBy(row => row.Id).Take(50)
            .Select(row => row.Id).ToListAsync(cancellationToken);
        foreach (var id in ids) await TryDeliverAsync(id, cancellationToken);
        await PruneSentAsync(now - SentEntryRetention, cancellationToken);
    }

    public async Task<bool> TryDeliverAsync(Guid id, CancellationToken cancellationToken)
    {
        // A caller-owned transaction may still roll back. Only the background dispatcher may send after commit.
        if (db.Ado.Transaction is not null) return false;
        var now = DateTime.UtcNow;
        var claim = Guid.NewGuid();
        var leaseUntil = now.AddMinutes(1);
        var changed = await db.Updateable<MessageOutboxEntry>()
            .SetColumns(row => row.ClaimId == claim).SetColumns(row => row.LeaseUntilUtc == leaseUntil)
            .SetColumns(row => row.Attempts == row.Attempts + 1)
            .Where(row => row.Id == id && row.Status == "pending" && row.AvailableAtUtc <= now
                && (row.LeaseUntilUtc == null || row.LeaseUntilUtc <= now))
            .ExecuteCommandAsync(cancellationToken);
        if (changed != 1) return false;
        var entry = await db.Queryable<MessageOutboxEntry>()
            .FirstAsync(row => row.Id == id && row.ClaimId == claim, cancellationToken);
        if (entry is null) return false;
        try
        {
            if (!MessageOutbox.IsKnownQueue(entry.QueueName)) throw new InvalidOperationException("Unknown queue.");
            // Claim 9 can follow eight crashes before result persistence; stop rather than retry forever.
            if (entry.Attempts > MaxAttempts) throw new InvalidOperationException("Delivery budget exhausted.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PublishWatchdog);
            await bus.PublishAsync(entry.QueueName, entry.TaskId, timeout.Token);
            var sent = DateTime.UtcNow;
            var markedSent = await db.Updateable<MessageOutboxEntry>()
                .SetColumns(row => row.Status == "sent").SetColumns(row => row.SentAtUtc == sent)
                .SetColumns(row => row.ClaimId == (Guid?)null).SetColumns(row => row.LeaseUntilUtc == (DateTime?)null)
                .SetColumns(row => row.LastError == (string?)null)
                .Where(row => row.Id == id && row.ClaimId == claim && row.Status == "pending")
                .ExecuteCommandAsync(cancellationToken);
            if (markedSent != 1)
            {
                logger.LogWarning("Outbox {OutboxId} was confirmed by RabbitMQ after its delivery lease was replaced; the current owner remains authoritative.", id);
                return false;
            }
            logger.LogInformation("Outbox {OutboxId} confirmed for {Queue}; attempt={Attempt}, ageMs={AgeMs}.",
                id, entry.QueueName, entry.Attempts, (sent - entry.CreatedAtUtc).TotalMilliseconds);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The broker may have accepted the message. Lease expiry permits safe at-least-once redelivery.
            throw;
        }
        catch (Exception exception)
        {
            var status = entry.Attempts >= MaxAttempts ? "failed" : "pending";
            var available = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(entry.Attempts - 1, 6))));
            var error = exception.GetType().Name;
            await db.Updateable<MessageOutboxEntry>()
                .SetColumns(row => row.Status == status).SetColumns(row => row.AvailableAtUtc == available)
                .SetColumns(row => row.LastError == error).SetColumns(row => row.ClaimId == (Guid?)null)
                .SetColumns(row => row.LeaseUntilUtc == (DateTime?)null)
                .Where(row => row.Id == id && row.ClaimId == claim && row.Status == "pending")
                .ExecuteCommandAsync(cancellationToken);
            logger.LogWarning("Outbox {OutboxId} delivery {Status} after attempt {Attempt} ({ErrorType}).",
                id, status, entry.Attempts, error);
            return false;
        }
    }

    /// <summary>Low-latency best effort after commit. A broker outage leaves the durable row for the worker.</summary>
    public static Task<bool> TryDeliverAsync(ISqlSugarClient db, IRabbitMessageBus bus, Guid id,
        CancellationToken cancellationToken) => new MessageOutboxDispatcher(db, bus,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageOutboxDispatcher>.Instance)
            .TryDeliverAsync(id, cancellationToken);

    private async Task PruneSentAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var ids = await db.Queryable<MessageOutboxEntry>()
            .Where(row => row.Status == "sent" && row.SentAtUtc < cutoff)
            .OrderBy(row => row.SentAtUtc).Take(250).Select(row => row.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0) return;
        var deleted = await db.Deleteable<MessageOutboxEntry>()
            .Where(row => ids.Contains(row.Id) && row.Status == "sent" && row.SentAtUtc < cutoff)
            .ExecuteCommandAsync(cancellationToken);
        if (deleted > 0) logger.LogInformation("Pruned {Count} sent message outbox row(s) older than {CutoffUtc}.", deleted, cutoff);
    }
}
