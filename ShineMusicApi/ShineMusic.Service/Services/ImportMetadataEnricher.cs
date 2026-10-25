// ImportMetadataEnricher.cs
// 导入期元数据补全器（唯一实现）。
//
// 背景：曲目入库前需要尽可能补全 歌名 → 歌手 → 专辑 → 时长 → 年份 → 地区 → 风格 → 封面，
// 这些信息来自两条链路：
//   1) 音频文件自身的标签（TagLib#，见 AudioMetadataParser）；
//   2) 外部元数据源（iTunes Search API，见 ArtworkFetcher）。
// 早期版本把 (2) 写在三个地方——导入队列（且只在“没有内嵌封面”时才跑）、
// 管理端发布流程、批量回填接口——导致同一个文件走不同入口得到不同完整度的数据。
// 现在统一收敛到这里：解析阶段调用一次，补不齐就留空并记告警；发布阶段不再做解析。
//
// 设计原则：
//   - 只补空字段，绝不覆盖标签里已有的值（人工修正过的元数据同样不会被覆盖）；
//   - 所有缺失字段都已具备时不发请求，避免无谓消耗 iTunes 的 20 次/分钟配额；
//   - 地区（Region）优先本地推导，只有推不出来时才借助外部源。

using System.Text.Json;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;

namespace ShineMusic.Service;

/// <summary>
/// 导入期元数据补全：把 TagLib# 解析结果与 iTunes 元数据合并成一条完整的导入项。
/// 注册为单例（依赖均为无状态服务/路径解析器）。
/// </summary>
public sealed class ImportMetadataEnricher(
    ArtworkFetcher artworkFetcher,
    QuarantineStoragePaths storagePaths,
    ILogger<ImportMetadataEnricher> logger)
{
    /// <summary>
    /// 判断导入项是否还有「值得为此请求外部源」的补全空间。
    /// 只把对用户可见的字段（封面 / 专辑 / 年份 / 地区 / 风格 / 时长 / 歌手）算作触发条件；
    /// 曲目号、碟片号、iTunes 曲目 ID、成人标记属于“顺带字段”——
    /// 一旦因为其它原因发起请求就一并带回，但不会单独为了它们去消耗 20 次/分钟的配额。
    /// </summary>
    public static bool NeedsEnrichment(LibraryImportItem item) =>
        string.IsNullOrWhiteSpace(item.ArtworkStorageKey)
        || string.IsNullOrWhiteSpace(item.AlbumTitle)
        || item.ReleaseYear is null
        || string.IsNullOrWhiteSpace(item.Region)
        || item.DurationMs is null or <= 0
        || DeserializeList(item.ArtistsJson).Count == 0
        || DeserializeList(item.GenresJson).Count == 0;

    /// <summary>
    /// 为导入项补全缺失元数据（就地修改，不负责保存）。
    /// </summary>
    /// <param name="item">待补全的导入项（解析阶段，文件仍在隔离区）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否真的写入了新字段。</returns>
    public async Task<bool> EnrichAsync(LibraryImportItem item, CancellationToken cancellationToken)
    {
        var artists = DeserializeList(item.ArtistsJson);

        // 地区可以先本地推导：标签里已有流派或艺人名时不必为此请求外部源。
        if (string.IsNullOrWhiteSpace(item.Region))
            item.Region = ItunesMetadataHelper.DeriveRegion(null, DeserializeList(item.GenresJson).FirstOrDefault(), artists.FirstOrDefault());

        if (!NeedsEnrichment(item))
            return false;

        var firstArtist = artists.FirstOrDefault();
        // 既没有歌手也没有曲名，外部源无从检索。
        if (string.IsNullOrWhiteSpace(firstArtist) && string.IsNullOrWhiteSpace(item.Title))
            return false;

        // 封面在发布前都放在隔离区，因此统一用隔离区目录。
        if (!storagePaths.TryResolve("artwork/.placeholder", out var placeholder))
            return false;
        var artworkDirectory = Path.GetDirectoryName(placeholder)!;

        var result = string.IsNullOrWhiteSpace(firstArtist)
            ? await artworkFetcher.FetchByTitleAsync(item.Title!, "artwork", artworkDirectory, cancellationToken)
            : await artworkFetcher.FetchAsync(firstArtist, item.AlbumTitle, item.Title, "artwork", artworkDirectory, cancellationToken);

        if (result is null || !result.Success)
        {
            logger.LogDebug("iTunes enrichment missed for {Title}: {Reason}", item.Title, result?.Error ?? "no_match");
            return false;
        }

        Apply(item, result, artists);
        return true;
    }

    /// <summary>
    /// 把 iTunes 结果合并进导入项：逐字段“只补空”。
    /// </summary>
    private static void Apply(LibraryImportItem item, ItunesMetadataResult result, IReadOnlyList<string> existingArtists)
    {
        // 封面：标签没有内嵌封面时用 iTunes 的图
        if (string.IsNullOrWhiteSpace(item.ArtworkStorageKey) && !string.IsNullOrWhiteSpace(result.StorageKey))
            item.ArtworkStorageKey = result.StorageKey;

        // 歌手：标签里完全没有歌手时，用严格同名的搜索结果兜底
        if (existingArtists.Count == 0 && !string.IsNullOrWhiteSpace(result.ArtistName))
            item.ArtistsJson = JsonSerializer.Serialize(new[] { result.ArtistName.Trim() });

        // 专辑
        if (string.IsNullOrWhiteSpace(item.AlbumTitle) && !string.IsNullOrWhiteSpace(result.AlbumTitle))
            item.AlbumTitle = result.AlbumTitle.Trim();

        // 年份
        if (!item.ReleaseYear.HasValue && result.ReleaseYear is > 0)
            item.ReleaseYear = result.ReleaseYear;

        // 时长（本地 ffprobe/TagLib 都失败时兜底）
        if (item.DurationMs is null or <= 0 && result.TrackTimeMs is > 0)
            item.DurationMs = result.TrackTimeMs;

        // 曲目号 / 碟片号
        if (!item.TrackNumber.HasValue && result.TrackNumber is > 0)
            item.TrackNumber = result.TrackNumber;
        if (!item.DiscNumber.HasValue && result.DiscNumber is > 0)
            item.DiscNumber = result.DiscNumber;

        // iTunes 曲目 ID（溯源用）
        if (!item.ItunesTrackId.HasValue && result.ItunesTrackId is > 0)
            item.ItunesTrackId = result.ItunesTrackId;

        // 风格：标签没有流派时，把 iTunes 的英文流派映射成应用内的中文 mood 标签
        if (DeserializeList(item.GenresJson).Count == 0 && !string.IsNullOrWhiteSpace(result.Genre))
        {
            var mood = GenreMoodMapper.Map(result.Genre);
            if (mood is not null)
                item.GenresJson = JsonSerializer.Serialize(new[] { mood });
        }

        // 上架商店代码：只作溯源，不代表艺人国籍
        if (string.IsNullOrWhiteSpace(item.ItunesCountry) && !string.IsNullOrWhiteSpace(result.Country))
            item.ItunesCountry = result.Country.Trim();

        // 地区：流派 → 艺人名文字 → 商店代码
        // 艺人名优先用本地（音频内嵌标签更可信，不会把「张韶涵」翻成 Angela Zhang 导致被判欧美）；
        // iTunes 流派是原始英文 genre（如 Mandopop/Pop），本地流派可能已被映射成中文 mood 标签。
        if (string.IsNullOrWhiteSpace(item.Region))
        {
            var genreForRegion = existingArtists.Count == 0
                ? result.Genre
                : result.Genre;
            var artistForRegion = existingArtists.Count > 0
                ? existingArtists[0]          // 本地艺人名优先（音频内嵌标签更可信）
                : result.ArtistName;          // 本地完全没有时才用 iTunes 兜底
            item.Region = ItunesMetadataHelper.DeriveRegion(result.Country, genreForRegion, artistForRegion);
        }

        // 成人内容标记
        if (!item.Explicit.HasValue)
            item.Explicit = ItunesMetadataHelper.ParseExplicit(result.Explicitness);
    }

    /// <summary>把 JSON 数组字符串解析为字符串列表；解析失败返回空列表。</summary>
    private static List<string> DeserializeList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
