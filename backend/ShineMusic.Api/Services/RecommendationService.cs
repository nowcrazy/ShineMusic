// 每日推荐算法服务。
//
// 输入信号（对应产品需求）：
//   1) 播放次数：按曲目与歌手累计（play + complete 事件）——越常听越靠前；
//   2) 跳过/快切：skip 事件作为负反馈，对曲目降权；
//   3) 不喜欢/不感兴趣：显式负反馈——不喜欢强降权、不感兴趣直接排除；
//   4) 收藏/喜欢：显式正反馈——收藏与 like 加权。
//
// 稳定性与去重：
//   - 同一用户同一天的结果落库缓存（DailyRecommendationItem），当日重复请求返回同一份；
//   - 近 NonRepeatDays 天已推荐过的曲目降权，实现跨日“不重复”；
//   - 随机抖动以 (用户, 日期) 为种子，保证同日稳定、隔日变化。
//
// 匿名用户或个性化候选为空时，退化为“全局热度 + 最新”兜底列表（Personalized=false）。

using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Data;
using ShineMusic.Api.Endpoints;

namespace ShineMusic.Api.Services;

/// <summary>每日推荐计算结果。</summary>
/// <param name="Tracks">推荐曲目（已按推荐度排序）。</param>
/// <param name="Personalized">是否个性化结果；false 表示热度/最新兜底。</param>
/// <param name="Date">推荐日期（服务器本地日期）。</param>
public sealed record DailyRecommendationResult(IReadOnlyList<Track> Tracks, bool Personalized, DateOnly Date);

/// <summary>每日推荐服务（Scoped，依赖 MusicDbContext）。</summary>
public sealed class RecommendationService(MusicDbContext db)
{
    /// <summary>跨日去重窗口：近 N 天内已推荐过的曲目会被降权。</summary>
    private const int NonRepeatDays = 7;

    /// <summary>
    /// 获取某用户的每日推荐。
    /// </summary>
    /// <param name="userId">用户 ID；为 null 表示匿名（走兜底列表）。</param>
    /// <param name="limit">返回条数上限（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<DailyRecommendationResult> GetDailyAsync(Guid? userId, int limit, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dateKey = ToDateKey(today);

        if (userId is Guid uid)
        {
            // 1) 当日已生成：直接按排名取回，保证同一天稳定。
            var existing = await db.DailyRecommendationItems.AsNoTracking()
                .Where(item => item.UserId == uid && item.DateKey == dateKey)
                .OrderBy(item => item.Rank)
                .Select(item => item.TrackId)
                .ToListAsync(cancellationToken);
            if (existing.Count > 0)
            {
                var loaded = await LoadTracksByIdsAsync(existing, cancellationToken);
                var ordered = existing.Where(loaded.ContainsKey).Select(id => loaded[id]).ToList();
                if (ordered.Count > 0)
                    return new DailyRecommendationResult(ordered, true, today);
            }

            // 2) 现算并落库。
            var personalized = await ComputePersonalizedAsync(uid, limit, today, dateKey, cancellationToken);
            if (personalized.Count > 0)
            {
                await PersistAsync(uid, dateKey, personalized.Select(track => track.Id).ToList(), cancellationToken);
                return new DailyRecommendationResult(personalized, true, today);
            }
        }

        // 3) 匿名 / 无个性化候选：热度 + 最新兜底（不落库）。
        var fallback = await ComputeFallbackAsync(limit, cancellationToken);
        return new DailyRecommendationResult(fallback, false, today);
    }

    /// <summary>基于用户行为计算个性化推荐（内存打分排序）。</summary>
    private async Task<List<Track>> ComputePersonalizedAsync(Guid userId, int limit, DateOnly today, int dateKey, CancellationToken cancellationToken)
    {
        // —— 聚合行为信号 ——
        var playCounts = await db.ListeningEvents.AsNoTracking()
            .Where(evt => evt.UserId == userId && (evt.EventType == ListeningEventTypes.Play || evt.EventType == ListeningEventTypes.Complete))
            .GroupBy(evt => evt.TrackId)
            .Select(group => new { TrackId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var skipCounts = await db.ListeningEvents.AsNoTracking()
            .Where(evt => evt.UserId == userId && evt.EventType == ListeningEventTypes.Skip)
            .GroupBy(evt => evt.TrackId)
            .Select(group => new { TrackId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        // 歌手亲和度：把收听事件按曲目关联到艺人后计数。
        var artistAffinity = await (from evt in db.ListeningEvents.AsNoTracking()
                                    join link in db.Set<TrackArtist>() on evt.TrackId equals link.TrackId
                                    where evt.UserId == userId &&
                                          (evt.EventType == ListeningEventTypes.Play || evt.EventType == ListeningEventTypes.Complete)
                                    group evt by link.ArtistId into grouped
                                    select new { ArtistId = grouped.Key, Count = grouped.Count() })
            .ToListAsync(cancellationToken);
        var favoriteIds = await db.Favorites.AsNoTracking()
            .Where(fav => fav.UserId == userId)
            .Select(fav => fav.TrackId)
            .ToListAsync(cancellationToken);
        var feedback = await db.UserTrackFeedbacks.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.TrackId, item.Kind })
            .ToListAsync(cancellationToken);

        // —— 近 N 天已推荐过的曲目（跨日去重） ——
        var recentKeys = new List<int>(NonRepeatDays);
        for (var offset = 1; offset <= NonRepeatDays; offset++)
            recentKeys.Add(ToDateKey(today.AddDays(-offset)));
        var recentRecommended = await db.DailyRecommendationItems.AsNoTracking()
            .Where(item => item.UserId == userId && recentKeys.Contains(item.DateKey))
            .Select(item => item.TrackId)
            .ToListAsync(cancellationToken);

        var playMap = playCounts.ToDictionary(x => x.TrackId, x => x.Count);
        var skipMap = skipCounts.ToDictionary(x => x.TrackId, x => x.Count);
        var artistMap = artistAffinity.ToDictionary(x => x.ArtistId, x => x.Count);
        var favoriteSet = favoriteIds.ToHashSet();
        var likedSet = feedback.Where(f => f.Kind == UserFeedbackKinds.Like).Select(f => f.TrackId).ToHashSet();
        var dislikedSet = feedback.Where(f => f.Kind == UserFeedbackKinds.Dislike).Select(f => f.TrackId).ToHashSet();
        var notInterestedSet = feedback.Where(f => f.Kind == UserFeedbackKinds.NotInterested).Select(f => f.TrackId).ToHashSet();
        var recentSet = recentRecommended.ToHashSet();

        // 轻量候选：打分阶段只取所需字段（Id/标题/发布时间/艺人 ID），
        // 避免把整库实体及其导航（艺人/流派/专辑/封面/媒体资产）全部加载进内存。
        var candidates = await db.Tracks.AsNoTracking()
            .Where(track => track.PublishedAt != null)
            .Select(track => new
            {
                track.Id,
                track.Title,
                track.PublishedAt,
                ArtistIds = track.TrackArtists.Select(link => link.ArtistId).ToList()
            })
            .ToListAsync(cancellationToken);

        // 以 (用户, 日期) 为种子：同日稳定、隔日变化。
        var rng = new Random(unchecked(userId.GetHashCode() * 397) ^ dateKey);

        var scored = new List<(Guid Id, string Title, double Score)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // 显式“不感兴趣”直接排除。
            if (notInterestedSet.Contains(candidate.Id))
                continue;

            double score = 0;

            // 歌手亲和：用户越常听该艺人的歌，其曲目越靠前。
            foreach (var artistId in candidate.ArtistIds)
                score += 2.5 * artistMap.GetValueOrDefault(artistId, 0);

            // 歌曲播放次数（封顶，避免单曲刷分）。
            score += 3.0 * Math.Min(playMap.GetValueOrDefault(candidate.Id, 0), 20);

            // 跳过惩罚。
            score -= 4.0 * skipMap.GetValueOrDefault(candidate.Id, 0);

            // 显式正/负反馈。
            if (favoriteSet.Contains(candidate.Id)) score += 6;
            if (likedSet.Contains(candidate.Id)) score += 8;
            if (dislikedSet.Contains(candidate.Id)) score -= 40;

            // 跨日去重：近 N 天推过则降权（不硬排除，曲库不足时仍可补位）。
            if (recentSet.Contains(candidate.Id)) score -= 25;

            // 新歌轻微加权（30 天内线性衰减）。
            if (candidate.PublishedAt.HasValue)
            {
                var days = (DateTimeOffset.UtcNow - candidate.PublishedAt.Value).TotalDays;
                score += Math.Max(0, 3 - days / 30.0);
            }

            // 稳定抖动：同日同用户结果可复现。
            score += rng.NextDouble() * 1.5;

            scored.Add((candidate.Id, candidate.Title, score));
        }

        // 只对最终入选的曲目加载完整实体（含导航），最多 limit 条。
        var orderedIds = scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => item.Id)
            .ToList();

        var loaded = await LoadTracksByIdsAsync(orderedIds, cancellationToken);
        return orderedIds.Where(loaded.ContainsKey).Select(id => loaded[id]).ToList();
    }

    /// <summary>兜底推荐：全局热度（收听次数）+ 最新发布，供匿名用户或冷启动。</summary>
    private async Task<List<Track>> ComputeFallbackAsync(int limit, CancellationToken cancellationToken)
    {
        var popularity = await db.ListeningEvents.AsNoTracking()
            .Where(evt => evt.EventType == ListeningEventTypes.Play || evt.EventType == ListeningEventTypes.Complete)
            .GroupBy(evt => evt.TrackId)
            .Select(group => new { TrackId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var popularityMap = popularity.ToDictionary(x => x.TrackId, x => x.Count);

        // 轻量候选：只取排序所需字段，选出 ID 后再加载完整实体。
        var candidates = await db.Tracks.AsNoTracking()
            .Where(track => track.PublishedAt != null)
            .Select(track => new { track.Id, track.PublishedAt })
            .ToListAsync(cancellationToken);

        var orderedIds = candidates
            .OrderByDescending(candidate => popularityMap.GetValueOrDefault(candidate.Id, 0))
            .ThenByDescending(candidate => candidate.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(limit)
            .Select(candidate => candidate.Id)
            .ToList();

        var loaded = await LoadTracksByIdsAsync(orderedIds, cancellationToken);
        return orderedIds.Where(loaded.ContainsKey).Select(id => loaded[id]).ToList();
    }

    /// <summary>按给定 ID 顺序（去重）批量加载已发布曲目。</summary>
    private async Task<Dictionary<Guid, Track>> LoadTracksByIdsAsync(IReadOnlyCollection<Guid> trackIds, CancellationToken cancellationToken)
    {
        if (trackIds.Count == 0)
            return new Dictionary<Guid, Track>();
        var tracks = await CatalogController.PublishedTracks(db)
            .Where(track => trackIds.Contains(track.Id))
            .ToListAsync(cancellationToken);
        return tracks.ToDictionary(track => track.Id);
    }

    /// <summary>把当日推荐明细落库（先清空当日旧记录再写入，保证幂等）。</summary>
    private async Task PersistAsync(Guid userId, int dateKey, IReadOnlyList<Guid> trackIds, CancellationToken cancellationToken)
    {
        await db.DailyRecommendationItems
            .Where(item => item.UserId == userId && item.DateKey == dateKey)
            .ExecuteDeleteAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var items = trackIds.Select((trackId, index) => new DailyRecommendationItem
        {
            UserId = userId,
            DateKey = dateKey,
            TrackId = trackId,
            Rank = index,
            CreatedAtUtc = now
        });
        db.DailyRecommendationItems.AddRange(items);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>把日期转换为 yyyyMMdd 整数键。</summary>
    private static int ToDateKey(DateOnly date) => date.Year * 10000 + date.Month * 100 + date.Day;
}
