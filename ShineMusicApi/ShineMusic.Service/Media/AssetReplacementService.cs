using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using SqlSugar;

namespace ShineMusic.Service;

public static class AssetReplacementPolicy
{
    // Numeric bitrate is comparable only within the same lossy encoding.
    public static bool IsUpgrade(MediaAsset current, LibraryImportItem candidate) =>
        ShineMusic.Model.Domain.AudioAdmissionPolicy.IsAcceptable(candidate.Codec, candidate.Container, candidate.BitrateKbps)
        && PlaybackMediaPolicy.CanReuseOriginal(candidate) &&
        string.Equals(current.Codec, candidate.Codec, StringComparison.OrdinalIgnoreCase)
        && candidate.Codec?.ToLowerInvariant() is "mp3" or "aac"
        && current.BitrateKbps is > 0 && candidate.BitrateKbps > current.BitrateKbps;
}

public sealed class AssetReplacementService(ISqlSugarClient db, LibraryStoragePaths paths,
    FfmpegMediaService media, TimeSpan retention)
{
    public async Task<ImportPublishResult> ReplaceAsync(Track track, LibraryImportItem item,
        string sourcePath, CancellationToken ct, Func<CancellationToken, Task>? beforeCommit = null)
    {
        var previousTrackId = item.PublishedTrackId;
        var previousStatus = item.Status;
        var current = await db.Queryable<MediaAsset>().FirstAsync(a => a.TrackId == track.Id && a.IsPublished && a.IsPrimary, ct);
        if (current is null) return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "primary_asset_missing");
        if (!AssetReplacementPolicy.IsUpgrade(current, item))
        {
            await db.Ado.BeginTranAsync();
            try
            {
                item.PublishedTrackId = track.Id;
                item.Status = LibraryImportItemStatuses.Published;
                await db.Updateable(item).ExecuteCommandAsync(ct);
                if (beforeCommit is not null) await beforeCommit(ct);
                await db.Ado.CommitTranAsync();
                return ImportPublishResult.Success(track.Id, item.Status);
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                item.PublishedTrackId = previousTrackId;
                item.Status = previousStatus;
                throw;
            }
        }
        var asset = new MediaAsset { TrackId = track.Id, Container = item.Container!, Codec = item.Codec!,
            ContentHash = item.ContentHash, BitrateKbps = item.BitrateKbps, IsPublished = true, IsPrimary = true,
            StorageKey = $"original/{track.Id:N}/{Guid.NewGuid():N}.{item.Container}" };
        if (!paths.TryResolve(asset.StorageKey, out var destination))
            return ImportPublishResult.Failure(ImportPublishErrorKind.Internal, "invalid_asset_path");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var committed = false;
        try
        {
            File.Copy(sourcePath, destination, false);
            await media.ValidateCompleteAsync(destination, ct);
            var probe = await media.ProbeAsync(destination, ct);
            if (!string.Equals(probe.Codec, item.Codec, StringComparison.OrdinalIgnoreCase)
                || probe.Container != item.Container || probe.BitrateKbps != item.BitrateKbps
                || probe.DurationMs is not > 0
                || !ShineMusic.Model.Domain.AudioAdmissionPolicy.IsAcceptable(probe.Codec, probe.Container, probe.BitrateKbps))
                return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "replacement_probe_mismatch");
            await db.Ado.BeginTranAsync();
            try
            {
                var until = DateTime.UtcNow.Add(retention);
                var changed = await db.Updateable<MediaAsset>()
                    .SetColumns(a => a.IsPrimary == false).SetColumns(a => a.IsPublished == false)
                    .SetColumns(a => a.RetainUntilUtc == until)
                    .Where(a => a.Id == current.Id && a.IsPublished && a.IsPrimary)
                    .ExecuteCommandAsync(ct);
                if (changed != 1) throw new InvalidOperationException("replacement_concurrent_change");
                await db.Updateable<MediaAsset>()
                    .SetColumns(a => a.IsPrimary == false)
                    .SetColumns(a => a.IsPublished == false).SetColumns(a => a.RetainUntilUtc == until)
                    .Where(a => a.TrackId == track.Id && a.IsPublished).ExecuteCommandAsync(ct);
                await db.Insertable(asset).ExecuteCommandAsync(ct);
                item.PublishedTrackId = track.Id;
                item.Status = LibraryImportItemStatuses.Published;
                await db.Updateable(item).ExecuteCommandAsync(ct);
                if (beforeCommit is not null) await beforeCommit(ct);
                await db.Ado.CommitTranAsync();
                committed = true;
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                item.PublishedTrackId = previousTrackId;
                item.Status = previousStatus;
                throw;
            }
            return ImportPublishResult.Success(track.Id, item.Status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaProcessingException
            or SqlSugarException or System.Data.Common.DbException or InvalidOperationException)
        {
            return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "replacement_failed");
        }
        finally
        {
            if (!committed)
            {
                try { File.Delete(destination); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
