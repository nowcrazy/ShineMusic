// =============================================================================
// 文件：ListeningController.cs
// 端点组：收听行为与用户反馈（个性化推荐的行为数据入口）。
// 路由前缀：/api/v1/me
// 鉴权边界：全部需登录（行为数据按用户归属，匿名无法归属）。
// 能力开关：ListeningEvents（关闭时整组返回 404）。
// 覆盖：
//   POST   /me/play-events                 上报一条收听事件（play/complete/skip）
//   GET    /me/feedback                    拉取我的反馈列表
//   PUT    /me/feedback/{trackId}          设置/更新对某曲目的反馈（like/dislike/not_interested）
//   DELETE /me/feedback/{trackId}          清除对某曲目的反馈
// 数据访问：1b-④ 起由 SqlSugar 承担（原 EF Core 已移除）。
// =============================================================================
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>Records listening events and explicit track feedback for the current user.</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[FeatureGate(nameof(FeatureOptions.ListeningEvents))]
[Tags("My Listening")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class ListeningController : ControllerBase
{
    [HttpPost("play-events", Name = "RecordPlayEvent")]
    [EnableRateLimiting("play-events")]
    public async Task<IResult> RecordPlayEvent(
        PlayEventRequest request,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!ListeningEventTypes.IsValid(request.EventType))
            return ApiResults.ValidationError(HttpContext, "eventType", "eventType must be one of: play, complete, skip.");
        // 已公开曲目任意用户可上报；私有曲目（用户上传、未公开）仅上传者本人可上报。
        if (!await CatalogQueries.AccessibleTracksWithDetails(db, userId).AnyAsync(track => track.Id == request.TrackId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        await db.Insertable(new ListeningEvent
        {
            UserId = userId,
            TrackId = request.TrackId,
            EventType = request.EventType,
            // 进度为非负值时才记录，避免异常客户端传入负数。
            PositionMs = request.PositionMs is >= 0 ? request.PositionMs : null,
            OccurredAtUtc = DateTime.UtcNow
        }).ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpGet("feedback", Name = "GetMyFeedback")]
    public async Task<IResult> GetFeedback(ISqlSugarClient db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var rows = await db.Queryable<UserTrackFeedback>()
            .Where(feedback => feedback.UserId == userId)
            .OrderByDescending(feedback => feedback.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        var items = rows.Select(feedback => new UserFeedbackDto(feedback.TrackId, feedback.Kind, feedback.UpdatedAtUtc)).ToList();
        return Results.Ok(items);
    }

    [HttpPut("feedback/{trackId:guid}", Name = "SetTrackFeedback")]
    public async Task<IResult> SetFeedback(
        Guid trackId,
        FeedbackRequest request,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!UserFeedbackKinds.IsValid(request.Kind))
            return ApiResults.ValidationError(HttpContext, "kind", "kind must be one of: like, dislike, not_interested.");
        // 已公开曲目任意用户可反馈；私有曲目（用户上传、未公开）仅上传者本人可反馈。
        if (!await CatalogQueries.AccessibleTracksWithDetails(db, userId).AnyAsync(track => track.Id == trackId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var now = DateTime.UtcNow;
        var exists = await db.Queryable<UserTrackFeedback>()
            .AnyAsync(feedback => feedback.UserId == userId && feedback.TrackId == trackId, cancellationToken);
        if (!exists)
        {
            await db.Insertable(new UserTrackFeedback
            {
                UserId = userId,
                TrackId = trackId,
                Kind = request.Kind,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }).ExecuteCommandAsync(cancellationToken);
        }
        else
        {
            await db.Updateable<UserTrackFeedback>()
                .SetColumns(feedback => feedback.Kind == request.Kind)
                .SetColumns(feedback => feedback.UpdatedAtUtc == now)
                .Where(feedback => feedback.UserId == userId && feedback.TrackId == trackId)
                .ExecuteCommandAsync(cancellationToken);
        }
        return Results.NoContent();
    }

    [HttpDelete("feedback/{trackId:guid}", Name = "ClearTrackFeedback")]
    public async Task<IResult> ClearFeedback(Guid trackId, ISqlSugarClient db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        await db.Deleteable<UserTrackFeedback>()
            .Where(feedback => feedback.UserId == userId && feedback.TrackId == trackId)
            .ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpGet("history", Name = "GetMyListeningHistory")]
    public async Task<IResult> GetHistory(
        ISqlSugarClient db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        // 按曲目聚合成一条历史：取最近一次收听时间 + 累计播放次数。
        // 用原生 SQL 聚合：绕开 SqlSugar GroupBy 后 CountAsync 语义不确定的风险。
        // PostgreSQL 下 UserId 为 uuid 列，参数必须直接传 Guid（传字符串会因 uuid=text 无匹配操作符报错）。
        var total = await db.Ado.GetIntAsync(
            "SELECT COUNT(DISTINCT \"TrackId\") FROM \"ListeningEvents\" WHERE \"UserId\" = @uid",
            new { uid = userId }, cancellationToken);
        var pageRows = await db.Ado.SqlQueryAsync<HistoryRow>(
            "SELECT \"TrackId\", MAX(\"OccurredAtUtc\") AS \"LastPlayed\", COUNT(*) AS \"PlayCount\" " +
            "FROM \"ListeningEvents\" WHERE \"UserId\" = @uid " +
            "GROUP BY \"TrackId\" ORDER BY \"LastPlayed\" DESC LIMIT @size OFFSET @offset",
            new { uid = userId, size, offset = (pageNumber - 1) * size }, cancellationToken);

        var trackIds = pageRows.Select(row => row.TrackId).ToList();
        var tracks = trackIds.Count == 0
            ? []
            : await CatalogQueries.AccessibleTracksWithDetails(db, userId)
                .Where(track => trackIds.Contains(track.Id))
                .ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        var trackMap = tracks.ToDictionary(track => track.Id);

        var items = pageRows
            .Where(row => trackMap.ContainsKey(row.TrackId))
            .Select(row => new ListeningHistoryItemDto(
                CatalogController.ToDto(trackMap[row.TrackId]),
                DateTime.SpecifyKind(row.LastPlayed, DateTimeKind.Utc),
                row.PlayCount))
            .ToArray();

        return Results.Ok(new PageResult<ListeningHistoryItemDto>(items, pageNumber, size, total));
    }

    /// <summary>历史聚合行（原生 SQL 结果映射 POCO）。</summary>
    public sealed class HistoryRow
    {
        public Guid TrackId { get; set; }
        public DateTime LastPlayed { get; set; }
        public int PlayCount { get; set; }
    }

    private bool TryGetPaging(int? page, int? pageSize, out IResult? error, out int pageNumber, out int size)
    {
        pageNumber = page ?? 1;
        size = pageSize ?? 20;
        error = null;
        if (pageNumber < 1)
            error = ApiResults.ValidationError(HttpContext, "page", "page must be at least 1.");
        else if (size < 1 || size > 100)
            error = ApiResults.ValidationError(HttpContext, "pageSize", "pageSize must be between 1 and 100.");
        return error is null;
    }
}
