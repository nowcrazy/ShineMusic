// 职责：酷我音乐歌单解析器。
// 端点：nplserver.kuwo.cn/pl.svc?op=getlistinfo&pid={id}（免登录，返回单引号伪 JSON，
// 归一化方式与 KuwoSourceProvider 相同）；musiclist 含 name/artist/album/duration(秒)/id。
// SourceTrackId 存 "MUSIC_{id}"，与酷我音源 provider 的 ProviderTrackId 形态一致。
// 支持 URL 形态：kuwo.cn/playlist_detail/{id}、kuwo.cn/playlist/{id}。

using System.Text.Json;
using System.Text.RegularExpressions;
using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>酷我音乐歌单解析器（复用命名 HttpClient "kuwo"，走 http 明文）。</summary>
public sealed partial class KuwoPlaylistParser(IHttpClientFactory httpClientFactory) : IPlaylistParser
{
    private const string ApiBase = "http://nplserver.kuwo.cn/pl.svc";

    /// <inheritdoc />
    public string Platform => "kuwo";

    /// <inheritdoc />
    public bool CanHandle(string url) =>
        url.Contains("kuwo.cn", StringComparison.OrdinalIgnoreCase)
        && url.Contains("playlist", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken)
    {
        var id = ExtractPlaylistId(url)
            ?? throw new PlaylistParseException("无法从链接中识别酷我歌单 ID。");
        var client = httpClientFactory.CreateClient(KuwoSourceProvider.HttpClientName);
        var raw = await PlaylistHttpText.GetStringAsync(
            client, $"{ApiBase}?op=getlistinfo&pid={id}&pn=0&rn=500&encode=utf8&keyset=pl2012&identity=kuwo&pcmp4=1",
            cancellationToken);

        using var doc = JsonDocument.Parse(NormalizePseudoJson(raw));
        var root = doc.RootElement;
        var name = GetString(root, "title") ?? $"酷我歌单 {id}";
        var cover = GetString(root, "pic");

        var items = new List<ParsedPlaylistTrack>();
        if (root.TryGetProperty("musiclist", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var song in list.EnumerateArray())
            {
                var title = GetString(song, "name");
                if (string.IsNullOrWhiteSpace(title))
                    continue;
                var artist = GetString(song, "artist");
                int? durationMs = int.TryParse(GetString(song, "duration"), out var seconds) ? seconds * 1000 : null;
                var numericId = GetIdAsString(song, "id");
                items.Add(new ParsedPlaylistTrack(
                    System.Net.WebUtility.HtmlDecode(title).Trim(),
                    string.IsNullOrWhiteSpace(artist) ? [] : [artist.Trim()],
                    GetString(song, "album"),
                    durationMs,
                    numericId is null ? null : $"MUSIC_{numericId}",
                    numericId is null ? null : $"https://www.kuwo.cn/play_detail/{numericId}"));
            }
        }
        if (items.Count == 0)
            throw new PlaylistParseException("歌单解析失败，请确认链接为公开歌单后重试。");
        return new ParsedPlaylist(name.Trim(), cover, null, items);
    }

    /// <summary>从 URL 中提取歌单数字 Id（playlist_detail/{id} 或 playlist/{id}）。</summary>
    private static string? ExtractPlaylistId(string url)
    {
        var match = Regex.Match(url, @"/playlist(?:_detail)?/(\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>酷我伪 JSON 归一化：仅把结构性单引号换成双引号（值内部撇号不受影响）。</summary>
    private static string NormalizePseudoJson(string raw) =>
        StructuralQuoteRegex().Replace(raw, "\"");

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Id 字段兼容数字与字符串两种形态。</summary>
    private static string? GetIdAsString(JsonElement element, string property)
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

    [GeneratedRegex(@"(?<=[\{\s,:])\'|\'(?=[\s,\}\]:])")]
    private static partial Regex StructuralQuoteRegex();
}
