using System.Text.Json;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;

namespace ShineMusic.Service;

/// <summary>所有导入元数据写入共用的只补空/人工保护规则。</summary>
public static class MetadataMerge
{
    public static List<MetadataFieldAuditDto> Audit(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<MetadataFieldAuditDto>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
    public static string[] List(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
    public static bool Locked(LibraryImportItem item, string field) => Audit(item.MetadataAuditJson).Any(a => a.Field == field && a.ManualLocked);
    public static bool HasConflicts(LibraryImportItem item) => Audit(item.MetadataAuditJson).Any(a => a.CandidateJson is not null);

    public static void RecordExisting(LibraryImportItem item, string field, object? value, string source, string? sourceId = null)
    {
        if (value is null) return;
        var rows = Audit(item.MetadataAuditJson);
        if (rows.Any(a => a.Field == field)) return;
        rows.Add(new(field, JsonSerializer.Serialize(value), source, sourceId, DateTime.UtcNow, false));
        item.MetadataAuditJson = JsonSerializer.Serialize(rows);
    }


    public static bool Fill<T>(LibraryImportItem item, string field, T current, T candidate, bool empty,
        Action<T> assign, string source, string? sourceId = null)
    {
        if (candidate is null || candidate is string text && string.IsNullOrWhiteSpace(text)
            || candidate is System.Collections.ICollection values && values.Count == 0) return false;
        var rows = Audit(item.MetadataAuditJson);
        var prior = rows.FirstOrDefault(a => a.Field == field);
        var value = JsonSerializer.Serialize(current);
        var proposed = JsonSerializer.Serialize(candidate);
        if (prior?.ManualLocked == true || !empty)
        {
            if (value != proposed)
            {
                var conflict = (prior ?? new(field, value, "existing", null, DateTime.UtcNow, false)) with
                { CandidateJson = proposed, CandidateSource = source, Reason = prior?.ManualLocked == true ? "manual_locked" : "existing_value_conflict" };
                rows.RemoveAll(a => a.Field == field);
                rows.Add(conflict);
                item.MetadataAuditJson = JsonSerializer.Serialize(rows);
            }
            return false;
        }
        assign(candidate);
        rows.RemoveAll(a => a.Field == field);
        rows.Add(new(field, proposed, source, sourceId, DateTime.UtcNow, false));
        item.MetadataAuditJson = JsonSerializer.Serialize(rows);
        return true;
    }

    public static void Manual(LibraryImportItem item, string field, object? value)
    {
        var rows = Audit(item.MetadataAuditJson);
        rows.RemoveAll(a => a.Field == field);
        rows.Add(new(field, JsonSerializer.Serialize(value), "manual", null, DateTime.UtcNow, true));
        item.MetadataAuditJson = JsonSerializer.Serialize(rows);
    }

    public static void ApplyTags(LibraryImportItem item, ParsedAudioMetadata tags)
    {
        Fill(item, "title", item.Title, tags.Title, string.IsNullOrWhiteSpace(item.Title), v => item.Title = v, "embedded");
        Fill(item, "artists", List(item.ArtistsJson), tags.Artists.ToArray(), List(item.ArtistsJson).Length == 0, v => item.ArtistsJson = JsonSerializer.Serialize(v), "embedded");
        Fill(item, "album", item.AlbumTitle, tags.AlbumTitle, string.IsNullOrWhiteSpace(item.AlbumTitle), v => item.AlbumTitle = v, "embedded");
        Fill(item, "releaseYear", item.ReleaseYear, tags.ReleaseYear, item.ReleaseYear is null, v => item.ReleaseYear = v, "embedded");
        Fill(item, "trackNumber", item.TrackNumber, tags.TrackNumber, item.TrackNumber is null, v => item.TrackNumber = v, "embedded");
        Fill(item, "discNumber", item.DiscNumber, tags.DiscNumber, item.DiscNumber is null, v => item.DiscNumber = v, "embedded");
        Fill(item, "originalGenres", List(item.OriginalGenresJson), tags.Genres.ToArray(), List(item.OriginalGenresJson).Length == 0, v => item.OriginalGenresJson = JsonSerializer.Serialize(v), "embedded");
        Fill(item, "genres", List(item.GenresJson), tags.Genres.ToArray(), List(item.GenresJson).Length == 0, v => item.GenresJson = JsonSerializer.Serialize(v), "embedded");
        Fill(item, "albumArtists", List(item.AlbumArtistsJson), tags.AlbumArtists?.ToArray() ?? [], List(item.AlbumArtistsJson).Length == 0, v => item.AlbumArtistsJson = JsonSerializer.Serialize(v), "embedded");
        Fill(item, "isrc", item.Isrc, tags.Isrc, string.IsNullOrWhiteSpace(item.Isrc), v => item.Isrc = v, "embedded");
        Fill(item, "cover", item.ArtworkStorageKey, tags.ArtworkStorageKey, string.IsNullOrWhiteSpace(item.ArtworkStorageKey), v => item.ArtworkStorageKey = v, "embedded");
        Fill(item, "lyrics", item.Lyrics, tags.Lyrics, string.IsNullOrWhiteSpace(item.Lyrics), v => item.Lyrics = v, "embedded");
    }
}
