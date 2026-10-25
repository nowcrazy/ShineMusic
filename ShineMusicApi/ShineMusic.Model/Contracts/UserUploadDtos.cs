// 用户上传（扫码导入）与“我的曲库”的对外契约。
// 用户上传与管理员导入共用同一套隔离暂存与后台处理队列；
// 内容哈希命中已有曲库的音频直接建曲库引用（秒级、不重复存储），
// 未命中的新歌进入处理队列，完成后自动私有入库（仅上传者可听），
// 是否转公开进入公共曲库由管理员审核决定。

namespace ShineMusic.Model.Contracts;

/// <summary>
/// 预检请求中的单个文件指纹，由客户端在上传前读取本地文件标签得到。
/// </summary>
/// <param name="FileName">本地文件名。</param>
/// <param name="SizeBytes">文件大小（字节）；读取失败时为 null。</param>
/// <param name="Title">标签中的标题；无标签时为 null。</param>
/// <param name="Artist">标签中的首位艺人；无标签时为 null。</param>
/// <param name="DurationMs">时长（毫秒）；读取失败时为 null。</param>
public sealed record UploadPrecheckFileDto(
    string FileName,
    long? SizeBytes,
    string? Title,
    string? Artist,
    long? DurationMs);

/// <summary>批量预检请求：上传前把本地文件指纹批量发给服务端查重。</summary>
public sealed record UploadPrecheckRequestDto(IReadOnlyList<UploadPrecheckFileDto> Files);

/// <summary>预检命中的曲目摘要。</summary>
/// <param name="TrackId">曲目 Id。</param>
/// <param name="Title">曲目标题。</param>
/// <param name="Artists">艺人名列表。</param>
/// <param name="IsPublic">是否已公开（公开曲目可被任何用户引用）。</param>
/// <param name="ArtworkUrl">封面图 URL；无封面时为 null。</param>
public sealed record PrecheckMatchDto(
    Guid TrackId,
    string Title,
    IReadOnlyList<string> Artists,
    bool IsPublic,
    string? ArtworkUrl);

/// <summary>单个文件的预检结果。</summary>
/// <param name="FileName">与请求对应的本地文件名。</param>
/// <param name="Match">命中的曲目；未命中为 null（客户端应上传该文件）。</param>
public sealed record UploadPrecheckResultDto(string FileName, PrecheckMatchDto? Match);

/// <summary>批量预检响应。</summary>
public sealed record UploadPrecheckResponseDto(IReadOnlyList<UploadPrecheckResultDto> Results);

/// <summary>上传响应中已引用（去重命中）的曲目：秒级加入曲库，无需等待处理。</summary>
public sealed record ReferencedTrackDto(Guid TrackId, string Title, IReadOnlyList<string> Artists);

/// <summary>上传响应中进入处理队列的新歌：转码 + 歌词自动匹配完成后私有入库。</summary>
public sealed record QueuedUploadDto(Guid ItemId, string DisplayName);

/// <summary>上传响应中无法引用/无法处理的文件（如他人私有曲目）。</summary>
public sealed record RejectedUploadDto(string DisplayName, string Reason);

/// <summary>用户上传响应：区分“已引用”（秒级加入曲库）与“处理中”（新歌走转码与歌词匹配）。</summary>
public sealed record UserUploadResponseDto(
    Guid? BatchId,
    IReadOnlyList<ReferencedTrackDto> Referenced,
    IReadOnlyList<QueuedUploadDto> Queued,
    IReadOnlyList<RejectedUploadDto> Rejected);

/// <summary>批量加入曲库请求：预检命中公共曲库的歌曲可直接引用，无需上传文件。</summary>
public sealed record AddToLibraryRequestDto(IReadOnlyList<Guid> TrackIds);

/// <summary>我的曲库条目。</summary>
/// <param name="TrackId">曲目 Id。</param>
/// <param name="Title">曲目标题。</param>
/// <param name="Artists">艺人名列表。</param>
/// <param name="AlbumTitle">专辑标题；无专辑时为 null。</param>
/// <param name="DurationMs">时长（毫秒）。</param>
/// <param name="ArtworkUrl">封面图 URL；无封面时为 null。</param>
/// <param name="IsPublic">是否已公开（私有曲目仅本人可见）。</param>
/// <param name="AddedAtUtc">加入曲库时间（UTC）。</param>
public sealed record UserLibraryItemDto(
    Guid TrackId,
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    long? DurationMs,
    string? ArtworkUrl,
    bool IsPublic,
    DateTime AddedAtUtc);
