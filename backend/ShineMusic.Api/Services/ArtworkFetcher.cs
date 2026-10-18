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

namespace ShineMusic.Api.Services;

/// <summary>
/// iTunes 元数据获取结果。一次调用同时拿到封面 + 流派 + 专辑 + 发行年。
/// </summary>
public sealed record ItunesMetadataResult(
    bool Success,
    string? StorageKey = null,
    string? ContentType = null,
    string? Error = null,
    // 下面是 iTunes 同步拿到的额外元数据，封面下载成功才会填
    string? Genre = null,
    string? AlbumTitle = null,
    int? ReleaseYear = null);

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
    /// <param name="album">专辑名（可选，有专辑优先搜专辑）</param>
    /// <param name="song">歌曲名（可选，专辑搜索失败时回退搜歌曲）</param>
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
        if (string.IsNullOrWhiteSpace(artist))
            return new ItunesMetadataResult(false, Error: "Artist name is required.");

        try
        {
            // 先从 iTunes 拿到完整元数据（包含封面 URL）
            var itunesResult = await SearchBestMatchAsync(artist, album, song, cancellationToken);
            if (itunesResult is null)
                return new ItunesMetadataResult(false, Error: "No matching track found on iTunes.");

            // 解析发行年
            int? releaseYear = null;
            if (!string.IsNullOrWhiteSpace(itunesResult.ReleaseDate)
                && DateTime.TryParse(itunesResult.ReleaseDate, CultureInfo.InvariantCulture, out var dt))
            {
                releaseYear = dt.Year;
            }

            // 即使没有封面也返回元数据（让调用方回填流派/专辑/年份）
            if (string.IsNullOrWhiteSpace(itunesResult.ArtworkUrl100))
            {
                return new ItunesMetadataResult(
                    Success: true,
                    Genre: itunesResult.PrimaryGenreName,
                    AlbumTitle: itunesResult.CollectionName,
                    ReleaseYear: releaseYear);
            }

            // 下载高分辨率封面
            var highResUrl = itunesResult.ArtworkUrl100
                .Replace("100x100", "600x600")
                .Replace("100x100bb", "600x600bb");

            var (bytes, contentType) = await DownloadImageAsync(highResUrl, cancellationToken);
            if (bytes is null || bytes.Length == 0)
            {
                // 封面下载失败，但元数据仍然可用
                return new ItunesMetadataResult(
                    Success: true,
                    Error: "Artwork download failed.",
                    Genre: itunesResult.PrimaryGenreName,
                    AlbumTitle: itunesResult.CollectionName,
                    ReleaseYear: releaseYear);
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
                ReleaseYear: releaseYear);
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

    /// <summary>
    /// 从 iTunes 搜索并返回最佳匹配的完整元数据项。
    /// 搜索策略：优先用 artist+album 搜专辑 → 回退 artist+song 搜歌曲 → 最后仅 artist 搜专辑。
    /// </summary>
    private async Task<ItunesResult?> SearchBestMatchAsync(
        string artist, string? album, string? song, CancellationToken ct)
    {
        // 策略 1：有专辑名 → 搜专辑
        if (!string.IsNullOrWhiteSpace(album))
        {
            var result = await SearchByTermAsync($"{artist} {album}", "album", ct);
            if (result is not null) return result;
        }

        // 策略 2：搜歌曲（更精准拿到 primaryGenreName）
        if (!string.IsNullOrWhiteSpace(song))
        {
            var result = await SearchByTermAsync($"{artist} {song}", "song", ct);
            if (result is not null) return result;
        }

        // 策略 3：仅搜歌手，取第一张专辑
        return await SearchByTermAsync(artist, "album", ct);
    }

    /// <summary>按指定 term 和 entity 类型搜索 iTunes，返回第一个有效结果。</summary>
    private async Task<ItunesResult?> SearchByTermAsync(string term, string entity, CancellationToken ct)
    {
        var url = $"{ItunesSearchUrl}?term={WebUtility.UrlEncode(term)}&media=music&entity={entity}&limit=5";

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in results.EnumerateArray())
            {
                var artworkOk = item.TryGetProperty("artworkUrl100", out var artwork)
                                && artwork.ValueKind == JsonValueKind.String
                                && artwork.GetString() is { Length: > 0 };
                var genreOk = item.TryGetProperty("primaryGenreName", out var genre)
                               && genre.ValueKind == JsonValueKind.String
                               && genre.GetString() is { Length: > 0 };

                // 只要有封面 OR 有流派就算有效结果
                if (artworkOk || genreOk)
                {
                    return DeserializeResult(item);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "iTunes search failed for term={Term} entity={Entity}", term, entity);
            return null;
        }
    }

    private static ItunesResult DeserializeResult(JsonElement item)
    {
        string? GetString(string name)
            => item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() : null;

        return new ItunesResult(
            ArtworkUrl100: GetString("artworkUrl100"),
            ArtistName: GetString("artistName"),
            CollectionName: GetString("collectionName"),
            TrackName: GetString("trackName"),
            PrimaryGenreName: GetString("primaryGenreName"),
            ReleaseDate: GetString("releaseDate"));
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
        string? ReleaseDate);
}
