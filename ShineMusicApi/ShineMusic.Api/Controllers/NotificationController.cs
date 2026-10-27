// =============================================================================
// 文件：NotificationController.cs
// 端点组：站内信（Notifications）—— 消息列表、未读数、单条已读、全部已读、删除。
// 路由前缀：/api/v1/me/notifications
// 鉴权边界：全部端点 [Authorize]，且查询/更新均以 sub 声明为过滤条件，
//           不存在读取或修改他人消息的路径（他人消息按 404 处理，不暴露存在性）。
// 未读角标：GET unread-count 供前端轮询；消息写入见 Services/NotificationWriter.cs。
// 数据访问：1b-④ 起由 SqlSugar 承担（原 EF Core 已移除）。
// =============================================================================
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>站内信：当前登录用户的消息列表与已读状态。</summary>
[ApiController]
[Route("api/v1/me/notifications")]
[Authorize]
[Tags("Notifications")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class NotificationController : ControllerBase
{
    // 【GET /api/v1/me/notifications】分页拉取当前用户的消息（可选只看未读）。
    // 返回：200 分页结果，按创建时间倒序；401 未登录。
    [HttpGet(Name = "GetMyNotifications")]
    public async Task<IResult> GetNotifications(
        ISqlSugarClient db,
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 1000);

        var query = db.Queryable<UserNotification>().Where(notification => notification.UserId == userId);
        if (unreadOnly)
            query = query.Where(notification => !notification.ReadAtUtc.HasValue);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .ToOffsetPageAsync(page, pageSize);
        var items = rows.Select(notification => new NotificationDto(
                notification.Id,
                notification.Kind,
                notification.Title,
                notification.Body,
                notification.LinkUrl,
                notification.ResourceType,
                notification.ResourceId,
                notification.ReadAtUtc.HasValue,
                notification.CreatedAtUtc))
            .ToList();

        return Results.Ok(new PageResult<NotificationDto>(items, page, pageSize, total));
    }

    // 【GET /api/v1/me/notifications/unread-count】未读消息数（顶部角标）。
    // 返回：200 { count }；401 未登录。
    [HttpGet("unread-count", Name = "GetUnreadNotificationCount")]
    public async Task<IResult> GetUnreadCount(ISqlSugarClient db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var count = await db.Queryable<UserNotification>()
            .CountAsync(notification => notification.UserId == userId && !notification.ReadAtUtc.HasValue, cancellationToken);
        return Results.Ok(new UnreadNotificationCountDto(count));
    }

    // 【POST /api/v1/me/notifications/{id}/read】把单条消息标记为已读（幂等）。
    // 返回：204；404 消息不存在或不属于当前用户；401 未登录。
    [HttpPost("{notificationId:guid}/read", Name = "MarkNotificationRead")]
    public async Task<IResult> MarkRead(Guid notificationId, ISqlSugarClient db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        // 直接按 (Id, UserId) 条件更新：他人消息命中 0 行，按不存在处理。
        var updated = await db.Updateable<UserNotification>()
            .SetColumns(notification => notification.ReadAtUtc == DateTime.UtcNow)
            .Where(notification => notification.Id == notificationId &&
                                   notification.UserId == userId &&
                                   !notification.ReadAtUtc.HasValue)
            .ExecuteCommandAsync(cancellationToken);
        if (updated == 0 && !await db.Queryable<UserNotification>().AnyAsync(
                notification => notification.Id == notificationId && notification.UserId == userId, cancellationToken))
            return ResourceNotFound();
        return Results.NoContent();
    }

    // 【POST /api/v1/me/notifications/read-all】把当前用户全部未读消息标记为已读。
    // 返回：204；401 未登录。
    [HttpPost("read-all", Name = "MarkAllNotificationsRead")]
    public async Task<IResult> MarkAllRead(ISqlSugarClient db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        await db.Updateable<UserNotification>()
            .SetColumns(notification => notification.ReadAtUtc == DateTime.UtcNow)
            .Where(notification => notification.UserId == userId && !notification.ReadAtUtc.HasValue)
            .ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    // 【DELETE /api/v1/me/notifications/{id}】删除单条消息。
    // 返回：204；404 消息不存在或不属于当前用户；401 未登录。
    [HttpDelete("{notificationId:guid}", Name = "DeleteNotification")]
    public async Task<IResult> Delete(Guid notificationId, ISqlSugarClient db, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var deleted = await db.Deleteable<UserNotification>()
            .Where(notification => notification.Id == notificationId && notification.UserId == userId)
            .ExecuteCommandAsync(cancellationToken);
        return deleted == 0 ? ResourceNotFound() : Results.NoContent();
    }

    /// <summary>统一 404 响应：不区分「不存在」与「不属于当前用户」，避免泄露他人消息存在性。</summary>
    private IResult ResourceNotFound() => Results.NotFound(
        new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceNotFound, "The requested resource was not found.", HttpContext.TraceIdentifier)));
}
