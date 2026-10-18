// 媒体库批量导入对外契约：描述导入批次/导入项、源文件信息、解析出的元数据、转码状态，以及管理员审核修正请求。
namespace ShineMusic.Api.Contracts;

/// <summary>
/// 导入源文件的基础信息。
/// </summary>
/// <param name="DisplayName">上传时的原始文件名，仅用于界面展示，不参与服务端存储路径拼装。</param>
/// <param name="SizeBytes">源文件大小，单位字节。</param>
/// <param name="Container">ffprobe 探测到的容器格式标识；探测失败时为 null。</param>
/// <param name="Codec">ffprobe 探测到的音频编码标识；探测失败时为 null。</param>
public sealed record ImportSourceFileDto(string DisplayName, long SizeBytes, string? Container, string? Codec);

/// <summary>
/// 从源文件解析出的音频元数据，管理员可在人工审核环节修正后再正式入库。
/// </summary>
/// <param name="Title">曲目标题；标签缺失时为 null。</param>
/// <param name="Artists">艺人名列表（原标签可能含多位艺人）。</param>
/// <param name="AlbumTitle">专辑标题；缺失时为 null。</param>
/// <param name="TrackNumber">碟内曲目号；缺失时为 null。</param>
/// <param name="DiscNumber">碟片号；缺失时为 null。</param>
/// <param name="ReleaseYear">发行年份；缺失时为 null。</param>
/// <param name="Genres">流派标签列表。</param>
/// <param name="DurationMs">音频时长，单位毫秒；探测失败时为 null。</param>
/// <param name="Container">容器格式标识；探测失败时为 null。</param>
/// <param name="Codec">音频编码标识；探测失败时为 null。</param>
/// <param name="BitrateKbps">音频码率，单位 kbps；探测失败时为 null。</param>
/// <param name="ArtworkAvailable">源文件中是否包含可提取的内嵌封面。</param>
/// <param name="Lyrics">当前歌词正文（可能来自内嵌标签、旁挂 .lrc、外部源自动匹配或人工补充）；无歌词时为 null。</param>
/// <param name="LyricsSource">歌词来源标识：embedded / sidecar / lrclib / manual；无歌词时为 null。</param>
/// <param name="LyricsConfidence">歌词匹配置信度（0~1）；外部源按相似度打分，其余为 1。</param>
public sealed record ImportMetadataDto(
    string? Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    int? TrackNumber,
    int? DiscNumber,
    int? ReleaseYear,
    IReadOnlyList<string> Genres,
    long? DurationMs,
    string? Container,
    string? Codec,
    int? BitrateKbps,
    bool ArtworkAvailable,
    string? Lyrics,
    string? LyricsSource,
    double? LyricsConfidence);

/// <summary>
/// 单个导入文件的服务端转码状态。
/// </summary>
/// <param name="Status">转码状态标识（排队中/处理中/完成/失败等）；未进入转码时为 null。</param>
/// <param name="OutputFormat">实际产出的目标格式标识；未产出时为 null。</param>
/// <param name="BitrateKbps">产出音频码率，单位 kbps；未产出时为 null。</param>
/// <param name="ErrorCode">转码失败时的机器可读错误码；成功或未执行时为 null。</param>
public sealed record ImportTranscodeDto(string? Status, string? OutputFormat, int? BitrateKbps, string? ErrorCode);

/// <summary>
/// 导入项元数据与目标点歌需求的匹配结果（读取时实时计算，元数据被修正后立即刷新）。
/// </summary>
/// <param name="Kind">结论档位：match（一致）/ fuzzy（微小差异）/ mismatch（不一致）。</param>
/// <param name="TrackDisplay">待上架曲目的展示串（标题 - 艺人）。</param>
/// <param name="RequestDisplay">目标需求的展示串（标题 - 艺人）。</param>
/// <param name="Blocking">是否会在发布时被强制阻断（由全局配置决定）。</param>
/// <param name="Message">给管理员看的中文提示。</param>
public sealed record SongRequestMatchDto(
    string Kind,
    string TrackDisplay,
    string RequestDisplay,
    bool Blocking,
    string Message);

/// <summary>
/// 批量导入中的单个文件项（一个源文件对应一个导入项）。
/// </summary>
/// <param name="Id">导入项唯一标识。</param>
/// <param name="Status">导入项审核/处理状态标识。</param>
/// <param name="SourceFile">源文件基础信息。</param>
/// <param name="Metadata">解析出的音频元数据。</param>
/// <param name="Transcode">服务端转码状态。</param>
/// <param name="Warnings">非阻断性警告信息（如元数据缺失、标签可疑等），不阻止导入但提示管理员核对。</param>
/// <param name="CreatedAt">导入项创建时间（UTC）。</param>
/// <param name="UpdatedAt">导入项最近更新时间（UTC）。</param>
/// <param name="SongRequestId">关联的点歌需求标识；未关联时为 null。</param>
/// <param name="SongRequestMatch">与关联需求的匹配结果；未关联或元数据未解析时为 null。</param>
public sealed record LibraryImportItemDto(
    Guid Id,
    string Status,
    ImportSourceFileDto SourceFile,
    ImportMetadataDto Metadata,
    ImportTranscodeDto Transcode,
    IReadOnlyList<string> Warnings,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? SongRequestId,
    SongRequestMatchDto? SongRequestMatch);

/// <summary>
/// 一次批量上传形成的导入批次，包含批次内全部导入项。
/// </summary>
/// <param name="Id">批次唯一标识。</param>
/// <param name="Status">批次整体处理状态标识。</param>
/// <param name="Items">批次内的导入项列表。</param>
/// <param name="CreatedAt">批次创建时间（UTC）。</param>
public sealed record LibraryImportBatchDto(Guid Id, string Status, IReadOnlyList<LibraryImportItemDto> Items, DateTime CreatedAt);

/// <summary>
/// 管理员人工审核导入项时提交的修正后元数据；未传字段保持原值。
/// </summary>
/// <param name="Title">修正后的曲目标题。</param>
/// <param name="Artists">修正后的艺人名列表。</param>
/// <param name="AlbumTitle">修正后的专辑标题。</param>
/// <param name="TrackNumber">修正后的碟内曲目号。</param>
/// <param name="DiscNumber">修正后的碟片号。</param>
/// <param name="ReleaseYear">修正后的发行年份。</param>
/// <param name="Genres">修正后的流派标签列表。</param>
/// <param name="Lyrics">修正后的歌词；传空字符串表示清空，不传则保持原值。</param>
/// <param name="SongRequestId">关联的点歌请求标识；当本次导入用于满足某条点歌诉求时传入，否则为 null。</param>
public sealed record UpdateImportItemRequest(
    string? Title,
    IReadOnlyList<string>? Artists,
    string? AlbumTitle,
    int? TrackNumber,
    int? DiscNumber,
    int? ReleaseYear,
    IReadOnlyList<string>? Genres,
    string? Lyrics,
    Guid? SongRequestId);

/// <summary>
/// 带上传者信息的导入条目（管理端审核列表用）。
/// </summary>
/// <param name="Item">导入条目本体。</param>
/// <param name="UploadedByUserId">批次上传者用户 Id；用于区分管理员导入与用户上传。</param>
public sealed record LibraryImportItemWithUploaderDto(LibraryImportItemDto Item, Guid? UploadedByUserId);