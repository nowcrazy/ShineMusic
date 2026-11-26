using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using ShineMusic.Service.Media;
using SqlSugar;

namespace ShineMusic.Service.SourceDownload;

/// <summary>
/// A database-fenced pointer to an immutable prefix snapshot. New attempts copy a snapshot into
/// their own file; they never open an old worker's live output or share a writable file.
/// </summary>
internal sealed class DownloadCheckpointStore(
    ISqlSugarClient db, QuarantineStoragePaths paths, ExternalDownloadTask task, Guid attemptId, string workerId,
    long maximumStoreBytes = 1024L * 1024 * 1024, long maximumCheckpointBytes = 256L * 1024 * 1024)
{
    internal const string DirectoryKey = "download-checkpoints";
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);
    private const long MinimumGrowth = 4 * 1024 * 1024;
    private long savedBytes;
    private DateTime lastSavedUtc;

    internal async Task<DownloadResumeState?> RestoreAsync(SourceDownloadInfo candidate, string destination,
        long maximumBytes, CancellationToken cancellationToken)
    {
        if (!OwnOutput(destination)) throw new SourceUnavailableException("The checkpoint destination is not owned by this attempt.");
        var snapshot = Parse(task.DownloadCheckpointJson);
        if (snapshot is null)
        {
            if (task.DownloadCheckpointJson is not null) await ClearAsync(cancellationToken);
            return null;
        }
        if (!Valid(snapshot, task.Id, maximumBytes, maximumCheckpointBytes)
            || snapshot.Fingerprint != Fingerprint(task, candidate) || !SafeDirectory(paths, out var directory))
        {
            await ClearAsync(cancellationToken);
            return null;
        }
        var sourcePath = Path.Combine(directory, Name(snapshot));
        var copied = false;
        try
        {
            if (!File.Exists(sourcePath) || (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                return null;
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, useAsync: true);
            if (source.Length != snapshot.WrittenBytes) return null;
            // A fresh attempt-owned file is the only writable destination; no old attempt is ever renamed.
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                81920, useAsync: true);
            await source.CopyToAsync(output, 81920, cancellationToken);
            output.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken)).ToLowerInvariant();
            if (hash != snapshot.PrefixSha256) return null;
            await AssertOwnedAsync(cancellationToken);
            savedBytes = snapshot.WrittenBytes;
            lastSavedUtc = DateTime.UtcNow;
            copied = true;
            return new(snapshot.WrittenBytes, snapshot.TotalLength, snapshot.StrongETag);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (!copied)
            {
                Delete(destination);
                await ClearAsync(cancellationToken);
            }
        }
    }

    internal async Task PublishAsync(FileStream source, SourceDownloadInfo candidate, string? strongTag,
        long? totalLength, long writtenBytes, long maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            await PublishCoreAsync(source, candidate, strongTag, totalLength, writtenBytes, maximumBytes, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Directory creation, the quota lock and quota enumeration are optional too.
            // A broken checkpoint directory must not fail an otherwise valid audio transfer.
        }
    }

    private async Task PublishCoreAsync(FileStream source, SourceDownloadInfo candidate, string? strongTag,
        long? totalLength, long writtenBytes, long maximumBytes, CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(task, candidate);
        if (fingerprint is null || !ValidTag(strongTag) || writtenBytes <= 0 || totalLength <= writtenBytes
            || totalLength > maximumBytes || writtenBytes > maximumCheckpointBytes || totalLength is null)
            return;
        if (!OwnOutput(source.Name)) throw new SourceUnavailableException("The checkpoint output is not owned by this attempt.");
        if (savedBytes > 0 && writtenBytes < Math.Max(savedBytes * 2, savedBytes + MinimumGrowth)
            && DateTime.UtcNow - lastSavedUtc < TimeSpan.FromSeconds(10)) return;
        if (!SafeDirectory(paths, out var directory)) return;
        Directory.CreateDirectory(directory);
        if (!SafeDirectory(paths, out directory)) return;

        // The shared lock also serializes quota reservations across processes. Contention skips an
        // optional checkpoint, rather than delaying the media transfer or exceeding the disk budget.
        FileStream quota;
        var lockPath = Path.Combine(directory, "quota.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) return;
        try { quota = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; }
        await using (quota)
        {
            long occupied = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!(file.EndsWith(".part", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal))) continue;
                try { occupied += new FileInfo(file).Length; }
                catch (FileNotFoundException) { /* The maintenance sweep won the race. */ }
                if (occupied >= maximumStoreBytes) return;
            }
            if (writtenBytes > maximumStoreBytes - occupied) return;
            var snapshot = new DownloadCheckpoint(1, task.Id, attemptId, Guid.NewGuid(), fingerprint,
                strongTag!, totalLength.Value, writtenBytes, "", DateTime.UtcNow);
            var finalPath = Path.Combine(directory, Name(snapshot));
            var temporaryPath = finalPath + ".tmp";
            var oldJson = task.DownloadCheckpointJson;
            var published = false;
            var position = source.Position;
            try
            {
                await source.FlushAsync(cancellationToken);
                source.Flush(flushToDisk: true);
                source.Position = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var snapshotFile = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long remaining = writtenBytes;
                    while (remaining > 0)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0) throw new IOException("The checkpoint prefix was incomplete.");
                        await snapshotFile.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        remaining -= read;
                    }
                    await snapshotFile.FlushAsync(cancellationToken);
                    snapshotFile.Flush(flushToDisk: true);
                }
                snapshot = snapshot with { PrefixSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() };
                File.Move(temporaryPath, finalPath);
                var json = JsonSerializer.Serialize(snapshot);
                var now = DateTime.UtcNow;
                var affected = await db.Updateable<ExternalDownloadTask>()
                    .SetColumns(row => row.DownloadCheckpointJson == json)
                    .Where(row => row.Id == task.Id && row.WorkerId == workerId && row.AttemptId == attemptId
                        && row.Status == ExternalDownloadTaskStatuses.Downloading
                        && row.SourceProvider == task.SourceProvider && row.ProviderTrackId == task.ProviderTrackId
                        && row.LeaseUntilUtc != null && row.LeaseUntilUtc > now)
                    .ExecuteCommandAsync(cancellationToken);
                if (affected != 1) throw new MediaLeaseLostException("download", task.Id);
                task.DownloadCheckpointJson = json;
                published = true;
                savedBytes = writtenBytes;
                lastSavedUtc = now;
                // A file still being copied by a successor is protected by its read handle.
                DeleteSnapshot(paths, Parse(oldJson), task.Id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Checkpointing is optional. Never downgrade an otherwise valid media transfer due to disk contention.
            }
            finally
            {
                source.Position = position;
                Delete(temporaryPath);
                if (!published) Delete(finalPath);
            }
        }
    }

    internal async Task ClearAsync(CancellationToken cancellationToken)
    {
        var json = task.DownloadCheckpointJson;
        if (json is null) return;
        var now = DateTime.UtcNow;
        var changed = await db.Updateable<ExternalDownloadTask>()
            .SetColumns(row => row.DownloadCheckpointJson == (string?)null)
            .Where(row => row.Id == task.Id && row.WorkerId == workerId && row.AttemptId == attemptId
                && row.DownloadCheckpointJson == json && row.LeaseUntilUtc != null && row.LeaseUntilUtc > now)
            .ExecuteCommandAsync(cancellationToken);
        if (changed != 1) throw new MediaLeaseLostException("download", task.Id);
        task.DownloadCheckpointJson = null;
        savedBytes = 0;
        DeleteSnapshot(paths, Parse(json), task.Id);
    }

    private async Task AssertOwnedAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (!await db.Queryable<ExternalDownloadTask>().AnyAsync(row => row.Id == task.Id && row.WorkerId == workerId
            && row.AttemptId == attemptId && row.Status == ExternalDownloadTaskStatuses.Downloading
            && row.LeaseUntilUtc != null && row.LeaseUntilUtc > now, cancellationToken))
            throw new MediaLeaseLostException("download", task.Id);
    }

    internal static string? Fingerprint(ExternalDownloadTask task, SourceDownloadInfo candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.ResolverId) || string.IsNullOrWhiteSpace(candidate.ResourceId)
            || !Uri.TryCreate(candidate.DownloadUrl, UriKind.Absolute, out var uri)) return null;
        // Strong validators are scoped to a resource URI. Hash the entire request URI and context;
        // a changed signed query restarts safely, without persisting URLs or credentials in clear text.
        var context = JsonSerializer.Serialize(new { task.SourceProvider, task.ProviderTrackId,
            candidate.ResolverId, candidate.ResourceId, candidate.Quality, candidate.Container,
            candidate.IsLossless, candidate.BitrateKbps, candidate.UseIsolatedDownloadClient,
            RequestUri = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped), candidate.RequestHeaders,
            task.ExpectedTitle, task.ExpectedArtistsJson, task.ExpectedAlbumTitle, task.ExpectedDurationMs });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context))).ToLowerInvariant();
    }

    private static bool ValidTag(string? value) => value is { Length: > 0 and <= 256 }
        && !value.Contains("://", StringComparison.Ordinal) && !value.Contains('?')
        && EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*";

    private static bool Valid(DownloadCheckpoint snapshot, Guid taskId, long maximumBytes, long maximumCheckpointBytes) =>
        snapshot.Version == 1 && snapshot.TaskId == taskId && snapshot.OwnerAttemptId != Guid.Empty && snapshot.SnapshotId != Guid.Empty
        && snapshot.WrittenBytes > 0 && snapshot.WrittenBytes < snapshot.TotalLength && snapshot.TotalLength <= maximumBytes
        && snapshot.WrittenBytes <= maximumCheckpointBytes && ValidTag(snapshot.StrongETag)
        && Hex(snapshot.Fingerprint) && Hex(snapshot.PrefixSha256)
        && snapshot.CreatedAtUtc <= DateTime.UtcNow.AddMinutes(5) && snapshot.CreatedAtUtc >= DateTime.UtcNow - MaximumAge;

    private static bool Hex(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static DownloadCheckpoint? Parse(string? json)
    {
        if (json is not { Length: > 0 and <= 4096 }) return null;
        try { return JsonSerializer.Deserialize<DownloadCheckpoint>(json); }
        catch (JsonException) { return null; }
    }

    private static string Name(DownloadCheckpoint snapshot) => $"{snapshot.TaskId:N}.{snapshot.OwnerAttemptId:N}.{snapshot.SnapshotId:N}.part";
    private bool OwnOutput(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith($"{task.Id:N}.{attemptId:N}.", StringComparison.Ordinal)
            || !paths.TryResolve("sourcedl/" + name, out var expected)
            || !Path.GetFullPath(path).Equals(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return false;
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        for (var current = new FileInfo(expected).Directory; current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private static bool SafeDirectory(QuarantineStoragePaths paths, out string directory)
    {
        directory = "";
        if (!paths.TryResolve(DirectoryKey, out var resolved)) return false;
        // Reject junctions/symlinks in the path, including a replaced quarantine root.
        for (var current = new DirectoryInfo(resolved); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        directory = resolved;
        return true;
    }

    private static void DeleteSnapshot(QuarantineStoragePaths paths, DownloadCheckpoint? snapshot, Guid expectedTaskId)
    {
        if (snapshot is null || snapshot.TaskId != expectedTaskId || snapshot.TaskId == Guid.Empty || snapshot.OwnerAttemptId == Guid.Empty
            || snapshot.SnapshotId == Guid.Empty || !SafeDirectory(paths, out var directory)) return;
        Delete(Path.Combine(directory, Name(snapshot)));
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static async Task SweepAsync(ISqlSugarClient db, QuarantineStoragePaths paths, CancellationToken cancellationToken)
    {
        if (!SafeDirectory(paths, out var directory) || !Directory.Exists(directory)) return;
        var now = DateTime.UtcNow;
        var cutoff = now - MaximumAge;
        var rows = await db.Queryable<ExternalDownloadTask>()
            .Where(row => row.DownloadCheckpointJson != null && (row.LeaseUntilUtc == null || row.LeaseUntilUtc <= now))
            .OrderBy(row => row.UpdatedAtUtc).Take(100).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var snapshot = Parse(row.DownloadCheckpointJson);
            if (row.Status is ExternalDownloadTaskStatuses.Queued or ExternalDownloadTaskStatuses.Downloading
                && snapshot is not null && snapshot.CreatedAtUtc >= cutoff) continue;
            var json = row.DownloadCheckpointJson;
            var changed = await db.Updateable<ExternalDownloadTask>()
                .SetColumns(value => value.DownloadCheckpointJson == (string?)null)
                .Where(value => value.Id == row.Id && value.DownloadCheckpointJson == json
                    && (value.LeaseUntilUtc == null || value.LeaseUntilUtc <= now))
                .ExecuteCommandAsync(cancellationToken);
            if (changed == 1) DeleteSnapshot(paths, snapshot, row.Id);
        }
        // Files without a committed pointer (crash before CAS) are bounded by quota until their TTL.
        foreach (var file in Directory.EnumerateFiles(directory).Take(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(file.EndsWith(".part", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal))
                || File.GetLastWriteTimeUtc(file) >= cutoff) continue;
            var pieces = Path.GetFileName(file).Split('.');
            if (pieces.Length < 4 || !Guid.TryParseExact(pieces[0], "N", out var id)) continue;
            var current = await db.Queryable<ExternalDownloadTask>().FirstAsync(row => row.Id == id, cancellationToken);
            var pointer = Parse(current?.DownloadCheckpointJson);
            if (pointer is not null && Path.GetFileName(file) == Name(pointer)) continue;
            Delete(file);
        }
    }
}

internal sealed record DownloadCheckpoint(int Version, Guid TaskId, Guid OwnerAttemptId, Guid SnapshotId,
    string Fingerprint, string StrongETag, long TotalLength, long WrittenBytes, string PrefixSha256, DateTime CreatedAtUtc);
internal sealed record DownloadResumeState(long WrittenBytes, long TotalLength, string StrongETag);
