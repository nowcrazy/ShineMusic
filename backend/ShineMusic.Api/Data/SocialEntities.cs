// 职责：定义社交互动相关的实体——
// 曲目评论与回复（TrackComment，支持一层嵌套回复）、用户关注关系（UserFollow）。
// 时间字段统一使用 UTC DateTime（与项目其它实体一致）。

namespace ShineMusic.Api.Data;

/// <summary>
/// 曲目评论（含回复）。顶层评论的 ParentId 为空；回复的 ParentId 指向其所属的顶层评论（仅一层嵌套）。
/// </summary>
public sealed class TrackComment
{
    /// <summary>评论唯一标识，Guid 主键。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>被评论的曲目 Id。</summary>
    public Guid TrackId { get; set; }

    /// <summary>发表评论的用户 Id。</summary>
    public Guid UserId { get; set; }

    /// <summary>父评论 Id：回复场景指向顶层评论；顶层评论为 null。</summary>
    public Guid? ParentId { get; set; }

    /// <summary>评论文本内容（最长 500 字符）。</summary>
    public required string Content { get; set; }

    /// <summary>发表时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>编辑时间（UTC）；与发表于同一时刻默认，未实装编辑时保持等于创建时间。</summary>
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// 用户关注关系：FollowerId 关注 FolloweeId。
/// 以 (FollowerId, FolloweeId) 复合主键防止重复关注，不支持自关注（写入端点校验）。
/// </summary>
public sealed class UserFollow
{
    /// <summary>关注者用户 Id（复合主键的一部分）。</summary>
    public Guid FollowerId { get; set; }

    /// <summary>被关注者用户 Id（复合主键的一部分）。</summary>
    public Guid FolloweeId { get; set; }

    /// <summary>关注时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 评论点赞关系：某用户对某条评论（顶层或回复）点过赞。
/// 以 (CommentId, UserId) 复合主键防止重复点赞，取消点赞即删除本行。
/// </summary>
public sealed class TrackCommentLike
{
    /// <summary>被点赞的评论 Id（复合主键的一部分）。</summary>
    public Guid CommentId { get; set; }

    /// <summary>点赞用户 Id（复合主键的一部分）。</summary>
    public Guid UserId { get; set; }

    /// <summary>点赞时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}