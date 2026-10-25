// 下载任务重试扫描：把「queued 且已到 NextRetryAtUtc」的任务重新投递到 RabbitMQ。
// 配合 SourceDownloadJob 内的退避写入（30s/2min/10min）构成完整的自动重试闭环；
// 消息重复投递由消费端原子认领保证幂等。

using ShineMusic.Model.Entities;
using ShineMusic.Service.Messaging;
using SqlSugar;

namespace ShineMusic.Service.SourceDownload;

/// <summary>下载任务重试扫描：到期任务重新投递下载队列（由 SourceDownloadRetrySweepService 每 15s 驱动）。</summary>
public sealed class SourceDownloadRetrySweepJob(
    ISqlSugarClient db,
    IRabbitMessageBus messageBus,
    ILogger<SourceDownloadRetrySweepJob> logger)
{
    /// <summary>执行一轮到期任务扫描与重投递（单轮最多 50 条）。</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var dueTaskIds = await db.Queryable<ExternalDownloadTask>()
            .Where(task => task.Status == ExternalDownloadTaskStatuses.Queued
                && task.NextRetryAtUtc != null
                && task.NextRetryAtUtc <= now)
            .OrderBy(task => task.NextRetryAtUtc)
            .Take(50)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
        foreach (var taskId in dueTaskIds)
            await messageBus.PublishAsync(QueueNames.Downloads, taskId, cancellationToken);
        if (dueTaskIds.Count > 0)
            logger.LogInformation("Re-published {Count} due source download task(s).", dueTaskIds.Count);
    }
}
