// 职责：定义身份认证与用户互动相关的实体——
// ASP.NET Core Identity 用户（ApplicationUser）、JWT 刷新令牌（RefreshToken）、
// 注册邀请（Invitation 及其邀请状态机常量）、收藏（Favorite）、用户歌单（UserPlaylist/UserPlaylistTrack）、
// 点歌/求歌记录与投票（SongRequestRecord/SongRequestVote 及其审核流程状态）、
// 需求状态事件（SongRequestEventRecord，时间线数据源）、站内信（UserNotification）、
// 管理员审计条目（AdminAuditEntry）。

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;
using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>应用用户，继承 IdentityUser 并以 Guid 作为主键类型。</summary>
[SugarTable("AspNetUsers")]
public sealed class ApplicationUser : IdentityUser<Guid>
{
    // SetsRequiredMembers：SqlSugar 的 Insertable/Updateable 泛型带 new() 约束；
    // 标注后无参构造即可满足约束（ORM 反序列化/实体写入场景由调用方负责填充 required 成员）。
    [SetsRequiredMembers]
    public ApplicationUser()
    {
        // 应用层生成 Guid 主键，替代 Identity 默认的字符串/自增键。
        Id = Guid.NewGuid();
        DisplayName = null!;
        // Identity 基类的下列属性在 C# 签名上为可空，但 AspNetUsers 表对应列均为 NOT NULL。
        // 统一在构造函数中赋默认值，避免 create-admin / 注册插入时违反非空约束。
        // （UserName / Email / Normalized* / SecurityStamp / ConcurrencyStamp 由 UserService 在创建时补写，
        //   这里兜底给空串，防止 UserService 漏赋时插入失败。）
        PhoneNumber = string.Empty;
        NormalizedUserName = string.Empty;
        NormalizedEmail = string.Empty;
        SecurityStamp = string.Empty;
        ConcurrencyStamp = string.Empty;
        LockoutEnd = default(DateTimeOffset);
    }

    /// <summary>主键（覆盖基类属性以挂 SqlSugar 主键标注）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public override Guid Id { get; set; }

    /// <summary>展示名（昵称），最长 100 字符（在 DbContext 中限制）。</summary>
    public required string DisplayName { get; set; }
    /// <summary>账号创建时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// 临时管理员有效期截止（UTC）：仅当用户拥有 tempadmin 角色时有意义，
    /// 自授予起 3 天；过期后读取角色时自动降级为普通用户并清空本字段。
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? TempAdminUntilUtc { get; set; }
}

/// <summary>JWT 刷新令牌记录；明文令牌只发给客户端，数据库仅保存其哈希。</summary>
[SugarTable("RefreshTokens")]
public sealed class RefreshToken
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public RefreshToken() { TokenHash = null!; }

    /// <summary>令牌记录唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>所属用户 Id 外键。</summary>
    public Guid UserId { get; set; }
    /// <summary>所属用户导航属性（配置为无反向集合的一对多关系）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ApplicationUser User { get; set; } = null!;
    /// <summary>刷新令牌的 SHA256 哈希（唯一索引）；用于服务端比对，杜绝明文泄露风险。</summary>
    public required string TokenHash { get; set; }
    /// <summary>签发时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>过期时间（UTC）；(UserId, ExpiresAtUtc) 复合索引用于快速清理/查询有效令牌。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime ExpiresAtUtc { get; set; }
    /// <summary>吊销时间（UTC）；非空即表示已登出/被撤销，不可再用于换取新令牌。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? RevokedAtUtc { get; set; }
    /// <summary>轮换链：本次令牌被刷新后，记录接替它的新令牌哈希，便于检测令牌重用。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ReplacedByTokenHash { get; set; }
}

/// <summary>注册邀请记录；系统为邀请制，受邀邮箱凭一次性令牌完成注册。</summary>
[SugarTable("Invitations")]
public sealed class Invitation
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public Invitation() { TokenHash = null!; }

    /// <summary>邀请唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>受邀邮箱地址；CDK/注册码类邀请（issued 状态）生成时尚无邮箱，为空，注册时由用户自填。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Email { get; set; }
    /// <summary>归一化（大写）邮箱，用于按邮箱+状态检索的复合索引；CDK 邀请为空。</summary>
    [SugarColumn(IsNullable = true)]
    public string? NormalizedEmail { get; set; }
    /// <summary>邀请令牌的哈希（唯一索引）；邮件链接中携带明文令牌，库内只存哈希。</summary>
    public required string TokenHash { get; set; }
    /// <summary>邀请当前状态，取值见 <see cref="InvitationStatuses"/>（邀请状态机，最长 32 字符）。</summary>
    public string Status { get; set; } = InvitationStatuses.Created;
    /// <summary>创建该邀请的管理员用户 Id。</summary>
    public Guid CreatedByUserId { get; set; }
    /// <summary>创建时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>邀请过期时间（UTC）；到期后注册链接失效，状态可流转为 expired。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime ExpiresAtUtc { get; set; }
    /// <summary>邀请邮件发送成功时间（UTC）；为空表示尚未成功发出。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? SentAtUtc { get; set; }
    /// <summary>被兑换（用户完成注册）的时间（UTC）；进入 redeemed 终态。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? RedeemedAtUtc { get; set; }
}

/// <summary>邀请状态机常量：created→sent→redeemed 为主线，旁支为投递失败、过期、撤销。</summary>
public static class InvitationStatuses
{
    /// <summary>已创建但尚未发送邮件。</summary>
    public const string Created = "created";
    /// <summary>邀请邮件已成功发送，等待用户注册。</summary>
    public const string Sent = "sent";
    /// <summary>CDK/注册码已生成（无绑定邮箱），等待用户在注册时自填邮箱兑换。</summary>
    public const string Issued = "issued";
    /// <summary>邮件投递失败，可重试发送。</summary>
    public const string DeliveryFailed = "delivery_failed";
    /// <summary>终态：用户已凭邀请完成注册（已兑换）。</summary>
    public const string Redeemed = "redeemed";
    /// <summary>终态：超过 ExpiresAtUtc 仍未兑换。</summary>
    public const string Expired = "expired";
    /// <summary>终态：被管理员主动撤销。</summary>
    public const string Revoked = "revoked";

    /// <summary>全部合法状态集合，用于写入前的状态取值校验。</summary>
    public static readonly HashSet<string> All =
    [
        Created,
        Sent,
        Issued,
        DeliveryFailed,
        Redeemed,
        Expired,
        Revoked
    ];
}

/// <summary>用户收藏（喜欢的曲目）；以 (UserId, TrackId) 复合主键保证同一用户不重复收藏。</summary>
[SugarTable("Favorites")]
public sealed class Favorite
{
    /// <summary>收藏用户 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid UserId { get; set; }
    /// <summary>被收藏曲目 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid TrackId { get; set; }
    /// <summary>收藏时间（UTC）；(UserId, CreatedAtUtc) 复合索引支撑“按收藏时间倒序列表”。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>用户自建歌单（与编辑精选 EditorialCollection 相区分）。</summary>
[SugarTable("UserPlaylists")]
public sealed class UserPlaylist
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public UserPlaylist() { Name = null!; }

    /// <summary>歌单唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>歌单所属用户 Id。</summary>
    public Guid UserId { get; set; }
    /// <summary>歌单名称，最长 100 字符。</summary>
    public required string Name { get; set; }
    /// <summary>歌单描述。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Description { get; set; }
    /// <summary>封面 Id（可空）。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? ArtworkId { get; set; }
    /// <summary>创建时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>最近更新时间（UTC）；(UserId, UpdatedAtUtc) 复合索引支撑“我的歌单按更新排序”。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>歌单内曲目关联集合（一对多，显式配置外键 PlaylistId）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<UserPlaylistTrack> Tracks { get; set; } = [];
}

/// <summary>用户歌单内的曲目条目；以 (PlaylistId, TrackId) 复合主键防止重复加入。</summary>
[SugarTable("UserPlaylistTracks")]
public sealed class UserPlaylistTrack
{
    /// <summary>所属歌单 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid PlaylistId { get; set; }
    /// <summary>所属歌单导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public UserPlaylist Playlist { get; set; } = null!;
    /// <summary>曲目 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid TrackId { get; set; }
    /// <summary>歌单内排序序号，决定用户歌单的曲目顺序。</summary>
    public int SortOrder { get; set; }
    /// <summary>加入歌单的时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>歌单导入产生的待补齐曲目：外部歌单中未匹配到曲库的条目，静默下载发布完成后转正为 UserPlaylistTrack。</summary>
[SugarTable("UserPlaylistPendingTracks")]
public sealed class UserPlaylistPendingTrack
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束。</summary>
    [SetsRequiredMembers]
    public UserPlaylistPendingTrack() { Title = null!; ArtistsJson = null!; SourcePlatform = null!; }

    /// <summary>待补齐条目唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>所属歌单 Id。</summary>
    public Guid PlaylistId { get; set; }
    /// <summary>曲目标题（外部平台原文）。</summary>
    public required string Title { get; set; }
    /// <summary>艺人名列表的 JSON 数组字符串。</summary>
    public required string ArtistsJson { get; set; }
    /// <summary>专辑名（可空）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? AlbumTitle { get; set; }
    /// <summary>时长（毫秒，可空）。</summary>
    [SugarColumn(IsNullable = true)]
    public int? DurationMs { get; set; }
    /// <summary>封面地址（可空，外部平台 CDN）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? CoverUrl { get; set; }
    /// <summary>来源平台标识：netease / qqmusic / kuwo / ytmusic。</summary>
    public required string SourcePlatform { get; set; }
    /// <summary>来源平台曲目 Id（可空）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? SourceTrackId { get; set; }
    /// <summary>来源页面地址（可空）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? SourceUrl { get; set; }
    /// <summary>原歌单内排序序号（0 起），转正时作为 UserPlaylistTrack.SortOrder。</summary>
    public int SortOrder { get; set; }
    /// <summary>状态：pending（待下载）/ downloading（下载中）/ no_source（无可用源）/ failed（获取失败）。</summary>
    public string Status { get; set; } = "pending";
    /// <summary>关联的音源下载任务 Id（可空，未找到可用源时为空）。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? DownloadTaskId { get; set; }
    /// <summary>创建时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>最近更新时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>点歌/求歌记录：用户提交想要上架的曲目，管理员走审核流程处理，其他用户可投票顶歌。</summary>
[SugarTable("SongRequests")]
public sealed class SongRequestRecord
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public SongRequestRecord() { Title = null!; NormalizedTitle = null!; ArtistName = null!; NormalizedArtistName = null!; }

    /// <summary>点歌记录唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>用户填写的曲目标题（原文）。</summary>
    public required string Title { get; set; }
    /// <summary>归一化标题（去大小写/空白等差异），用于查重复合索引。</summary>
    public required string NormalizedTitle { get; set; }
    /// <summary>用户填写的艺术家名（原文）。</summary>
    public required string ArtistName { get; set; }
    /// <summary>归一化艺术家名，用于查重复合索引。</summary>
    public required string NormalizedArtistName { get; set; }
    /// <summary>用户填写的专辑名（原文，可空）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? AlbumTitle { get; set; }
    /// <summary>归一化专辑名，可空；参与查重复合索引。</summary>
    [SugarColumn(IsNullable = true)]
    public string? NormalizedAlbumTitle { get; set; }
    /// <summary>提交者附言（补充说明）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Note { get; set; }
    /// <summary>审核流程状态，取值见 <see cref="SongRequestStatuses"/>，默认 open。</summary>
    public string Status { get; set; } = SongRequestStatuses.Open;
    /// <summary>管理员处理备注（拒绝原因/处理说明等）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? AdminNote { get; set; }
    /// <summary>匹配到的曲库曲目 Id：该点歌被满足并上架后，回填对应 Track。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? MatchedTrackId { get; set; }
    /// <summary>归档时间（UTC）：已满足的需求在保留期后由后台任务写入，公共需求池不再展示；
    /// 记录本身保留，提交人仍可在「我的」中查看历史。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? ArchivedAtUtc { get; set; }
    /// <summary>提交该点歌的用户 Id。</summary>
    public Guid CreatedByUserId { get; set; }
    /// <summary>创建时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>状态/备注最近更新时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>该点歌收到的投票集合（一对多导航）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<SongRequestVote> Votes { get; set; } = [];
}

/// <summary>点歌投票（“顶歌”）；以 (RequestId, UserId) 复合主键保证一人对同一记录只能投一票。</summary>
[SugarTable("SongRequestVotes")]
public sealed class SongRequestVote
{
    /// <summary>被投票的点歌记录 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid RequestId { get; set; }
    /// <summary>点歌记录导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public SongRequestRecord Request { get; set; } = null!;
    /// <summary>投票用户 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid UserId { get; set; }
    /// <summary>投票时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>点歌需求状态事件：每次状态推进/撤回/重新开启追加一条，构成对用户可见的处理时间线。</summary>
[SugarTable("SongRequestEvents")]
public sealed class SongRequestEventRecord
{
    /// <summary>事件唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>所属点歌需求 Id；(RequestId, CreatedAtUtc) 复合索引支撑按时间线正序读取。</summary>
    public Guid RequestId { get; set; }
    /// <summary>事件类型，取值见 <see cref="SongRequestEventKinds"/>（最长 32 字符）。</summary>
    public string Kind { get; set; } = SongRequestEventKinds.Submitted;
    /// <summary>变更前状态；提交事件为 null。</summary>
    [SugarColumn(IsNullable = true)]
    public string? FromStatus { get; set; }
    /// <summary>变更后状态。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ToStatus { get; set; }
    /// <summary>操作人用户 Id；系统自动事件（如归档）为 null。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? ActorUserId { get; set; }
    /// <summary>操作人身份：user（提交人）/ admin（管理员）/ system（系统任务）。</summary>
    public string ActorKind { get; set; } = SongRequestActorKinds.System;
    /// <summary>事件备注：拒绝原因、重新开启原因等；仅提交人本人与管理员可见。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Note { get; set; }
    /// <summary>事件发生时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>点歌需求事件类型常量。</summary>
public static class SongRequestEventKinds
{
    /// <summary>用户提交需求（open）。</summary>
    public const string Submitted = "submitted";
    /// <summary>管理员纳入计划（planned）。</summary>
    public const string Planned = "planned";
    /// <summary>管理员开始处理（in_progress）。</summary>
    public const string InProgress = "in_progress";
    /// <summary>已入库满足（fulfilled）。</summary>
    public const string Fulfilled = "fulfilled";
    /// <summary>管理员拒绝（declined）。</summary>
    public const string Declined = "declined";
    /// <summary>提交人或管理员撤回（withdrawn）。</summary>
    public const string Withdrawn = "withdrawn";
    /// <summary>管理员重新开启已拒绝的需求（回到 open）。</summary>
    public const string Reopened = "reopened";
}

/// <summary>点歌需求事件的操作人身份常量。</summary>
public static class SongRequestActorKinds
{
    /// <summary>提交人本人。</summary>
    public const string User = "user";
    /// <summary>管理员。</summary>
    public const string Admin = "admin";
    /// <summary>系统任务（后台归档等）。</summary>
    public const string System = "system";
}

/// <summary>站内信：面向单个用户的通知，带已读状态，支撑顶部未读角标与消息列表。</summary>
[SugarTable("UserNotifications")]
public sealed class UserNotification
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public UserNotification() { Title = null!; }

    /// <summary>站内信唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>接收用户 Id；(UserId, CreatedAtUtc) 复合索引支撑按时间倒序拉取列表。</summary>
    public Guid UserId { get; set; }
    /// <summary>通知类型，取值见 <see cref="NotificationKinds"/>（最长 64 字符）。</summary>
    public string Kind { get; set; } = NotificationKinds.System;
    /// <summary>标题（最长 200 字符）。</summary>
    public required string Title { get; set; }
    /// <summary>正文说明（最长 1000 字符）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Body { get; set; }
    /// <summary>点击后跳转的前端页面路径（如 /pages/requests/index）；无跳转时为 null。</summary>
    [SugarColumn(IsNullable = true)]
    public string? LinkUrl { get; set; }
    /// <summary>关联资源类型（song_request 等），便于按业务回溯。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ResourceType { get; set; }
    /// <summary>关联资源 Id（字符串形式）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ResourceId { get; set; }
    /// <summary>已读时间（UTC）；为空表示未读，(UserId, ReadAtUtc) 索引支撑未读数统计。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? ReadAtUtc { get; set; }
    /// <summary>创建时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>站内信类型常量。</summary>
public static class NotificationKinds
{
    /// <summary>需求被拒绝（附原因）。</summary>
    public const string SongRequestDeclined = "song_request.declined";
    /// <summary>需求已入库满足。</summary>
    public const string SongRequestFulfilled = "song_request.fulfilled";
    /// <summary>已拒绝的需求被重新开启。</summary>
    public const string SongRequestReopened = "song_request.reopened";
    /// <summary>需求进入计划/处理中。</summary>
    public const string SongRequestProgress = "song_request.progress";
    /// <summary>需求被管理员撤回。</summary>
    public const string SongRequestWithdrawn = "song_request.withdrawn";
    /// <summary>系统通知（兜底类型）。</summary>
    public const string System = "system";
}

/// <summary>
/// 用户自定义图片主题配置：每用户至多一条（UserId 即主键，与 ApplicationUser 一对一），
/// 与 themes/{userId}.{ext} 壁纸文件配套；颜色为前端从壁纸取色后的 #RRGGBB 色板。
/// </summary>
[SugarTable("UserThemeProfiles")]
public sealed class UserThemeProfile
{
    /// <summary>所属用户 Id（主键，一对一）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid UserId { get; set; }
    /// <summary>壁纸文件扩展名（.jpg/.png/.webp）；null 表示尚未上传壁纸。</summary>
    [SugarColumn(IsNullable = true)]
    public string? WallpaperExtension { get; set; }
    /// <summary>主强调色（#RRGGBB）；尚未取色时为 null。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Accent { get; set; }
    /// <summary>次强调色（#RRGGBB），可空。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Accent2 { get; set; }
    /// <summary>第三强调色（#RRGGBB），可空。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Accent3 { get; set; }
    /// <summary>壁纸上的暗色遮罩不透明度（0-0.85），控制壁纸上的文字可读性。</summary>
    public float MaskOpacity { get; set; } = 0.45f;
    /// <summary>是否启用自定义主题；关闭后回退预设主题但保留配置（多端同步的开关事实源）。</summary>
    public bool Enabled { get; set; }
    /// <summary>最近更新时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>管理员操作审计条目：所有高权限管理动作均追加一条不可变留痕记录。</summary>
[SugarTable("AdminAuditEntries")]
public sealed class AdminAuditEntry
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public AdminAuditEntry() { Action = null!; ResourceType = null!; ResourceId = null!; TraceId = null!; }

    /// <summary>审计条目唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>执行操作的管理员用户 Id（取自 JWT 的 sub 声明）。</summary>
    public Guid ActorUserId { get; set; }
    /// <summary>动作标识（如邀请创建/撤销、点歌审核等），最长 100 字符。</summary>
    public required string Action { get; set; }
    /// <summary>被操作资源类型，最长 100 字符。</summary>
    public required string ResourceType { get; set; }
    /// <summary>被操作资源 Id（字符串形式），最长 100 字符。</summary>
    public required string ResourceId { get; set; }
    /// <summary>操作详情（可选，JSON 序列化后的快照，如变更前后内容）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? DetailsJson { get; set; }
    /// <summary>请求追踪 Id（HttpContext.TraceIdentifier），用于串联日志与审计，最长 100 字符。</summary>
    public required string TraceId { get; set; }
    /// <summary>客户端 IP 地址（最长 64）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Ip { get; set; }
    /// <summary>客户端类型：web-admin / app / unknown（最长 32）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ClientType { get; set; }
    /// <summary>设备描述：PC 端为浏览器+系统，App 端为机型+系统版本（最长 256）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? DeviceInfo { get; set; }
    /// <summary>审计写入时间（UTC）；(CreatedAtUtc, ActorUserId) 复合索引支撑按时间/操作人检索。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>登录审计条目：记录每次登录尝试（含失败）的账号、结果、IP 与客户端设备信息。</summary>
[SugarTable("LoginAuditEntries")]
public sealed class LoginAuditEntry
{
    /// <summary>无参构造：满足 SqlSugar 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public LoginAuditEntry() { Login = null!; Result = null!; Reason = null!; Ip = null!; }

    /// <summary>条目唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>登录尝试的账号（邮箱或用户名原样记录，最长 256）。</summary>
    public required string Login { get; set; }
    /// <summary>命中账号的用户 Id；账号不存在时为 null。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? UserId { get; set; }
    /// <summary>结果：success / failure（最长 16）。</summary>
    public required string Result { get; set; }
    /// <summary>原因：ok / account_not_found / bad_password / account_locked / missing_credentials（最长 32）。</summary>
    public required string Reason { get; set; }
    /// <summary>客户端 IP 地址（最长 64）。</summary>
    public required string Ip { get; set; }
    /// <summary>客户端类型：web-admin / app / web-app / unknown（最长 32）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ClientType { get; set; }
    /// <summary>设备描述：PC 端为浏览器+系统，App 端为机型+系统版本（最长 256）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? DeviceInfo { get; set; }
    /// <summary>原始 User-Agent（最长 512，排查用）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? UserAgent { get; set; }
    /// <summary>发生时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>点歌审核流程状态常量：open→planned→in_progress→fulfilled 为主线，declined 为拒绝终态。</summary>
public static class SongRequestStatuses
{
    /// <summary>待处理：新提交、等待管理员受理。</summary>
    public const string Open = "open";
    /// <summary>已纳入计划（管理员确认需求，排队处理中）。</summary>
    public const string Planned = "planned";
    /// <summary>处理中：正在寻源/导入/转码。</summary>
    public const string InProgress = "in_progress";
    /// <summary>终态：已满足，曲目已上架（通常配合 MatchedTrackId 回填）。</summary>
    public const string Fulfilled = "fulfilled";
    /// <summary>终态：管理员拒绝（原因记录在 AdminNote）。</summary>
    public const string Declined = "declined";
    /// <summary>终态：提交人（或管理员）主动撤回；仅在未进入处理前允许，保留记录不物理删除。</summary>
    public const string Withdrawn = "withdrawn";

    /// <summary>全部合法状态集合，用于状态流转校验。</summary>
    public static readonly HashSet<string> All = [Open, Planned, InProgress, Fulfilled, Declined, Withdrawn];
}

/// <summary>应用角色：映射 Identity 标准表 AspNetRoles（当前仅有 admin 一个角色）。</summary>
[SugarTable("AspNetRoles")]
public sealed class ApplicationRole : IdentityRole<Guid>
{
    /// <summary>主键（覆盖基类属性以挂 SqlSugar 主键标注）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public override Guid Id { get; set; }
}

/// <summary>
/// 内置角色名常量：user 普通用户 / admin 管理员（长期）/ tempadmin 临时管理员（3 天有效）。
/// 角色互斥：一个用户同一时刻只拥有其中一个。
/// </summary>
public static class RoleNames
{
    /// <summary>普通用户。</summary>
    public const string User = "user";
    /// <summary>管理员（长期有效）。</summary>
    public const string Admin = "admin";
    /// <summary>临时管理员：自授予起 3 天内视同管理员，到期自动降级为普通用户。</summary>
    public const string TempAdmin = "tempadmin";
}

/// <summary>用户-角色关联：映射 Identity 标准表 AspNetUserRoles（复合主键 UserId+RoleId）。</summary>
[SugarTable("AspNetUserRoles")]
public sealed class ApplicationUserRole : IdentityUserRole<Guid>
{
    /// <summary>用户 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public override Guid UserId { get; set; }

    /// <summary>角色 Id（复合主键的一部分）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public override Guid RoleId { get; set; }
}
