// 音乐目录（曲目/艺人/专辑/合集）对外契约：定义查询类 DTO、分页包装、统一错误响应以及系统能力快照。
namespace ShineMusic.Model.Contracts;

/// <summary>
/// 分页查询结果的统一包装。
/// </summary>
/// <typeparam name="T">当前页元素类型。</typeparam>
/// <param name="Items">当前页的数据列表。</param>
/// <param name="Page">当前页码，从 1 开始。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Total">符合查询条件的记录总数（用于客户端计算总页数）。</param>
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>
/// 所有 API 错误响应的统一外层结构，与成功响应的 JSON 形态区分。
/// </summary>
/// <param name="Error">错误详情。</param>
public sealed record ApiErrorResponse(ApiErrorDetail Error);

/// <summary>
/// 单条错误详情，错误码稳定可被客户端程序化处理，消息面向最终用户。
/// </summary>
/// <param name="Code">机器可读的稳定错误码（如 authentication_required、permission_denied、internal_error）。</param>
/// <param name="Message">面向用户的错误描述文案。</param>
/// <param name="TraceId">请求跟踪标识，与服务端日志关联，便于问题排查。</param>
public sealed record ApiErrorDetail(string Code, string Message, string TraceId);

/// <summary>
/// 艺人摘要信息，用于嵌套在曲目、专辑等对象中。
/// </summary>
/// <param name="Id">艺人唯一标识。</param>
/// <param name="Name">艺人名称。</param>
public sealed record ArtistSummaryDto(Guid Id, string Name);

/// <summary>
/// 专辑摘要信息，用于嵌套展示。
/// </summary>
/// <param name="Id">专辑唯一标识。</param>
/// <param name="Title">专辑标题。</param>
public sealed record AlbumSummaryDto(Guid Id, string Title);

/// <summary>
/// 可播放音频流的技术信息，客户端据此选择播放地址与解码方式。
/// </summary>
/// <param name="Container">音频容器格式标识（如 m4a、flac）。</param>
/// <param name="Codec">音频编码格式标识（如 aac、flac）。</param>
/// <param name="BitrateKbps">音频码率，单位 kbps；原始文件信息缺失时可能为 null。</param>
public sealed record AudioDto(string Container, string Codec, int? BitrateKbps);

/// <summary>
/// 曲目（可播放的单条音频）的完整展示信息。
/// </summary>
/// <param name="Id">曲目唯一标识。</param>
/// <param name="Title">曲目标题。</param>
/// <param name="Artists">参与该曲目的艺人列表（支持多位艺人）。</param>
/// <param name="Album">所属专辑摘要；单曲等无专辑场景为 null。</param>
/// <param name="TrackNumber">在专辑碟片内的曲目序号；未知时为 null。</param>
/// <param name="DiscNumber">碟片号，用于多碟专辑；未知时为 null。</param>
/// <param name="ReleaseYear">发行年份；未知时为 null。</param>
/// <param name="Genres">流派/风格标签列表。</param>
/// <param name="DurationMs">曲目时长，单位毫秒；探测失败时为 null。</param>
/// <param name="ArtworkUrl">封面图片地址；无封面时为 null。</param>
/// <param name="Audio">可播放音频信息；未完成转码或当前功能开关不允许播放时为 null。</param>
public sealed record TrackDto(
    Guid Id,
    string Title,
    IReadOnlyList<ArtistSummaryDto> Artists,
    AlbumSummaryDto? Album,
    int? TrackNumber,
    int? DiscNumber,
    int? ReleaseYear,
    IReadOnlyList<string> Genres,
    long? DurationMs,
    string? ArtworkUrl,
    AudioDto? Audio);

/// <summary>
/// 艺人详情页数据。
/// </summary>
/// <param name="Id">艺人唯一标识。</param>
/// <param name="Name">艺人名称。</param>
/// <param name="Biography">艺人简介文案；未维护时为 null。</param>
/// <param name="Albums">该艺人关联的专辑摘要列表。</param>
/// <param name="Tracks">该艺人关联的曲目列表。</param>
public sealed record ArtistDetailDto(
    Guid Id,
    string Name,
    string? Biography,
    IReadOnlyList<AlbumSummaryDto> Albums,
    IReadOnlyList<TrackDto> Tracks);

/// <summary>
/// 专辑详情页数据。
/// </summary>
/// <param name="Id">专辑唯一标识。</param>
/// <param name="Title">专辑标题。</param>
/// <param name="ReleaseYear">发行年份；未知时为 null。</param>
/// <param name="Artists">专辑艺人列表（合辑可能包含多位艺人）。</param>
/// <param name="ArtworkUrl">专辑封面地址；无封面时为 null。</param>
/// <param name="Tracks">专辑包含的曲目列表。</param>
public sealed record AlbumDetailDto(
    Guid Id,
    string Title,
    int? ReleaseYear,
    IReadOnlyList<ArtistSummaryDto> Artists,
    string? ArtworkUrl,
    IReadOnlyList<TrackDto> Tracks);

/// <summary>
/// 合集（运营精选集或歌单）的摘要信息。
/// </summary>
/// <param name="Id">合集唯一标识。</param>
/// <param name="Title">合集标题。</param>
/// <param name="Description">合集描述；未填写时为 null。</param>
/// <param name="ArtworkUrl">合集封面地址；无封面时为 null。</param>
/// <param name="TrackCount">合集内曲目数量（冗余计数，避免列表场景再查明细）。</param>
public sealed record CollectionSummaryDto(
    Guid Id,
    string Title,
    string? Description,
    string? ArtworkUrl,
    int TrackCount);

/// <summary>
/// 合集详情，包含完整曲目列表。
/// </summary>
/// <param name="Id">合集唯一标识。</param>
/// <param name="Title">合集标题。</param>
/// <param name="Description">合集描述；未填写时为 null。</param>
/// <param name="ArtworkUrl">合集封面地址；无封面时为 null。</param>
/// <param name="Tracks">合集内的曲目列表。</param>
public sealed record CollectionDetailDto(
    Guid Id,
    string Title,
    string? Description,
    string? ArtworkUrl,
    IReadOnlyList<TrackDto> Tracks);

/// <summary>
/// 音乐首页的聚合数据，一次请求返回首页各区块内容。
/// </summary>
/// <param name="Featured">首页主推/焦点曲目；未配置时为 null。</param>
/// <param name="Collections">精选合集摘要列表。</param>
/// <param name="RecentlyAdded">最近入库曲目的分页结果。</param>
public sealed record MusicHomeDto(
    TrackDto? Featured,
    IReadOnlyList<CollectionSummaryDto> Collections,
    PageResult<TrackDto> RecentlyAdded);

/// <summary>
/// 服务端当前功能开关与上传限制的快照，客户端据此显隐功能入口并做上传前校验。
/// 各布尔字段与 Features 配置节一一对应。
/// </summary>
/// <param name="AnonymousPlayback">是否允许匿名试听。</param>
/// <param name="UserCollections">是否开放用户个人歌单/收藏。</param>
/// <param name="SongRequests">是否开放点歌功能。</param>
/// <param name="AdminLibraryImport">是否开放管理员媒体库导入。</param>
/// <param name="ManualReview">导入内容是否需人工审核。</param>
/// <param name="EditorialCollections">是否开放运营精选合集。</param>
/// <param name="ListeningEvents">是否采集播放/聆听事件。</param>
/// <param name="PersonalizedRecommendations">是否启用个性化推荐。</param>
/// <param name="ServerTranscoding">是否启用服务端转码。</param>
/// <param name="UserUploads">是否开放用户上传本地音频（扫码导入）：上传的歌曲默认私有、可立即收听，是否转公开由管理员审核决定。</param>
/// <param name="AllowedFormats">允许上传的音频容器格式标识列表。</param>
/// <param name="MaxUploadSizeBytes">单文件上传大小上限，单位字节；未配置限制时为 null。</param>
/// <param name="MinimumPasswordStrength">修改密码要求达到的最低强度等级（0-4），由 PasswordPolicy:MinimumStrength 下发。</param>
public sealed record SystemCapabilitiesDto(
    bool AnonymousPlayback,
    bool UserCollections,
    bool SongRequests,
    bool AdminLibraryImport,
    bool ManualReview,
    bool EditorialCollections,
    bool ListeningEvents,
    bool PersonalizedRecommendations,
    bool ServerTranscoding,
    bool UserUploads,
    IReadOnlyList<string> AllowedFormats,
    long? MaxUploadSizeBytes,
    int MinimumPasswordStrength);

/// <summary>
/// 歌词展示信息，前端播放时按需拉取对应语言/格式的歌词。
/// </summary>
/// <param name="Id">歌词唯一标识。</param>
/// <param name="TrackId">所属曲目 Id。</param>
/// <param name="Language">语言代码（如 zh-CN、en）；未知时为 null。</param>
/// <param name="Format">歌词格式：plain（纯文本）或 lrc（带时间轴）。</param>
/// <param name="Content">歌词正文内容。</param>
/// <param name="IsDefault">是否为该曲目默认展示的歌词。</param>
/// <param name="UpdatedAtUtc">最后更新时间（UTC）。</param>
public sealed record LyricDto(
    Guid Id,
    Guid TrackId,
    string? Language,
    string Format,
    string Content,
    bool IsDefault,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 管理端创建歌词请求体。
/// </summary>
/// <param name="Language">语言代码；可为空。</param>
/// <param name="Format">歌词格式：plain 或 lrc，默认 plain。</param>
/// <param name="Content">歌词正文，不能为空。</param>
/// <param name="IsDefault">是否设为该曲目默认歌词。</param>
public sealed record CreateLyricRequest(
    string? Language,
    string? Format,
    string Content,
    bool IsDefault);

/// <summary>
/// 管理端更新歌词请求体（所有字段可选，仅更新提供的字段）。
/// </summary>
/// <param name="Language">语言代码；传空字符串表示清空。</param>
/// <param name="Format">歌词格式：plain 或 lrc。</param>
/// <param name="Content">歌词正文。</param>
/// <param name="IsDefault">是否设为默认歌词。</param>
public sealed record UpdateLyricRequest(
    string? Language,
    string? Format,
    string? Content,
    bool? IsDefault);
