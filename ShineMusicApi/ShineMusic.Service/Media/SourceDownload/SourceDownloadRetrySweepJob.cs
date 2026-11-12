// 下载任务重试扫描（每 15s 驱动）：把「queued 且已到 NextRetryAtUtc」和「queued + NextRetryAtUtc == null 超期未推进」
// 的任务重新投递到 RabbitMQ。覆盖两类场景：
//   1. SourceDownloadJob 自动退避写入 NextRetryAtUtc 的定时重排；
//   2. SourceDownloadService.CreateTasks 投递时 RabbitMQ 短暂不可用导致的「裸排队」死任务
//      （queued + NextRetryAtUtc == null，创建后超过裸排队滞后期仍未推进）。
// 消费端原子认领（queued → downloading）保证重复投递安全。

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
    /// <summary>每轮扫描的任务上限（private const 编译器内联为字面量，SqlSugar 可解析）。</summary>
    private const int BatchLimit = 50;

    /// <summary>执行一轮到期任务扫描与重投递。</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        // 裸排队滞后期阈值：在 lambda 外预计算好 DateTime，SqlSugar 可解析闭包捕获的局部变量。
        // 若写成 task.UpdatedAtUtc < now - privateStaticField，SqlSugar Expression 解析器访问不了 private 成员会抛异常。
        var bareStuckThreshold = now - TimeSpan.FromSeconds(30);

        // ===== 1) 已到 NextRetryAtUtc 的自动退避任务 =====
        var retryDueIds = await db.Queryable<ExternalDownloadTask>()
            .Where(task => task.Status == ExternalDownloadTaskStatuses.Queued
                && task.NextRetryAtUtc != null
                && task.NextRetryAtUtc <= now)
            .OrderBy(task => task.NextRetryAtUtc)
            .Take(BatchLimit)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);

        // ===== 2) 裸排队死任务：queued + NextRetryAtUtc == null 且 UpdatedAtUtc 距今超过滞后期 =====
        // 正常新建的 queued 任务创建时 PublishAsync 会立即投递，消费端应在几秒内推进到 downloading。
        // 超过 grace period 仍原地不动 = PublishAsync 曾失败或消费者未连上 RabbitMQ，此时兜底重投。
        var bareStuckIds = await db.Queryable<ExternalDownloadTask>()
            .Where(task => task.Status == ExternalDownloadTaskStatuses.Queued
                && task.NextRetryAtUtc == null
                && task.UpdatedAtUtc < bareStuckThreshold)
            .OrderBy(task => task.UpdatedAtUtc)
            .Take(BatchLimit)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);

        var totalRepublished = 0;

        foreach (var taskId in retryDueIds)
        {
            await messageBus.PublishAsync(QueueNames.Downloads, taskId, cancellationToken);
            totalRepublished++;
        }

        foreach (var taskId in bareStuckIds)
        {
            await messageBus.PublishAsync(QueueNames.Downloads, taskId, cancellationToken);
            totalRepublished++;
        }

        if (totalRepublished > 0)
            logger.LogInformation(
                "Re-published {Total} source download task(s) (retry-due={RetryDue}, bare-stuck={BareStuck}).",
                totalRepublished, retryDueIds.Count, bareStuckIds.Count);
    }
}
