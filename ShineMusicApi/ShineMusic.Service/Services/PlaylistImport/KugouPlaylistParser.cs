// 职责：酷狗音乐歌单解析器。
// 元数据（名称/封面/简介）：m.kugou.com/plist/list/{specialid}/?json=true（需移动端 UA，命名 HttpClient "kugou" 已配置；
//   尾斜杠必须保留——无斜杠会先 301 到 http 再跳编码 ID，.NET 不跟随 https→http 降级重定向，2026-10-28 实测）。
// 曲目全量：mobiles.kugou.com/api/v3/special/song?specialid={id}&page=N&pagesize=100&version=9150，
//   该端点分页有效（plist/list 的 page/pagesize 参数均被平台忽略、每页恒返回前 10 条，2026-10-28 实测 417 首歌单 5 页拉全且无重复）。
// 曲目归一化：filename 形态「艺人 - 歌名」按首个分隔符拆艺人，remark 作歌名（兜底取分隔符之后），
// duration 为整数秒；SourceTrackId 存 hash（128k 档），与酷狗音源 provider 的 ProviderTrackId 形态一致。
// 支持 URL 形态：www.kugou.com/yy/special/single/{id}.html、m.kugou.com/plist/list/{id}、t1.kugou.com 短链（跟随重定向取 ID）。

using System.Text.Json;
using System.Text.RegularExpressions;
using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>酷狗音乐歌单解析器（复用命名 HttpClient "kugou"）。</summary>
public sealed partial class KugouPlaylistParser(IHttpClientFactory httpClientFactory) : IPlaylistParser
{
    private const string ListBase = "https://m.kugou.com/plist/list";
    private const string SongsBase = "https://mobiles.kugou.com/api/v3/special/song";

    /// <summary>每页 100 条（special/song 支持 pagesize）；页数上限防止 total 异常导致失控翻页。</summary>
    private const int PageSize = 100;
    private const int MaxPages = 50;

    /// <inheritdoc />
    public string Platform => "kugou";

    /// <inheritdoc />
    public bool CanHandle(string url) =>
        url.Contains("kugou.com", StringComparison.OrdinalIgnoreCase)
        && (url.Contains("/special/single/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/plist/list", StringComparison.OrdinalIgnoreCase)
            || url.Contains("://t1.kugou.com", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<ParsedPlaylist> ParseAsync(string url, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(KugouSourceProvider.HttpClientName);
        var id = ExtractPlaylistId(url)
            ?? await ResolveShortLinkIdAsync(client, url, cancellationToken)
            ?? throw new PlaylistParseException("无法从链接中识别酷狗歌单 ID。");

        var items = new List<ParsedPlaylistTrack>();
        string? name = null;
        string? cover = null;
        string? description = null;

        // 歌单元数据（名称/封面/简介）：plist/list 首页
        var metaRaw = await PlaylistHttpText.GetStringAsync(
            client, $"{ListBase}/{id}/?json=true", cancellationToken);
        using (var metaDoc = JsonDocument.Parse(metaRaw))
        {
            if (metaDoc.RootElement.TryGetProperty("info", out var metaInfo)
                && metaInfo.TryGetProperty("list", out var listInfo))
            {
                name = GetString(listInfo, "specialname");
                cover = GetString(listInfo, "imgurl")?.Replace("{size}", "150", StringComparison.Ordinal);
                description = GetString(listInfo, "intro");
            }
        }

        // 全量曲目：special/song 分页（plist/list 的 page/pagesize 参数被平台忽略，不可用）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = int.MaxValue;
        for (var page = 1; page <= MaxPages && items.Count < total; page++)
        {
            var raw = await PlaylistHttpText.GetStringAsync(
                client, $"{SongsBase}?specialid={id}&page={page}&pagesize={PageSize}&version=9150", cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data))
                break;
            if (page == 1
                && data.TryGetProperty("total", out var totalElement)
                && totalElement.ValueKind == JsonValueKind.Number)
                total = totalElement.GetInt32();
            if (!data.TryGetProperty("info", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                break;
            var pageCount = tracks.GetArrayLength();
            if (pageCount == 0)
                break;
            foreach (var song in tracks.EnumerateArray())
            {
                var hash = GetString(song, "hash");
                var (artist, fallbackTitle) = SplitFileName(GetString(song, "filename"));
                var title = GetString(song, "remark");
                if (string.IsNullOrWhiteSpace(title))
                    title = fallbackTitle;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(hash) || !seen.Add(hash))
                    continue;
                int? durationMs = song.TryGetProperty("duration", out var durationElement)
                    && durationElement.ValueKind == JsonValueKind.Number
                        ? durationElement.GetInt32() * 1000
                        : null;
                items.Add(new ParsedPlaylistTrack(
                    title.Trim(),
                    string.IsNullOrWhiteSpace(artist) ? [] : [artist.Trim()],
                    AlbumTitle: null,
                    durationMs,
                    hash,
                    $"https://www.kugou.com/song/#hash={hash}"));
            }
            if (pageCount < PageSize)
                break;
        }

        if (items.Count == 0)
            throw new PlaylistParseException("歌单解析失败，请确认链接为公开歌单后重试。");
        return new ParsedPlaylist(
            string.IsNullOrWhiteSpace(name) ? $"酷狗歌单 {id}" : name.Trim(),
            cover, description, items);
    }

    /// <summary>从 URL 中提取歌单数字 Id（special/single/{id} 或 plist/list/{id}）。</summary>
    private static string? ExtractPlaylistId(string url)
    {
        var match = PlaylistIdRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>t1.kugou.com 短链：GET 跟随重定向，从最终落地 URL 提取歌单 Id；非短链或提取失败返回 null。</summary>
    private static async Task<string?> ResolveShortLinkIdAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        if (!url.Contains("://t1.kugou.com", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            var finalUri = response.RequestMessage?.RequestUri?.ToString();
            return finalUri is null ? null : ExtractPlaylistId(finalUri);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>filename 形态为「艺人 - 歌名」，按首个分隔符拆分；无分隔符时整体作歌名。</summary>
    private static (string? Artist, string? Title) SplitFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return (null, null);
        var index = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (index <= 0)
            return (null, fileName.Trim());
        return (fileName[..index].Trim(), fileName[(index + 3)..].Trim());
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"/(?:special/single|plist/list)/(\d+)")]
    private static partial Regex PlaylistIdRegex();
}
