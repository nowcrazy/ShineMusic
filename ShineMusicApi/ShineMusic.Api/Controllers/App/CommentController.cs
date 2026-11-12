// =============================================================================
// 文件：CommentController.cs
// 端点组：曲目评论与回复 + 评论点赞。
// 路由前缀：/api/v1
// 鉴权边界：读取（GET）匿名可访问（公开曲目）；发表 / 删除 / 点赞需登录。
// 覆盖：
//   GET    /tracks/{trackId}/comments        分页拉取某曲目顶层评论（含嵌套回复、点赞数）
//   POST   /tracks/{trackId}/comments        发表评论或回复
//   DELETE /comments/{commentId}             删除本人评论（管理员可删除任意评论）
//   PUT    /comments/{commentId}/like        点赞（幂等）
//   DELETE /comments/{commentId}/like        取消点赞（幂等）
// =============================================================================
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>Reads and writes track comments, replies and likes.</summary>
[ApiController]
[Route("api/v1")]
[Tags("Comments")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
public sealed class CommentController : ControllerBase
{
    [HttpGet("tracks/{trackId:guid}/comments", Name = "GetTrackComments")]
    [AllowAnonymous]
    public async Task<IResult> GetComments(
        Guid trackId,
        ISqlSugarClient db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;
        if (!await db.Queryable<Track>().AnyAsync(track => track.Id == trackId && track.PublishedAt != null, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var topQuery = db.Queryable<TrackComment>()
            .Where(comment => comment.TrackId == trackId && comment.ParentId == null);
        var total = await topQuery.CountAsync(cancellationToken);

        var tops = await topQuery
            .OrderByDescending(comment => comment.CreatedAtUtc)
            .Skip((pageNumber - 1) * size).Take(size)
            .ToListAsync(cancellationToken);

        var topIds = tops.Select(comment => comment.Id).ToList();
        var replies = topIds.Count == 0
            ? []
            : await db.Queryable<TrackComment>()
                .Where(comment => comment.ParentId != null && topIds.Contains(comment.ParentId!.Value))
                .OrderBy(comment => comment.CreatedAtUtc)
                .ToListAsync(cancellationToken);

        var names = await LoadNames(db, tops.Select(comment => comment.UserId)
            .Concat(replies.Select(comment => comment.UserId)), cancellationToken);

        // 点赞计数 + 当前用户是否已点赞（匿名时不判定）
        var likeCounts = await LoadLikeCounts(db, tops.Select(c => c.Id).Concat(replies.Select(c => c.Id)), cancellationToken);
        var likedByMe = await LoadLikedByMe(db, tops.Select(c => c.Id).Concat(replies.Select(c => c.Id)), cancellationToken);

        var items = tops.Select(comment => ToDto(comment, names, replies, likeCounts, likedByMe)).ToArray();
        return Results.Ok(new PageResult<TrackCommentDto>(items, pageNumber, size, total));
    }

    [HttpPost("tracks/{trackId:guid}/comments", Name = "CreateTrackComment")]
    [Authorize]
    public async Task<IResult> CreateComment(
        Guid trackId,
        CreateCommentRequest request,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var content = request.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content) || content.Length > 500)
            return ApiResults.ValidationError(HttpContext, "content", "Comment must be between 1 and 500 characters.");
        if (!await db.Queryable<Track>().AnyAsync(track => track.Id == trackId && track.PublishedAt != null, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        Guid? parentId = null;
        if (request.ParentId.HasValue)
        {
            var parent = await db.Queryable<TrackComment>()
                .FirstAsync(comment => comment.Id == request.ParentId.Value, cancellationToken);
            if (parent is null || parent.TrackId != trackId || parent.ParentId != null)
                return ApiResults.ValidationError(HttpContext, "parentId",
                    "Parent comment must be a top-level comment on the same track.");
            parentId = parent.Id;
        }

        var now = DateTime.UtcNow;
        var comment = new TrackComment
        {
            TrackId = trackId,
            UserId = userId,
            ParentId = parentId,
            Content = content,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await db.Insertable(comment).ExecuteCommandAsync(cancellationToken);

        var name = await db.Queryable<ApplicationUser>()
            .Where(user => user.Id == userId)
            .Select(user => user.DisplayName)
            .FirstAsync(cancellationToken) ?? string.Empty;
        return Results.Created($"/api/v1/tracks/{trackId}/comments",
            new TrackCommentDto(comment.Id, trackId, userId, name, parentId, content, now, null, 0, false));
    }

    [HttpDelete("comments/{commentId:guid}", Name = "DeleteTrackComment")]
    [Authorize]
    public async Task<IResult> DeleteComment(
        Guid commentId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var comment = await db.Queryable<TrackComment>().FirstAsync(item => item.Id == commentId, cancellationToken);
        if (comment is null)
            return ApiResults.NotFound(HttpContext);

        var isAdmin = HttpContext.User.IsInRole("admin");
        if (!isAdmin && comment.UserId != userId)
            return ApiResults.Error(HttpContext, StatusCodes.Status403Forbidden, ErrorCodes.PermissionDenied,
                "You can only delete your own comments.");

        // 级联删除：父评论的回复与点赞由外键 ON DELETE CASCADE 一并清除。
        await db.Deleteable<TrackComment>().Where(item => item.Id == commentId).ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpPut("comments/{commentId:guid}/like", Name = "LikeTrackComment")]
    [Authorize]
    public async Task<IResult> LikeComment(
        Guid commentId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!await db.Queryable<TrackComment>().AnyAsync(comment => comment.Id == commentId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var exists = await db.Queryable<TrackCommentLike>().AnyAsync(
            like => like.CommentId == commentId && like.UserId == userId, cancellationToken);
        if (!exists)
        {
            try
            {
                await db.Insertable(new TrackCommentLike { CommentId = commentId, UserId = userId }).ExecuteCommandAsync(cancellationToken);
            }
            catch (Exception)
            {
                // 并发重复点赞命中 (CommentId, UserId) 复合主键：视为已点赞，保持幂等。
            }
        }

        var count = await db.Queryable<TrackCommentLike>()
            .Where(like => like.CommentId == commentId)
            .CountAsync(cancellationToken);
        return Results.Ok(new CommentLikeResult(count, true));
    }

    [HttpDelete("comments/{commentId:guid}/like", Name = "UnlikeTrackComment")]
    [Authorize]
    public async Task<IResult> UnlikeComment(
        Guid commentId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!await db.Queryable<TrackComment>().AnyAsync(comment => comment.Id == commentId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        await db.Deleteable<TrackCommentLike>()
            .Where(like => like.CommentId == commentId && like.UserId == userId)
            .ExecuteCommandAsync(cancellationToken);

        var count = await db.Queryable<TrackCommentLike>()
            .Where(like => like.CommentId == commentId)
            .CountAsync(cancellationToken);
        return Results.Ok(new CommentLikeResult(count, false));
    }

    private static bool TryGetPaging(int? page, int? pageSize, out IResult? error, out int pageNumber, out int size)
    {
        pageNumber = page ?? 1;
        size = pageSize ?? 20;
        error = null;
        if (pageNumber < 1)
            error = Results.BadRequest(
                new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ValidationError, "page: page must be at least 1.", string.Empty)));
        else if (size < 1 || size > 1000)
            error = Results.BadRequest(
                new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ValidationError, "pageSize: pageSize must be between 1 and 1000.", string.Empty)));
        return error is null;
    }

    private static async Task<Dictionary<Guid, string>> LoadNames(
        ISqlSugarClient db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];
        var rows = await db.Queryable<ApplicationUser>()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.DisplayName })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Id, row => row.DisplayName);
    }

    private static async Task<Dictionary<Guid, int>> LoadLikeCounts(
        ISqlSugarClient db, IEnumerable<Guid> commentIds, CancellationToken cancellationToken)
    {
        var ids = commentIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];
        // 按评论分组统计点赞数（DB 端聚合，等价 EF 的 GroupBy + ToDictionaryAsync）。
        var rows = await db.Queryable<TrackCommentLike>()
            .Where(like => ids.Contains(like.CommentId))
            .GroupBy(like => like.CommentId)
            .Select(like => new { like.CommentId, Count = SqlFunc.AggregateCount(like.CommentId) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.CommentId, row => row.Count);
    }

    private async Task<HashSet<Guid>> LoadLikedByMe(
        ISqlSugarClient db, IEnumerable<Guid> commentIds, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var viewerId))
            return new HashSet<Guid>();
        var ids = commentIds.Distinct().ToList();
        if (ids.Count == 0)
            return new HashSet<Guid>();
        var liked = await db.Queryable<TrackCommentLike>()
            .Where(like => ids.Contains(like.CommentId) && like.UserId == viewerId)
            .Select(like => like.CommentId)
            .ToListAsync(cancellationToken);
        return liked.ToHashSet();
    }

    private static TrackCommentDto ToDto(
        TrackComment comment,
        Dictionary<Guid, string> names,
        IReadOnlyList<TrackComment> allReplies,
        Dictionary<Guid, int> likeCounts,
        HashSet<Guid> likedByMe)
    {
        names.TryGetValue(comment.UserId, out var name);
        var replies = comment.ParentId is null
            ? allReplies.Where(reply => reply.ParentId == comment.Id)
                .Select(reply => ToDto(reply, names, allReplies, likeCounts, likedByMe)).ToArray()
            : null;
        likeCounts.TryGetValue(comment.Id, out var count);
        return new TrackCommentDto(
            comment.Id,
            comment.TrackId,
            comment.UserId,
            name ?? string.Empty,
            comment.ParentId,
            comment.Content,
            comment.CreatedAtUtc,
            replies,
            count,
            likedByMe.Contains(comment.Id));
    }
}
