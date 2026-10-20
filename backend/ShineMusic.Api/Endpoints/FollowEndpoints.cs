// =============================================================================
// 文件：FollowEndpoints.cs
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
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;

namespace ShineMusic.Api.Endpoints;

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
        MusicDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = from follow in db.UserFollows.AsNoTracking()
                    join user in db.Users.AsNoTracking() on follow.FolloweeId equals user.Id
                    where follow.FollowerId == userId
                    orderby follow.CreatedAtUtc descending
                    select new UserSummaryDto(user.Id, user.DisplayName);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<UserSummaryDto>(items, pageNumber, size, total));
    }

    [HttpGet("me/followers", Name = "GetMyFollowers")]
    public async Task<IResult> GetFollowers(
        MusicDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = from follow in db.UserFollows.AsNoTracking()
                    join user in db.Users.AsNoTracking() on follow.FollowerId equals user.Id
                    where follow.FolloweeId == userId
                    orderby follow.CreatedAtUtc descending
                    select new UserSummaryDto(user.Id, user.DisplayName);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<UserSummaryDto>(items, pageNumber, size, total));
    }

    [HttpPut("me/following/{targetUserId:guid}", Name = "FollowUser")]
    public async Task<IResult> Follow(
        Guid targetUserId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (targetUserId == userId)
            return ApiResults.ValidationError(HttpContext, "userId", "You cannot follow yourself.");

        if (!await db.Users.AnyAsync(user => user.Id == targetUserId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        if (!await db.UserFollows.AnyAsync(follow => follow.FollowerId == userId && follow.FolloweeId == targetUserId, cancellationToken))
        {
            db.UserFollows.Add(new UserFollow { FollowerId = userId, FolloweeId = targetUserId, CreatedAtUtc = DateTime.UtcNow });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
        return Results.NoContent();
    }

    [HttpDelete("me/following/{targetUserId:guid}", Name = "UnfollowUser")]
    public async Task<IResult> Unfollow(
        Guid targetUserId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        await db.UserFollows
            .Where(follow => follow.FollowerId == userId && follow.FolloweeId == targetUserId)
            .ExecuteDeleteAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpGet("users/{targetUserId:guid}/follow-summary", Name = "GetFollowSummary")]
    public async Task<IResult> GetSummary(
        Guid targetUserId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        HttpContext.User.TryGetUserId(out var viewerId);

        if (!await db.Users.AnyAsync(user => user.Id == targetUserId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var isFollowing = viewerId != Guid.Empty &&
            await db.UserFollows.AnyAsync(follow => follow.FollowerId == viewerId && follow.FolloweeId == targetUserId, cancellationToken);
        var followersCount = await db.UserFollows.CountAsync(follow => follow.FolloweeId == targetUserId, cancellationToken);
        var followingCount = await db.UserFollows.CountAsync(follow => follow.FollowerId == targetUserId, cancellationToken);

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