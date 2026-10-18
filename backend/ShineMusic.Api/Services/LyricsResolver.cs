// LyricsResolver.cs
// 歌词自动补全服务：在导入流水线解析完元数据后，按「内嵌标签 → 旁挂 .lrc → 外部歌词源」三层依次尝试，
// 命中即返回，并把来源与置信度一并带出，供审核界面判断是否需要人工核对。
//
// 设计要点：
//   1. 任何一层失败都只返回 null，绝不向上抛异常——歌词是可选内容，不能阻断导入与播放；
//   2. 外部源（LRCLIB）先用 get 接口按时长精确匹配，失败再走 search，按时长+标题+艺人相似度打分排序；
//   3. 所有外部候选都要过 MinScore 阈值，避免把同名不同版本（Live/Remix）的歌词错误写入。

using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Data;
using ShineMusic.Api.Options;

namespace ShineMusic.Api.Services;

/// <summary>
/// 一次歌词匹配的输入：来自已解析的音频元数据与隔离区源文件路径。
/// </summary>
/// <param name="Title">曲目标题（来自标签或文件名）。</param>
/// <param name="Artists">艺人名列表，取第一位作为主艺人参与匹配。</param>
/// <param name="AlbumTitle">专辑名，可为空。</param>
/// <param name="DurationMs">音频时长（毫秒），用于精确匹配与时长打分。</param>
/// <param name="EmbeddedLyrics">音频标签中的内嵌歌词（ID3 USLT 等），可为空。</param>
/// <param name="SourcePath">隔离区中的音频文件绝对路径，用于查找同名旁挂歌词。</param>
public sealed record LyricsQuery(
    string? Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    long? DurationMs,
    string? EmbeddedLyrics,
    string? SourcePath);

/// <summary>
/// 一次歌词匹配的结果。
/// </summary>
/// <param name="Content">歌词正文（纯文本或带 [mm:ss] 时间轴的 LRC）。</param>
/// <param name="Source">来源标识，取值见 <see cref="LyricsSources"/>。</param>
/// <param name="Confidence">置信度 0~1：内嵌/旁挂为 1，外部源按相似度打分。</param>
/// <param name="ProviderId">外部源记录 ID（如 LRCLIB 的 id），非外部来源为 null。</param>
public sealed record LyricsMatch(string Content, string Source, double Confidence, string? ProviderId);

/// <summary>
/// 歌词解析器：为导入条目自动补全歌词。
/// </summary>
public interface ILyricsResolver
{
    /// <summary>
    /// 依次尝试内嵌标签、旁挂歌词文件与外部歌词源，返回首个命中结果。
    /// </summary>
    /// <param name="query">匹配输入。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中的歌词；全部未命中时返回 null（调用方据此记录 lyrics_missing 告警）。</returns>
    Task<LyricsMatch?> ResolveAsync(LyricsQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// 基于「内嵌标签 → 旁挂 .lrc → LRCLIB」的歌词解析实现。
/// </summary>
public sealed class LyricsResolver(
    IHttpClientFactory httpClientFactory,
    IOptions<LyricsOptions> options,
    ILogger<LyricsResolver> logger) : ILyricsResolver
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称。</summary>
    public const string HttpClientName = "lyrics";

    /// <summary>单份歌词的最大字节数（旁挂文件读取上限），超出视为无效。</summary>
    private const long MaxLyricsBytes = 512 * 1024;

    /// <summary>单份歌词的最大字符数，防止异常数据撑爆数据库字段。</summary>
    private const int MaxLyricsChars = 20000;

    /// <summary>外部源 JSON 反序列化选项（camelCase、大小写不敏感）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LyricsOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<LyricsMatch?> ResolveAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        // 第 1 层：音频内嵌歌词，可信度最高，直接采用。
        var embedded = NormalizeContent(query.EmbeddedLyrics);
        if (embedded is not null)
            return new LyricsMatch(embedded, LyricsSources.Embedded, 1.0, null);

        // 第 2 层：与音频同名的旁挂歌词文件（隔离区中为 {itemId}.lrc / {itemId}.txt）。
        var sidecar = TryReadSidecar(query.SourcePath);
        if (sidecar is not null)
            return new LyricsMatch(sidecar, LyricsSources.Sidecar, 1.0, null);

        // 第 3 层：外部歌词源。开关关闭或缺少标题时直接放弃，交由人工补充。
        if (!_options.AutoFetchEnabled || string.IsNullOrWhiteSpace(query.Title))
            return null;

        try
        {
            return await FetchFromProviderAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 外部源超时：属于可预期的降级，不记错误日志，避免刷屏。
            logger.LogDebug("Lyrics lookup timed out for {Title}.", query.Title);
            return null;
        }
        catch (Exception exception)
        {
            // 外部源异常（DNS/网络/JSON 解析）同样只降级，不影响导入。
            logger.LogWarning(exception, "Lyrics lookup failed for {Title}.", query.Title);
            return null;
        }
    }

    /// <summary>
    /// 查找并读取与音频同名的旁挂歌词文件（.lrc 优先，其次 .txt）。
    /// </summary>
    private static string? TryReadSidecar(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return null;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);

        foreach (var extension in new[] { ".lrc", ".LRC", ".txt", ".TXT" })
        {
            var candidate = Path.Combine(directory, baseName + extension);
            if (!File.Exists(candidate))
                continue;
            try
            {
                if (new FileInfo(candidate).Length is 0 or > MaxLyricsBytes)
                    continue;
                var content = NormalizeContent(ReadTextFile(candidate));
                if (content is not null)
                    return content;
            }
            catch (IOException)
            {
                // 单个候选文件读取失败时继续尝试下一个扩展名。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// 读取文本文件并按 BOM 或内容推断编码：优先 UTF-8/UTF-16，
    /// 非 UTF-8（常见于 GBK 编码的 .lrc）时尝试 GB18030，运行时不支持则按 Latin1 兜底。
    /// </summary>
    private static string ReadTextFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            // 严格解码：遇到非法字节序列直接抛异常，进入下面的兜底分支。
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return Encoding.GetEncoding("gb18030").GetString(bytes);
            }
            catch (ArgumentException)
            {
                // 运行时未注册代码页提供程序时，退回 Latin1，保证不抛异常。
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }

    /// <summary>
    /// 调用外部歌词源：先精确匹配，失败再搜索并打分。
    /// </summary>
    private async Task<LyricsMatch?> FetchFromProviderAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 30)));
        var token = timeout.Token;

        var client = httpClientFactory.CreateClient(HttpClientName);
        var title = query.Title!.Trim();
        var artist = query.Artists.FirstOrDefault()?.Trim() ?? string.Empty;
        long? durationSeconds = query.DurationMs is > 0
            ? (long)Math.Round(query.DurationMs.Value / 1000.0, MidpointRounding.AwayFromZero)
            : null;

        // 1) get：标题 + 艺人 + 时长精确匹配（LRCLIB 对时长敏感，实测 ±5 秒内可命中）。
        if (durationSeconds.HasValue && artist.Length > 0)
        {
            var exactUrl = BuildUrl("get", new Dictionary<string, string?>
            {
                ["track_name"] = title,
                ["artist_name"] = artist,
                ["album_name"] = query.AlbumTitle,
                ["duration"] = durationSeconds.Value.ToString(CultureInfo.InvariantCulture)
            });
            var exact = await GetJsonAsync<LrclibRecord>(client, exactUrl, token);
            var exactMatch = BuildMatch(exact, 1.0);
            if (exactMatch is not null)
                return exactMatch;
        }

        // 2) search：返回多个候选，按标题/艺人/时长/专辑加权打分后取最优。
        var searchUrl = BuildUrl("search", new Dictionary<string, string?>
        {
            ["track_name"] = title,
            ["artist_name"] = artist.Length > 0 ? artist : null
        });
        var candidates = await GetJsonAsync<List<LrclibRecord>>(client, searchUrl, token);
        if (candidates is null || candidates.Count == 0)
            return null;

        LrclibRecord? best = null;
        double bestScore = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Instrumental || PickContent(candidate) is null)
                continue;
            var score = Score(query, candidate);
            if (score < _options.MinScore || score <= bestScore)
                continue;
            best = candidate;
            bestScore = score;
        }

        return best is null ? null : BuildMatch(best, bestScore);
    }

    /// <summary>
    /// 把外部源记录转换为匹配结果；纯音乐或无歌词内容时返回 null。
    /// </summary>
    private LyricsMatch? BuildMatch(LrclibRecord? record, double confidence)
    {
        if (record is null || record.Instrumental)
            return null;
        var content = PickContent(record);
        if (content is null)
            return null;
        return new LyricsMatch(
            content,
            LyricsSources.Lrclib,
            Math.Round(Math.Clamp(confidence, 0, 1), 3),
            record.Id > 0 ? record.Id.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>
    /// 选择歌词正文：优先逐行时间轴版本（可在播放器内滚动高亮），否则退回纯文本。
    /// </summary>
    private string? PickContent(LrclibRecord record)
    {
        var synced = NormalizeContent(record.SyncedLyrics);
        if (_options.PreferSynced && synced is not null)
            return synced;
        return synced ?? NormalizeContent(record.PlainLyrics);
    }

    /// <summary>
    /// 候选打分：标题 45% + 艺人 35% + 时长 15% + 专辑 5%。
    /// </summary>
    private static double Score(LyricsQuery query, LrclibRecord candidate)
    {
        var titleScore = Similarity(NormalizeText(query.Title), NormalizeText(candidate.TrackName));
        var artistScore = BestArtistSimilarity(query.Artists, candidate.ArtistName);
        var durationScore = DurationScore(query.DurationMs, candidate.Duration);
        var albumScore = query.AlbumTitle is null || candidate.AlbumName is null
            ? 0
            : Similarity(NormalizeText(query.AlbumTitle), NormalizeText(candidate.AlbumName));
        return 0.45 * titleScore + 0.35 * artistScore + 0.15 * durationScore + 0.05 * albumScore;
    }

    /// <summary>
    /// 时长接近度：偏差 0 秒为 1，偏差 10 秒及以上为 0，中间线性衰减。
    /// </summary>
    private static double DurationScore(long? durationMs, double candidateSeconds)
    {
        if (durationMs is null or <= 0 || candidateSeconds <= 0)
            return 0;
        var difference = Math.Abs(durationMs.Value / 1000.0 - candidateSeconds);
        return Math.Max(0, 1 - difference / 10.0);
    }

    /// <summary>
    /// 多艺人匹配：候选的艺人字段可能用 / & , feat. 连接多位艺人，拆开后取最高相似度。
    /// </summary>
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

    /// <summary>
    /// 文本归一化：小写、去掉括号内的版本说明（Live/Remastered 等）、去掉 feat. 之后的内容、
    /// 去掉空白与标点，只保留可用于比较的主体字符。
    /// </summary>
    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var lowered = value.Trim().ToLowerInvariant();
        // 截断 feat. / featuring 之后的部分（合作艺人不应影响主歌名匹配）。
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

    /// <summary>
    /// 归一化编辑距离相似度：1 - 编辑距离 / 较长串长度。
    /// </summary>
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

    /// <summary>
    /// 拼接外部源请求地址，跳过空参数并做 URL 编码。
    /// </summary>
    private string BuildUrl(string path, IReadOnlyDictionary<string, string?> parameters)
    {
        var builder = new StringBuilder(_options.BaseUrl.TrimEnd('/'));
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

    /// <summary>
    /// 发起 GET 请求并反序列化 JSON；非 2xx（含 404/503）一律视为未命中，返回 null。
    /// </summary>
    private async Task<T?> GetJsonAsync<T>(HttpClient client, string url, CancellationToken cancellationToken) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_options.UserAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    /// <summary>
    /// 清洗歌词正文：统一换行、去首尾空白、截断超长内容；空白内容返回 null。
    /// </summary>
    private static string? NormalizeContent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
            return null;
        return normalized.Length > MaxLyricsChars ? normalized[..MaxLyricsChars] : normalized;
    }

    /// <summary>
    /// LRCLIB 返回的记录结构（仅声明本服务用到的字段）。
    /// </summary>
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
