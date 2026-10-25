// 职责：站内信的统一写入入口。
// 业务动作（需求被拒绝/入库/重新开启等）产生对用户有意义的变更时，
// 向提交人投递一条站内信，驱动前端未读角标与消息列表。

using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>站内信写入帮助类。</summary>
public static class NotificationWriter
{
    /// <summary>需求池页面路径：站内信默认跳转目标。</summary>
    public const string RequestsPageUrl = "/pages/requests/index";

    /// <summary>
    /// 向指定用户投递一条站内信（立即 INSERT；调用方负责包事务）。
    /// </summary>
    /// <param name="db">SqlSugar 客户端（事务内调用自动并入当前事务）。</param>
    /// <param name="userId">接收人用户 Id；为空 Guid 时静默忽略（历史数据兜底）。</param>
    /// <param name="kind">通知类型，取值见 <see cref="NotificationKinds"/>。</param>
    /// <param name="title">标题（超长会被截断到 200 字符）。</param>
    /// <param name="body">正文说明。</param>
    /// <param name="linkUrl">点击跳转的前端页面路径。</param>
    /// <param name="resourceType">关联资源类型。</param>
    /// <param name="resourceId">关联资源 Id。</param>
    public static void Push(
        ISqlSugarClient db,
        Guid userId,
        string kind,
        string title,
        string? body = null,
        string? linkUrl = null,
        string? resourceType = null,
        string? resourceId = null)
    {
        if (userId == Guid.Empty)
            return;

        db.Insertable(BuildNotification(userId, kind, title, body, linkUrl, resourceType, resourceId)).ExecuteCommand();
    }

    /// <summary>构造站内信实体（两版 Push 共用字段规整逻辑）。</summary>
    private static UserNotification BuildNotification(
        Guid userId, string kind, string title, string? body, string? linkUrl, string? resourceType, string? resourceId) =>
        new()
        {
            UserId = userId,
            Kind = kind,
            Title = Truncate(title, 200),
            Body = string.IsNullOrWhiteSpace(body) ? null : Truncate(body, 1000),
            LinkUrl = string.IsNullOrWhiteSpace(linkUrl) ? null : Truncate(linkUrl, 300),
            ResourceType = resourceType,
            ResourceId = resourceId,
            CreatedAtUtc = DateTime.UtcNow
        };

    /// <summary>按字符上限截断字符串，避免超出数据库列长度导致写入失败。</summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
