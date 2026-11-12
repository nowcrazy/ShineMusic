// RabbitMQ 消息总线：全局单连接 + 发布通道，面向「投递一个任务 Id」的极简语义。
// 设计要点：
// - 连接懒建立 + 失败自动重连：RabbitMQ 容器启动慢于后端时不阻塞应用启动；
// - 队列为 durable 惰性声明（发布/消费两侧都会声明，幂等），消息持久化（Persistent），
//   进程或 broker 重启后未消费的任务不丢；
// - 消息体只是业务 Id（Guid），业务状态机仍在 PostgreSQL 业务表，消息仅作触发器；
//   消费端原子认领（状态机推进）保证重复投递幂等。
// - 消费并发由消费方 prefetch + ConsumerDispatchConcurrency 控制，本类只负责连接与发布。

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.Messaging;

/// <summary>逻辑队列名常量（durable 队列，默认交换机按 routing key 直投）。</summary>
public static class QueueNames
{
    /// <summary>外部音源下载队列（并发由 SourceDownload:Download:MaxConcurrentDownloads 控制）。</summary>
    public const string Downloads = "shinemusic.downloads";

    /// <summary>曲库导入/转码队列（并发由 Transcoding:MaxConcurrentJobs 控制）。</summary>
    public const string Imports = "shinemusic.imports";
}

/// <summary>任务消息发布入口（控制器/编排层/恢复与重试扫描共用）。</summary>
public interface IRabbitMessageBus
{
    /// <summary>把任务 Id 投递到指定队列（持久化消息；连接未就绪时先尝试重连）。</summary>
    Task PublishAsync(string queue, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// RabbitMQ 连接持有者：单例。按需建立连接（带自动恢复），发布经独立通道 + 信号量串行化。
/// 消费方通过 <see cref="CreateChannelAsync"/> 取独立通道自行消费。
/// </summary>
public sealed class RabbitMessageBus(
    IOptions<RabbitOptions> options,
    ILogger<RabbitMessageBus> logger) : IRabbitMessageBus, IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publishChannel;

    /// <inheritdoc />
    public async Task PublishAsync(string queue, Guid id, CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(id.ToString("N"));
        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetPublishChannelAsync(cancellationToken);
            var properties = new BasicProperties { Persistent = true };
            await channel.BasicPublishAsync(string.Empty, queue, mandatory: false, basicProperties: properties, body: body, cancellationToken);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    /// <summary>
    /// 为消费方创建独立通道（已声明队列并设置 prefetch）。
    /// 同时把 ConsumerDispatchConcurrency 设为 prefetch，使预取数真正代表本通道的并行处理上限；
    /// 默认值 1 时整条通道逐条串行处理（预取只做缓冲）。
    /// </summary>
    public async Task<IChannel> CreateChannelAsync(string queue, ushort prefetch, CancellationToken cancellationToken)
    {
        var current = await GetConnectionAsync(cancellationToken);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: prefetch);
        var channel = await current.CreateChannelAsync(channelOptions, cancellationToken);
        await DeclareQueueAsync(channel, queue, cancellationToken);
        await channel.BasicQosAsync(0, prefetch, false, cancellationToken);
        return channel;
    }

    /// <summary>声明 durable 队列（幂等，发布/消费两侧共用）。</summary>
    public static async Task DeclareQueueAsync(IChannel channel, string queue, CancellationToken cancellationToken)
        => await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: cancellationToken);

    private async Task<IChannel> GetPublishChannelAsync(CancellationToken cancellationToken)
    {
        if (_publishChannel is { IsOpen: true })
            return _publishChannel;
        if (_publishChannel is not null)
            await _publishChannel.DisposeAsync();
        var current = await GetConnectionAsync(cancellationToken);
        _publishChannel = await current.CreateChannelAsync(cancellationToken: cancellationToken);
        await DeclareQueueAsync(_publishChannel, QueueNames.Downloads, cancellationToken);
        await DeclareQueueAsync(_publishChannel, QueueNames.Imports, cancellationToken);
        return _publishChannel;
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
            return _connection;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;
            var rabbit = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = rabbit.HostName,
                Port = rabbit.Port,
                UserName = rabbit.UserName,
                Password = rabbit.Password,
                AutomaticRecoveryEnabled = true,
                // 单连接多路复用：下载/导入两个消费通道共享，并发上限给足两者的和。
                ConsumerDispatchConcurrency = 8
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            logger.LogInformation("RabbitMQ connection established to {HostName}:{Port}.", rabbit.HostName, rabbit.Port);
            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_publishChannel is not null)
            await _publishChannel.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
        _connectionLock.Dispose();
        _publishLock.Dispose();
    }
}
