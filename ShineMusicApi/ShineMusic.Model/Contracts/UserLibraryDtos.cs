// 用户曲库相关对外契约：用户歌单的展示/创建/更新 DTO，以及用户点歌（求歌）请求的提交、处理与展示 DTO。
namespace ShineMusic.Model.Contracts;

/// <summary>
/// 用户歌单的摘要信息，用于歌单列表等场景。
/// </summary>
/// <param name="Id">歌单唯一标识。</param>
/// <param name="Name">歌单名称。</param>
/// <param name="Description">歌单描述；未填写时为 null。</param>
/// <param name="TrackCount">歌单内曲目数量。</param>
/// <param name="UpdatedAt">歌单信息（含曲目增删）的最近更新时间（UTC）。</param>
/// <param name="ArtworkUrl">歌单封面地址；未设置封面时为 null。</param>
public sealed record PlaylistSummaryDto(Guid Id, string Name, string? Description, int TrackCount, DateTime UpdatedAt, string? ArtworkUrl = null);

/// <summary>
/// 用户歌单详情，包含完整曲目列表。
/// </summary>
/// <param name="Id">歌单唯一标识。</param>
/// <param name="Name">歌单名称。</param>
/// <param name="Description">歌单描述；未填写时为 null。</param>
/// <param name="Tracks">歌单内的曲目列表。</param>
/// <param name="UpdatedAt">歌单最近更新时间（UTC）。</param>
/// <param name="ArtworkUrl">歌单封面地址；未设置封面时为 null。</param>
/// <param name="PendingTracks">歌单导入产生的待补齐曲目（未匹配到曲库、静默下载进行中或失败的条目）。</param>
public sealed record PlaylistDetailDto(Guid Id, string Name, string? Description, IReadOnlyList<TrackDto> Tracks, DateTime UpdatedAt, string? ArtworkUrl = null, IReadOnlyList<PlaylistPendingTrackDto>? PendingTracks = null);

/// <summary>
/// 歌单导入产生的待补齐曲目（前端灰色展示，发布完成后自动转正）。
/// </summary>
/// <param name="Id">待补齐条目唯一标识。</param>
/// <param name="Title">曲目标题。</param>
/// <param name="Artists">艺人名列表。</param>
/// <param name="AlbumTitle">专辑名。</param>
/// <param name="DurationMs">时长（毫秒）。</param>
/// <param name="CoverUrl">封面地址。</param>
/// <param name="SortOrder">原歌单内排序序号（0 起）。</param>
/// <param name="Status">状态：pending/downloading/no_source/failed。</param>
public sealed record PlaylistPendingTrackDto(
    Guid Id,
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    int? DurationMs,
    string? CoverUrl,
    int SortOrder,
    string Status);

/// <summary>
/// 创建歌单的请求体；字段均可空，实际必填规则由服务端校验。
/// </summary>
/// <param name="Name">歌单名称。</param>
/// <param name="Description">歌单描述。</param>
/// <param name="ArtworkId">预先上传的封面图片资源标识；不使用封面时为 null。</param>
public sealed record CreatePlaylistRequest(string? Name, string? Description, Guid? ArtworkId = null);

/// <summary>
/// 更新歌单信息的请求体；未传字段表示不修改。
/// </summary>
/// <param name="Name">新的歌单名称。</param>
/// <param name="Description">新的歌单描述。</param>
/// <param name="ArtworkId">新封面图片资源标识。</param>
/// <param name="ClearArtwork">是否显式移除现有封面（true 时忽略 ArtworkId 并清空封面）。</param>
public sealed record UpdatePlaylistRequest(string? Name, string? Description, Guid? ArtworkId = null, bool ClearArtwork = false);

/// <summary>
/// 用户提交点歌（求歌）请求的请求体，描述希望曲库收录的歌曲；字段必填规则由服务端校验。
/// </summary>
/// <param name="Title">期望收录的曲目标题。</param>
/// <param name="ArtistName">艺人名称。</param>
/// <param name="AlbumTitle">专辑名称。</param>
/// <param name="Note">用户附加说明/备注。</param>
public sealed record CreateSongRequestRequest(string? Title, string? ArtistName, string? AlbumTitle, string? Note);

/// <summary>
/// 管理员处理点歌请求的请求体。
/// </summary>
/// <param name="Status">目标状态标识（如待处理、已采纳、已拒绝等，取值由服务端约定）。</param>
/// <param name="AdminNote">管理员处理备注，对用户可见或留档。</param>
/// <param name="MatchedTrackId">采纳时匹配到的正式曲库曲目标识；未匹配时为 null。</param>
public sealed record UpdateSongRequestRequest(string? Status, string? AdminNote, Guid? MatchedTrackId);

/// <summary>
/// 点歌请求的展示信息。
/// </summary>
/// <param name="Id">点歌请求唯一标识。</param>
/// <param name="Title">曲目标题。</param>
/// <param name="ArtistName">艺人名称。</param>
/// <param name="AlbumTitle">专辑名称；用户未提供时为 null。</param>
/// <param name="Status">当前处理状态标识。</param>
/// <param name="VoteCount">该请求获得的支持票数，用于排序热门诉求。</param>
/// <param name="MyVote">当前查看用户是否已为该请求投过票。</param>
/// <param name="MatchedTrackId">已采纳并匹配的曲库曲目标识；未匹配时为 null。</param>
/// <param name="CreatedAt">请求创建时间（UTC）。</param>
/// <param name="UpdatedAt">请求最近更新时间（UTC）。</param>
/// <param name="CreatedByMe">是否由当前查看用户本人提交。</param>
public sealed record SongRequestDto(
    Guid Id,
    string Title,
    string ArtistName,
    string? AlbumTitle,
    string Status,
    int VoteCount,
    bool MyVote,
    Guid? MatchedTrackId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool CreatedByMe);

/// <summary>
/// 点歌需求时间线上的单个事件，用于在卡片上展示「提交 → 纳入计划 → 处理中 → 已入库」的进度。
/// </summary>
/// <param name="Id">事件唯一标识。</param>
/// <param name="Kind">事件类型：submitted/planned/in_progress/fulfilled/declined/withdrawn/reopened。</param>
/// <param name="FromStatus">变更前状态；提交事件为 null。</param>
/// <param name="ToStatus">变更后状态。</param>
/// <param name="ActorKind">操作人身份：user/admin/system。</param>
/// <param name="Note">备注（拒绝原因/重新开启原因）；仅提交人本人与管理员可见，其余情况为 null。</param>
/// <param name="CreatedAt">事件发生时间（UTC）。</param>
public sealed record SongRequestTimelineItemDto(
    Guid Id,
    string Kind,
    string? FromStatus,
    string? ToStatus,
    string ActorKind,
    string? Note,
    DateTime CreatedAt);

/// <summary>
/// 管理员重新开启已拒绝需求的请求体。
/// </summary>
/// <param name="Reason">重新开启原因，写入时间线并通知提交人。</param>
public sealed record ReopenSongRequestRequest(string? Reason);
