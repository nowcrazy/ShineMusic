// 社交互动（评论 / 关注）对外契约：评论、回复、关注列表与关注状态相关 DTO。
namespace ShineMusic.Api.Contracts;

/// <summary>
/// 曲目评论（或回复）的展示信息。顶层评论的 <paramref name="Replies"/> 内嵌其回复列表；
/// 回复本身的 Replies 恒为空。
/// </summary>
/// <param name="Id">评论唯一标识。</param>
/// <param name="TrackId">被评论曲目 Id。</param>
/// <param name="UserId">发表用户 Id。</param>
/// <param name="UserName">发表用户展示名（昵称）。</param>
/// <param name="ParentId">父评论 Id；顶层评论为 null。</param>
/// <param name="Content">评论文本。</param>
/// <param name="CreatedAtUtc">发表时间（UTC）。</param>
/// <param name="Replies">该评论的回复列表（仅顶层评论填充）。</param>
/// <param name="LikeCount">该评论的点赞总数。</param>
/// <param name="LikedByMe">当前用户是否已点赞（未登录恒为 false）。</param>
public sealed record TrackCommentDto(
    Guid Id,
    Guid TrackId,
    Guid UserId,
    string UserName,
    Guid? ParentId,
    string Content,
    DateTime CreatedAtUtc,
    IReadOnlyList<TrackCommentDto>? Replies,
    int LikeCount,
    bool LikedByMe);

/// <summary>发表评论 / 回复的请求体。</summary>
/// <param name="Content">评论文本，1~500 字符。</param>
/// <param name="ParentId">父评论 Id（回复时传入，指向同一曲目的顶层评论）；顶层评论为 null。</param>
public sealed record CreateCommentRequest(string Content, Guid? ParentId);

/// <summary>用户公开摘要（关注列表 / 评论作者展示用）。</summary>
/// <param name="Id">用户唯一标识。</param>
/// <param name="DisplayName">展示名（昵称）。</param>
public sealed record UserSummaryDto(Guid Id, string DisplayName);

/// <summary>关注关系状态快照。</summary>
/// <param name="IsFollowing">当前用户是否已关注目标用户。</param>
/// <param name="FollowingCount">目标用户的粉丝数（被多少人关注）。</param>
/// <param name="FollowersCount">当前用户关注了多少人（关注数）。</param>
public sealed record FollowStatusDto(bool IsFollowing, int FollowersCount, int FollowingCount);

/// <summary>点赞/取消点赞操作后的最新状态。</summary>
/// <param name="LikeCount">该评论最新点赞总数。</param>
/// <param name="LikedByMe">操作后当前用户是否处于已点赞状态。</param>
public sealed record CommentLikeResult(int LikeCount, bool LikedByMe);