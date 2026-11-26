using System.Globalization;
using System.Text;
using Microsoft.Net.Http.Headers;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Application;

/// <summary>Builds an original-byte attachment after its caller has authorized the track.</summary>
public static class OriginalAudioExportResults
{
    /// <summary>The caller must validate the track's ownership, publication and recycle state first.</summary>
    public static async Task<IResult> CreateAsync(HttpContext context, Track track, ISqlSugarClient db,
        LibraryStoragePaths libraryPaths, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        // A historical original/ path may contain a compatibility rendition.
        // Only explicitly retained, currently published originals can be exported.
        var asset = await db.Queryable<MediaAsset>()
            .Where(candidate => candidate.TrackId == track.Id && candidate.IsPublished && candidate.IsOriginal)
            .OrderByDescending(candidate => candidate.IsPrimary).OrderBy(candidate => candidate.Id)
            .FirstAsync(cancellationToken);
        if (asset is null || !libraryPaths.TryResolve(asset.StorageKey, out var path))
            return ApiResults.NotFound(context);

        FileStream? content = null;
        DateTimeOffset lastModified;
        try
        {
            // Open before returning so missing/inaccessible sources fail here.
            // The native file result owns and disposes the successful stream.
            content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            content?.Dispose();
            return ApiResults.NotFound(context);
        }

        var contentType = PlaybackMediaPolicy.TryGetContentType(asset.Container) ?? "application/octet-stream";
        var extension = contentType == "application/octet-stream" ? "audio" : asset.Container.ToLowerInvariant();
        var fileName = DownloadName(track.Title, track.Id, extension);
        return Results.File(content, contentType, fileDownloadName: fileName, lastModified: lastModified,
            entityTag: new EntityTagHeaderValue($"\"{asset.Id:N}-{content.Length:x}-{lastModified.UtcTicks:x}\""),
            enableRangeProcessing: true);
    }

    private static string DownloadName(string title, Guid trackId, string extension)
    {
        var name = new StringBuilder();
        foreach (var character in title)
        {
            if (name.Length >= 120) break;
            name.Append(char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format
                || "<>:\"/\\|?*".Contains(character) ? '_' : character);
        }
        if (name.Length > 0 && char.IsHighSurrogate(name[^1])) name.Length--;
        var stem = name.ToString().Trim(' ', '.');
        if (stem.Length == 0) stem = $"track-{trackId:N}";
        var device = stem.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL"
            || (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal))
                && device[3] is >= '1' and <= '9'))
            stem = "_" + stem;
        return $"{stem}.{extension}";
    }
}
