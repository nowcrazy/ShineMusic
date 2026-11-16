// ArtworkFetcher.cs
// iTunes 元数据 + 封面自动获取服务：当音频文件没有内嵌封面/流派/专辑/年份时，
// 根据歌手/专辑/曲目信息调用 iTunes Search API 搜索并一次性拿到所有缺失字段。
//
// iTunes Search API 特点：
// - 完全免费，无需 API Key
// - 支持 artist + album + song 组合搜索
// - 返回 artworkUrl100（100x100），URL 中把 100x100 替换为 600x600 可获高分辨率
// - 同时返回 primaryGenreName（英文流派）、collectionName（专辑名）、releaseDate（ISO 日期）
// - 有调用频率限制（约 20 次/分钟）

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text;
using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Service;

/// <summary>
/// iTunes 元数据获取结果。一次调用同时拿到封面 + 流派 + 专辑 + 发行年，
/// 以及歌手、时长、曲序/碟序、曲目 ID、商店代码、成人标记等溯源字段。
/// </summary>
public sealed record ItunesMetadataResult(
    bool Success,
    string? StorageKey = null,
    string? ContentType = null,
    string? Error = null,
    // 匹配确认后的元数据，无封面时同样返回。
    string? Genre = null,
    string? AlbumTitle = null,
    int? ReleaseYear = null,
    string? ArtistName = null,
    long? TrackTimeMs = null,
    int? TrackNumber = null,
    int? DiscNumber = null,
    long? ItunesTrackId = null,
    string? Country = null,
    string? Explicitness = null,
    string? ReleaseDate = null,
    string? TrackTitle = null);

/// <summary>
/// 基于 iTunes Search API 的元数据 + 封面自动获取服务。
/// 当音频文件缺失封面、流派、专辑、发行年中的任一项时，
/// 根据已知的 歌手/专辑/歌曲名 组合搜索 iTunes，一次性补全所有字段。
/// </summary>
public sealed class ArtworkFetcher(
    IHttpClientFactory httpClientFactory,
    ILogger<ArtworkFetcher> logger)
{
    private const string ItunesSearchUrl = "https://itunes.apple.com/search";
    private const int MaxImageSizeBytes = 10 * 1024 * 1024; // 10MB

    /// <summary>
    /// 根据歌手+专辑+歌曲名从 iTunes 获取封面图并保存，
    /// 同时返回搜索结果中的 流派/专辑名/发行年 供调用方回填缺失字段。
    /// </summary>
    /// <param name="artist">歌手名（必填）</param>
    /// <param name="album">专辑名（可选，已知时必须精确匹配）。</param>
    /// <param name="song">歌曲名（可选，有歌名时优先搜索歌曲实体）。</param>
    /// <param name="storageKeyPrefix">存储键前缀，如 "artwork"</param>
    /// <param name="outputDirectory">封面图保存的物理目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>
    /// 成功时返回：封面存储键 + iTunes 流派/专辑/发行年；
    /// iTunes 搜索命中但封面下载失败时，Genre/AlbumTitle/ReleaseYear 仍有值（Error 字段说明原因）。
    /// </returns>
    public async Task<ItunesMetadataResult> FetchAsync(
        string artist,
        string? album,
        string? song,
        string storageKeyPrefix,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(song))
            return await FetchMatchedAsync(song, [artist], album, null, storageKeyPrefix, outputDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(artist))
            return new ItunesMetadataResult(false, Error: "Artist name is required.");

        try
        {
            var itunesResult = await SearchBestMatchAsync(artist, album, song, cancellationToken);
            if (itunesResult is null)
                return new ItunesMetadataResult(false, Error: "No matching track found on iTunes.");

            return await BuildResultAsync(itunesResult, storageKeyPrefix, outputDirectory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch iTunes metadata for {Artist} - {Album}", artist, album);
            return new ItunesMetadataResult(false, Error: exception.Message);
        }
    }

    /// <summary>缺少艺人时不自动猜测歌曲身份，也不发起网络请求。</summary>
    public Task<ItunesMetadataResult> FetchByTitleAsync(
        string title,
        string storageKeyPrefix,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("Title-only metadata lookup skipped: identity insufficient.");
        return Task.FromResult(new ItunesMetadataResult(false, Error: "identity_insufficient"));
    }

    /// <summary>只有标题、艺人、已知时长与专辑均确认后，才返回元数据并下载封面。</summary>
    public async Task<ItunesMetadataResult> FetchMatchedAsync(
        string title,
        IReadOnlyList<string> artists,
        string? album,
        long? durationMs,
        string storageKeyPrefix,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || artists is null || !artists.Any(a => !string.IsNullOrWhiteSpace(a)))
            return new ItunesMetadataResult(false, Error: "identity_insufficient");

        try
        {
            var expected = new TrackIdentity(title, artists, durationMs, album);
            var candidates = await SearchCandidatesAsync($"{string.Join(" ", artists)} {title}", "song", cancellationToken);
            var matches = candidates.Where(candidate =>
                TrackIdentityMatcher.Evaluate(expected, new TrackIdentity(candidate.TrackName ?? "",
                    [candidate.ArtistName ?? ""], candidate.TrackTimeMs, candidate.CollectionName)).Verdict == IdentityVerdict.Matched
                && (string.IsNullOrWhiteSpace(album) || NormalizeAlbum(album) == NormalizeAlbum(candidate.CollectionName)))
                .ToList();
            if (matches.Count == 0)
                return new ItunesMetadataResult(false, Error: "metadata_not_found");
            if (string.IsNullOrWhiteSpace(album)
                && matches.Select(candidate => NormalizeAlbum(candidate.CollectionName)).Distinct().Skip(1).Any())
                return new ItunesMetadataResult(false, Error: "metadata_ambiguous");

            return await BuildResultAsync(matches[0], storageKeyPrefix, outputDirectory, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ItunesMetadataResult(false, Error: "metadata_source_unavailable");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to fetch matched iTunes metadata for {Title}", title);
            return new ItunesMetadataResult(false, Error: "metadata_source_unavailable");
        }
    }

    // Preserve version decorations and punctuation: Deluxe/Live albums must not collapse into the original.
    private static string NormalizeAlbum(string? album)
        => string.Concat((album ?? "").Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static string? IsoDate(string? value)
        => value is { Length: >= 10 } && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// 把一次 iTunes 搜索命中项落地为「封面下载 + 完整元数据」结果：
    /// 解析发行年 → 无封面直接返回元数据 → 下载高分辨率封面（失败仍返回元数据）。
    /// </summary>
    private async Task<ItunesMetadataResult> BuildResultAsync(
        ItunesResult itunesResult,
        string storageKeyPrefix,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        // 解析发行年
        int? releaseYear = null;
        if (!string.IsNullOrWhiteSpace(itunesResult.ReleaseDate)
            && DateTime.TryParse(itunesResult.ReleaseDate, CultureInfo.InvariantCulture, out var dt))
        {
            releaseYear = dt.Year;
        }

        // 即使没有封面也返回元数据（让调用方回填流派/专辑/年份等）
        if (string.IsNullOrWhiteSpace(itunesResult.ArtworkUrl100))
        {
            return MetadataOnly(itunesResult, releaseYear);
        }

        // 下载高分辨率封面
        var highResUrl = itunesResult.ArtworkUrl100
            .Replace("100x100", "600x600")
            .Replace("100x100bb", "600x600bb");

        var (bytes, contentType) = await DownloadImageAsync(highResUrl, cancellationToken);
        if (bytes is null || bytes.Length == 0)
        {
            // 封面下载失败，但元数据仍然可用
            return MetadataOnly(itunesResult, releaseYear, "Artwork download failed.");
        }

        var extension = contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => "jpg"
        };

        var fileName = $"{Guid.NewGuid():N}.{extension}";
        Directory.CreateDirectory(outputDirectory);
        var filePath = Path.Combine(outputDirectory, fileName);
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);

        var storageKey = $"{storageKeyPrefix.TrimEnd('/')}/{fileName}";

        return new ItunesMetadataResult(
            Success: true,
            StorageKey: storageKey,
            ContentType: contentType ?? "image/jpeg",
            Genre: itunesResult.PrimaryGenreName,
            AlbumTitle: itunesResult.CollectionName,
            ReleaseYear: releaseYear,
            ArtistName: itunesResult.ArtistName,
            TrackTimeMs: itunesResult.TrackTimeMs,
            TrackNumber: itunesResult.TrackNumber,
            DiscNumber: itunesResult.DiscNumber,
            ItunesTrackId: itunesResult.ItunesTrackId,
            Country: itunesResult.Country,
            Explicitness: itunesResult.Explicitness,
            ReleaseDate: IsoDate(itunesResult.ReleaseDate),
            TrackTitle: itunesResult.TrackName);
    }

    /// <summary>构造「无封面（或封面下载失败）但元数据可用」的结果。</summary>
    private static ItunesMetadataResult MetadataOnly(ItunesResult itunesResult, int? releaseYear, string? error = null)
        => new(
            Success: true,
            Error: error,
            Genre: itunesResult.PrimaryGenreName,
            AlbumTitle: itunesResult.CollectionName,
            ReleaseYear: releaseYear,
            ArtistName: itunesResult.ArtistName,
            TrackTimeMs: itunesResult.TrackTimeMs,
            TrackNumber: itunesResult.TrackNumber,
            DiscNumber: itunesResult.DiscNumber,
            ItunesTrackId: itunesResult.ItunesTrackId,
            Country: itunesResult.Country,
            Explicitness: itunesResult.Explicitness,
            ReleaseDate: IsoDate(itunesResult.ReleaseDate),
            TrackTitle: itunesResult.TrackName);

    /// <summary>仅在没有歌曲名时保留原有专辑查询路径。</summary>
    private async Task<ItunesResult?> SearchBestMatchAsync(
        string artist,
        string? album,
        string? song,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(album))
            return await SearchByTermAsync($"{artist} {album}", "album", ct);

        return null;
    }

    /// <summary>按指定 term 和 entity 类型搜索 iTunes，返回第一个含有可用元数据的结果。</summary>
    private async Task<ItunesResult?> SearchByTermAsync(string term, string entity, CancellationToken ct)
        => (await SearchCandidatesAsync(term, entity, ct)).FirstOrDefault(result =>
            !string.IsNullOrWhiteSpace(result.ArtworkUrl100)
            || !string.IsNullOrWhiteSpace(result.PrimaryGenreName)
            || result.TrackNumber is > 0
            || result.DiscNumber is > 0
            || !string.IsNullOrWhiteSpace(result.ReleaseDate));

    private async Task<IReadOnlyList<ItunesResult>> SearchCandidatesAsync(string term, string entity, CancellationToken ct)
    {
        var url = $"{ItunesSearchUrl}?term={WebUtility.UrlEncode(term)}&media=music&entity={entity}&limit=20";

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return [];

            return results.EnumerateArray().Take(20).Select(DeserializeResult).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "iTunes search failed for term={Term} entity={Entity}", term, entity);
            throw;
        }
    }

    private static ItunesResult DeserializeResult(JsonElement item)
    {
        string? GetString(string name)
            => item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() : null;

        long? GetLong(string name)
            => item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)
                ? v : null;

        int? GetInt(string name)
            => item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
                ? v : null;

        return new ItunesResult(
            ArtworkUrl100: GetString("artworkUrl100"),
            ArtistName: GetString("artistName"),
            CollectionName: GetString("collectionName"),
            TrackName: GetString("trackName"),
            PrimaryGenreName: GetString("primaryGenreName"),
            ReleaseDate: GetString("releaseDate"),
            TrackTimeMs: GetLong("trackTimeMillis"),
            TrackNumber: GetInt("trackNumber"),
            DiscNumber: GetInt("discNumber"),
            ItunesTrackId: GetLong("trackId"),
            Country: GetString("country"),
            Explicitness: GetString("trackExplicitness"));
    }

    /// <summary>下载图片，限制大小不超过 10MB。</summary>
    private async Task<(byte[]? Bytes, string? ContentType)> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return (null, null);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxImageSizeBytes)
            return (null, null);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxImageSizeBytes) return (null, null);
            await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return (ms.ToArray(), contentType);
    }

    /// <summary>iTunes 搜索结果项（完整字段）。</summary>
    private sealed record ItunesResult(
        string? ArtworkUrl100,
        string? ArtistName,
        string? CollectionName,
        string? TrackName,
        string? PrimaryGenreName,
        string? ReleaseDate,
        long? TrackTimeMs,
        int? TrackNumber,
        int? DiscNumber,
        long? ItunesTrackId,
        string? Country,
        string? Explicitness);
}
