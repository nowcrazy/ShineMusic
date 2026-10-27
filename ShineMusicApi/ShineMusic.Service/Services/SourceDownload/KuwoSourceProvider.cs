// 职责：酷我音乐音源 provider（HTTP 直链型）。
// 端点链（2026-10 实测零 Cookie/零签名/零风控）：
//   1. 搜索：search.kuwo.cn/r.s —— 返回单引号伪 JSON，需归一化后解析；条目含 MUSICRID/歌名/艺人/专辑/时长；
//   2. 详情：wapi.kuwo.cn/api/www/music/musicInfo —— JSON（零签名）；旧端点 player.kuwo.cn/webmusic/st/getNewMuiseByRid
//      已于 2026-10 失效（返回站点首页 HTML），详情快照由此接口兜底；
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
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider, IQualityLadderProvider
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称。</summary>
    public const string HttpClientName = "kuwo";

    private const string SearchBase = "http://search.kuwo.cn/r.s";
    private const string DetailBase = "http://wapi.kuwo.cn/api/www/music/musicInfo";
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
        // vipver=1 + show_copyright_off=1 必须带上：缺省时酷我搜索只返回翻唱/伴奏/Live 等变体，
        // 原版（受版权保护）不下发，导致歌单批量补齐大面积 no_source（实测《偏爱》原版 212s 仅在带 vipver 时出现）。
        var url = $"{SearchBase}?all={Uri.EscapeDataString(query)}&ft=music&itemset=web_2013&client=kt&pn=0"
            + $"&rn={Math.Clamp(limit, 1, 50)}&rformat=json&encoding=utf8&vipver=1&show_copyright_off=1";
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
    /// <remarks>详情时长（data.duration，整数秒）是下载校验的权威基准，解析失败返回 null 由调用方降级。</remarks>
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var rid = providerTrackId.Replace("MUSIC_", "", StringComparison.OrdinalIgnoreCase);
        var client = httpClientFactory.CreateClient(HttpClientName);
        string json;
        try
        {
            json = await client.GetStringAsync($"{DetailBase}?mid={Uri.EscapeDataString(rid)}", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
                return null;
            var title = StripHighlight(GetString(data, "name"));
            if (string.IsNullOrWhiteSpace(title))
                return null;
            var artist = StripHighlight(GetString(data, "artist"));
            var album = StripHighlight(GetString(data, "album"));
            var cover = GetString(data, "pic");
            long? durationMs = null;
            if (data.TryGetProperty("duration", out var durationEl))
            {
                var seconds = durationEl.ValueKind switch
                {
                    JsonValueKind.Number => durationEl.GetInt64(),
                    JsonValueKind.String when long.TryParse(durationEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => 0
                };
                if (seconds > 0)
                    durationMs = seconds * 1000;
            }
            return new SourceSearchResult(
                Provider,
                providerTrackId,
                title,
                string.IsNullOrWhiteSpace(artist) ? [] : [artist],
                string.IsNullOrWhiteSpace(album) ? null : album,
                durationMs,
                [],
                string.IsNullOrWhiteSpace(cover) ? null : cover,
                $"https://www.kuwo.cn/play_detail/{rid}",
                null,
                SourceQualities.Standard,
                IsLosslessAvailable: false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var candidates = await ResolveDownloadCandidatesAsync(providerTrackId, cancellationToken);
        return candidates[0];
    }

    /// <inheritdoc />
    /// <remarks>
    /// 音质按「无损 → 有损」逐级探测，顺序即优先级；下载编排层会逐档下载并校验，验收通过才采用。
    /// 注意：不能传 br=192k/320k/320kmp3 —— 实测（2026-10）带这些参数时 anti.s 对任意 rid 都返回
    /// 同一个兜底直链（.../588957081.mp3），会让不同歌曲下载出相同字节并被误判为重复。
    /// 不传 br 才会返回该曲目自身的资源（未登录态下最高 128k）。
    /// </remarks>
    public async Task<IReadOnlyList<SourceDownloadInfo>> ResolveDownloadCandidatesAsync(
        string providerTrackId,
        CancellationToken cancellationToken)
    {
        var rid = providerTrackId.StartsWith("MUSIC_", StringComparison.OrdinalIgnoreCase)
            ? providerTrackId
            : $"MUSIC_{providerTrackId}";
        var client = httpClientFactory.CreateClient(HttpClientName);
        var candidates = new List<SourceDownloadInfo>();

        // 1) 无损优先：convert_url3 返回 JSON，code=200 且 url 非空才代表该曲目有无损资源。
        var flacUrl = await TryResolveUrlAsync(
            client,
            $"{AntiBase}?type=convert_url3&rid={Uri.EscapeDataString(rid)}&format=flac&response=url",
            cancellationToken);
        if (flacUrl is not null)
            candidates.Add(new SourceDownloadInfo(flacUrl, "flac", IsLossless: true, BitrateKbps: null, SourceQualities.Lossless));

        // 2) 有损兜底：该曲目自身的最高可用码率（未登录态为 128k）。
        var mp3Url = await TryResolveUrlAsync(
            client,
            $"{AntiBase}?type=convert_url&rid={Uri.EscapeDataString(rid)}&format=mp3&response=url",
            cancellationToken);
        if (mp3Url is not null && !candidates.Any(c => string.Equals(c.DownloadUrl, mp3Url, StringComparison.Ordinal)))
            candidates.Add(new SourceDownloadInfo(mp3Url, "mp3", IsLossless: false, BitrateKbps: 128, SourceQualities.Standard));

        if (candidates.Count == 0)
            throw new SourceUnavailableException("酷我未返回可下载资源（该曲目可能无版权、已下架或需要登录）。");
        return candidates;
    }

    /// <summary>解析一个 convert_url 请求：返回 http 直链，失败/空资源返回 null（不抛异常，便于逐级降档）。</summary>
    private static async Task<string?> TryResolveUrlAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        string raw;
        try
        {
            raw = (await client.GetStringAsync(url, cancellationToken)).Trim();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return raw;

        // convert_url3 返回 {"code":200,"msg":"success","url":"https://..."}；无权限时为 code=-1。
        if (!raw.StartsWith('{'))
            return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 200)
                return null;
            if (!root.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                return null;
            var resolved = urlElement.GetString()?.Trim();
            return resolved is not null && resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? resolved : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    [GeneratedRegex(@"(?<=[\{\s,:])\'|\'(?=[\s,\}\]:])")]
    private static partial Regex StructuralQuoteRegex();
}
