// 职责：QQ 音乐歌单解析器。
// 端点：c.y.qq.com/v8/fcg-bin/fcg_v8_playlist_cp.fcg?id={disstid}&format=json&newsong=1&platform=h5
// （Referer 必须为 y.qq.com，公开歌单免登录）；响应 cdlist[0].songlist 含
// songname / singer[].name / albumname / interval(秒) / songmid。
// 支持 URL 形态：y.qq.com/n/ryqq/playlist/{id}、y.qq.com/n/yqq/playlist/{id}.html、
// playsquare 页 ?playlistid={id}。

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>QQ 音乐歌单解析器（命名 HttpClient "qqmusic"）。</summary>
public sealed class QQMusicPlaylistParser(IHttpClientFactory httpClientFactory) : IPlaylistParser
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称。</summary>
    public const string HttpClientName = "qqmusic";

    private const string ApiBase = "https://c.y.qq.com/v8/fcg-bin/fcg_v8_playlist_cp.fcg";

    /// <inheritdoc />
    public string Platform => "qqmusic";

    /// <inheritdoc />
    public bool CanHandle(string url) =>
        url.Contains("y.qq.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("i.y.qq.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("c.y.qq.com", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken)
    {
        var id = ExtractPlaylistId(url)
            ?? throw new PlaylistParseException("无法从链接中识别 QQ 音乐歌单 ID。");
        var client = httpClientFactory.CreateClient(HttpClientName);
        var raw = await PlaylistHttpText.GetStringAsync(
            client, $"{ApiBase}?id={id}&format=json&newsong=1&platform=h5", cancellationToken);

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("cdlist", out var cdlist)
            || cdlist.ValueKind != JsonValueKind.Array
            || cdlist.GetArrayLength() == 0)
            throw new PlaylistParseException("歌单解析失败，请确认链接为公开歌单后重试。");

        var first = cdlist[0];
        var name = GetString(first, "dissname") ?? $"QQ 音乐歌单 {id}";
        var cover = GetString(first, "logo");
        var description = GetString(first, "desc");

        var items = new List<ParsedPlaylistTrack>();
        if (first.TryGetProperty("songlist", out var songlist) && songlist.ValueKind == JsonValueKind.Array)
        {
            foreach (var song in songlist.EnumerateArray())
            {
                var title = GetString(song, "songname");
                if (string.IsNullOrWhiteSpace(title))
                    continue;
                var artists = new List<string>();
                if (song.TryGetProperty("singer", out var singerArray) && singerArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var singer in singerArray.EnumerateArray())
                    {
                        var singerName = GetString(singer, "name");
                        if (!string.IsNullOrWhiteSpace(singerName))
                            artists.Add(singerName.Trim());
                    }
                }
                int? durationMs = GetInt(song, "interval") is { } seconds ? seconds * 1000 : null;
                var mid = GetString(song, "songmid");
                items.Add(new ParsedPlaylistTrack(
                    System.Net.WebUtility.HtmlDecode(title).Trim(),
                    artists,
                    GetString(song, "albumname"),
                    durationMs,
                    mid,
                    mid is null ? null : $"https://y.qq.com/n/ryqq/songDetail/{mid}"));
            }
        }
        if (items.Count == 0)
            throw new PlaylistParseException("歌单解析失败，请确认链接为公开歌单后重试。");
        return new ParsedPlaylist(name.Trim(), cover, description, items);
    }

    /// <summary>从 URL 中提取歌单数字 Id（支持路径段与 query 两种形态）。</summary>
    private static string? ExtractPlaylistId(string url)
    {
        var pathMatch = Regex.Match(url, @"/playlist(?:/|\.html\?id=|/detail/)(\d+)|/playlist/(\d+)\.html");
        if (pathMatch.Success)
            return pathMatch.Groups[1].Success ? pathMatch.Groups[1].Value : pathMatch.Groups[2].Value;
        var queryMatch = Regex.Match(url, @"[?&#](?:playlistid|id|disstid)=(\d+)");
        return queryMatch.Success ? queryMatch.Groups[1].Value : null;
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
}
