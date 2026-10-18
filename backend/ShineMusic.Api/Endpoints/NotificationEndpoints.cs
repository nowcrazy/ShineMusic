// =============================================================================
// 文件：NotificationEndpoints.cs
// 端点组：站内信（Notifications）—— 消息列表、未读数、单条已读、全部已读、删除。
// 路由前缀：/api/v1/me/notifications
// 鉴权边界：全部端点 [Authorize]，且查询/更新均以 sub 声明为过滤条件，
//           不存在读取或修改他人消息的路径（他人消息按 404 处理，不暴露存在性）。
// 未读角标：GET unread-count 供前端轮询；消息写入见 Services/NotificationWriter.cs。
// =============================================================================
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;

namespace ShineMusic.Api.Endpoints;

/// <summary>站内信：当前登录用户的消息列表与已读状态。</summary>
[ApiController]
[Route("api/v1/me/notifications")]
[Authorize]
[Tags("Notifications")]
public sealed class NotificationController : ControllerBase
{
    // 【GET /api/v1/me/notifications】分页拉取当前用户的消息（可选只看未读）。
    // 返回：200 分页结果，按创建时间倒序；401 未登录。
    [HttpGet(Name = "GetMyNotifications")]
    public async Task<IResult> GetNotifications(
        MusicDbContext db,
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.UserNotifications.AsNoTracking().Where(notification => notification.UserId == userId);
        if (unreadOnly)
            query = query.Where(notification => notification.ReadAtUtc == null);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(notification => new NotificationDto(
                notification.Id,
                notification.Kind,
                notification.Title,
                notification.Body,
                notification.LinkUrl,
                notification.ResourceType,
                notification.ResourceId,
                notification.ReadAtUtc != null,
                notification.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(new PageResult<NotificationDto>(items, page, pageSize, total));
    }

    // 【GET /api/v1/me/notifications/unread-count】未读消息数（顶部角标）。
    // 返回：200 { count }；401 未登录。
    [HttpGet("unread-count", Name = "GetUnreadNotificationCount")]
    public async Task<IResult> GetUnreadCount(MusicDbContext db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var count = await db.UserNotifications
            .CountAsync(notification => notification.UserId == userId && notification.ReadAtUtc == null, cancellationToken);
        return Results.Ok(new UnreadNotificationCountDto(count));
    }

    // 【POST /api/v1/me/notifications/{id}/read】把单条消息标记为已读（幂等）。
    // 返回：204；404 消息不存在或不属于当前用户；401 未登录。
    [HttpPost("{notificationId:guid}/read", Name = "MarkNotificationRead")]
    public async Task<IResult> MarkRead(Guid notificationId, MusicDbContext db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        // 直接按 (Id, UserId) 条件更新：他人消息命中 0 行，按不存在处理。
        var updated = await db.UserNotifications
            .Where(notification => notification.Id == notificationId &&
                                   notification.UserId == userId &&
                                   notification.ReadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAtUtc, DateTime.UtcNow), cancellationToken);
        if (updated == 0 && !await db.UserNotifications.AnyAsync(
                notification => notification.Id == notificationId && notification.UserId == userId, cancellationToken))
            return ResourceNotFound();
        return Results.NoContent();
    }

    // 【POST /api/v1/me/notifications/read-all】把当前用户全部未读消息标记为已读。
    // 返回：204；401 未登录。
    [HttpPost("read-all", Name = "MarkAllNotificationsRead")]
    public async Task<IResult> MarkAllRead(MusicDbContext db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        await db.UserNotifications
            .Where(notification => notification.UserId == userId && notification.ReadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAtUtc, DateTime.UtcNow), cancellationToken);
        return Results.NoContent();
    }

    // 【DELETE /api/v1/me/notifications/{id}】删除单条消息。
    // 返回：204；404 消息不存在或不属于当前用户；401 未登录。
    [HttpDelete("{notificationId:guid}", Name = "DeleteNotification")]
    public async Task<IResult> Delete(Guid notificationId, MusicDbContext db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var deleted = await db.UserNotifications
            .Where(notification => notification.Id == notificationId && notification.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? ResourceNotFound() : Results.NoContent();
    }

    /// <summary>统一 404 响应：不区分「不存在」与「不属于当前用户」，避免泄露他人消息存在性。</summary>
    private IResult ResourceNotFound() => Results.NotFound(
        new ApiErrorResponse(new ApiErrorDetail("resource_not_found", "The requested resource was not found.", HttpContext.TraceIdentifier)));
}
