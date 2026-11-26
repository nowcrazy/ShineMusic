using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ShineMusic.Service.Messaging;
using ShineMusic.Tasks;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

public sealed class QueueDeliveryTests
{
    [Fact]
    public async Task Malformed_delivery_is_retained_before_ack_and_never_reaches_the_processor()
    {
        var body = Encoding.UTF8.GetBytes("not-a-task-id");
        var retaining = Signal();
        var retained = Signal();
        var events = new List<string>();
        var delivery = TestConsumer.DeliverAsync(body, 41,
            (_, _) => throw new InvalidOperationException("Malformed input must not reach the processor."),
            async (payload, reason, _) =>
            {
                Assert.Equal(body, payload.ToArray());
                Assert.Equal("malformed_message", reason);
                events.Add("retain");
                retaining.SetResult();
                await retained.Task;
            },
            (tag, token) =>
            {
                Assert.Equal(41UL, tag);
                Assert.False(token.CanBeCanceled);
                events.Add("ack");
                return Task.CompletedTask;
            }, UnexpectedRequeue);

        await retaining.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(delivery.IsCompleted);
        Assert.Equal(new[] { "retain" }, events);
        retained.SetResult();
        await delivery;
        Assert.Equal(new[] { "retain", "ack" }, events);
    }

    [Fact]
    public async Task Processing_failure_retains_original_body_and_sanitized_reason_before_ack()
    {
        var id = Guid.NewGuid();
        var body = Body(id);
        var events = new List<string>();
        await TestConsumer.DeliverAsync(body, 52,
            (actual, _) =>
            {
                Assert.Equal(id, actual);
                events.Add("process");
                throw new IOException("A provider secret must not become a dead-letter reason.");
            },
            (payload, reason, _) =>
            {
                Assert.Equal(body, payload.ToArray());
                Assert.Equal(nameof(IOException), reason);
                events.Add("retain");
                return Task.CompletedTask;
            },
            (tag, _) =>
            {
                Assert.Equal(52UL, tag);
                events.Add("ack");
                return Task.CompletedTask;
            }, UnexpectedRequeue);

        Assert.Equal(new[] { "process", "retain", "ack" }, events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retention_failure_keeps_delivery_in_broker_for_redelivery(bool malformed)
    {
        var body = malformed ? Encoding.UTF8.GetBytes("invalid") : Body(Guid.NewGuid());
        var events = new List<string>();
        await TestConsumer.DeliverAsync(body, 63,
            (_, _) => throw new InvalidOperationException("Processing failed."),
            (_, _, _) =>
            {
                events.Add("retain");
                throw new IOException("database unavailable");
            }, UnexpectedAck,
            (tag, token) =>
            {
                Assert.Equal(63UL, tag);
                Assert.False(token.CanBeCanceled);
                events.Add("requeue");
                return Task.CompletedTask;
            },
            beforeRequeue: exception =>
            {
                Assert.IsType<IOException>(exception);
                events.Add("backoff");
                return Task.CompletedTask;
            });

        Assert.Equal(new[] { "retain", "backoff", "requeue" }, events);
    }

    [Fact]
    public async Task Successful_delivery_is_acknowledged_only_after_processing_completes()
    {
        var processing = Signal();
        var completed = Signal();
        var acknowledgements = new List<ulong>();
        var delivery = TestConsumer.DeliverAsync(Body(Guid.NewGuid()), 74,
            async (_, _) =>
            {
                processing.SetResult();
                await completed.Task;
            }, UnexpectedRetention,
            (tag, _) =>
            {
                acknowledgements.Add(tag);
                return Task.CompletedTask;
            }, UnexpectedRequeue);

        await processing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(acknowledgements);
        completed.SetResult();
        await delivery;
        Assert.Equal(new[] { 74UL }, acknowledgements);
    }

    [Fact]
    public async Task Shutdown_during_processing_requeues_without_retention_or_ack()
    {
        using var shutdown = new CancellationTokenSource();
        var processing = Signal();
        var requeued = new List<ulong>();
        var delivery = TestConsumer.DeliverAsync(Body(Guid.NewGuid()), 85,
            async (_, token) =>
            {
                processing.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, UnexpectedRetention, UnexpectedAck,
            (tag, token) =>
            {
                Assert.False(token.CanBeCanceled);
                requeued.Add(tag);
                return Task.CompletedTask;
            }, stoppingToken: shutdown.Token);

        await processing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();
        await delivery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 85UL }, requeued);
    }

    [Fact]
    public async Task Already_stopping_consumer_requeues_without_starting_work()
    {
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var requeued = new List<ulong>();
        await TestConsumer.DeliverAsync(Body(Guid.NewGuid()), 96,
            (_, _) => throw new InvalidOperationException("Stopped consumer must not start work."),
            UnexpectedRetention, UnexpectedAck,
            (tag, token) =>
            {
                Assert.False(token.CanBeCanceled);
                requeued.Add(tag);
                return Task.CompletedTask;
            }, stoppingToken: shutdown.Token);

        Assert.Equal(new[] { 96UL }, requeued);
    }

    [Fact]
    public async Task Shutdown_during_retention_requeues_without_waiting_for_retry_backoff()
    {
        using var shutdown = new CancellationTokenSource();
        var retaining = Signal();
        var requeued = new List<ulong>();
        var delivery = TestConsumer.DeliverAsync(Encoding.UTF8.GetBytes("invalid"), 98,
            (_, _) => throw new InvalidOperationException("Malformed input must not be processed."),
            async (_, _, token) =>
            {
                retaining.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, UnexpectedAck,
            (tag, token) =>
            {
                Assert.False(token.CanBeCanceled);
                requeued.Add(tag);
                return Task.CompletedTask;
            },
            beforeRequeue: _ => throw new InvalidOperationException("Shutdown must not wait for retention retry backoff."),
            stoppingToken: shutdown.Token);

        await retaining.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();
        await delivery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 98UL }, requeued);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ack_transport_failure_does_not_reclassify_completed_work_or_retain_twice(bool processFails)
    {
        var retentionCount = 0;
        var acknowledgementCount = 0;
        await Assert.ThrowsAsync<IOException>(() => TestConsumer.DeliverAsync(Body(Guid.NewGuid()), 107,
            (_, _) => processFails ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask,
            (_, _, _) =>
            {
                retentionCount++;
                return Task.CompletedTask;
            },
            (tag, _) =>
            {
                Assert.Equal(107UL, tag);
                acknowledgementCount++;
                throw new IOException("channel closed before acknowledgement completed");
            }, UnexpectedRequeue));

        Assert.Equal(processFails ? 1 : 0, retentionCount);
        Assert.Equal(1, acknowledgementCount);
    }

    [Fact]
    public async Task Concurrent_deliveries_confirm_only_their_own_tags_after_their_own_outcomes()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var gates = ids.ToDictionary(id => id, _ => Signal());
        var started = ids.ToDictionary(id => id, _ => Signal());
        var tags = ids.Select((id, index) => (id, tag: (ulong)(index + 201))).ToDictionary(row => row.id, row => row.tag);
        var retained = new ConcurrentQueue<Guid>();
        var acknowledged = new ConcurrentQueue<ulong>();
        var requeued = new ConcurrentQueue<ulong>();
        var deliveries = ids.Select(id => TestConsumer.DeliverAsync(Body(id), tags[id],
            async (actual, _) =>
            {
                Assert.Equal(id, actual);
                started[actual].SetResult();
                await gates[actual].Task;
                if (actual == ids[1] || actual == ids[3]) throw new InvalidOperationException();
            },
            (payload, reason, _) =>
            {
                var actual = Guid.ParseExact(Encoding.UTF8.GetString(payload.Span), "N");
                Assert.Equal(nameof(InvalidOperationException), reason);
                Assert.True(gates[actual].Task.IsCompleted);
                if (actual == ids[3]) throw new IOException("retention unavailable for this delivery");
                retained.Enqueue(actual);
                return Task.CompletedTask;
            },
            (tag, _) =>
            {
                var actual = tags.Single(row => row.Value == tag).Key;
                Assert.True(gates[actual].Task.IsCompleted);
                if (actual == ids[1]) Assert.Contains(actual, retained);
                acknowledged.Enqueue(tag);
                return Task.CompletedTask;
            },
            (tag, _) =>
            {
                requeued.Enqueue(tag);
                return Task.CompletedTask;
            })).ToArray();

        await Task.WhenAll(started.Values.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(acknowledged);
        Assert.Empty(requeued);
        // Complete deliveries in reverse order, with the failing retention finishing before healthy work.
        for (var index = ids.Length - 1; index >= 0; index--)
        {
            gates[ids[index]].SetResult();
            await deliveries[index].WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(new[] { tags[ids[2]], tags[ids[1]], tags[ids[0]] }, acknowledged);
        Assert.Equal(new[] { tags[ids[3]] }, requeued);
        Assert.Equal(new[] { ids[1] }, retained);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static byte[] Body(Guid id) => Encoding.UTF8.GetBytes(id.ToString("N"));
    private static Task UnexpectedAck(ulong _, CancellationToken __) => throw new InvalidOperationException("Unexpected acknowledgement.");
    private static Task UnexpectedRequeue(ulong _, CancellationToken __) => throw new InvalidOperationException("Unexpected requeue.");
    private static Task UnexpectedRetention(ReadOnlyMemory<byte> _, string __, CancellationToken ___)
        => throw new InvalidOperationException("Unexpected retention.");

    private sealed class TestConsumer() : RabbitQueueConsumerService(null!, null!, NullLogger.Instance)
    {
        protected override string Queue => QueueNames.Downloads;
        protected override ushort Prefetch => 1;
        protected override Task ProcessAsync(Guid id, IServiceProvider services, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public static Task DeliverAsync(ReadOnlyMemory<byte> body, ulong tag,
            Func<Guid, CancellationToken, Task> process,
            Func<ReadOnlyMemory<byte>, string, CancellationToken, Task> retain,
            Func<ulong, CancellationToken, Task> acknowledge,
            Func<ulong, CancellationToken, Task> requeue,
            Func<Exception, Task>? beforeRequeue = null,
            CancellationToken stoppingToken = default)
            => HandleDeliveryAsync(body, tag, process, retain, acknowledge, requeue, stoppingToken, beforeRequeue);
    }
}
