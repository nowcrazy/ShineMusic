// 职责：歌词定时补跑后台服务。
// 每 6 小时扫描「已发布但无歌词」的曲目，重跑 LRCLIB 外部匹配，命中即落库为该曲目的默认歌词。
// 覆盖的场景：入库时外部源未命中（网络抖动/曲库覆盖随时间增长），事后自动补齐，避免人工逐首维护。
// 温和策略：单轮上限 50 首、单曲间隔 400ms，避免触发外部歌词源限流；纯音乐等天然无歌词的曲目
// 会在每轮被重复尝试（成本极低，6h × 50 首的请求量可忽略）。

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Data;

namespace ShineMusic.Api.Services;

/// <summary>歌词定时补跑后台服务。</summary>
public sealed partial class LyricsBackfillService(
    IServiceScopeFactory scopeFactory,
    ILyricsResolver lyricsResolver,
    ILogger<LyricsBackfillService> logger) : BackgroundService
{
    /// <summary>补跑间隔（6 小时）。</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    /// <summary>启动后首次补跑前的缓冲（避开启动恢复/迁移的资源竞争窗口）。</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    /// <summary>单轮补跑的曲目上限。</summary>
    private const int BatchSize = 50;

    /// <summary>单曲之间的请求间隔（对外部歌词源保持温和）。</summary>
    private static readonly TimeSpan PerTrackDelay = TimeSpan.FromMilliseconds(400);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BackfillOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // 单轮失败仅记录，不中断后续轮次。
                logger.LogError(exception, "Lyrics backfill sweep failed.");
            }
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>执行一轮补跑：取最老的一批无歌词已发布曲目逐首匹配，命中即落库。</summary>
    private async Task BackfillOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
        // SQLite 不支持 DateTimeOffset 的 ORDER BY：先投影 Id+AddedAt 在内存中排序取批，再按批加载完整实体。
        var candidates = await db.Tracks.AsNoTracking()
            .Where(track => track.PublishedAt != null && !track.Lyrics.Any())
            .Select(track => new { track.Id, track.AddedAt })
            .ToListAsync(cancellationToken);
        var selectedIds = candidates
            .OrderBy(track => track.AddedAt)
            .Take(BatchSize)
            .Select(track => track.Id)
            .ToList();
        if (selectedIds.Count == 0)
            return;
        var tracks = await db.Tracks
            .Where(track => selectedIds.Contains(track.Id))
            .Include(track => track.TrackArtists).ThenInclude(trackArtist => trackArtist.Artist)
            .Include(track => track.Album)
            .ToListAsync(cancellationToken);
        // 保持「最老优先」的处理顺序。
        tracks = tracks.OrderBy(track => selectedIds.IndexOf(track.Id)).ToList();

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
                db.Lyrics.Add(new Lyric
                {
                    TrackId = track.Id,
                    Format = LrcTimestampRegex().IsMatch(match.Content) ? LyricFormats.Lrc : LyricFormats.Plain,
                    Content = match.Content,
                    IsDefault = true,
                    Source = match.Source,
                    Confidence = match.Confidence,
                    ProviderId = match.ProviderId
                });
                await db.SaveChangesAsync(cancellationToken);
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
