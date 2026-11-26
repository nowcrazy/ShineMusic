// RabbitMQ 消费端：downloads / imports 两个队列的常驻监听。
// 每个消费端独占一个通道，通道的 ConsumerDispatchConcurrency 与 prefetch 同值，
// 因此队列内部并行度 = Prefetch（配置 1 即逐条串行，>1 时同一进程内可并行处理）。
// 异常隔离：单条消息失败先写入数据库死信表，再 ack；数据库不可用时延迟 nack 回队列；
// 业务失败（SourceDownloadJob / LibraryImportJob）已在内部吞掉并更新状态，消费端几乎不会抛异常。
// 消费循环自动重连：连接断开后外层循环 5s 重试，RabbitMQ.Client 自身也启用了 AutomaticRecoveryEnabled。
// 注意：两个消费端必须是不同的具体类型注册（TryAddEnumerable 按具体类型去重），
// 工厂方式注册两个同类型实例只会启动第一个。

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.Messaging;
using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Tasks;

/// <summary>RabbitMQ 队列消费端基类：常驻监听 + 每条消息一个 DI 作用域 + 手动 ack。</summary>
public abstract class RabbitQueueConsumerService(
    RabbitMessageBus bus,
    IServiceScopeFactory scopeFactory,
    ILogger logger) : BackgroundService
{
    /// <summary>监听的队列名。</summary>
    protected abstract string Queue { get; }

    /// <summary>预取数（= 本队列并发上限）。</summary>
    protected abstract ushort Prefetch { get; }

    /// <summary>处理单条消息（services 为本条消息新建的 DI 作用域）。</summary>
    protected abstract Task ProcessAsync(Guid id, IServiceProvider services, CancellationToken cancellationToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var channel = await bus.CreateChannelAsync(Queue, Prefetch, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    await HandleDeliveryAsync(ea.Body, ea.DeliveryTag,
                        async (id, token) =>
                        {
                            await using var scope = scopeFactory.CreateAsyncScope();
                            await ProcessAsync(id, scope.ServiceProvider, token);
                        },
                        RetainAsync,
                        async (tag, token) => await channel.BasicAckAsync(tag, multiple: false, token),
                        async (tag, token) => await channel.BasicNackAsync(tag, multiple: false, requeue: true, token),
                        stoppingToken,
                        async exception =>
                        {
                            logger.LogError("Could not retain failed message on {Queue} ({ErrorType}); requeueing delivery.",
                                Queue, exception.GetType().Name);
                            // Avoid a tight redelivery loop while the database is unavailable.
                            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                        });
                };
                await channel.BasicConsumeAsync(Queue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
                logger.LogInformation("RabbitMQ consumer started on {Queue} (prefetch={Prefetch}).", Queue, Prefetch);
                // 阻塞到停止信号（RabbitMQ.Client 自动恢复会重建通道/消费者，无需显式循环）。
                // 用 TaskCompletionSource 等待而不是 Task.Delay(Infinite, token)：后者在收到停止信号时
                // 会抛 OperationCanceledException，调试器开启「抛出即中断」时每次停止都会弹异常框。
                var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = stoppingToken.Register(() => stop.TrySetResult());
                await stop.Task;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RabbitMQ consumer for {Queue} interrupted; retrying in 5s.", Queue);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Retain a failed delivery before acknowledging it. Isolated consumers may override the store.</summary>
    protected virtual async Task RetainAsync(ReadOnlyMemory<byte> body, string reason, CancellationToken cancellationToken)
    {
        logger.LogWarning("Retaining failed RabbitMQ delivery on {Queue} ({Reason}) in the database dead-letter store.",
            Queue, reason);
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MessageDeadLetterStore>()
            .RecordAsync(Queue, body, reason, cancellationToken);
    }

    /// <summary>
    /// Process or durably retain one delivery before ack. Transport confirmation failures remain unacknowledged;
    /// they must not be mistaken for processing failures or cause another delivery's tag to be confirmed.
    /// </summary>
    protected static async Task HandleDeliveryAsync(ReadOnlyMemory<byte> body, ulong deliveryTag,
        Func<Guid, CancellationToken, Task> processAsync,
        Func<ReadOnlyMemory<byte>, string, CancellationToken, Task> retainAsync,
        Func<ulong, CancellationToken, Task> acknowledgeAsync,
        Func<ulong, CancellationToken, Task> requeueAsync,
        CancellationToken stoppingToken,
        Func<Exception, Task>? beforeRetentionRequeueAsync = null)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            await requeueAsync(deliveryTag, CancellationToken.None);
            return;
        }

        var messageId = TryParseGuid(body.Span);
        string? failure = messageId is null ? "malformed_message" : null;
        if (messageId.HasValue)
        {
            try
            {
                await processAsync(messageId.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await requeueAsync(deliveryTag, CancellationToken.None);
                return;
            }
            catch (Exception exception)
            {
                // Transport/provider exception messages can contain credentials or payloads.
                failure = exception.GetType().Name;
            }
        }

        if (failure is not null)
        {
            try
            {
                await retainAsync(body, failure, stoppingToken);
            }
            catch (Exception exception)
            {
                try
                {
                    if (!stoppingToken.IsCancellationRequested && beforeRetentionRequeueAsync is not null)
                        await beforeRetentionRequeueAsync(exception);
                }
                finally
                {
                    await requeueAsync(deliveryTag, CancellationToken.None);
                }
                return;
            }
        }

        // Ack is deliberately outside the processing/retention catches: a failed ack leaves the broker delivery
        // outstanding and must not create a second dead letter or issue a contradictory nack on a closed channel.
        await acknowledgeAsync(deliveryTag, CancellationToken.None);
    }

    private static Guid? TryParseGuid(ReadOnlySpan<byte> body)
    {
        try
        {
            var text = Encoding.UTF8.GetString(body);
            if (Guid.TryParseExact(text, "N", out var result))
                return result;
        }
        catch { }
        return null;
    }
}

/// <summary>音源下载队列消费端（并发取 SourceDownload:Download:MaxConcurrentDownloads）。</summary>
public sealed class SourceDownloadConsumerService(
    RabbitMessageBus bus,
    IServiceScopeFactory scopeFactory,
    IOptions<SourceDownloadOptions> options,
    ILogger<SourceDownloadConsumerService> logger) : RabbitQueueConsumerService(bus, scopeFactory, logger)
{
    /// <inheritdoc />
    protected override string Queue => QueueNames.Downloads;

    /// <inheritdoc />
    protected override ushort Prefetch => (ushort)Math.Clamp(options.Value.Download.MaxConcurrentDownloads, 1, 4);

    /// <inheritdoc />
    protected override Task ProcessAsync(Guid id, IServiceProvider services, CancellationToken cancellationToken)
        => services.GetRequiredService<SourceDownloadJob>().ProcessAsync(id, cancellationToken);
}

/// <summary>曲库导入/转码队列消费端（并发取 Transcoding:MaxConcurrentJobs）。</summary>
public sealed class LibraryImportConsumerService(
    RabbitMessageBus bus,
    IServiceScopeFactory scopeFactory,
    IOptions<TranscodingOptions> options,
    ILogger<LibraryImportConsumerService> logger) : RabbitQueueConsumerService(bus, scopeFactory, logger)
{
    /// <inheritdoc />
    protected override string Queue => QueueNames.Imports;

    /// <inheritdoc />
    protected override ushort Prefetch => (ushort)Math.Clamp(options.Value.MaxConcurrentJobs, 1, 4);

    /// <inheritdoc />
    protected override Task ProcessAsync(Guid id, IServiceProvider services, CancellationToken cancellationToken)
        => services.GetRequiredService<LibraryImportJob>().ProcessAsync(id, cancellationToken);
}
