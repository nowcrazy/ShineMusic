// 收听行为与推荐相关实体：
//   ListeningEvent          —— 用户播放/播完/跳过的原始事件流（推荐算法的行为输入）；
//   UserTrackFeedback       —— 用户对曲目的显式反馈（喜欢/不喜欢/不感兴趣），复合主键 (UserId, TrackId)；
//   DailyRecommendationItem —— 已生成的“每日推荐”明细，用于当日结果稳定与跨日去重。
// 时间字段统一使用 UTC 的 DateTime（与项目其它实体一致，规避 SQLite 对 DateTimeOffset 的排序限制）。

using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>收听事件类型常量。</summary>
public static class ListeningEventTypes
{
    /// <summary>开始播放（记一次“收听”）。</summary>
    public const string Play = "play";

    /// <summary>自然播放结束（完整听完）。</summary>
    public const string Complete = "complete";

    /// <summary>播放途中被跳过/切走（负反馈信号）。</summary>
    public const string Skip = "skip";

    /// <summary>全部合法取值。</summary>
    public static readonly string[] All = [Play, Complete, Skip];

    /// <summary>判断给定事件类型是否合法。</summary>
    public static bool IsValid(string? type) =>
        type is not null && Array.Exists(All, item => string.Equals(item, type, StringComparison.Ordinal));
}

/// <summary>
/// 单条收听事件。匿名用户不产生记录（UserId 非空由写入端点保证）。
/// </summary>
[SugarTable("ListeningEvents")]
public sealed class ListeningEvent
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>触发事件的用户。</summary>
    public Guid UserId { get; set; }

    /// <summary>被收听的曲目。</summary>
    public Guid TrackId { get; set; }

    /// <summary>事件类型，取值见 <see cref="ListeningEventTypes"/>。</summary>
    public string EventType { get; set; } = ListeningEventTypes.Play;

    /// <summary>事件发生时的播放进度（毫秒）；用于判断“是否完整收听”等，可空。</summary>
    [SugarColumn(IsNullable = true)]
    public long? PositionMs { get; set; }

    /// <summary>事件发生时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>用户显式反馈类型常量。</summary>
public static class UserFeedbackKinds
{
    /// <summary>喜欢（正反馈）。</summary>
    public const string Like = "like";

    /// <summary>不喜欢（负反馈，强烈降权）。</summary>
    public const string Dislike = "dislike";

    /// <summary>不感兴趣（负反馈，直接排除）。</summary>
    public const string NotInterested = "not_interested";

    /// <summary>全部合法取值。</summary>
    public static readonly string[] All = [Like, Dislike, NotInterested];

    /// <summary>判断给定反馈类型是否合法。</summary>
    public static bool IsValid(string? kind) =>
        kind is not null && Array.Exists(All, item => string.Equals(item, kind, StringComparison.Ordinal));
}

/// <summary>
/// 用户对某曲目的显式反馈；同一 (用户, 曲目) 至多一条，复合主键去重。
/// </summary>
[SugarTable("UserTrackFeedbacks")]
public sealed class UserTrackFeedback
{
    /// <summary>反馈发起用户。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid UserId { get; set; }

    /// <summary>被反馈的曲目。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid TrackId { get; set; }

    /// <summary>反馈类型，取值见 <see cref="UserFeedbackKinds"/>。</summary>
    public string Kind { get; set; } = UserFeedbackKinds.Like;

    /// <summary>首次反馈时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>最近一次变更时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// “每日推荐”中的一条明细（用户 + 日期 + 曲目 + 排名）。
/// 用于当日结果稳定（命中即直接返回）与跨日去重（避免近期重复推荐）。
/// </summary>
[SugarTable("DailyRecommendationItems")]
public sealed class DailyRecommendationItem
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>推荐目标用户。</summary>
    public Guid UserId { get; set; }

    /// <summary>推荐所属日期键，格式 yyyyMMdd（按服务器本地日期）。</summary>
    public int DateKey { get; set; }

    /// <summary>被推荐的曲目。</summary>
    public Guid TrackId { get; set; }

    /// <summary>在当日推荐中的排名（从 0 开始）。</summary>
    public int Rank { get; set; }

    /// <summary>生成时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>个性化推荐策略版本；每个生效日期最多一条，修改策略从指定日期开始生效。</summary>
[SugarTable("RecommendationStrategyVersions")]
public sealed class RecommendationStrategyVersion
{
    /// <summary>策略生效日期键，格式 yyyyMMdd，同时作为主键避免同日重复版本。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public int EffectiveDateKey { get; set; }

    /// <summary>默认每日推荐曲目数。</summary>
    public int DailyTrackCount { get; set; }
    /// <summary>艺人亲和度权重。</summary>
    public double ArtistAffinityWeight { get; set; }
    /// <summary>播放次数权重。</summary>
    public double PlayCountWeight { get; set; }
    /// <summary>单曲播放加分的封顶次数。</summary>
    public int PlayCountCap { get; set; }
    /// <summary>跳过行为惩罚。</summary>
    public double SkipPenalty { get; set; }
    /// <summary>收藏加分。</summary>
    public double FavoriteBoost { get; set; }
    /// <summary>喜欢反馈加分。</summary>
    public double LikeBoost { get; set; }
    /// <summary>不喜欢反馈惩罚。</summary>
    public double DislikePenalty { get; set; }
    /// <summary>近期推荐降权的天数。</summary>
    public int NonRepeatDays { get; set; }
    /// <summary>近期重复推荐惩罚。</summary>
    public double RecentRecommendationPenalty { get; set; }
    /// <summary>新歌加权的线性衰减天数。</summary>
    public int NewTrackBoostDays { get; set; }
    /// <summary>新歌发布当天的最高加分。</summary>
    public double NewTrackBoostMax { get; set; }
    /// <summary>最后修改时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>最后修改策略的管理员用户 Id。</summary>
    public Guid UpdatedByUserId { get; set; }
}
