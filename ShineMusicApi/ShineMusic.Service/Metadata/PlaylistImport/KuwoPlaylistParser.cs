// 职责：酷我音乐歌单解析器。歌单 API 使用匿名 Cookie 派生的 Secret 请求头，并按页返回曲目。
// 支持 URL 形态：kuwo.cn/playlist_detail/{id}、kuwo.cn/playlist/{id}。

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>酷我音乐歌单解析器（复用命名 HttpClient "kuwo"）。</summary>
public sealed partial class KuwoPlaylistParser(IHttpClientFactory httpClientFactory) : IPlaylistParser
{
    private const string PlaylistPageBase = "https://www.kuwo.cn/playlist_detail";
    private const string PlaylistApiBase = "https://www.kuwo.cn/api/www/playlist/playListInfo";
    private const string CookieName = "Hm_Iuvt_cdb524f42f23cer9b268564v7y735ewrq2324";
    private const int PageSize = 20;
    private const int MaxPages = 500;

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
        var pageUrl = $"{PlaylistPageBase}/{id}";
        var cookie = await GetAnonymousCookieAsync(client, pageUrl, cancellationToken);

        var items = new List<ParsedPlaylistTrack>();
        string? name = null;
        string? cover = null;
        string? description = null;
        int? expectedTrackCount = null;

        for (var page = 1; page <= MaxPages && (!expectedTrackCount.HasValue || items.Count < expectedTrackCount.Value); page++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{PlaylistApiBase}?pid={Uri.EscapeDataString(id)}&pn={page}&rn={PageSize}&httpsStatus=1&reqId={Guid.NewGuid():D}&plat=web_www&from=");
            request.Headers.Referrer = new Uri(pageUrl);
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Cookie", $"{CookieName}={cookie}");
            request.Headers.TryAddWithoutValidation("Secret", CreateSecret(cookie));

            using var response = await SendAsync(client, request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new PlaylistParseException("酷我歌单接口请求失败，请稍后重试。");

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.Number
                || code.GetInt32() != 200
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
                throw new PlaylistParseException("酷我未返回有效的歌单数据，请确认链接为公开歌单。");

            var pageTotal = GetInt(data, "total");
            if (!pageTotal.HasValue || pageTotal.Value < 0)
                throw new PlaylistParseException("酷我未返回有效的歌单曲目总数。");
            if (expectedTrackCount.HasValue && expectedTrackCount.Value != pageTotal.Value)
                throw new PlaylistParseException("酷我歌单曲目总数在分页期间发生变化，请稍后重试。");
            expectedTrackCount = pageTotal.Value;

            if (page == 1)
            {
                name = GetString(data, "name");
                cover = GetString(data, "img");
                description = GetString(data, "info") ?? GetString(data, "desc");
            }

            if (!data.TryGetProperty("musicList", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                throw new PlaylistParseException("酷我歌单曲目列表格式无效。");

            var pageCount = tracks.GetArrayLength();
            foreach (var song in tracks.EnumerateArray())
            {
                var title = GetString(song, "name");
                var trackId = GetTrackId(song);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(trackId))
                    continue;

                int? durationMs = GetInt(song, "duration") is { } seconds ? seconds * 1000 : null;
                var artist = GetString(song, "artist");
                items.Add(new ParsedPlaylistTrack(
                    WebUtility.HtmlDecode(title).Trim(),
                    string.IsNullOrWhiteSpace(artist) ? [] : [artist.Trim()],
                    GetString(song, "album"),
                    durationMs,
                    $"MUSIC_{trackId}",
                    $"https://www.kuwo.cn/play_detail/{trackId}"));
            }

            if (pageCount == 0)
                break;
        }

        if (!expectedTrackCount.HasValue || items.Count != expectedTrackCount.Value)
            throw new PlaylistParseException("酷我歌单曲目未能完整解析，请稍后重试。");
        if (items.Count == 0)
            throw new PlaylistParseException("歌单解析失败，请确认链接为公开歌单后重试。");

        return new ParsedPlaylist(
            string.IsNullOrWhiteSpace(name) ? $"酷我歌单 {id}" : name.Trim(),
            cover,
            description,
            items);
    }

    private static async Task<string> GetAnonymousCookieAsync(
        HttpClient client,
        string pageUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        using var response = await SendAsync(client, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PlaylistParseException("无法读取酷我公开歌单页面，请稍后重试。");

        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            throw new PlaylistParseException("酷我未返回匿名歌单访问凭据，请稍后重试。");

        foreach (var setCookie in setCookies)
        {
            var pair = setCookie.Split(';', 2)[0].Trim();
            var separator = pair.IndexOf('=');
            if (separator > 0 && pair[..separator].Equals(CookieName, StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        throw new PlaylistParseException("酷我未返回匿名歌单访问凭据，请稍后重试。");
    }

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
            throw new PlaylistParseException("无法连接酷我音乐，请稍后重试。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlaylistParseException("酷我音乐响应超时，请稍后重试。");
        }
    }

    private static string CreateSecret(string cookie)
    {
        var key = string.Concat(CookieName.Select(character =>
            ((int)character).ToString(CultureInfo.InvariantCulture)));
        var offset = key.Length / 5;
        var multiplier = int.Parse(
            string.Concat(Enumerable.Range(1, 5).Select(index =>
                offset * index < key.Length ? key[offset * index].ToString() : string.Empty)),
            CultureInfo.InvariantCulture);
        var increment = (CookieName.Length + 1) / 2;
        const double modulus = int.MaxValue;
        var random = RandomNumberGenerator.GetInt32(100_000_000);

        key += random.ToString(CultureInfo.InvariantCulture);
        while (key.Length > 10)
        {
            var first = ParseJavaScriptInteger(key[..10]);
            var second = ParseJavaScriptInteger(key[10..]);
            key = JavaScriptNumberToString(first + second);
        }

        var state = (multiplier * double.Parse(key, CultureInfo.InvariantCulture) + increment) % modulus;
        var secret = new StringBuilder(cookie.Length * 2 + 8);
        foreach (var character in cookie)
        {
            var mask = (int)Math.Floor((double)state / modulus * 255);
            var value = character ^ mask;
            secret.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            state = (multiplier * state + increment) % modulus;
        }

        secret.Append(random.ToString("x8", CultureInfo.InvariantCulture));
        return secret.ToString();
    }

    private static double ParseJavaScriptInteger(string value)
    {
        var length = 0;
        if (value.Length > 0 && (value[0] == '+' || value[0] == '-'))
            length++;
        while (length < value.Length && char.IsAsciiDigit(value[length]))
            length++;
        return length == 0
            ? double.NaN
            : double.Parse(value[..length], NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static string JavaScriptNumberToString(double value)
    {
        if (!double.IsFinite(value) || value == 0)
            return value.ToString(CultureInfo.InvariantCulture);

        var roundTrip = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var exponentIndex = roundTrip.IndexOf('e');
        if (exponentIndex < 0)
            return roundTrip;

        var mantissa = roundTrip[..exponentIndex];
        var exponent = int.Parse(roundTrip[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var decimalIndex = mantissa.IndexOf('.');
        var integerDigits = decimalIndex < 0 ? mantissa.Length : decimalIndex;
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        var decimalPosition = integerDigits + exponent;

        if (decimalPosition > 0 && decimalPosition <= 21)
        {
            return decimalPosition >= digits.Length
                ? digits + new string('0', decimalPosition - digits.Length)
                : digits.Insert(decimalPosition, ".");
        }

        if (decimalPosition <= 0 && decimalPosition > -6)
            return "0." + new string('0', -decimalPosition) + digits;

        var scientificExponent = decimalPosition - 1;
        var scientificMantissa = digits.Length == 1
            ? digits
            : digits.Insert(1, ".");
        return $"{scientificMantissa}e{(scientificExponent >= 0 ? "+" : string.Empty)}{scientificExponent}";
    }

    private static string? ExtractPlaylistId(string url)
    {
        var match = PlaylistIdRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? GetTrackId(JsonElement song)
    {
        var id = GetValueAsString(song, "rid");
        if (!string.IsNullOrWhiteSpace(id))
            return id;
        var musicRid = GetString(song, "musicrid");
        return musicRid?.StartsWith("MUSIC_", StringComparison.OrdinalIgnoreCase) == true
            ? musicRid["MUSIC_".Length..]
            : musicRid;
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

    [GeneratedRegex(@"/playlist(?:_detail)?/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistIdRegex();
}
