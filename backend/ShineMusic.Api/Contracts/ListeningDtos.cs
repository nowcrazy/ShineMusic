// 收听行为、用户反馈与每日推荐的 API 契约（DTO）。

namespace ShineMusic.Api.Contracts;

/// <summary>
/// 上报一条收听事件（POST /api/v1/me/play-events）。
/// </summary>
/// <param name="TrackId">被收听的曲目 ID。</param>
/// <param name="EventType">事件类型：play / complete / skip。</param>
/// <param name="PositionMs">事件发生时的播放进度（毫秒），可选。</param>
public sealed record PlayEventRequest(Guid TrackId, string EventType, long? PositionMs);

/// <summary>
/// 设置曲目反馈（PUT /api/v1/me/feedback/{trackId}）。
/// </summary>
/// <param name="Kind">反馈类型：like / dislike / not_interested。</param>
public sealed record FeedbackRequest(string Kind);

/// <summary>用户对某曲目的反馈记录。</summary>
public sealed record UserFeedbackDto(Guid TrackId, string Kind, DateTime UpdatedAtUtc);

/// <summary>
/// 每日推荐结果（GET /api/v1/recommendations/daily）。
/// </summary>
/// <param name="Date">推荐日期（yyyy-MM-dd，服务器本地日期）。</param>
/// <param name="Personalized">是否为基于该用户行为的个性化结果；false 表示热门/最新兜底。</param>
/// <param name="Tracks">推荐曲目列表（默认 30 首，互不重复）。</param>
public sealed record DailyRecommendationDto(string Date, bool Personalized, IReadOnlyList<TrackDto> Tracks);
