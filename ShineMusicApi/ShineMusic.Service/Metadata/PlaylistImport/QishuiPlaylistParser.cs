// 职责：汽水音乐公开歌单/专辑分享页解析器。只接受 SSR 内嵌曲目数与声明曲目数一致的结果。

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>汽水音乐公开歌单/专辑分享页解析器（命名 HttpClient "qishui"）。</summary>
public sealed partial class QishuiPlaylistParser(IHttpClientFactory httpClientFactory) : IPlaylistParser
{
    public const string HttpClientName = "qishui";
    private const string MusicHost = "music.douyin.com";

    /// <inheritdoc />
    public string Platform => "qishui";

    /// <inheritdoc />
    public bool CanHandle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Host.Equals(MusicHost, StringComparison.OrdinalIgnoreCase))
            return IsSharePage(uri);

        return uri.Host.Equals("qishui.douyin.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/s/", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new PlaylistParseException("无法识别汽水音乐歌单分享链接。");

        var client = httpClientFactory.CreateClient(HttpClientName);
        if (uri.Host.Equals("qishui.douyin.com", StringComparison.OrdinalIgnoreCase))
            uri = await ResolveShortLinkAsync(client, uri, cancellationToken);

        if (!uri.Host.Equals(MusicHost, StringComparison.OrdinalIgnoreCase) || !IsSharePage(uri))
            throw new PlaylistParseException("汽水音乐短链接未能解析到公开歌单或专辑页面。");

        var isPlaylist = uri.AbsolutePath.EndsWith("/playlist", StringComparison.OrdinalIgnoreCase);
        var shareId = ShareIdRegex().Match(uri.Query);
        if (!shareId.Success)
            throw new PlaylistParseException("无法从汽水音乐链接中识别歌单或专辑编号。");

        var id = shareId.Groups[1].Value;
        var pageUrl = isPlaylist
            ? $"https://{MusicHost}/qishui/share/playlist?playlist_id={Uri.EscapeDataString(id)}"
            : $"https://{MusicHost}/qishui/share/album?album_id={Uri.EscapeDataString(id)}";
        var html = await PlaylistHttpText.GetStringAsync(client, pageUrl, cancellationToken);
        var routerJson = ExtractRouterData(html)
            ?? throw new PlaylistParseException("汽水音乐未返回可解析的公开分享数据。");

        using var doc = JsonDocument.Parse(routerJson);
        if (!doc.RootElement.TryGetProperty("loaderData", out var loaderData)
            || !loaderData.TryGetProperty(isPlaylist ? "playlist_page" : "album_page", out var sharePage))
            throw new PlaylistParseException("汽水音乐分享页数据格式无效。");

        var infoProperty = isPlaylist ? "playlistInfo" : "albumInfo";
        var info = sharePage.TryGetProperty(infoProperty, out var shareInfo)
            ? shareInfo
            : throw new PlaylistParseException("汽水音乐未返回分享信息。");
        var expectedCount = GetInt(info, "count_tracks");
        if (!expectedCount.HasValue || expectedCount.Value < 0)
            throw new PlaylistParseException("汽水音乐未返回有效的曲目总数。");

        var name = GetString(info, isPlaylist ? "title" : "name")
            ?? $"汽水音乐{(isPlaylist ? "歌单" : "专辑")} {id}";
        var fallbackArtists = ReadArtists(info);
        if (isPlaylist && fallbackArtists.Count == 0
            && info.TryGetProperty("owner", out var owner)
            && GetString(owner, "nickname") is { Length: > 0 } ownerName)
            fallbackArtists.Add(ownerName);

        if (!sharePage.TryGetProperty(isPlaylist ? "medias" : "trackList", out var trackList)
            || trackList.ValueKind != JsonValueKind.Array
            || trackList.GetArrayLength() != expectedCount.Value)
            throw new PlaylistParseException("汽水音乐分享曲目未能完整解析，请稍后重试。");

        var expectedSongCount = isPlaylist
            ? trackList.EnumerateArray().Count(entry => GetString(entry, "type") == "track")
            : expectedCount.Value;
        var tracks = new List<ParsedPlaylistTrack>(expectedSongCount);
        foreach (var entry in trackList.EnumerateArray())
        {
            var track = entry;
            if (isPlaylist)
            {
                var mediaType = GetString(entry, "type");
                if (mediaType == "video")
                    continue;
                if (mediaType != "track")
                    throw new PlaylistParseException("汽水音乐歌单包含无法识别的内容类型，无法保证曲目完整。");
                if (!entry.TryGetProperty("entity", out var entity)
                    || !entity.TryGetProperty("track", out track))
                    throw new PlaylistParseException("汽水音乐歌单曲目数据格式无效。");
            }

            var title = GetString(track, "name");
            var trackId = GetValueAsString(track, "id");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(trackId))
                throw new PlaylistParseException("汽水音乐分享曲目数据不完整。");

            int? durationMs = GetInt(track, "duration");
            var artists = ReadArtists(track);
            tracks.Add(new ParsedPlaylistTrack(
                WebUtility.HtmlDecode(title).Trim(),
                artists.Count == 0 ? fallbackArtists : artists,
                GetAlbumTitle(track) ?? (isPlaylist ? null : name),
                durationMs,
                trackId,
                null));
        }

        if (tracks.Count != expectedSongCount)
            throw new PlaylistParseException("汽水音乐分享曲目未能完整解析，请稍后重试。");

        return new ParsedPlaylist(
            name.Trim(),
            GetCoverUrl(info),
            GetString(info, "intro"),
            tracks);
    }

    private static async Task<Uri> ResolveShortLinkAsync(
        HttpClient client,
        Uri shortUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, shortUri);
        using var response = await SendAsync(client, request, cancellationToken);
        var finalUri = response.RequestMessage?.RequestUri;
        if (!response.IsSuccessStatusCode || finalUri is null)
            throw new PlaylistParseException("汽水音乐分享短链接解析失败，请稍后重试。");
        return finalUri;
    }

    private static bool IsSharePage(Uri uri) =>
        uri.AbsolutePath.Equals("/qishui/share/album", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.Equals("/qishui/share/playlist", StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new PlaylistParseException("无法连接汽水音乐，请稍后重试。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlaylistParseException("汽水音乐响应超时，请稍后重试。");
        }
    }

    private static string? ExtractRouterData(string html)
    {
        var marker = RouterDataMarkerRegex().Match(html);
        if (!marker.Success)
            return null;

        var start = marker.Index + marker.Length;
        while (start < html.Length && char.IsWhiteSpace(html[start]))
            start++;
        if (start >= html.Length || html[start] != '{')
            return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < html.Length; index++)
        {
            var character = html[index];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            if (character == '"')
                inString = true;
            else if (character == '{')
                depth++;
            else if (character == '}' && --depth == 0)
                return html[start..(index + 1)];
        }

        return null;
    }

    private static List<string> ReadArtists(JsonElement element)
    {
        var artists = new List<string>();
        if (!element.TryGetProperty("artists", out var values) || values.ValueKind != JsonValueKind.Array)
            return artists;

        foreach (var value in values.EnumerateArray())
        {
            var name = GetString(value, "name");
            if (!string.IsNullOrWhiteSpace(name))
                artists.Add(name.Trim());
        }

        return artists;
    }

    private static string? GetCoverUrl(JsonElement albumInfo)
    {
        if (!albumInfo.TryGetProperty("url_cover", out var cover)
            || !cover.TryGetProperty("urls", out var urls)
            || urls.ValueKind != JsonValueKind.Array
            || urls.GetArrayLength() == 0)
            return null;

        var baseUrl = urls[0].ValueKind == JsonValueKind.String ? urls[0].GetString() : null;
        var uri = GetString(cover, "uri");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(uri))
            return null;

        var template = GetString(cover, "template_prefix");
        var suffix = string.IsNullOrWhiteSpace(template)
            ? string.Empty
            : $"~{template}-crop-center:720:720.jpg";
        return $"{baseUrl.TrimEnd('/')}/{uri}{suffix}";
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetAlbumTitle(JsonElement track)
    {
        if (!track.TryGetProperty("album", out var album))
            return null;
        if (album.ValueKind == JsonValueKind.String)
            return album.GetString();
        return album.ValueKind == JsonValueKind.Object ? GetString(album, "name") : null;
    }

    private static int? GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? GetValueAsString(JsonElement element, string property)
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

    [GeneratedRegex(@"_ROUTER_DATA\s*=\s*", RegexOptions.CultureInvariant)]
    private static partial Regex RouterDataMarkerRegex();

    [GeneratedRegex(@"(?:[?&])(?:album_id|playlist_id)=(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShareIdRegex();
}
