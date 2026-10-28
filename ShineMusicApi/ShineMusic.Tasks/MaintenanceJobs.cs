// MaintenanceJobs.cs
// 维护型后台任务与队列引导装配（轻量 BackgroundService 定时器）。
// 该文件包含四类职责内聚的类型：
//   1. LyricsBackfillJob / SongRequestArchiveJob：周期任务的纯业务逻辑（scoped，供定时器按作用域调用）；
//   2. PeriodicJobService：通用定时器骨架（固定间隔 + 单轮异常自吞，崩溃不影响宿主）；
//   3. 四个具体定时器：歌词补跑（6h）、点歌归档（配置间隔）、导入失败重扫（5min）、下载重试重扫（15s）；
//   4. QueueBootstrap：启动时恢复中断任务（中间态重置 + 重新投递 RabbitMQ）。
//
// 与 Hangfire 方案的差异：调度不再持久化（重启后定时器从零计时，对维护型任务无影响）；
// 业务状态机仍由业务表承载，消息重复投递由各处理器的状态守卫保证幂等。

using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.Messaging;
using ShineMusic.Service.SourceDownload;
using SqlSugar;

namespace ShineMusic.Tasks;

/// <summary>
/// 歌词定时补跑：扫描「已发布但无歌词」的曲目，重跑外部源多链路匹配，命中即落库为默认歌词。
/// 覆盖的场景：入库时外部源未命中（网络抖动/曲库覆盖随时间增长），事后自动补齐，避免人工逐首维护。
/// 温和策略：单轮上限 50 首、单曲间隔 400ms，避免触发外部歌词源限流。
/// </summary>
public sealed partial class LyricsBackfillJob(
    ISqlSugarClient db,
    ILyricsResolver lyricsResolver,
    ILogger<LyricsBackfillJob> logger)
{
    /// <summary>单轮补跑的曲目上限。</summary>
    private const int BatchSize = 50;

    /// <summary>单曲之间的请求间隔（对外部歌词源保持温和）。</summary>
    private static readonly TimeSpan PerTrackDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>执行一轮补跑：取最老的一批无歌词已发布曲目逐首匹配，命中即落库。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // PostgreSQL 原生支持 timestamptz 排序，直接在库内「最老优先」取批。
        var tracks = await db.Queryable<Track>()
            .Where(track => track.PublishedAt != null &&
                !SqlFunc.Subqueryable<Lyric>().Where(lyric => lyric.TrackId == track.Id).Any())
            .OrderBy(track => track.AddedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (tracks.Count == 0)
            return;

        // 导航水合（替代 Include）：艺人关联与专辑各取一次批量查询。
        var trackIds = tracks.Select(track => track.Id).ToList();
        var artistLinks = await db.Queryable<TrackArtist>()
            .InnerJoin<Artist>((link, artist) => link.ArtistId == artist.Id)
            .Where((link, artist) => trackIds.Contains(link.TrackId))
            .Select((link, artist) => new { link.TrackId, Artist = artist })
            .ToListAsync(cancellationToken);
        var artistsByTrack = artistLinks
            .GroupBy(link => link.TrackId)
            .ToDictionary(group => group.Key, group => group.Select(link => link.Artist).ToList());
        var albumIds = tracks.Where(track => track.AlbumId.HasValue).Select(track => track.AlbumId!.Value).Distinct().ToList();
        var albums = albumIds.Count == 0
            ? new List<Album>()
            : await db.Queryable<Album>().Where(album => albumIds.Contains(album.Id)).ToListAsync(cancellationToken);
        var albumMap = albums.ToDictionary(album => album.Id);
        foreach (var track in tracks)
        {
            track.TrackArtists = artistsByTrack.TryGetValue(track.Id, out var links)
                ? links.Select(artist => new TrackArtist { TrackId = track.Id, ArtistId = artist.Id, Artist = artist }).ToList()
                : [];
            track.Album = track.AlbumId.HasValue ? albumMap.GetValueOrDefault(track.AlbumId.Value) : null;
        }

        var matched = 0;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artists = track.TrackArtists.Select(trackArtist => trackArtist.Artist.Name).ToList();
            // 只走外部源匹配：内嵌/旁挂在入库流水线已尝试过（此处无源文件上下文，传 null 跳过前两层）。
            var match = await lyricsResolver.ResolveAsync(
                new LyricsQuery(track.Title, artists, track.Album?.Title, track.DurationMs, null, null),
                cancellationToken);
            if (match is not null)
            {
                await db.Insertable(new Lyric
                {
                    TrackId = track.Id,
                    Format = LrcTimestampRegex().IsMatch(match.Content) ? LyricFormats.Lrc : LyricFormats.Plain,
                    Content = match.Content,
                    IsDefault = true,
                    Source = match.Source,
                    Confidence = match.Confidence,
                    ProviderId = match.ProviderId
                }).ExecuteCommandAsync(cancellationToken);
                matched++;
                logger.LogInformation("Lyrics backfill matched track {TrackId} ({Title}) via {Source}.",
                    track.Id, track.Title, match.Source);
            }
            await Task.Delay(PerTrackDelay, cancellationToken);
        }
        logger.LogInformation("Lyrics backfill sweep finished: {Matched}/{Total} tracks matched.", matched, tracks.Count);
    }

    /// <summary>LRC 时间轴标记（[mm:ss]）识别：命中即按逐行歌词格式落库。</summary>
    [GeneratedRegex(@"\[\d{1,2}:\d{1,2}")]
    private static partial Regex LrcTimestampRegex();
}

/// <summary>
/// 点歌需求自动归档：把「已满足（fulfilled）且超过保留期」的需求写入 ArchivedAtUtc，
/// 使其从公共需求池隐藏；记录本身保留，提交人仍可在「我的」查看历史。
/// </summary>
public sealed class SongRequestArchiveJob(
    ISqlSugarClient db,
    IOptions<SongRequestOptions> options,
    ILogger<SongRequestArchiveJob> logger)
{
    /// <summary>执行一次归档扫描：单条 UPDATE 批量归档，避免把大量实体加载进内存。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var archiveAfterDays = Math.Max(1, options.Value.ArchiveAfterDays);
        var cutoff = DateTime.UtcNow.AddDays(-archiveAfterDays);
        var now = DateTime.UtcNow;

        var archived = await db.Updateable<SongRequestRecord>()
            .SetColumns(request => request.ArchivedAtUtc == now)
            .Where(request =>
                request.Status == SongRequestStatuses.Fulfilled &&
                request.ArchivedAtUtc == null &&
                request.UpdatedAtUtc < cutoff)
            .ExecuteCommandAsync(cancellationToken);

        if (archived > 0)
            logger.LogInformation(
                "Archived {Count} fulfilled song request(s) older than {Days} day(s).",
                archived,
                archiveAfterDays);
    }
}

/// <summary>
/// 通用周期任务骨架：固定间隔触发一轮业务逻辑；单轮异常仅记日志（下一轮自然再来），
/// 停止信号到达时立即退出。首轮在启动后等待一个间隔再执行（启动恢复已覆盖存量）。
/// </summary>
public abstract class PeriodicJobService(
    IServiceScopeFactory scopeFactory,
    ILogger logger) : BackgroundService
{
    /// <summary>触发间隔。</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>执行一轮业务逻辑（services 为本轮新建的 DI 作用域）。</summary>
    protected abstract Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 停止信号用 TCS 等待，避免 PeriodicTimer.WaitForNextTickAsync(token) / Task.Delay(Infinite, token)
        // 在停止时抛 OperationCanceledException（调试器「抛出即中断」会在每次停止调试时弹异常框）。
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stoppingToken.Register(() => stop.TrySetResult());
        while (!stoppingToken.IsCancellationRequested)
        {
            // 不带 token 的延时不会被取消，因此不会抛出异常；到点或被 stop 抢先都会正常返回。
            var tick = await Task.WhenAny(Task.Delay(Interval, CancellationToken.None), stop.Task);
            if (tick == stop.Task) break;
            try
            {
                using var scope = scopeFactory.CreateScope();
                await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Periodic job {Job} failed; next round will retry.", GetType().Name);
            }
        }
    }
}

/// <summary>歌词补跑定时器：每 6 小时一轮。</summary>
public sealed class LyricsBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<LyricsBackfillService> logger) : PeriodicJobService(scopeFactory, logger)
{
    /// <inheritdoc />
    protected override TimeSpan Interval => TimeSpan.FromHours(6);

    /// <inheritdoc />
    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
        => services.GetRequiredService<LyricsBackfillJob>().RunAsync(cancellationToken);
}

/// <summary>点歌归档定时器：间隔取 SongRequest:ArchiveScanIntervalMinutes；开关关闭时直接空转退出。</summary>
public sealed class SongRequestArchiveService(
    IServiceScopeFactory scopeFactory,
    IOptions<SongRequestOptions> options,
    ILogger<SongRequestArchiveService> logger) : PeriodicJobService(scopeFactory, logger)
{
    /// <inheritdoc />
    protected override TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(options.Value.ArchiveScanIntervalMinutes, 1, 1440));

    /// <inheritdoc />
    protected override async Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!options.Value.ArchiveEnabled)
            return;
        await services.GetRequiredService<SongRequestArchiveJob>().RunAsync(cancellationToken);
    }
}

/// <summary>导入失败重扫定时器：每 15 秒一轮（开发调试期缩短，生产可改回 5 分钟）。</summary>
public sealed class LibraryImportRetrySweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<LibraryImportRetrySweepService> logger) : PeriodicJobService(scopeFactory, logger)
{
    /// <inheritdoc />
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
        => services.GetRequiredService<LibraryImportRetrySweepJob>().SweepAsync(cancellationToken);
}

/// <summary>下载重试重扫定时器：每 15 秒一轮（退避精度要求最高的链路）。</summary>
public sealed class SourceDownloadRetrySweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<SourceDownloadRetrySweepService> logger) : PeriodicJobService(scopeFactory, logger)
{
    /// <inheritdoc />
    protected override TimeSpan Interval => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
        => services.GetRequiredService<SourceDownloadRetrySweepJob>().SweepAsync(cancellationToken);
}

/// <summary>
/// 队列引导装配：应用启动、SqlSugar CodeFirst 建表完成后调用。
/// 恢复中断任务：导入流水线中间态重置重新投递；下载任务 downloading→queued 重置，
/// 到期任务立即投递，带未来重试点的任务交给 15s 重扫定时器到点投递。
/// </summary>
public static class QueueBootstrap
{
    /// <summary>恢复中断任务并投递到 RabbitMQ。需在建表完成后调用（队列与业务表共用同一库）。</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var messageBus = scope.ServiceProvider.GetRequiredService<IRabbitMessageBus>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Queue.Bootstrap");

        // ===== 1) 曲库导入流水线恢复 =====
        // 中间状态（进程重启时可能卡在处理中）重置为 Uploaded（待重新处理），并重置转码子状态为 queued。
        var retryableStatuses = new[]
        {
            LibraryImportItemStatuses.Validating,
            LibraryImportItemStatuses.ParsingMetadata,
            LibraryImportItemStatuses.Transcoding
        };
        await db.Updateable<LibraryImportItem>()
            .SetColumns(item => item.Status == LibraryImportItemStatuses.Uploaded)
            .SetColumns(item => item.TranscodeStatus == "queued")
            .Where(item => retryableStatuses.Contains(item.Status))
            .ExecuteCommandAsync(cancellationToken);
        var queuedItemIds = await db.Queryable<LibraryImportItem>()
            .Where(item => item.Status == LibraryImportItemStatuses.Uploaded)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var itemId in queuedItemIds)
            await messageBus.PublishAsync(QueueNames.Imports, itemId, cancellationToken);
        if (queuedItemIds.Count > 0)
            logger.LogInformation("Recovered {Count} queued library import item(s).", queuedItemIds.Count);

        // ===== 2) 外部音源下载任务恢复 =====
        // downloading 重置为 queued（下载文件不可续传，整体重来）。
        await db.Updateable<ExternalDownloadTask>()
            .SetColumns(task => task.Status == ExternalDownloadTaskStatuses.Queued)
            .SetColumns(task => task.NextRetryAtUtc == (DateTime?)null)
            .SetColumns(task => task.UpdatedAtUtc == DateTime.UtcNow)
            .Where(task => task.Status == ExternalDownloadTaskStatuses.Downloading)
            .ExecuteCommandAsync(cancellationToken);
        // 已到期的排队任务立即投递；带未来重试点的任务由 15s 重扫定时器到点投递。
        var now = DateTime.UtcNow;
        var dueTaskIds = await db.Queryable<ExternalDownloadTask>()
            .Where(task => task.Status == ExternalDownloadTaskStatuses.Queued
                && (task.NextRetryAtUtc == null || task.NextRetryAtUtc <= now))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
        foreach (var taskId in dueTaskIds)
            await messageBus.PublishAsync(QueueNames.Downloads, taskId, cancellationToken);
        if (dueTaskIds.Count > 0)
            logger.LogInformation("Recovered {Count} queued source download task(s).", dueTaskIds.Count);
    }
}
