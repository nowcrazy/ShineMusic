using System.Globalization;
using System.Text.Json;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;

namespace ShineMusic.Service;

/// <summary>自动导入与手动补全共用：确认同曲后只补空字段，保留来源和人工锁。</summary>
public sealed class ImportMetadataEnricher(ArtworkFetcher artworkFetcher, QuarantineStoragePaths storagePaths,
    ILogger<ImportMetadataEnricher> logger, ProviderMetadataEnricher providerMetadata)
{
    public static bool NeedsEnrichment(LibraryImportItem item)
    {
        bool Missing(string field, bool empty) => empty && !MetadataMerge.Locked(item, field);
        return Missing("cover", string.IsNullOrWhiteSpace(item.ArtworkStorageKey))
            || Missing("album", string.IsNullOrWhiteSpace(item.AlbumTitle))
            || (Missing("releaseDate", item.ReleaseDate is null) && !MetadataMerge.Locked(item, "releaseYear"))
            || Missing("releaseYear", item.ReleaseYear is null)
            || Missing("trackNumber", item.TrackNumber is null) || Missing("discNumber", item.DiscNumber is null)
            || Missing("originalGenres", MetadataMerge.List(item.OriginalGenresJson).Length == 0);
    }

    public async Task<bool> EnrichAsync(LibraryImportItem item, CancellationToken cancellationToken) =>
        (await EnrichFieldsAsync(item, cancellationToken)).Count > 0;

    public async Task<IReadOnlyList<string>> EnrichFieldsAsync(LibraryImportItem item, CancellationToken cancellationToken)
    {
        if (!NeedsEnrichment(item)) return [];
        var artists = MetadataMerge.List(item.ArtistsJson);
        if (string.IsNullOrWhiteSpace(item.Title) || artists.Length == 0) return [];
        var providerFilled = await providerMetadata.EnrichAsync(item, cancellationToken);
        if (!NeedsEnrichment(item)) return providerFilled;
        if (!storagePaths.TryResolve("artwork/.placeholder", out var placeholder)) return providerFilled;
        var result = await artworkFetcher.FetchMatchedAsync(item.Title, artists, item.AlbumTitle, item.DurationMs,
            "artwork", Path.GetDirectoryName(placeholder)!, cancellationToken);
        if (!result.Success)
        {
            var warnings = MetadataMerge.List(item.WarningsJson).ToList();
            var code = result.Error is "metadata_source_unavailable" or "metadata_ambiguous" or "identity_insufficient"
                ? result.Error : "metadata_not_found";
            if (!warnings.Contains(code)) warnings.Add(code);
            item.WarningsJson = JsonSerializer.Serialize(warnings);
            logger.LogDebug("Metadata unresolved for {Id}: {Reason}", item.Id, result.Error);
            return providerFilled;
        }
        var filled = Apply(item, result);
        if (result.StorageKey is { } unused && unused != item.ArtworkStorageKey && storagePaths.TryResolve(unused, out var path))
            try { File.Delete(path); } catch (IOException) { }
        return providerFilled.Concat(filled).Distinct().ToArray();
    }

    /// <summary>调用者须先确认录音身份；现有值和人工空值均受保护。</summary>
    public static IReadOnlyList<string> Apply(LibraryImportItem item, ItunesMetadataResult result)
    {
        if (!result.Success) return [];
        var filled = new List<string>();
        var sourceId = result.ItunesTrackId?.ToString(CultureInfo.InvariantCulture);
        void Fill<T>(string field, T current, T candidate, bool empty, Action<T> assign)
        {
            if (MetadataMerge.Fill(item, field, current, candidate, empty, assign, "itunes", sourceId)) filled.Add(field);
        }
        Fill("album", item.AlbumTitle, result.AlbumTitle, string.IsNullOrWhiteSpace(item.AlbumTitle), v => item.AlbumTitle = v);
        // 日期与既有年份不一致时记录冲突，不让同一条目同时声称两个发行年份。
        var date = result.ReleaseDate;
        if (date is not null && item.ReleaseYear is { } year && !date.StartsWith(year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            MetadataMerge.Fill(item, "releaseDate", item.ReleaseDate, date, false, v => item.ReleaseDate = v, "itunes", sourceId);
        else if (!MetadataMerge.Locked(item, "releaseYear"))
            Fill("releaseDate", item.ReleaseDate, date, item.ReleaseDate is null, v => item.ReleaseDate = v);
        Fill("releaseYear", item.ReleaseYear, result.ReleaseYear, item.ReleaseYear is null, v => item.ReleaseYear = v);
        Fill("trackNumber", item.TrackNumber, result.TrackNumber, item.TrackNumber is null, v => item.TrackNumber = v);
        Fill("discNumber", item.DiscNumber, result.DiscNumber, item.DiscNumber is null, v => item.DiscNumber = v);
        Fill("cover", item.ArtworkStorageKey, result.StorageKey, string.IsNullOrWhiteSpace(item.ArtworkStorageKey), v => item.ArtworkStorageKey = v);
        var genres = string.IsNullOrWhiteSpace(result.Genre) ? Array.Empty<string>() : new[] { result.Genre.Trim() };
        Fill("originalGenres", MetadataMerge.List(item.OriginalGenresJson), genres, MetadataMerge.List(item.OriginalGenresJson).Length == 0, v => item.OriginalGenresJson = JsonSerializer.Serialize(v));
        if (GenreMoodMapper.Map(result.Genre) is { } mood)
            if (MetadataMerge.Fill(item, "genres", MetadataMerge.List(item.GenresJson), new[] { mood }, MetadataMerge.List(item.GenresJson).Length == 0,
                v => item.GenresJson = JsonSerializer.Serialize(v), "derived:genre-map", sourceId)) filled.Add("genres");
        Fill("explicit", item.IsExplicit, ItunesMetadataHelper.ParseExplicit(result.Explicitness), item.IsExplicit is null, v => item.IsExplicit = v);
        Fill("itunesTrackId", item.ItunesTrackId, result.ItunesTrackId, item.ItunesTrackId is null, v => item.ItunesTrackId = v);
        Fill("itunesCountry", item.ItunesCountry, result.Country, string.IsNullOrWhiteSpace(item.ItunesCountry), v => item.ItunesCountry = v);
        return filled;
    }
}
