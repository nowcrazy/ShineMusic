// 职责：酷我音乐音源 provider（HTTP 直链型）。
// 端点链（2026-10 实测零 Cookie/零签名/零风控）：
//   1. 搜索：search.kuwo.cn/r.s —— 返回单引号伪 JSON，需归一化后解析；条目含 MUSICRID/歌名/艺人/专辑/时长；
//   2. 详情：player.kuwo.cn/webmusic/st/getNewMuiseByRid —— XML，尽力而为取快照（失败仅降级）；
//   3. 下载地址：antiserver.kuwo.cn/anti.s?type=convert_url —— 直接返回纯文本 mp3 直链，CDN 支持 Range。
// 局限：音质封顶 128k（无损需登录）；腾讯独家版权曲（如周杰伦原曲）无资源，此时 anti.s 返回空 → 判定不可下载。
// 注：酷我 CDN 的 https 证书在本机 schannel 下不受信，全链路走 http 明文（公开音频，无敏感数据）。

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>酷我音乐音源提供者。</summary>
public sealed partial class KuwoSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称。</summary>
    public const string HttpClientName = "kuwo";

    private const string SearchBase = "http://search.kuwo.cn/r.s";
    private const string DetailBase = "http://player.kuwo.cn/webmusic/st/getNewMuiseByRid";
    private const string AntiBase = "http://antiserver.kuwo.cn/anti.s";

    /// <inheritdoc />
    public string Provider => "kuwo";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Kuwo.Enabled;

    /// <inheritdoc />
    public string? DisabledReason => IsEnabled ? null : "酷我音源未启用（SourceDownload:Kuwo:Enabled=false）。";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{SearchBase}?all={Uri.EscapeDataString(query)}&ft=music&itemset=web_2013&client=kt&pn=0"
            + $"&rn={Math.Clamp(limit, 1, 50)}&rformat=json&encoding=utf8";
        var raw = await client.GetStringAsync(url, cancellationToken);

        using var document = JsonDocument.Parse(NormalizePseudoJson(raw));
        if (!document.RootElement.TryGetProperty("abslist", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SourceSearchResult>();
        foreach (var entry in list.EnumerateArray())
        {
            var musicRid = GetString(entry, "MUSICRID");
            var title = StripHighlight(GetString(entry, "SONGNAME"));
            if (string.IsNullOrWhiteSpace(musicRid) || string.IsNullOrWhiteSpace(title))
                continue;
            var artist = StripHighlight(GetString(entry, "ARTIST"));
            var album = StripHighlight(GetString(entry, "ALBUM"));
            long? durationMs = long.TryParse(GetString(entry, "DURATION"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? seconds * 1000
                : null;
            var numericId = musicRid.Replace("MUSIC_", "", StringComparison.Ordinal);
            results.Add(new SourceSearchResult(
                Provider,
                musicRid,
                title,
                string.IsNullOrWhiteSpace(artist) ? [] : [artist],
                string.IsNullOrWhiteSpace(album) ? null : album,
                durationMs,
                [],
                CoverUrl: null,
                SourcePageUrl: $"https://www.kuwo.cn/play_detail/{numericId}",
                License: null,
                SourceQualities.High,
                IsLosslessAvailable: false));
        }
        return results.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        // 详情接口返回简单 XML；尽力而为解析，失败返回 null（建任务时快照降级，不阻塞）。
        var client = httpClientFactory.CreateClient(HttpClientName);
        string xml;
        try
        {
            xml = await client.GetStringAsync($"{DetailBase}?RID={Uri.EscapeDataString(providerTrackId)}", cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        var title = ExtractXmlValue(xml, "Song_Name") ?? ExtractXmlValue(xml, "SONGNAME");
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var artist = ExtractXmlValue(xml, "Artist") ?? ExtractXmlValue(xml, "ARTIST");
        var album = ExtractXmlValue(xml, "Album") ?? ExtractXmlValue(xml, "ALBUM");
        long? durationMs = long.TryParse(ExtractXmlValue(xml, "Duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds * 1000
            : null;
        var numericId = providerTrackId.Replace("MUSIC_", "", StringComparison.Ordinal);
        return new SourceSearchResult(
            Provider,
            providerTrackId,
            title,
            string.IsNullOrWhiteSpace(artist) ? [] : [artist],
            string.IsNullOrWhiteSpace(album) ? null : album,
            durationMs,
            [],
            CoverUrl: null,
            SourcePageUrl: $"https://www.kuwo.cn/play_detail/{numericId}",
            License: null,
            SourceQualities.Standard,
            IsLosslessAvailable: false);
    }

    /// <inheritdoc />
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var rid = providerTrackId.StartsWith("MUSIC_", StringComparison.OrdinalIgnoreCase)
            ? providerTrackId
            : $"MUSIC_{providerTrackId}";
        var client = httpClientFactory.CreateClient(HttpClientName);
        // 酷我 anti.s 支持 br 参数指定码率：320kmp3 返回 320k MP3（搜索 MINFO 字段确认大部分曲目提供该档位）；
        // 平台无 320k 资源时自动回退最高可用码率，响应仍为 http 直链。
        var url = $"{AntiBase}?type=convert_url&rid={Uri.EscapeDataString(rid)}&format=mp3&response=url&br=320kmp3";
        var downloadUrl = (await client.GetStringAsync(url, cancellationToken)).Trim();
        // 无版权/无资源时 anti.s 返回空串或非 http 内容，判定为不可下载（任务走自动重试后转 failed）。
        if (!downloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new SourceUnavailableException("酷我未返回下载地址（该曲目可能无版权或已下架）。");
        return new SourceDownloadInfo(downloadUrl, "mp3", IsLossless: false, BitrateKbps: 320, SourceQualities.High);
    }

    /// <summary>酷我 r.s 返回单引号伪 JSON：仅把结构性单引号换成双引号（值内部的撇号不受影响）。</summary>
    private static string NormalizePseudoJson(string raw) =>
        StructuralQuoteRegex().Replace(raw, "\"");

    /// <summary>剥离搜索字段中的 &lt;em&gt; 高亮标签，并解码 HTML 实体（如 &amp;nbsp;）。</summary>
    private static string? StripHighlight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        var stripped = value.Replace("<em>", "", StringComparison.Ordinal).Replace("</em>", "", StringComparison.Ordinal);
        return System.Net.WebUtility.HtmlDecode(stripped).Trim();
    }

    /// <summary>从 JsonElement 上安全取字符串字段。</summary>
    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>从酷我详情 XML 中提取指定标签文本（接口偶发字段名大小写差异，调用方给两个候选）。</summary>
    private static string? ExtractXmlValue(string xml, string tag)
    {
        var match = Regex.Match(xml, $"<{tag}>(?<value>.*?)</{tag}>", RegexOptions.Singleline);
        if (!match.Success)
            return null;
        var value = match.Groups["value"].Value.Trim();
        // 兼容 CDATA 包裹。
        if (value.StartsWith("<![CDATA[", StringComparison.Ordinal) && value.EndsWith("]]>", StringComparison.Ordinal))
            value = value[9..^3];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [GeneratedRegex(@"(?<=[\{\s,:])\'|\'(?=[\s,\}\]:])")]
    private static partial Regex StructuralQuoteRegex();
}
