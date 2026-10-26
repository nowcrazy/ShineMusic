// 职责：定义外部平台歌单解析的抽象契约（IPlaylistParser）与解析结果模型
// （ParsedPlaylist / ParsedPlaylistTrack）。每个平台一个 parser 实现，
// 以单例注册并汇入 IEnumerable<IPlaylistParser>，由 PlaylistImportService 按 URL 选择。

namespace ShineMusic.Service.PlaylistImport;

/// <summary>外部平台歌单解析器：判断能否处理给定 URL，并把歌单页面/接口解析为统一模型。</summary>
public interface IPlaylistParser
{
    /// <summary>平台标识：netease / qqmusic / kuwo / ytmusic / spotify。</summary>
    string Platform { get; }

    /// <summary>判断该解析器是否支持给定 URL（按域名/路径形态匹配）。</summary>
    bool CanHandle(string url);

    /// <summary>解析歌单为统一模型；失败抛出异常（由上层收敛为中文友好提示）。</summary>
    Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken);
}

/// <summary>解析得到的歌单整体信息。</summary>
/// <param name="Name">歌单名。</param>
/// <param name="CoverUrl">封面地址（可空）。</param>
/// <param name="Description">歌单描述（可空）。</param>
/// <param name="Tracks">曲目列表（保持原歌单顺序）。</param>
public sealed record ParsedPlaylist(
    string Name,
    string? CoverUrl,
    string? Description,
    IReadOnlyList<ParsedPlaylistTrack> Tracks);

/// <summary>解析得到的单个曲目。</summary>
/// <param name="Title">曲目标题。</param>
/// <param name="Artists">艺人名列表。</param>
/// <param name="AlbumTitle">专辑名（可空）。</param>
/// <param name="DurationMs">时长（毫秒，可空）。</param>
/// <param name="SourceTrackId">平台曲目 Id（可空）。</param>
/// <param name="SourceUrl">曲目页面地址（可空）。</param>
public sealed record ParsedPlaylistTrack(
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    int? DurationMs,
    string? SourceTrackId,
    string? SourceUrl);
