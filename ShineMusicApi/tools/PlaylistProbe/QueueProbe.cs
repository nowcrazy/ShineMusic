using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ShineMusic.Model.Options;
using ShineMusic.Service.Messaging;
using ShineMusic.Tasks;

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "ShineMusicApi/ShineMusic.Api/appsettings.json")).Build();
var options = config.GetSection("RabbitMQ").Get<RabbitOptions>() ?? new();
var rows = new List<object>();
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
using var services = new ServiceCollection().BuildServiceProvider();
await using var bus = new RabbitMessageBus(Options.Create(options), NullLogger<RabbitMessageBus>.Instance);
try
{
    foreach (ushort limit in new ushort[] { 1, 2, 3 })
    {
        var queue = "shinemusic.probe." + Guid.NewGuid().ToString("N");
        await using var channel = await bus.CreateChannelAsync(queue, limit, budget.Token);
        try
        {
            var ids = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
            using var consumer = new ProbeConsumer(bus, services.GetRequiredService<IServiceScopeFactory>(), queue, limit, ids[0]);
            // Publish directly on production-created channel: bus.PublishAsync would also declare business queues.
            foreach (var id in ids)
                await channel.BasicPublishAsync("", queue, false, new BasicProperties { Persistent = true }, Encoding.UTF8.GetBytes(id.ToString("N")), budget.Token);
            await consumer.StartAsync(budget.Token);
            await consumer.Done.Task.WaitAsync(budget.Token);
            await consumer.StopAsync(budget.Token);
            var state = await channel.QueueDeclarePassiveAsync(queue, budget.Token);
            var passed = consumer.Peak == limit && consumer.Failures == 1 && consumer.Completed == 8 && state.MessageCount == 0;
            rows.Add(new { test = "production_consumer_failure_isolation", queue, limit, consumer.Peak, consumer.Failures, consumer.Completed, remaining = state.MessageCount, passed, events = consumer.Events.ToArray() });

            var redeliveryId = Guid.NewGuid();
            await channel.BasicPublishAsync("", queue, false, new BasicProperties { Persistent = true }, Encoding.UTF8.GetBytes(redeliveryId.ToString("N")), budget.Token);
            bool initialRedelivered;
            await using (var first = await bus.CreateChannelAsync(queue, 1, budget.Token))
            {
                var delivery = await first.BasicGetAsync(queue, false, budget.Token) ?? throw new InvalidOperationException("missing_delivery");
                initialRedelivered = delivery.Redelivered;
                // Dispose without ack simulates channel loss, independently of requeue:false failure handling.
            }
            var retry = await channel.BasicGetAsync(queue, false, budget.Token) ?? throw new InvalidOperationException("missing_redelivery");
            var sameId = Encoding.UTF8.GetString(retry.Body.Span) == redeliveryId.ToString("N");
            await channel.BasicAckAsync(retry.DeliveryTag, false, budget.Token);
            rows.Add(new { test = "unacked_channel_close_redelivery", queue, initialRedelivered, retry.Redelivered, sameId, passed = !initialRedelivered && retry.Redelivered && sameId });
        }
        finally { await channel.QueueDeleteAsync(queue, false, false, CancellationToken.None); }
    }
}
catch (Exception ex) { rows.Add(new { test = "probe_error", code = ex.GetType().Name, passed = false }); }
await File.WriteAllTextAsync(Path.Combine(output, "queue.json"), JsonSerializer.Serialize(new { configuration = "appsettings.json only; credentials omitted", productionDownloads = config["SourceDownload:Download:MaxConcurrentDownloads"], productionTranscodes = config["Transcoding:MaxConcurrentJobs"], results = rows }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Queue evidence written; inspect passed fields.");

sealed class ProbeConsumer(RabbitMessageBus bus, IServiceScopeFactory scopes, string queue, ushort limit, Guid failId)
    : RabbitQueueConsumerService(bus, scopes, NullLogger.Instance)
{
    protected override string Queue => queue;
    protected override ushort Prefetch => limit;
    private readonly object gate = new();
    private int active, seen;
    public int Peak { get; private set; }
    public int Failures { get; private set; }
    public int Completed { get; private set; }
    public ConcurrentQueue<object> Events { get; } = new();
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task ProcessAsync(Guid id, IServiceProvider services, CancellationToken token)
    {
        lock (gate) { Peak = Math.Max(Peak, ++active); }
        Events.Enqueue(new { id, phase = "start", at = DateTimeOffset.UtcNow });
        try
        {
            await Task.Delay(250, token);
            if (id == failId) { lock (gate) ++Failures; throw new InvalidOperationException("intentional_probe_failure"); }
            lock (gate) ++Completed;
        }
        finally
        {
            Events.Enqueue(new { id, phase = "end", at = DateTimeOffset.UtcNow });
            lock (gate) { --active; if (++seen == 9) Done.TrySetResult(); }
        }
    }
}
