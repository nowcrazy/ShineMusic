using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using ShineMusic.Model.Entities;
using ShineMusic.Service.Messaging;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

public sealed class MessageOutboxTests
{
    [Fact]
    public async Task Scheduling_without_a_business_transaction_is_rejected()
    {
        using var f = new OutboxFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => MessageOutbox.EnqueueAsync(
            f.Db, QueueNames.Downloads, Guid.NewGuid(), default));

        Assert.Empty(f.Db.Queryable<MessageOutboxEntry>().ToList());
    }

    [Fact]
    public async Task Failed_scheduling_rolls_back_the_owned_business_mutation()
    {
        using var f = new OutboxFixture();
        var business = f.SeedBusiness();

        await Assert.ThrowsAsync<ArgumentException>(() => MessageOutbox.MutateAsync(
            f.Db, "unknown-queue", business.Id,
            () => f.Db.Updateable<OutboxBusinessState>().SetColumns(row => row.State == "queued")
                .Where(row => row.Id == business.Id).ExecuteCommandAsync(), default));

        Assert.Equal("draft", f.Business(business.Id).State);
        Assert.Empty(f.Db.Queryable<MessageOutboxEntry>().ToList());
        Assert.Null(f.Db.Ado.Transaction);
    }

    [Fact]
    public async Task Outer_rollback_removes_both_the_business_change_and_its_trigger()
    {
        using var f = new OutboxFixture();
        var business = f.SeedBusiness();
        await f.Db.Ado.BeginTranAsync();

        var id = await MessageOutbox.MutateAsync(f.Db, QueueNames.Imports, business.Id,
            () => f.Db.Updateable<OutboxBusinessState>().SetColumns(row => row.State == "queued")
                .Where(row => row.Id == business.Id).ExecuteCommandAsync(), default, f.Past);

        Assert.NotNull(f.Db.Ado.Transaction);
        Assert.Equal("queued", f.Business(business.Id).State);
        Assert.Equal(business.Id, f.Entry(id).TaskId);
        await f.Db.Ado.RollbackTranAsync();

        Assert.Equal("draft", f.Business(business.Id).State);
        Assert.Empty(f.Db.Queryable<MessageOutboxEntry>().ToList());
        Assert.Empty(f.Bus.Calls);
    }

    [Fact]
    public async Task Caller_owned_transaction_is_never_published_until_commit()
    {
        using var f = new OutboxFixture();
        var business = f.SeedBusiness();
        await f.Db.Ado.BeginTranAsync();
        var id = await MessageOutbox.MutateAsync(f.Db, QueueNames.Downloads, business.Id,
            () => f.Db.Updateable<OutboxBusinessState>().SetColumns(row => row.State == "queued")
                .Where(row => row.Id == business.Id).ExecuteCommandAsync(), default, f.Past);

        Assert.False(await f.Dispatcher().TryDeliverAsync(id, default));
        await f.Dispatcher().DispatchAsync(default);
        Assert.Empty(f.Bus.Calls);
        Assert.Equal(0, f.Entry(id).Attempts);
        Assert.NotNull(f.Db.Ado.Transaction);

        await f.Db.Ado.CommitTranAsync();
        Assert.True(await f.Dispatcher().TryDeliverAsync(id, default));
        Assert.Equal("sent", f.Entry(id).Status);
        Assert.Equal("queued", f.Business(business.Id).State);
        Assert.Equal((QueueNames.Downloads, business.Id), Assert.Single(f.Bus.Calls));
    }

    [Fact]
    public async Task Broker_failure_after_commit_keeps_a_retryable_durable_entry()
    {
        using var f = new OutboxFixture();
        var taskId = Guid.NewGuid();
        var id = await f.ScheduleAsync(taskId);
        f.Bus.Publish = _ => throw new TimeoutException("simulated confirmation timeout");

        Assert.False(await f.Dispatcher().TryDeliverAsync(id, default));

        var entry = f.Entry(id);
        Assert.Equal("pending", entry.Status);
        Assert.Equal(1, entry.Attempts);
        Assert.Equal(nameof(TimeoutException), entry.LastError);
        Assert.Null(entry.SentAtUtc);
        Assert.Null(entry.ClaimId);
        Assert.Null(entry.LeaseUntilUtc);
        Assert.True(entry.AvailableAtUtc > DateTime.UtcNow);
        Assert.Equal((QueueNames.Downloads, taskId), Assert.Single(f.Bus.Calls));
    }

    [Fact]
    public async Task Confirmation_marks_sent_and_subsequent_dispatch_does_not_publish_again()
    {
        using var f = new OutboxFixture();
        var taskId = Guid.NewGuid();
        var id = await f.ScheduleAsync(taskId);

        await f.Dispatcher().DispatchAsync(default);

        var entry = f.Entry(id);
        Assert.Equal("sent", entry.Status);
        Assert.NotNull(entry.SentAtUtc);
        Assert.Equal(1, entry.Attempts);
        Assert.Null(entry.LastError);
        Assert.Null(entry.ClaimId);
        Assert.Null(entry.LeaseUntilUtc);
        Assert.False(await f.Dispatcher().TryDeliverAsync(id, default));
        await f.Dispatcher().DispatchAsync(default);
        Assert.Equal((QueueNames.Downloads, taskId), Assert.Single(f.Bus.Calls));
    }

    [Fact]
    public async Task Two_dispatchers_cannot_both_publish_an_unexpired_claim()
    {
        using var f = new OutboxFixture();
        using var secondDb = f.NewClient();
        var id = await f.ScheduleAsync(Guid.NewGuid());
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Bus.Publish = async token =>
        {
            publishing.TrySetResult();
            await confirm.Task.WaitAsync(token);
        };

        var first = f.Dispatcher().TryDeliverAsync(id, default);
        await publishing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(await f.Dispatcher(secondDb).TryDeliverAsync(id, default));
            Assert.Equal(1, f.Entry(id).Attempts);
            Assert.Single(f.Bus.Calls);
        }
        finally
        {
            confirm.TrySetResult();
        }

        Assert.True(await first);
        Assert.Equal("sent", f.Entry(id).Status);
    }

    [Fact]
    public async Task Expired_owner_failure_cannot_overwrite_the_next_owners_confirmation()
    {
        using var f = new OutboxFixture();
        using var secondDb = f.NewClient();
        var id = await f.ScheduleAsync(Guid.NewGuid());
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failOldOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        f.Bus.Publish = async token =>
        {
            if (Interlocked.Increment(ref call) != 1) return;
            publishing.TrySetResult();
            await failOldOwner.Task.WaitAsync(token);
            throw new InvalidOperationException("old delivery failed after lease replacement");
        };

        var first = f.Dispatcher().TryDeliverAsync(id, default);
        await publishing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            f.ExpireLease(secondDb, id);
            Assert.True(await f.Dispatcher(secondDb).TryDeliverAsync(id, default));
        }
        finally
        {
            failOldOwner.TrySetResult();
        }

        Assert.False(await first);
        var entry = f.Entry(id);
        Assert.Equal("sent", entry.Status);
        Assert.Equal(2, entry.Attempts);
        Assert.NotNull(entry.SentAtUtc);
        Assert.Null(entry.LastError);
        Assert.Null(entry.ClaimId);
        Assert.Null(entry.LeaseUntilUtc);
        Assert.Equal(2, f.Bus.Calls.Count);
    }

    [Fact]
    public async Task Crash_after_broker_acceptance_allows_redelivery_after_lease_expiry()
    {
        using var f = new OutboxFixture();
        using var stopped = new CancellationTokenSource();
        var taskId = Guid.NewGuid();
        var id = await f.ScheduleAsync(taskId);
        var accepted = 0;
        f.Bus.Publish = _ =>
        {
            // The broker already accepted this call, but the process stops before persisting sent.
            Interlocked.Increment(ref accepted);
            stopped.Cancel();
            throw new OperationCanceledException(stopped.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Dispatcher().TryDeliverAsync(id, stopped.Token));

        var abandoned = f.Entry(id);
        Assert.Equal("pending", abandoned.Status);
        Assert.NotNull(abandoned.ClaimId);
        Assert.NotNull(abandoned.LeaseUntilUtc);
        Assert.Null(abandoned.SentAtUtc);
        Assert.False(await f.Dispatcher().TryDeliverAsync(id, default));
        f.ExpireLease(f.Db, id);
        f.Bus.Publish = _ =>
        {
            Interlocked.Increment(ref accepted);
            return Task.CompletedTask;
        };

        Assert.True(await f.Dispatcher().TryDeliverAsync(id, default));
        Assert.Equal(2, accepted);
        Assert.All(f.Bus.Calls, sent => Assert.Equal((QueueNames.Downloads, taskId), sent));
        Assert.Equal("sent", f.Entry(id).Status);
        Assert.Equal(2, f.Entry(id).Attempts);
    }

    [Fact]
    public async Task Eight_failures_exhaust_budget_and_legacy_repair_cannot_bypass_it()
    {
        using var f = new OutboxFixture();
        var taskId = Guid.NewGuid();
        var id = await f.ScheduleAsync(taskId);
        f.Bus.Publish = _ => throw new InvalidOperationException("simulated broker failure");

        for (var attempt = 1; attempt <= MessageOutboxDispatcher.MaxAttempts; attempt++)
        {
            f.MakeDue(id);
            Assert.False(await f.Dispatcher().TryDeliverAsync(id, default));
            var after = f.Entry(id);
            Assert.Equal(attempt, after.Attempts);
            Assert.Equal(attempt == MessageOutboxDispatcher.MaxAttempts ? "failed" : "pending", after.Status);
        }

        Assert.Null(await MessageOutbox.EnsureAsync(f.Db, QueueNames.Downloads, taskId, default));
        f.MakeDue(id);
        await f.Dispatcher().DispatchAsync(default);
        Assert.False(await f.Dispatcher().TryDeliverAsync(id, default));
        Assert.Single(f.Db.Queryable<MessageOutboxEntry>().ToList());
        var failed = f.Entry(id);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(MessageOutboxDispatcher.MaxAttempts, failed.Attempts);
        Assert.Equal(nameof(InvalidOperationException), failed.LastError);
        Assert.Null(failed.ClaimId);
        Assert.Null(failed.LeaseUntilUtc);
        Assert.Equal(MessageOutboxDispatcher.MaxAttempts, f.Bus.Calls.Count);
    }

    [Fact]
    public async Task Background_dispatch_skips_not_yet_available_work()
    {
        using var f = new OutboxFixture();
        var id = await MessageOutbox.MutateAsync(f.Db, QueueNames.Imports, Guid.NewGuid(),
            () => Task.CompletedTask, default, DateTime.UtcNow.AddHours(1));

        await f.Dispatcher().DispatchAsync(default);

        Assert.Empty(f.Bus.Calls);
        Assert.Equal("pending", f.Entry(id).Status);
        Assert.Equal(0, f.Entry(id).Attempts);
    }

    [SugarTable("OutboxTestBusinessStates")]
    public sealed class OutboxBusinessState
    {
        [SugarColumn(IsPrimaryKey = true)]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string State { get; set; } = "draft";
    }

    private sealed class OutboxFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "shine-outbox-" + Guid.NewGuid().ToString("N"));
        public SqlSugarClient Db { get; }
        public ControlledBus Bus { get; } = new();
        public DateTime Past { get; } = new(DateTime.UtcNow.AddMinutes(-5).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
            DateTimeKind.Utc);

        public OutboxFixture()
        {
            Directory.CreateDirectory(_root);
            Db = NewClient();
            Db.CodeFirst.InitTables<MessageOutboxEntry, OutboxBusinessState>();
            Db.CodeFirst.InitTables<MessageDeadLetter>();
        }

        public SqlSugarClient NewClient() => new(new ConnectionConfig
        {
            ConnectionString = $"Data Source={Path.Combine(_root, "outbox.db")};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.DbTableName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                EntityService = (property, column) => column.DbColumnName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
            }
        });

        public OutboxBusinessState SeedBusiness()
        {
            var business = new OutboxBusinessState();
            Db.Insertable(business).ExecuteCommand();
            return business;
        }

        public Task<Guid> ScheduleAsync(Guid taskId) => MessageOutbox.MutateAsync(Db,
            QueueNames.Downloads, taskId, () => Task.CompletedTask, default, Past);

        public MessageOutboxEntry Entry(Guid id) => Db.Queryable<MessageOutboxEntry>().Single(row => row.Id == id);
        public OutboxBusinessState Business(Guid id) => Db.Queryable<OutboxBusinessState>().Single(row => row.Id == id);
        public MessageOutboxDispatcher Dispatcher(ISqlSugarClient? db = null) => new(db ?? Db, Bus,
            NullLogger<MessageOutboxDispatcher>.Instance);

        public void MakeDue(Guid id) => Db.Updateable<MessageOutboxEntry>()
            .SetColumns(row => row.AvailableAtUtc == Past).Where(row => row.Id == id).ExecuteCommand();

        public void ExpireLease(ISqlSugarClient db, Guid id) => db.Updateable<MessageOutboxEntry>()
            .SetColumns(row => row.LeaseUntilUtc == Past).Where(row => row.Id == id).ExecuteCommand();

        public void Dispose()
        {
            if (Db.Ado.Transaction is not null) Db.Ado.RollbackTran();
            Db.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ControlledBus : IRabbitMessageBus
    {
        public ConcurrentQueue<(string Queue, Guid Id)> Calls { get; } = new();
        public Func<CancellationToken, Task> Publish { get; set; } = _ => Task.CompletedTask;

        public Task PublishAsync(string queue, Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue((queue, id));
            return Publish(cancellationToken);
        }
    }
}
