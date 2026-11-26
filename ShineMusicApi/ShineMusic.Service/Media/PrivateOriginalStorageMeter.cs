using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>
/// Measures retained bytes from disk, including historical originals without persisted sizes.
/// Call under the workflow mutation/advisory transaction lock when making an allocation decision.
/// </summary>
internal static class PrivateOriginalStorageMeter
{
    internal sealed record Usage(long Bytes, Dictionary<string, long> Originals, Dictionary<string, long> Paths);

    internal static async Task<Usage> MeasureAsync(ISqlSugarClient db, LibraryStoragePaths paths,
        Guid owner, CancellationToken ct)
    {
        // Recycled tracks and retired original rows still hold storage until their existing sweep releases it.
        var assets = await db.Queryable<MediaAsset>().InnerJoin<Track>((asset, track) => asset.TrackId == track.Id)
            .Where((asset, track) => asset.IsOriginal && track.OwnerUserId == owner && track.PublishedAt == null)
            .Select((asset, track) => new { asset.StorageKey, asset.ContentHash }).ToListAsync(ct);
        var reviews = await db.Queryable<LibraryWorkflowReview>().Where(review => review.OwnerUserId == owner && review.Status == "pending")
            .Select(review => new { StorageKey = review.OriginalStorageKey, review.ContentHash }).ToListAsync(ct);
        var references = assets.Select(asset => (asset.StorageKey, asset.ContentHash))
            .Concat(reviews.Select(review => (review.StorageKey, review.ContentHash)));
        var measuredPaths = new Dictionary<string, long>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var originals = new Dictionary<string, long>(StringComparer.Ordinal);
        long used = 0;
        try
        {
            foreach (var (key, hash) in references)
            {
                ct.ThrowIfCancellationRequested();
                if (!paths.TryResolve(key, out var path)) throw Unavailable();
                if (!measuredPaths.TryGetValue(path, out var bytes))
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw Unavailable();
                    bytes = file.Length;
                    measuredPaths[path] = bytes;
                }
                else continue; // The same retained file may also be a review snapshot, including legacy hash metadata.
                var identity = Identity(hash, path);
                if (originals.TryGetValue(identity, out var previous))
                {
                    // A shared content hash cannot safely describe differently sized originals.
                    if (previous != bytes) throw Unavailable();
                    continue;
                }
                originals.Add(identity, bytes);
                used = checked(used + bytes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        { throw Unavailable(); }
        return new(used, originals, measuredPaths);
    }

    internal static async Task EnsureCapacityAsync(ISqlSugarClient db, LibraryStoragePaths paths, Guid owner,
        string hash, long bytes, long limit, CancellationToken ct, string? storageKey = null)
    {
        if (limit == 0) return;
        if (limit < 0 || bytes < 0) throw Unavailable();
        var usage = await MeasureAsync(db, paths, owner, ct);
        // Same-user reuploads add no retained bytes, including after a limit was lowered.
        if (usage.Originals.TryGetValue(Identity(hash, string.Empty), out var retainedBytes))
        {
            if (retainedBytes != bytes) throw Unavailable();
            return;
        }
        if (storageKey is not null && paths.TryResolve(storageKey, out var storedPath)
            && usage.Paths.ContainsKey(storedPath)) return;
        if (bytes > limit || usage.Bytes > limit - bytes)
            throw new MediaProcessingException(ErrorCodes.PrivateStorageQuotaExceeded, "The retained original storage quota was exceeded.");
    }

    private static string Identity(string? hash, string path) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit)
        ? "sha256:" + hash.ToLowerInvariant()
        : "path:" + (OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);

    private static MediaProcessingException Unavailable() => new(ErrorCodes.PrivateStorageUsageUnavailable,
        "The retained original storage usage is unavailable.");
}
