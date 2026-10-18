// =============================================================================
// 文件：ListeningEndpoints.cs
// 端点组：收听行为与用户反馈（个性化推荐的行为数据入口）。
// 路由前缀：/api/v1/me
// 鉴权边界：全部需登录（行为数据按用户归属，匿名无法归属）。
// 能力开关：ListeningEvents（关闭时整组返回 404）。
// 覆盖：
//   POST   /me/play-events                 上报一条收听事件（play/complete/skip）
//   GET    /me/feedback                    拉取我的反馈列表
//   PUT    /me/feedback/{trackId}          设置/更新对某曲目的反馈（like/dislike/not_interested）
//   DELETE /me/feedback/{trackId}          清除对某曲目的反馈
// =============================================================================
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

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
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!ListeningEventTypes.IsValid(request.EventType))
            return ApiResults.ValidationError(HttpContext, "eventType", "eventType must be one of: play, complete, skip.");
        // 已公开曲目任意用户可上报；私有曲目（用户上传、未公开）仅上传者本人可上报。
        if (!await AccessibleTracks(db, userId).AnyAsync(track => track.Id == request.TrackId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        db.ListeningEvents.Add(new ListeningEvent
        {
            UserId = userId,
            TrackId = request.TrackId,
            EventType = request.EventType,
            // 进度为非负值时才记录，避免异常客户端传入负数。
            PositionMs = request.PositionMs is >= 0 ? request.PositionMs : null,
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpGet("feedback", Name = "GetMyFeedback")]
    public async Task<IResult> GetFeedback(MusicDbContext db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var items = await db.UserTrackFeedbacks.AsNoTracking()
            .Where(feedback => feedback.UserId == userId)
            .OrderByDescending(feedback => feedback.UpdatedAtUtc)
            .Select(feedback => new UserFeedbackDto(feedback.TrackId, feedback.Kind, feedback.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
        return Results.Ok(items);
    }

    [HttpPut("feedback/{trackId:guid}", Name = "SetTrackFeedback")]
    public async Task<IResult> SetFeedback(
        Guid trackId,
        FeedbackRequest request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!UserFeedbackKinds.IsValid(request.Kind))
            return ApiResults.ValidationError(HttpContext, "kind", "kind must be one of: like, dislike, not_interested.");
        // 已公开曲目任意用户可反馈；私有曲目（用户上传、未公开）仅上传者本人可反馈。
        if (!await AccessibleTracks(db, userId).AnyAsync(track => track.Id == trackId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var now = DateTime.UtcNow;
        var existing = await db.UserTrackFeedbacks
            .FirstOrDefaultAsync(feedback => feedback.UserId == userId && feedback.TrackId == trackId, cancellationToken);
        if (existing is null)
        {
            db.UserTrackFeedbacks.Add(new UserTrackFeedback
            {
                UserId = userId,
                TrackId = trackId,
                Kind = request.Kind,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }
        else
        {
            existing.Kind = request.Kind;
            existing.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpDelete("feedback/{trackId:guid}", Name = "ClearTrackFeedback")]
    public async Task<IResult> ClearFeedback(Guid trackId, MusicDbContext db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        await db.UserTrackFeedbacks
            .Where(feedback => feedback.UserId == userId && feedback.TrackId == trackId)
            .ExecuteDeleteAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>当前用户可访问的曲目：已公开曲目 + 本人上传的私有曲目。</summary>
    private static IQueryable<Track> AccessibleTracks(MusicDbContext db, Guid userId) =>
        db.Tracks.AsNoTracking()
            .Where(track => track.PublishedAt != null || track.OwnerUserId == userId);


}
