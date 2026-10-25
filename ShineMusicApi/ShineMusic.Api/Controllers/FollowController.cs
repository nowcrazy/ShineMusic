// =============================================================================
// 文件：FollowController.cs
// 端点组：用户关注 / 粉丝。
// 路由前缀：/api/v1
// 鉴权边界：全部需登录（关注关系按当前用户归属）。
// 覆盖：
//   GET    /me/following                      我的关注列表（分页）
//   GET    /me/followers                      我的粉丝列表（分页）
//   PUT    /me/following/{userId}             关注某用户
//   DELETE /me/following/{userId}             取消关注
//   GET    /users/{userId}/follow-summary     某用户的关注状态与粉丝/关注数
// =============================================================================
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Api.Extensions;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>Manages the authenticated user's follow relationships.</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Tags("Follow")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
public sealed class FollowController : ControllerBase
{
    [HttpGet("me/following", Name = "GetMyFollowing")]
    public async Task<IResult> GetFollowing(
        ISqlSugarClient db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        // 联表：关注关系 → 用户（被关注者），取用户摘要。
        var query = db.Queryable<UserFollow, ApplicationUser>((follow, user) => follow.FolloweeId == user.Id)
            .Where((follow, user) => follow.FollowerId == userId);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending((follow, user) => follow.CreatedAtUtc)
            .Select((follow, user) => new { user.Id, user.DisplayName })
            .Skip((pageNumber - 1) * size).Take(size)
            .ToListAsync(cancellationToken);
        var items = rows.Select(row => new UserSummaryDto(row.Id, row.DisplayName)).ToArray();
        return Results.Ok(new PageResult<UserSummaryDto>(items, pageNumber, size, total));
    }

    [HttpGet("me/followers", Name = "GetMyFollowers")]
    public async Task<IResult> GetFollowers(
        ISqlSugarClient db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        // 联表：关注关系 → 用户（关注者），取用户摘要。
        var query = db.Queryable<UserFollow, ApplicationUser>((follow, user) => follow.FollowerId == user.Id)
            .Where((follow, user) => follow.FolloweeId == userId);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending((follow, user) => follow.CreatedAtUtc)
            .Select((follow, user) => new { user.Id, user.DisplayName })
            .Skip((pageNumber - 1) * size).Take(size)
            .ToListAsync(cancellationToken);
        var items = rows.Select(row => new UserSummaryDto(row.Id, row.DisplayName)).ToArray();
        return Results.Ok(new PageResult<UserSummaryDto>(items, pageNumber, size, total));
    }

    [HttpPut("me/following/{targetUserId:guid}", Name = "FollowUser")]
    public async Task<IResult> Follow(
        Guid targetUserId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (targetUserId == userId)
            return ApiResults.ValidationError(HttpContext, "userId", "You cannot follow yourself.");

        if (!await db.Queryable<ApplicationUser>().AnyAsync(user => user.Id == targetUserId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        if (!await db.Queryable<UserFollow>().AnyAsync(follow => follow.FollowerId == userId && follow.FolloweeId == targetUserId, cancellationToken))
        {
            try
            {
                await db.Insertable(new UserFollow { FollowerId = userId, FolloweeId = targetUserId, CreatedAtUtc = DateTime.UtcNow })
                    .ExecuteCommandAsync(cancellationToken);
            }
            catch (Exception)
            {
                // 并发重复关注命中 (FollowerId, FolloweeId) 复合主键：视为已关注，保持幂等。
            }
        }
        return Results.NoContent();
    }

    [HttpDelete("me/following/{targetUserId:guid}", Name = "UnfollowUser")]
    public async Task<IResult> Unfollow(
        Guid targetUserId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        await db.Deleteable<UserFollow>()
            .Where(follow => follow.FollowerId == userId && follow.FolloweeId == targetUserId)
            .ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpGet("users/{targetUserId:guid}/follow-summary", Name = "GetFollowSummary")]
    public async Task<IResult> GetSummary(
        Guid targetUserId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        HttpContext.User.TryGetUserId(out var viewerId);

        if (!await db.Queryable<ApplicationUser>().AnyAsync(user => user.Id == targetUserId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var isFollowing = viewerId != Guid.Empty &&
            await db.Queryable<UserFollow>().AnyAsync(follow => follow.FollowerId == viewerId && follow.FolloweeId == targetUserId, cancellationToken);
        var followersCount = await db.Queryable<UserFollow>()
            .Where(follow => follow.FolloweeId == targetUserId)
            .CountAsync(cancellationToken);
        var followingCount = await db.Queryable<UserFollow>()
            .Where(follow => follow.FollowerId == targetUserId)
            .CountAsync(cancellationToken);

        return Results.Ok(new FollowStatusDto(isFollowing, followersCount, followingCount));
    }

    private static bool TryGetPaging(int? page, int? pageSize, out IResult? error, out int pageNumber, out int size)
    {
        pageNumber = page ?? 1;
        size = pageSize ?? 20;
        error = null;
        if (pageNumber < 1)
            error = Results.BadRequest(
                new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ValidationError, "page: page must be at least 1.", string.Empty)));
        else if (size < 1 || size > 100)
            error = Results.BadRequest(
                new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ValidationError, "pageSize: pageSize must be between 1 and 100.", string.Empty)));
        return error is null;
    }
}
