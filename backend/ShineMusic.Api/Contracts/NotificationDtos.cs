// 站内信对外契约：消息列表项与未读数。
namespace ShineMusic.Api.Contracts;

/// <summary>
/// 站内信列表项。
/// </summary>
/// <param name="Id">消息唯一标识。</param>
/// <param name="Kind">消息类型（如 song_request.declined）。</param>
/// <param name="Title">标题。</param>
/// <param name="Body">正文说明；无正文时为 null。</param>
/// <param name="LinkUrl">点击跳转的前端页面路径；无跳转时为 null。</param>
/// <param name="ResourceType">关联资源类型；无关联时为 null。</param>
/// <param name="ResourceId">关联资源 Id；无关联时为 null。</param>
/// <param name="IsRead">是否已读。</param>
/// <param name="CreatedAt">创建时间（UTC）。</param>
public sealed record NotificationDto(
    Guid Id,
    string Kind,
    string Title,
    string? Body,
    string? LinkUrl,
    string? ResourceType,
    string? ResourceId,
    bool IsRead,
    DateTime CreatedAt);

/// <summary>
/// 未读消息数，供顶部角标轮询。
/// </summary>
/// <param name="Count">未读条数。</param>
public sealed record UnreadNotificationCountDto(int Count);
