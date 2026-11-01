// 职责：歌单导入的「待补齐曲目」后台补齐任务与定时器。
//
// 背景（线上事故）：导入 200+ 首歌单时，旧实现在 HTTP 请求线程里逐曲执行
// 「曲库匹配 + 多音源并行搜索 + 可下载预判 + 建下载任务」，单曲实测 25~50 秒，
// 200 首需 1.5 小时以上；前端 120 秒超时后掐断请求，CancellationToken 触发，
// 循环中途终止，结果只落库了前几首（表现为「导入 200+ 首却只有 2 个下载任务」），
// 用户重试又生成多个残缺歌单。
//
// 现在导入只做纯 DB 落库并立即返回，由本任务分轮次异步补齐：
// 每轮先结算已下载完成的行（转正/终态），再按最老优先补齐一小批，单轮有时间预算。

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShineMusic.Service.PlaylistImport;

namespace ShineMusic.Tasks;

/// <summary>
/// 歌单待补齐曲目的后台补齐任务：搜索音源、创建下载任务、把 pending 行推进到 downloading。
/// 单轮批量小、条间节流，对音源平台保持温和。
/// </summary>
public sealed class PlaylistPendingBackfillJob(
    PlaylistImportService importService,
    ILogger<PlaylistPendingBackfillJob> logger)
{
    /// <summary>单轮最多补齐的曲目数（配合收紧的搜索预算，单首约 1~8 秒，20 首约 20~100 秒）。</summary>
    private const int BatchSize = 20;

    /// <summary>单轮时间预算：超出即收尾，剩余曲目由下一轮继续。</summary>
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(100);

    /// <summary>执行一轮：结算 + 补齐。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var outcome = await importService.BackfillPendingAsync(BatchSize, TimeBudget, cancellationToken);
        var downloadQueue = await importService.ProcessDownloadQueueAsync(BatchSize, TimeBudget, cancellationToken);
        if (outcome.Settled > 0 || outcome.Processed > 0 || downloadQueue.Processed > 0)
        {
            logger.LogInformation(
                "Playlist backfill round: settledPlaylists={Settled}, playlistProcessed={Processed}, playlistQueued={Queued}, playlistNoSource={NoSource}, standaloneProcessed={StandaloneProcessed}, standaloneQueued={StandaloneQueued}, standaloneNoSource={StandaloneNoSource}.",
                outcome.Settled,
                outcome.Processed,
                outcome.Queued,
                outcome.NoSource,
                downloadQueue.Processed,
                downloadQueue.Queued,
                downloadQueue.NoSource);
        }
    }
}

/// <summary>歌单补齐定时器：每 5 秒一轮（小批量持续吞吐，200 首约 10~20 分钟补齐）。</summary>
public sealed class PlaylistPendingBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<PlaylistPendingBackfillService> logger) : PeriodicJobService(scopeFactory, logger)
{
    /// <inheritdoc />
    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
        => services.GetRequiredService<PlaylistPendingBackfillJob>().RunAsync(cancellationToken);
}
