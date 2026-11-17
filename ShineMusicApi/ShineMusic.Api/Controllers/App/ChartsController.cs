using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Entities;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>Public charts based on recorded plays, current favorites and publication time.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/charts")]
[Tags("Charts")]
public sealed class ChartsController(ISqlSugarClient db) : ControllerBase
{
    [HttpGet(Name = "GetCharts")]
    public async Task<IResult> GetCharts(CancellationToken cancellationToken, [FromQuery] string? genre = null)
    {
        var genreName = string.IsNullOrWhiteSpace(genre) ? null : genre.Trim();
        var now = DateTime.UtcNow;
        var weekly = await PlayIds(now.AddDays(-7), now, genreName, cancellationToken);
        var favoritesQuery = db.Queryable<Favorite, Track>((favorite, track) => favorite.TrackId == track.Id)
            .Where((favorite, track) => track.PublishedAt.HasValue);
        if (genreName is not null)
            favoritesQuery = favoritesQuery.Where((favorite, track) => SqlFunc.Subqueryable<TrackGenre>()
                .LeftJoin<Genre>((link, g) => link.GenreId == g.Id)
                .Where((link, g) => link.TrackId == track.Id && g.Name == genreName).Any());
        var favorites = await favoritesQuery
            .GroupBy((favorite, track) => favorite.TrackId)
            .OrderByDescending((favorite, track) => SqlFunc.AggregateCount(favorite.TrackId))
            .OrderBy((favorite, track) => favorite.TrackId)
            .Select((favorite, track) => new RankedTrack { TrackId = favorite.TrackId, Count = SqlFunc.AggregateCount(favorite.TrackId) })
            .Take(20).ToListAsync(cancellationToken);
        var allTime = await PlayIds(null, now, genreName, cancellationToken);
        var arrivalsQuery = CatalogQueries.PublishedTracks(db);
        if (genreName is not null)
            arrivalsQuery = arrivalsQuery.Where(track => SqlFunc.Subqueryable<TrackGenre>()
                .LeftJoin<Genre>((link, g) => link.GenreId == g.Id)
                .Where((link, g) => link.TrackId == track.Id && g.Name == genreName).Any());
        var arrivals = await arrivalsQuery.OrderByDescending(track => track.PublishedAt).OrderBy(track => track.Id)
            .Select(track => new RankedTrack { TrackId = track.Id }).Take(20).ToListAsync(cancellationToken);

        var ids = weekly.Concat(favorites).Concat(allTime).Concat(arrivals).Select(row => row.TrackId).Distinct().ToList();
        var tracks = ids.Count == 0 ? [] : await CatalogQueries.PublishedTracks(db)
            .Where(track => ids.Contains(track.Id)).ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        var map = tracks.ToDictionary(track => track.Id);
        object Chart(string id, string title, string description, List<RankedTrack> rows) => new
        {
            id, title, description,
            tracks = rows.Where(row => map.ContainsKey(row.TrackId))
                .Select(row => CatalogController.ToDto(map[row.TrackId])).ToArray()
        };
        const string rules = "仅含已发布且有记录的曲目，最多20首；次数相同按曲目ID升序，无记录时为空榜。";
        return Results.Ok(new
        {
            items = new[]
            {
                Chart("weekly-plays", "七日播放榜", "按最近7天（UTC）已记录的开始播放事件次数降序，不重复计算播完和跳过事件。" + rules, weekly),
                Chart("favorites", "收藏榜", "按当前收藏用户数降序，每位用户对同一曲目只计一次，取消收藏后不再计入。" + rules, favorites),
                Chart("all-time-plays", "累计播放榜", "按截至当前已记录的全部开始播放事件次数降序，不重复计算播完和跳过事件。" + rules, allTime),
                Chart("new-arrivals", "新声入库", "按曲目发布时间降序，最多20首；发布时间相同按曲目ID升序，无已发布曲目时为空榜。", arrivals)
            }
        });
    }

    private Task<List<RankedTrack>> PlayIds(DateTime? since, DateTime now, string? genreName, CancellationToken cancellationToken)
    {
        // A play is the existing ingestion contract's start event, not a duration-qualified listen.
        // No session identifier exists: repeated recorded starts count separately; complete/skip do not.
        var query = db.Queryable<ListeningEvent, Track>((item, track) => item.TrackId == track.Id)
            .Where((item, track) => item.ListeningEventType == ListeningEventTypes.Play && item.OccurredAtUtc <= now)
            .Where((item, track) => track.PublishedAt.HasValue);
        if (since.HasValue)
            query = query.Where((item, track) => item.OccurredAtUtc >= since.Value);
        if (genreName is not null)
            query = query.Where((item, track) => SqlFunc.Subqueryable<TrackGenre>()
                .LeftJoin<Genre>((link, g) => link.GenreId == g.Id)
                .Where((link, g) => link.TrackId == track.Id && g.Name == genreName).Any());
        return query.GroupBy((item, track) => item.TrackId)
            .OrderByDescending((item, track) => SqlFunc.AggregateCount(item.TrackId)).OrderBy((item, track) => item.TrackId)
            .Select((item, track) => new RankedTrack { TrackId = item.TrackId, Count = SqlFunc.AggregateCount(item.TrackId) })
            .Take(20).ToListAsync(cancellationToken);
    }

    private sealed class RankedTrack
    {
        public Guid TrackId { get; set; }
        public int Count { get; set; }
    }
}
