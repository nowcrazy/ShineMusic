// 职责：YouTube Music / Spotify 歌单解析器。
// YouTube Music：经 YtDlpBridge.GetPlaylistAsync（--flat-playlist --dump-single-json）解析，
//   国内网络需配置 SourceDownload:YtDlp:Proxy；
// Spotify：无官方 API 凭据无法解析，直接抛出带中文指引的 SourceUnavailableException。

using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>yt-dlp 系歌单解析器（YouTube Music 经子进程；Spotify 仅作友好提示）。</summary>
public sealed class YtDlpPlaylistParser(YtDlpBridge bridge) : IPlaylistParser
{
    /// <inheritdoc />
    public string Platform => "ytmusic";

    /// <inheritdoc />
    public bool CanHandle(string url) =>
        url.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken)
    {
        if (url.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase))
            throw new SourceUnavailableException("Spotify 歌单解析暂不支持（需官方 API 凭据），可复制到 YouTube Music 后导入。");

        YtDlpPlaylist playlist;
        try
        {
            playlist = await bridge.GetPlaylistAsync(url, cancellationToken);
        }
        catch (SourceUnavailableException exception)
        {
            // yt-dlp 未就绪/网络不通时给出中文指引（国内访问 YouTube Music 需代理）。
            throw new SourceUnavailableException(
                $"YouTube Music 歌单解析失败：{exception.Message}（国内网络需在 SourceDownload:YtDlp:Proxy 配置代理）");
        }

        var items = new List<ParsedPlaylistTrack>();
        foreach (var entry in playlist.Entries)
        {
            // artist 字段优先，缺省回退 uploader（YouTube 自动生成的 " - Topic" 频道名剔除后缀）。
            var artist = entry.Artist ?? entry.Uploader;
            if (artist is not null && artist.EndsWith(" - Topic", StringComparison.Ordinal))
                artist = artist[..^" - Topic".Length];
            items.Add(new ParsedPlaylistTrack(
                entry.Title!.Trim(),
                string.IsNullOrWhiteSpace(artist) ? [] : [artist.Trim()],
                entry.Album,
                entry.DurationMs is > 0 ? (int?)Math.Min(entry.DurationMs.Value, int.MaxValue) : null,
                entry.Id,
                entry.WebpageUrl));
        }
        if (items.Count == 0)
            throw new PlaylistParseException("歌单解析失败，请确认链接为公开歌单后重试。");
        return new ParsedPlaylist(playlist.Title, null, null, items);
    }
}
