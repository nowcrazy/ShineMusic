// =============================================================================
// 文件：CommentEndpoints.cs
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
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;

namespace ShineMusic.Api.Endpoints;

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
        MusicDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;
        if (!await db.Tracks.AnyAsync(track => track.Id == trackId && track.PublishedAt != null, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var topQuery = db.TrackComments.AsNoTracking()
            .Where(comment => comment.TrackId == trackId && comment.ParentId == null);
        var total = await topQuery.CountAsync(cancellationToken);

        var tops = await topQuery
            .OrderByDescending(comment => comment.CreatedAtUtc)
            .Skip((pageNumber - 1) * size).Take(size)
            .ToListAsync(cancellationToken);

        var topIds = tops.Select(comment => comment.Id).ToList();
        var replies = await db.TrackComments.AsNoTracking()
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
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var content = request.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content) || content.Length > 500)
            return ApiResults.ValidationError(HttpContext, "content", "Comment must be between 1 and 500 characters.");
        if (!await db.Tracks.AnyAsync(track => track.Id == trackId && track.PublishedAt != null, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        Guid? parentId = null;
        if (request.ParentId.HasValue)
        {
            var parent = await db.TrackComments.AsNoTracking()
                .FirstOrDefaultAsync(comment => comment.Id == request.ParentId.Value, cancellationToken);
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
        db.TrackComments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);

        var name = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        return Results.Created($"/api/v1/tracks/{trackId}/comments",
            new TrackCommentDto(comment.Id, trackId, userId, name, parentId, content, now, null, 0, false));
    }

    [HttpDelete("comments/{commentId:guid}", Name = "DeleteTrackComment")]
    [Authorize]
    public async Task<IResult> DeleteComment(
        Guid commentId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var comment = await db.TrackComments.FirstOrDefaultAsync(item => item.Id == commentId, cancellationToken);
        if (comment is null)
            return ApiResults.NotFound(HttpContext);

        var isAdmin = HttpContext.User.IsInRole("admin");
        if (!isAdmin && comment.UserId != userId)
            return ApiResults.Error(HttpContext, StatusCodes.Status403Forbidden, ErrorCodes.PermissionDenied,
                "You can only delete your own comments.");

        // 级联删除：父评论的回复与点赞由外键 ON DELETE CASCADE 一并清除。
        db.TrackComments.Remove(comment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpPut("comments/{commentId:guid}/like", Name = "LikeTrackComment")]
    [Authorize]
    public async Task<IResult> LikeComment(
        Guid commentId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!await db.TrackComments.AnyAsync(comment => comment.Id == commentId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var exists = await db.TrackCommentLikes.AnyAsync(
            like => like.CommentId == commentId && like.UserId == userId, cancellationToken);
        if (!exists)
        {
            db.TrackCommentLikes.Add(new TrackCommentLike { CommentId = commentId, UserId = userId });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }

        var count = await db.TrackCommentLikes.CountAsync(like => like.CommentId == commentId, cancellationToken);
        return Results.Ok(new CommentLikeResult(count, true));
    }

    [HttpDelete("comments/{commentId:guid}/like", Name = "UnlikeTrackComment")]
    [Authorize]
    public async Task<IResult> UnlikeComment(
        Guid commentId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!await db.TrackComments.AnyAsync(comment => comment.Id == commentId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        await db.TrackCommentLikes
            .Where(like => like.CommentId == commentId && like.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        var count = await db.TrackCommentLikes.CountAsync(like => like.CommentId == commentId, cancellationToken);
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
        else if (size < 1 || size > 100)
            error = Results.BadRequest(
                new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ValidationError, "pageSize: pageSize must be between 1 and 100.", string.Empty)));
        return error is null;
    }

    private static async Task<Dictionary<Guid, string>> LoadNames(
        MusicDbContext db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
    }

    private static async Task<Dictionary<Guid, int>> LoadLikeCounts(
        MusicDbContext db, IEnumerable<Guid> commentIds, CancellationToken cancellationToken)
    {
        var ids = commentIds.Distinct().ToList();
        return await db.TrackCommentLikes.AsNoTracking()
            .Where(like => ids.Contains(like.CommentId))
            .GroupBy(like => like.CommentId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken);
    }

    private async Task<HashSet<Guid>> LoadLikedByMe(
        MusicDbContext db, IEnumerable<Guid> commentIds, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var viewerId))
            return new HashSet<Guid>();
        var ids = commentIds.Distinct().ToList();
        var liked = await db.TrackCommentLikes.AsNoTracking()
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