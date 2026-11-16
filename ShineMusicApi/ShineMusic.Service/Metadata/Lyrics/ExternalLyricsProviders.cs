// 职责：外部歌词源提供者链（多源化）。
// 架构：LyricsResolver 的第 3 层按 LyricsOptions.ExternalSources 配置顺序依次调用各
// IExternalLyricsProvider，任一命中即返回；单源超时/异常只降级到下一源，绝不影响导入。
//
// 当前实现：
//   1. LrclibLyricsProvider（https://lrclib.net）——开源多语言源，原有逻辑平移，优先精确匹配再搜索打分；
//   2. KuGouLyricsProvider（酷狗）——中文曲目覆盖率高；三步链路：搜索候选 → 按时长取歌词候选 → base64 解码 LRC。
//      注：酷狗 CDN 的 https 证书在本机 schannel 下不受信，故走 http 明文（歌词非敏感数据，可接受）。
//   3. MiguLyricsProvider（咪咕）——运营商版权曲库（含主流华语）；两步链路：search_all 响应直接携带
//      lyricUrl 直链，GET 即得标准 LRC。零风控零签名（仅需 Referer: m.music.migu.cn）。
// 网易同ID查询见 NeteaseLyricsProvider；匿名搜索仍不使用。
// 酷我（歌词需 zlib+base64+XOR 解码且主流华语曲库有缺口）、Genius/Musixmatch（需密钥且免费层残缺）。

using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;

namespace ShineMusic.Service.Lyrics;

/// <summary>
/// 外部歌词源提供者：按标题/艺人/时长匹配并取回歌词正文。
/// 实现方先验证身份与时间轴再评分；调用者取消必须传播。
/// </summary>
public interface IExternalLyricsProvider
{
    /// <summary>来源标识，写入 <see cref="LyricsMatch.Source"/>（lrclib / kugou）。</summary>
    string Source { get; }

    /// <summary>尝试为给定曲目匹配歌词；未命中返回 null。</summary>
    Task<LyricsMatch?> FetchAsync(LyricsQuery query, CancellationToken cancellationToken);
}

/// <summary>归一化后的候选曲目视图（各外部源统一折算后交给打分器）。</summary>
public readonly record struct LyricsCandidateInfo(string? Title, string? Artist, string? Album, double DurationSeconds);

/// <summary>
/// 歌词候选排序分：标题 45% + 艺人 35% + 时长 15% + 专辑 5%。
/// 仅用于已经通过共享身份闸门的候选排序，不代表准确率。
/// </summary>
internal static class LyricsMatching
{
    /// <summary>计算查询与候选的加权相似度（0~1）。</summary>
    public static double Score(LyricsQuery query, LyricsCandidateInfo candidate)
    {
        var titleScore = Similarity(NormalizeText(query.Title), NormalizeText(candidate.Title));
        var artistScore = BestArtistSimilarity(query.Artists, candidate.Artist);
        var durationScore = DurationScore(query.DurationMs, candidate.DurationSeconds);
        var albumScore = query.AlbumTitle is null || candidate.Album is null
            ? 0
            : Similarity(NormalizeText(query.AlbumTitle), NormalizeText(candidate.Album));
        return 0.45 * titleScore + 0.35 * artistScore + 0.15 * durationScore + 0.05 * albumScore;
    }

    /// <summary>时长接近度：偏差 0 秒为 1，偏差 10 秒及以上为 0，中间线性衰减。</summary>
    private static double DurationScore(long? durationMs, double candidateSeconds)
    {
        if (durationMs is null or <= 0 || candidateSeconds <= 0)
            return 0;
        var difference = Math.Abs(durationMs.Value / 1000.0 - candidateSeconds);
        return Math.Max(0, 1 - difference / 10.0);
    }

    /// <summary>多艺人匹配：候选艺人字段可能用 / 、 &amp; 、 feat. 连接多位艺人，拆开后取最高相似度。</summary>
    private static double BestArtistSimilarity(IReadOnlyList<string> artists, string? candidateArtist)
    {
        if (artists.Count == 0 || string.IsNullOrWhiteSpace(candidateArtist))
            return 0;
        var candidateParts = candidateArtist
            .Split(['/', '&', ',', '、', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeText)
            .Where(part => part.Length > 0)
            .ToArray();
        if (candidateParts.Length == 0)
            return 0;

        double best = 0;
        foreach (var artist in artists)
        {
            var normalized = NormalizeText(artist);
            if (normalized.Length == 0)
                continue;
            foreach (var part in candidateParts)
                best = Math.Max(best, Similarity(normalized, part));
        }
        return best;
    }

    /// <summary>文本归一化：小写、去括号内版本说明、截断 feat. 后缀、去空白标点，只保留比较主体。</summary>
    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var lowered = value.Trim().ToLowerInvariant();
        foreach (var marker in new[] { "feat.", "feat ", "featuring" })
        {
            var index = lowered.IndexOf(marker, StringComparison.Ordinal);
            if (index > 0)
            {
                lowered = lowered[..index];
                break;
            }
        }

        var builder = new StringBuilder(lowered.Length);
        var depth = 0;
        foreach (var character in lowered)
        {
            if (character is '(' or '（' or '[' or '【')
            {
                depth++;
                continue;
            }
            if (character is ')' or '）' or ']' or '】')
            {
                if (depth > 0) depth--;
                continue;
            }
            if (depth > 0)
                continue;
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character))
                continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>归一化编辑距离相似度：1 - 编辑距离 / 较长串长度。</summary>
    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
            return 1;
        if (left.Length == 0 || right.Length == 0)
            return 0;
        if (string.Equals(left, right, StringComparison.Ordinal))
            return 1;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return 1.0 - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }
}

/// <summary>外部歌词源的共享 HTTP 小助手：带 User-Agent 的 GET JSON，非 2xx 一律视为未命中。</summary>
internal static class ExternalLyricsHttp
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<T?> GetJsonAsync<T>(HttpClient client, string url, string? userAgent, CancellationToken cancellationToken) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(userAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    /// <summary>拼接查询字符串：跳过空参数并做 URL 编码。</summary>
    public static string BuildUrl(string baseUrl, string path, IReadOnlyDictionary<string, string?> parameters)
    {
        var builder = new StringBuilder(baseUrl.TrimEnd('/'));
        builder.Append('/').Append(path);
        var first = true;
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return builder.ToString();
    }
}

/// <summary>
/// LRCLIB 源（https://lrclib.net）：先 get 精确匹配（标题+艺人+时长），失败再走 search 打分。
/// </summary>
public sealed class LrclibLyricsProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<LyricsOptions> options) : IExternalLyricsProvider
{
    private readonly LyricsOptions _options = options.Value;

    /// <inheritdoc />
    public string Source => LyricsSources.Lrclib;

    /// <inheritdoc />
    public async Task<LyricsMatch?> FetchAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(LyricsResolver.HttpClientName);
        var title = query.Title!.Trim();
        var artist = query.Artists.Count > 0 ? query.Artists[0]?.Trim() ?? string.Empty : string.Empty;
        long? durationSeconds = query.DurationMs is > 0
            ? (long)Math.Round(query.DurationMs.Value / 1000.0, MidpointRounding.AwayFromZero)
            : null;

        // 1) get：标题 + 艺人 + 时长精确匹配（LRCLIB 对时长敏感，实测 ±5 秒内可命中）。
        if (durationSeconds.HasValue && artist.Length > 0)
        {
            var exactUrl = ExternalLyricsHttp.BuildUrl(_options.BaseUrl, "get", new Dictionary<string, string?>
            {
                ["track_name"] = title,
                ["artist_name"] = artist,
                ["album_name"] = query.AlbumTitle,
                ["duration"] = durationSeconds.Value.ToString(CultureInfo.InvariantCulture)
            });
            var exact = await ExternalLyricsHttp.GetJsonAsync<LrclibRecord>(client, exactUrl, _options.UserAgent, cancellationToken);
            var exactMatch = BuildMatch(query, exact);
            if (exactMatch is not null)
                return exactMatch;
        }

        // 2) search：返回多个候选，按标题/艺人/时长/专辑加权打分后取最优。
        var searchUrl = ExternalLyricsHttp.BuildUrl(_options.BaseUrl, "search", new Dictionary<string, string?>
        {
            ["track_name"] = title,
            ["artist_name"] = artist.Length > 0 ? artist : null
        });
        var candidates = await ExternalLyricsHttp.GetJsonAsync<List<LrclibRecord>>(client, searchUrl, _options.UserAgent, cancellationToken);
        if (candidates is null || candidates.Count == 0)
            return null;

        return candidates.Take(20)
            .Select(candidate => BuildMatch(query, candidate))
            .Where(match => match is not null)
            .OrderByDescending(match => match!.Confidence).FirstOrDefault();
    }

    private LyricsMatch? BuildMatch(LyricsQuery query, LrclibRecord? record)
    {
        if (record is null) return null;
        var id = record.Id.ToString(CultureInfo.InvariantCulture);
        var info = new LyricsCandidateInfo(record.TrackName, record.ArtistName, record.AlbumName, record.Duration);
        if (!LyricsValidation.ValidateIdentity(query, Source, id, info)) return null;
        if (record.Instrumental)
        {
            LyricsValidation.Reject(query, Source, id, "instrumental");
            return null;
        }
        var content = PickContent(record);
        if (!LyricsValidation.ValidateContent(query, Source, id, content)) return null;
        var score = LyricsMatching.Score(query, info);
        if (score < _options.MinScore)
        {
            LyricsValidation.Reject(query, Source, id, "score_below_threshold");
            return null;
        }
        return new LyricsMatch(content!, Source, Math.Round(score, 3), id);
    }

    /// <summary>选择歌词正文：优先逐行时间轴版本（可滚动高亮），否则退回纯文本。</summary>
    private string? PickContent(LrclibRecord record)
    {
        var synced = LyricsContent.Normalize(record.SyncedLyrics);
        if (_options.PreferSynced && synced is not null)
            return synced;
        return synced ?? LyricsContent.Normalize(record.PlainLyrics);
    }

    /// <summary>LRCLIB 返回的记录结构（仅声明用到的字段）。</summary>
    private sealed class LrclibRecord
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("trackName")] public string? TrackName { get; init; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; init; }
        [JsonPropertyName("albumName")] public string? AlbumName { get; init; }
        [JsonPropertyName("duration")] public double Duration { get; init; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; init; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; init; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; init; }
    }
}

/// <summary>
/// 酷狗源：中文曲目覆盖率高。三步链路——
/// 1) mobilecdn 搜索候选（含 hash）；2) 打分取最优；3) krcs 按时长取歌词候选并下载 base64 LRC 解码。
/// 全部走 http（https 证书在本机不受信）；歌词为公开文本，无敏感数据。
/// </summary>
public sealed class KuGouLyricsProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<LyricsOptions> options) : IExternalLyricsProvider
{
    private const string SearchBase = "http://mobilecdn.kugou.com/api/v3/search/song";
    private const string LyricSearchBase = "http://krcs.kugou.com/search";
    private const string LyricDownloadBase = "http://lyrics.kugou.com/download";

    private readonly LyricsOptions _options = options.Value;

    /// <inheritdoc />
    public string Source => LyricsSources.KuGou;

    /// <inheritdoc />
    public async Task<LyricsMatch?> FetchAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Title))
            return null;
        var client = httpClientFactory.CreateClient(LyricsResolver.HttpClientName);
        var artist = query.Artists.Count > 0 ? query.Artists[0]?.Trim() ?? string.Empty : string.Empty;
        var keyword = artist.Length > 0 ? $"{query.Title.Trim()} {artist}" : query.Title.Trim();

        // 1) 搜索候选（pagesize 收敛为 5，减少下游打分成本）。
        var searchUrl = $"{SearchBase}?format=json&keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize=5&showtype=1";
        var search = await ExternalLyricsHttp.GetJsonAsync<KuGouSearchResponse>(client, searchUrl, _options.UserAgent, cancellationToken);
        var infos = search?.Data?.Info;
        if (infos is null || infos.Count == 0)
            return null;

        var ranked = infos.Take(5).Where(info => LyricsValidation.ValidateIdentity(query, Source, info.Hash,
                new LyricsCandidateInfo(StripHighlight(info.SongName), StripHighlight(info.SingerName), StripHighlight(info.AlbumName), info.Duration)))
            .Select(info => (Song: info, Score: LyricsMatching.Score(query, new LyricsCandidateInfo(
                StripHighlight(info.SongName), StripHighlight(info.SingerName), StripHighlight(info.AlbumName), info.Duration))))
            .Where(item => item.Score >= _options.MinScore).OrderByDescending(item => item.Score).ToArray();
        foreach (var (best, bestScore) in ranked)
        {
        if (string.IsNullOrWhiteSpace(best.Hash)) continue;

        // 3) 按时长取歌词候选并逐个尝试下载解码（前 3 个候选即止）。
        var durationMs = best.Duration > 0 ? (long)(best.Duration * 1000) : query.DurationMs ?? 0;
        var candidatesUrl = $"{LyricSearchBase}?ver=1&man=yes&client=mobi&keyword=&duration={durationMs}&hash={Uri.EscapeDataString(best.Hash)}";
        var candidates = await ExternalLyricsHttp.GetJsonAsync<KuGouLyricCandidates>(client, candidatesUrl, _options.UserAgent, cancellationToken);
        if (candidates?.Candidates is null)
            continue;

        foreach (var candidate in candidates.Candidates.Take(3))
        {
            if (string.IsNullOrWhiteSpace(candidate.AccessKey))
                continue;
            var downloadUrl = $"{LyricDownloadBase}?ver=1&client=pc&id={candidate.Id}&accesskey={Uri.EscapeDataString(candidate.AccessKey)}&fmt=lrc&charset=utf8";
            var download = await ExternalLyricsHttp.GetJsonAsync<KuGouLyricDownload>(client, downloadUrl, _options.UserAgent, cancellationToken);
            var content = DecodeContent(download?.Content);
            if (!LyricsValidation.ValidateContent(query, Source, candidate.Id.ToString(CultureInfo.InvariantCulture), content))
                continue;
            return new LyricsMatch(content!, LyricsSources.KuGou, Math.Round(Math.Clamp(bestScore, 0, 1), 3), candidate.Id.ToString(CultureInfo.InvariantCulture));
        }
        }
        return null;
    }

    /// <summary>剥离酷狗搜索结果中的 &lt;em&gt; 高亮标签（不参与相似度计算）。</summary>
    private static string? StripHighlight(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : value.Replace("<em>", "", StringComparison.Ordinal).Replace("</em>", "", StringComparison.Ordinal);

    /// <summary>base64 解码 LRC 正文并清洗；解码失败或空白返回 null。</summary>
    private static string? DecodeContent(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;
        try
        {
            return LyricsContent.Normalize(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class KuGouSearchResponse
    {
        [JsonPropertyName("data")] public KuGouSearchData? Data { get; init; }
    }

    private sealed class KuGouSearchData
    {
        [JsonPropertyName("info")] public List<KuGouSongInfo>? Info { get; init; }
    }

    private sealed class KuGouSongInfo
    {
        [JsonPropertyName("hash")] public string? Hash { get; init; }
        [JsonPropertyName("songname")] public string? SongName { get; init; }
        [JsonPropertyName("singername")] public string? SingerName { get; init; }
        [JsonPropertyName("album_name")] public string? AlbumName { get; init; }
        [JsonPropertyName("duration")] public double Duration { get; init; }
    }

    private sealed class KuGouLyricCandidates
    {
        [JsonPropertyName("candidates")] public List<KuGouLyricCandidate>? Candidates { get; init; }
    }

    private sealed class KuGouLyricCandidate
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("accesskey")] public string? AccessKey { get; init; }
    }

    private sealed class KuGouLyricDownload
    {
        [JsonPropertyName("content")] public string? Content { get; init; }
    }
}

/// <summary>
/// 咪咕源：运营商版权曲库，主流华语覆盖好。两步链路——
/// 1) search_all.do 搜索（响应直接携带 lyricUrl 直链，无需再解析歌词候选）；2) GET lyricUrl 取回标准 LRC。
/// 零风控零签名，仅需 Referer: m.music.migu.cn 与常规 UA。
/// </summary>
public sealed class MiguLyricsProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<LyricsOptions> options) : IExternalLyricsProvider
{
    private const string SearchUrl = "https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/search_all.do";
    private const string Referer = "http://m.music.migu.cn/";

    private readonly LyricsOptions _options = options.Value;

    /// <inheritdoc />
    public string Source => LyricsSources.Migu;

    /// <inheritdoc />
    public async Task<LyricsMatch?> FetchAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Title))
            return null;
        var client = httpClientFactory.CreateClient(LyricsResolver.HttpClientName);
        var artist = query.Artists.Count > 0 ? query.Artists[0]?.Trim() ?? string.Empty : string.Empty;
        var keyword = artist.Length > 0 ? $"{query.Title.Trim()} {artist}" : query.Title.Trim();

        // 1) 搜索（searchSwitch 限定只搜歌曲；isCopyright=1 只要正版条目，其 lyricUrl 更可靠）。
        var searchUrl = $"{SearchUrl}?text={Uri.EscapeDataString(keyword)}&pageNo=1&pageSize=10&searchSwitch={Uri.EscapeDataString("{\"song\":1}")}&isCopyright=1";
        MiguSearchResponse? search;
        using (var request = new HttpRequestMessage(HttpMethod.Get, searchUrl))
        {
            if (!string.IsNullOrWhiteSpace(_options.UserAgent))
                request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", Referer);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            search = await response.Content.ReadFromJsonAsync<MiguSearchResponse>(ExternalLyricsHttp.JsonOptions, cancellationToken);
        }

        var results = search?.SongResultData?.Result;
        if (results is null || results.Count == 0)
            return null;

        var ranked = results.Take(10).Where(entry => LyricsValidation.ValidateIdentity(query, Source, entry.CopyrightId ?? entry.SongId,
                new LyricsCandidateInfo(entry.Name,
                    entry.Singers is { Count: > 0 } ? string.Join(" & ", entry.Singers.Select(s => s.Name)) : null,
                    entry.Albums?.FirstOrDefault()?.Name, ParseDuration(entry.Duration))))
            .Select(entry => (Song: entry, Score: LyricsMatching.Score(query, new LyricsCandidateInfo(entry.Name,
                entry.Singers is { Count: > 0 } ? string.Join(" & ", entry.Singers.Select(s => s.Name)) : null,
                entry.Albums?.FirstOrDefault()?.Name, ParseDuration(entry.Duration)))))
            .Where(item => item.Score >= _options.MinScore).OrderByDescending(item => item.Score).Take(5).ToArray();
        foreach (var (best, bestScore) in ranked)
        {
            if (string.IsNullOrWhiteSpace(best.LyricUrl)) continue;
            var lrcText = await GetPlainTextAsync(client, best.LyricUrl, cancellationToken);
            var content = LyricsContent.Normalize(lrcText);
            if (!LyricsValidation.ValidateContent(query, Source, best.CopyrightId ?? best.SongId, content)) continue;
            return new LyricsMatch(content!, Source, Math.Round(bestScore, 3), best.CopyrightId ?? best.SongId);
        }
        return null;
    }

    /// <summary>咪咕 duration 可能是秒数字符串或数字，宽松解析；解析失败返回 0。</summary>
    private static double ParseDuration(JsonElement? duration) => duration switch
    {
        { ValueKind: JsonValueKind.Number } number => number.GetDouble(),
        { ValueKind: JsonValueKind.String } text when double.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) => seconds,
        _ => 0
    };

    /// <summary>GET 纯文本（歌词直链），非 2xx 或空体返回 null。</summary>
    private async Task<string?> GetPlainTextAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_options.UserAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private sealed class MiguSearchResponse
    {
        [JsonPropertyName("songResultData")] public MiguSongResultData? SongResultData { get; init; }
    }

    private sealed class MiguSongResultData
    {
        [JsonPropertyName("result")] public List<MiguSongEntry>? Result { get; init; }
    }

    private sealed class MiguSongEntry
    {
        [JsonPropertyName("songId")] public string? SongId { get; init; }
        [JsonPropertyName("copyrightId")] public string? CopyrightId { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("singers")] public List<MiguSinger>? Singers { get; init; }
        [JsonPropertyName("albums")] public List<MiguAlbum>? Albums { get; init; }
        [JsonPropertyName("duration")] public JsonElement? Duration { get; init; }
        [JsonPropertyName("lyricUrl")] public string? LyricUrl { get; init; }
    }

    private sealed class MiguSinger
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed class MiguAlbum
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }
}

/// <summary>歌词正文清洗：统一换行和首尾空白；外部结果由 LyricsValidation 检查长度，不截断时间轴。</summary>
internal static class LyricsContent
{

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
            return null;
        return normalized;
    }
}
