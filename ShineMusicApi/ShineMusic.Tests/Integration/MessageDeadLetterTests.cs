using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Entities;
using ShineMusic.Service.Messaging;
using ShineMusic.Tests.Acceptance;
using Xunit;

namespace ShineMusic.Tests.Integration;

[Collection("Source download database acceptance")]
public sealed class MessageDeadLetterTests
{
    [Fact]
    public async Task Failed_message_body_is_retained_exactly_and_malformed_message_cannot_be_replayed()
    {
        using var f = Fixture();
        var store = new MessageDeadLetterStore(f.Db);
        byte[] body = [0, 255, 2, 3, 128];
        await store.RecordAsync(QueueNames.Imports, body, "malformed_message", default);
        var stored = f.Db.Queryable<MessageDeadLetter>().Single();

        Assert.Equal(body, Convert.FromBase64String(stored.BodyBase64));
        Assert.Null(stored.TaskId);
        var result = await store.ReplayAsync(stored.Id, Context(), default);
        Assert.Equal(DeadLetterReplayStatus.InvalidMessage, result.Status);
        Assert.Empty(f.Db.Queryable<MessageOutboxEntry>().ToList());
        Assert.Empty(f.Db.Queryable<AdminAuditEntry>().ToList());
        Assert.False(Assert.Single((await store.ListAsync(null, 1, 20, default)).Items).CanReplay);
    }

    [Fact]
    public async Task Replay_atomically_schedules_once_and_records_an_audit_without_publishing()
    {
        using var f = Fixture();
        var task = f.Seed();
        var store = new MessageDeadLetterStore(f.Db);
        await store.RecordAsync(QueueNames.Downloads, Encoding.UTF8.GetBytes(task.Id.ToString("N")), "consumer_failed", default);
        var deadLetter = f.Db.Queryable<MessageDeadLetter>().Single();
        Assert.Equal(task.Id, deadLetter.TaskId);

        var result = await store.ReplayAsync(deadLetter.Id, Context(), default);

        Assert.Equal(DeadLetterReplayStatus.Scheduled, result.Status);
        var outbox = f.Db.Queryable<MessageOutboxEntry>().Single();
        Assert.Equal(result.OutboxId, outbox.Id);
        Assert.Equal((QueueNames.Downloads, task.Id, "pending"), (outbox.QueueName, outbox.TaskId, outbox.Status));
        var saved = f.Db.Queryable<MessageDeadLetter>().Single();
        Assert.Equal(1, saved.ReplayCount);
        Assert.NotNull(saved.ReplayedAtUtc);
        Assert.Equal(outbox.Id, saved.ReplayOutboxId);
        Assert.Null(saved.ReplayLeaseId);
        Assert.Equal("message.dead_letter_replay_scheduled", f.Db.Queryable<AdminAuditEntry>().Single().Action);
        Assert.Empty(f.Bus.Messages);
        Assert.Equal(DeadLetterReplayStatus.Conflict, (await store.ReplayAsync(deadLetter.Id, Context(), default)).Status);
        Assert.Single(f.Db.Queryable<MessageOutboxEntry>().ToList());
    }

    [Fact]
    public async Task Truncated_body_is_marked_and_cannot_be_replayed_even_with_a_valid_guid_prefix()
    {
        using var f = Fixture();
        var task = f.Seed();
        var store = new MessageDeadLetterStore(f.Db);
        // Guid.TryParseExact alone accepts surrounding whitespace: preserve the size/shape guard too.
        var body = Encoding.UTF8.GetBytes(task.Id.ToString("N") + new string(' ', 5000));
        await store.RecordAsync(QueueNames.Downloads, body, new string('x', 1100), default);
        var stored = f.Db.Queryable<MessageDeadLetter>().Single();

        Assert.Null(stored.TaskId);
        Assert.Equal(4096, Convert.FromBase64String(stored.BodyBase64).Length);
        Assert.StartsWith("body_truncated(5032->4096); ", stored.FailureReason, StringComparison.Ordinal);
        Assert.Equal(1000, stored.FailureReason.Length);
        Assert.Equal(DeadLetterReplayStatus.InvalidMessage, (await store.ReplayAsync(stored.Id, Context(), default)).Status);
        Assert.False(Assert.Single((await store.ListAsync(null, 1, 20, default)).Items).CanReplay);
        Assert.Empty(f.Db.Queryable<MessageOutboxEntry>().ToList());
    }

    [Theory]
    [InlineData("padded")]
    [InlineData("empty")]
    public async Task Only_complete_nonempty_guid_bodies_get_a_task_id(string shape)
    {
        using var f = Fixture();
        var store = new MessageDeadLetterStore(f.Db);
        var body = shape == "padded" ? $" {Guid.NewGuid():N} " : Guid.Empty.ToString("N");
        await store.RecordAsync(QueueNames.Imports, Encoding.UTF8.GetBytes(body), "malformed_message", default);
        var stored = f.Db.Queryable<MessageDeadLetter>().Single();
        Assert.Null(stored.TaskId);
        Assert.Equal(DeadLetterReplayStatus.InvalidMessage, (await store.ReplayAsync(stored.Id, Context(), default)).Status);
    }

    [Fact]
    public async Task Compensation_does_not_republish_unreplayed_dead_letter_until_administrator_schedules_it()
    {
        using var f = Fixture();
        var task = f.Seed();
        var store = new MessageDeadLetterStore(f.Db);
        await store.RecordAsync(QueueNames.Downloads, Encoding.UTF8.GetBytes(task.Id.ToString("N")), "consumer_failed", default);
        var stored = f.Db.Queryable<MessageDeadLetter>().Single();

        Assert.Null(await MessageOutbox.EnsureAsync(f.Db, QueueNames.Downloads, task.Id, default));
        Assert.Empty(f.Db.Queryable<MessageOutboxEntry>().ToList());
        Assert.Equal(DeadLetterReplayStatus.Scheduled, (await store.ReplayAsync(stored.Id, Context(), default)).Status);
        Assert.Single(f.Db.Queryable<MessageOutboxEntry>().ToList());
    }

    [Theory]
    [InlineData("active")]
    [InlineData("completed")]
    [InlineData("future_retry")]
    [InlineData("missing")]
    public async Task Replay_cannot_override_business_guards(string state)
    {
        using var f = Fixture();
        var task = f.Seed();
        if (state == "active") task.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1);
        if (state == "completed") task.Status = ExternalDownloadTaskStatuses.Completed;
        if (state == "future_retry") task.NextRetryAtUtc = DateTime.UtcNow.AddMinutes(1);
        f.Db.Updateable(task).ExecuteCommand();
        if (state == "missing") f.Db.Deleteable<ExternalDownloadTask>().Where(row => row.Id == task.Id).ExecuteCommand();
        var store = new MessageDeadLetterStore(f.Db);
        await store.RecordAsync(QueueNames.Downloads, Encoding.UTF8.GetBytes(task.Id.ToString("N")), "consumer_failed", default);
        var deadLetter = f.Db.Queryable<MessageDeadLetter>().Single();

        Assert.Equal(DeadLetterReplayStatus.Conflict, (await store.ReplayAsync(deadLetter.Id, Context(), default)).Status);
        Assert.Empty(f.Db.Queryable<MessageOutboxEntry>().ToList());
        Assert.Null(f.Db.Queryable<MessageDeadLetter>().Single().ReplayLeaseId);
        Assert.False(Assert.Single((await store.ListAsync(null, 1, 20, default)).Items).CanReplay);
    }

    [Fact]
    public async Task Missing_audit_actor_rolls_back_outbox_and_dead_letter_changes()
    {
        using var f = Fixture();
        var task = f.Seed();
        var store = new MessageDeadLetterStore(f.Db);
        await store.RecordAsync(QueueNames.Downloads, Encoding.UTF8.GetBytes(task.Id.ToString("N")), "consumer_failed", default);
        var deadLetter = f.Db.Queryable<MessageDeadLetter>().Single();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplayAsync(deadLetter.Id, new DefaultHttpContext(), default));

        Assert.Empty(f.Db.Queryable<MessageOutboxEntry>().ToList());
        var unchanged = f.Db.Queryable<MessageDeadLetter>().Single();
        Assert.Null(unchanged.ReplayedAtUtc);
        Assert.Null(unchanged.ReplayLeaseId);
        Assert.Equal(0, unchanged.ReplayCount);
    }

    [Fact]
    public async Task Failed_outbox_retry_resets_budget_and_preserves_sent_history()
    {
        using var f = Fixture();
        var task = f.Seed();
        var failed = new MessageOutboxEntry { QueueName = QueueNames.Downloads, TaskId = task.Id, Status = "failed", Attempts = 8, LastError = "PublishException" };
        var sent = new MessageOutboxEntry { QueueName = QueueNames.Downloads, TaskId = task.Id, Status = "sent", Attempts = 1, SentAtUtc = DateTime.UtcNow };
        f.Db.Insertable(new[] { failed, sent }).ExecuteCommand();
        var store = new MessageDeadLetterStore(f.Db);

        var list = await store.ListFailedOutboxAsync(null, 1, 20, default);
        Assert.Equal(failed.Id, Assert.Single(list.Items).Id);
        Assert.Equal(DeadLetterReplayStatus.Scheduled, (await store.RetryOutboxAsync(failed.Id, Context(), default)).Status);
        Assert.Equal(DeadLetterReplayStatus.Conflict, (await store.RetryOutboxAsync(failed.Id, Context(), default)).Status);
        Assert.Equal(DeadLetterReplayStatus.Conflict, (await store.RetryOutboxAsync(sent.Id, Context(), default)).Status);
        var reset = f.Db.Queryable<MessageOutboxEntry>().Single(row => row.Id == failed.Id);
        Assert.Equal("pending", reset.Status);
        Assert.Equal(0, reset.Attempts);
        Assert.Null(reset.LastError);
        var history = f.Db.Queryable<MessageOutboxEntry>().Single(row => row.Id == sent.Id);
        Assert.Equal(("sent", 1), (history.Status, history.Attempts));
        Assert.NotNull(history.SentAtUtc);
        Assert.Equal("message.outbox_retry_scheduled", f.Db.Queryable<AdminAuditEntry>().Single().Action);
    }

    [Fact]
    public async Task Queue_filter_and_paging_are_applied_before_loading_messages()
    {
        using var f = Fixture();
        var store = new MessageDeadLetterStore(f.Db);
        for (var i = 0; i < 3; i++)
            await store.RecordAsync(QueueNames.Downloads, Encoding.UTF8.GetBytes("invalid"), "malformed_message", default);
        await store.RecordAsync(QueueNames.Imports, Encoding.UTF8.GetBytes("invalid"), "malformed_message", default);

        var list = await store.ListAsync(QueueNames.Downloads, 2, 2, default);

        Assert.Equal(3, list.Total);
        Assert.Equal(QueueNames.Downloads, Assert.Single(list.Items).QueueName);
        var auth = typeof(MessageDeadLetterAdminController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(auth);
        Assert.Equal("admin", auth.Roles);
    }

    private static SourceDownloadAcceptanceFixture Fixture()
    {
        var fixture = new SourceDownloadAcceptanceFixture();
        fixture.Db.CodeFirst.InitTables<MessageDeadLetter, MessageOutboxEntry, AdminAuditEntry>();
        return fixture;
    }

    private static DefaultHttpContext Context() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"))
    };
}
