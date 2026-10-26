// 职责：网易云音乐歌单解析器。
// 主路径：GET 歌单页 HTML，提取 <textarea id="song-list-pre-data"> 内嵌的歌单 JSON
// （tracks 全量含 name/artists[].name/album.name/duration(ms)/id）；
// 兜底路径：/api/v6/playlist/detail（公开歌单免登录，tracks 字段名为 ar/al/dt）。
// 支持 URL 形态：/playlist?id=xxx、/#/playlist?id=xxx、/playlist/xxx。

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>网易云音乐歌单解析器（命名 HttpClient "netease"）。</summary>
public sealed partial class NeteasePlaylistParser(IHttpClientFactory httpClientFactory) : IPlaylistParser
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称。</summary>
    public const string HttpClientName = "netease";

    private const string PlaylistPageBase = "https://music.163.com/playlist";
    private const string PlaylistApiBase = "https://music.163.com/api/v6/playlist/detail";

    /// <inheritdoc />
    public string Platform => "netease";

    /// <inheritdoc />
    public bool CanHandle(string url) =>
        url.Contains("music.163.com", StringComparison.OrdinalIgnoreCase)
        && url.Contains("playlist", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken)
    {
        var id = ExtractPlaylistId(url)
            ?? throw new PlaylistParseException("无法从链接中识别网易云歌单 ID。");
        var client = httpClientFactory.CreateClient(HttpClientName);

        // 主路径：歌单页内嵌 JSON（包含完整 tracks，字段名 artists/album/duration）。
        var html = await PlaylistHttpText.GetStringAsync(client, $"{PlaylistPageBase}?id={id}", cancellationToken);
        var embedded = ExtractEmbeddedJson(html);
        if (embedded is not null)
        {
            var parsed = TryParseEmbedded(id, embedded);
            if (parsed is not null)
                return parsed;
        }

        // 兜底：官方 API（字段名缩写 ar/al/dt）。
        return await ParseViaApiAsync(client, id, cancellationToken);
    }

    /// <summary>从 URL 中提取歌单数字 Id（支持 ?id= 与 /playlist/{id} 两种形态）。</summary>
    private static string? ExtractPlaylistId(string url)
    {
        var queryMatch = Regex.Match(url, @"[?&#]id=(\d+)");
        if (queryMatch.Success)
            return queryMatch.Groups[1].Value;
        var pathMatch = Regex.Match(url, @"/playlist/(\d+)");
        return pathMatch.Success ? pathMatch.Groups[1].Value : null;
    }

    /// <summary>提取歌单页中 song-list-pre-data 文本域的内容（HTML 实体解码）。</summary>
    private static string? ExtractEmbeddedJson(string html)
    {
        var match = TextareaRegex().Match(html);
        if (!match.Success)
            return null;
        var content = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return content.Length > 0 ? content : null;
    }

    /// <summary>解析内嵌 JSON（页面内嵌格式：tracks[].artists/album/duration）。</summary>
    private static ParsedPlaylist? TryParseEmbedded(string id, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 部分版本外层再包一层 { playlist: {...} }。
            if (!root.TryGetProperty("tracks", out var tracks)
                && root.TryGetProperty("playlist", out var inner))
                inner.TryGetProperty("tracks", out tracks);
            if (tracks.ValueKind != JsonValueKind.Array)
                return null;

            var container = root.TryGetProperty("playlist", out var pl) ? pl : root;
            var name = GetString(container, "name") ?? $"网易云歌单 {id}";
            var cover = GetString(container, "coverImgUrl");
            var description = GetString(container, "description");

            var items = new List<ParsedPlaylistTrack>();
            foreach (var track in tracks.EnumerateArray())
            {
                var title = GetString(track, "name");
                if (string.IsNullOrWhiteSpace(title))
                    continue;
                var artists = new List<string>();
                if (track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var artist in artistArray.EnumerateArray())
                    {
                        var artistName = GetString(artist, "name");
                        if (!string.IsNullOrWhiteSpace(artistName))
                            artists.Add(artistName.Trim());
                    }
                }
                string? album = null;
                if (track.TryGetProperty("album", out var albumElement) && albumElement.ValueKind == JsonValueKind.Object)
                    album = GetString(albumElement, "name");
                int? durationMs = GetInt(track, "duration");
                var trackId = GetNumberAsString(track, "id");
                items.Add(new ParsedPlaylistTrack(
                    title.Trim(),
                    artists,
                    album,
                    durationMs,
                    trackId,
                    trackId is null ? null : $"https://music.163.com/song?id={trackId}"));
            }
            return items.Count == 0
                ? null
                : new ParsedPlaylist(name.Trim(), cover, description, items);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>兜底：官方 API 解析（tracks[].ar/al/dt 缩写字段）。</summary>
    private static async Task<ParsedPlaylist> ParseViaApiAsync(HttpClient client, string id, CancellationToken cancellationToken)
    {
        var raw = await PlaylistHttpText.GetStringAsync(client, $"{PlaylistApiBase}?id={id}&n=100000&s=8", cancellationToken);
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("playlist", out var playlist)
            || !playlist.TryGetProperty("tracks", out var tracks)
            || tracks.ValueKind != JsonValueKind.Array)
            throw new PlaylistParseException("歌单解析失败，请确认链接为公开歌单后重试。");

        var name = GetString(playlist, "name") ?? $"网易云歌单 {id}";
        var cover = GetString(playlist, "coverImgUrl");
        var description = GetString(playlist, "description");

        var items = new List<ParsedPlaylistTrack>();
        foreach (var track in tracks.EnumerateArray())
        {
            var title = GetString(track, "name");
            if (string.IsNullOrWhiteSpace(title))
                continue;
            var artists = new List<string>();
            if (track.TryGetProperty("ar", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var artist in artistArray.EnumerateArray())
                {
                    var artistName = GetString(artist, "name");
                    if (!string.IsNullOrWhiteSpace(artistName))
                        artists.Add(artistName.Trim());
                }
            }
            string? album = null;
            if (track.TryGetProperty("al", out var albumElement) && albumElement.ValueKind == JsonValueKind.Object)
                album = GetString(albumElement, "name");
            int? durationMs = GetInt(track, "dt");
            var trackId = GetNumberAsString(track, "id");
            items.Add(new ParsedPlaylistTrack(
                title.Trim(),
                artists,
                album,
                durationMs,
                trackId,
                trackId is null ? null : $"https://music.163.com/song?id={trackId}"));
        }
        if (items.Count == 0)
            throw new PlaylistParseException("歌单解析失败，请确认链接为公开歌单后重试。");
        return new ParsedPlaylist(name.Trim(), cover, description, items);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>数字或字符串形态的 Id 统一转字符串。</summary>
    private static string? GetNumberAsString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }

    [GeneratedRegex(@"<textarea[^>]*id=""song-list-pre-data""[^>]*>(.*?)</textarea>", RegexOptions.Singleline)]
    private static partial Regex TextareaRegex();
}

/// <summary>歌单解析失败（链接无效/平台风控/格式变更）；消息对用户可见。</summary>
public sealed class PlaylistParseException(string message) : Exception(message);
