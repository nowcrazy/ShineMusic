using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Model.Ports;

namespace ShineMusic.Service.Media;

/// <summary>Builds bounded, disposable AAC renditions without adding them to the permanent media catalog.</summary>
public sealed class TemporaryPlaybackService : ITemporaryPlaybackService, IDisposable
{
    private const string PartialMarker = ".partial.";
    private readonly TemporaryPlaybackOptions _options;
    private readonly IMediaTranscoder _transcoder;
    private readonly ILogger<TemporaryPlaybackService> _logger;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _transcodeSlots;
    private readonly Dictionary<string, KeyLock> _keyLocks = new(StringComparer.Ordinal);
    private readonly object _keyLockSync = new();

    public TemporaryPlaybackService(
        IOptions<TemporaryPlaybackOptions> options,
        IOptions<StorageOptions> storageOptions,
        IMediaTranscoder transcoder,
        IHostEnvironment environment,
        ILogger<TemporaryPlaybackService> logger)
    {
        _options = options.Value;
        _transcoder = transcoder;
        _logger = logger;
        ValidateOptions(_options);
        _cacheRoot = Path.GetFullPath(_options.CacheDirectory, environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        EnsureNoReparsePoints(_cacheRoot);
        EnsureOutsideStorage(_cacheRoot, storageOptions.Value.LibraryRoot, environment.ContentRootPath, "library");
        EnsureOutsideStorage(_cacheRoot, storageOptions.Value.QuarantineRoot, environment.ContentRootPath, "quarantine");
        _transcodeSlots = new SemaphoreSlim(_options.MaxConcurrentTranscodes, _options.MaxConcurrentTranscodes);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            await CleanupAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Temporary playback cache is not currently available; original audio playback remains enabled.");
        }
    }

    public async Task<TemporaryPlaybackLease> GetAacAsync(
        Guid trackId,
        string originalFilePath,
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        if (trackId == Guid.Empty) throw new ArgumentException("Track id is required.", nameof(trackId));
        if (string.IsNullOrWhiteSpace(sourceIdentity)) throw new ArgumentException("Source identity is required.", nameof(sourceIdentity));

        var sourcePath = Path.GetFullPath(originalFilePath);
        if (IsWithin(sourcePath, _cacheRoot)) throw new ArgumentException("A temporary cache file cannot be used as the stored original.", nameof(originalFilePath));
        var sourceBefore = GetSourceStamp(sourcePath);
        var cacheKey = CreateCacheKey(trackId, sourcePath, sourceIdentity, sourceBefore);
        var targetPath = Path.Combine(_cacheRoot, cacheKey + ".m4a");
        KeyLock keyLock;
        lock (_keyLockSync)
        {
            if (!_keyLocks.TryGetValue(cacheKey, out keyLock!))
                _keyLocks[cacheKey] = keyLock = new KeyLock();
            keyLock.Users++;
        }

        var entered = false;
        try
        {
            await keyLock.Gate.WaitAsync(cancellationToken);
            entered = true;
            Directory.CreateDirectory(_cacheRoot);
            if (!IsValidCachedFile(targetPath))
            {
                TryDelete(targetPath);
                await BuildCacheFileAsync(sourcePath, sourceBefore, targetPath, cancellationToken);
            }

            var entry = _entries.GetOrAdd(targetPath, static path => new CacheEntry(path));
            var lease = AcquireLease(entry);
            try
            {
                await EnforceCacheLimitAsync(cancellationToken);
                return lease;
            }
            catch
            {
                await lease.DisposeAsync();
                await EnforceCacheLimitAsync(CancellationToken.None, throwWhenOverLimit: false);
                throw;
            }
        }
        finally
        {
            if (entered) keyLock.Gate.Release();
            lock (_keyLockSync)
            {
                if (--keyLock.Users == 0)
                {
                    _keyLocks.Remove(cacheKey);
                    keyLock.Gate.Dispose();
                }
            }
        }
    }

    /// <summary>Removes idle entries and stale partial outputs, skipping files with active response leases.</summary>
    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_cacheRoot)) return;
        var now = DateTimeOffset.UtcNow;
        var idleCutoff = now.AddMinutes(-_options.IdleMinutes);
        foreach (var path in Directory.EnumerateFiles(_cacheRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!IsOwnedCacheFile(name)) continue;
            if (name.Contains(PartialMarker, StringComparison.Ordinal))
            {
                if (TryGetLastWriteTime(path) is { } partialTime && partialTime < now.AddHours(-1)) TryDelete(path);
                continue;
            }

            if (!string.Equals(Path.GetExtension(path), ".m4a", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = _entries.GetOrAdd(path, static file => new CacheEntry(file));
            lock (entry.Sync)
            {
                var lastUse = entry.LastAccessUtc ?? TryGetLastWriteTime(path);
                if (entry.ActiveLeases == 0 && lastUse is not null && lastUse < idleCutoff)
                {
                    if (TryDelete(path)) _entries.TryRemove(path, out _);
                }
            }
            await Task.Yield();
        }

        await EnforceCacheLimitAsync(cancellationToken, throwWhenOverLimit: false);
    }

    private async Task BuildCacheFileAsync(
        string sourcePath,
        SourceStamp sourceBefore,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await _transcodeSlots.WaitAsync(cancellationToken);
        var partialPath = Path.Combine(_cacheRoot, Path.GetFileNameWithoutExtension(targetPath)
            + PartialMarker + Guid.NewGuid().ToString("N") + ".m4a");
        try
        {
            using var generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var conversion = _transcoder.TranscodeToAacAsync(sourcePath, partialPath, _options.AacBitrateKbps, generationCancellation.Token);
            try
            {
                while (!conversion.IsCompleted)
                {
                    var completed = await Task.WhenAny(conversion, Task.Delay(200, cancellationToken));
                    if (completed == conversion) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (FileLength(partialPath) > _options.MaxEntryBytes)
                    {
                        await generationCancellation.CancelAsync();
                        try { await conversion; }
                        catch (OperationCanceledException) { }
                        throw new IOException("Temporary AAC rendition exceeds the configured size limit.");
                    }
                }
                await conversion;
            }
            finally
            {
                if (!conversion.IsCompleted)
                {
                    await generationCancellation.CancelAsync();
                    try { await conversion; }
                    catch (OperationCanceledException) { }
                }
            }
            var output = new FileInfo(partialPath);
            if (!output.Exists || output.Length <= 0)
                throw new IOException("Temporary AAC conversion did not produce a readable file.");
            if (output.Length > _options.MaxEntryBytes)
                throw new IOException("Temporary AAC rendition exceeds the configured size limit.");

            var probe = await _transcoder.ProbeAsync(partialPath, cancellationToken);
            if (!string.Equals(probe.Codec, "aac", StringComparison.OrdinalIgnoreCase)
                || !probe.Container.Equals("m4a", StringComparison.OrdinalIgnoreCase)
                && !probe.Container.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Temporary playback conversion did not produce AAC in an M4A-compatible container.");

            if (GetSourceStamp(sourcePath) != sourceBefore)
                throw new IOException("The source audio changed during temporary playback conversion.");

            File.Move(partialPath, targetPath, overwrite: true);
            var entry = _entries.GetOrAdd(targetPath, static path => new CacheEntry(path));
            lock (entry.Sync) entry.LastAccessUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            TryDelete(partialPath);
            _transcodeSlots.Release();
        }
    }

    private static TemporaryPlaybackLease AcquireLease(CacheEntry entry)
    {
        lock (entry.Sync)
        {
            var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var info = new FileInfo(entry.Path);
            entry.ActiveLeases++;
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
            var entityTag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
            return new TemporaryPlaybackLease(stream, info.Length, new DateTimeOffset(info.LastWriteTimeUtc), entityTag,
                () => ReleaseLeaseAsync(entry));
        }
    }

    private static ValueTask ReleaseLeaseAsync(CacheEntry entry)
    {
        lock (entry.Sync)
        {
            entry.ActiveLeases = Math.Max(0, entry.ActiveLeases - 1);
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
        }
        return ValueTask.CompletedTask;
    }

    private async Task EnforceCacheLimitAsync(CancellationToken cancellationToken, bool throwWhenOverLimit = true)
    {
        if (!Directory.Exists(_cacheRoot)) return;
        var files = Directory.EnumerateFiles(_cacheRoot, "*.m4a", SearchOption.TopDirectoryOnly)
            .Where(path => IsOwnedCacheFile(Path.GetFileName(path)))
            .Where(path => !Path.GetFileName(path).Contains(PartialMarker, StringComparison.Ordinal))
            .Select(path => (Path: path, Entry: _entries.GetOrAdd(path, static file => new CacheEntry(file)), Length: FileLength(path)))
            .Where(item => item.Length > 0)
            .ToList();
        var total = files.Sum(file => file.Length);
        foreach (var file in files.OrderBy(item => item.Entry.LastAccessUtc ?? TryGetLastWriteTime(item.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total <= _options.MaxCacheBytes) break;
            lock (file.Entry.Sync)
            {
                if (file.Entry.ActiveLeases != 0) continue;
                if (TryDelete(file.Path))
                {
                    total -= file.Length;
                    _entries.TryRemove(file.Path, out _);
                }
            }
            await Task.Yield();
        }

        if (throwWhenOverLimit && total > _options.MaxCacheBytes)
            throw new IOException("Temporary playback cache is at capacity while existing streams are active.");
    }

    private bool IsValidCachedFile(string path)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        return info.Length > 0 && info.Length <= _options.MaxEntryBytes;
    }

    private static SourceStamp GetSourceStamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Source audio is missing.", path);
        return new SourceStamp(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private string CreateCacheKey(Guid trackId, string sourcePath, string sourceIdentity, SourceStamp stamp)
    {
        var material = string.Join('\n', trackId.ToString("N"), sourceIdentity, sourcePath,
            stamp.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            stamp.LastWriteTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "aac", _options.AacBitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static DateTimeOffset? TryGetLastWriteTime(string path)
    {
        try { return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove temporary playback file {FileName}.", Path.GetFileName(path));
            return false;
        }
    }

    private static void ValidateOptions(TemporaryPlaybackOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CacheDirectory)) throw new InvalidOperationException("TemporaryPlayback:CacheDirectory is required.");
        if (options.AacBitrateKbps is < 96 or > 320) throw new InvalidOperationException("TemporaryPlayback:AacBitrateKbps must be between 96 and 320.");
        if (options.MaxEntryBytes <= 0 || options.MaxCacheBytes < options.MaxEntryBytes) throw new InvalidOperationException("TemporaryPlayback cache size limits are invalid.");
        if (options.IdleMinutes <= 0 || options.CleanupIntervalMinutes <= 0 || options.MaxConcurrentTranscodes <= 0)
            throw new InvalidOperationException("TemporaryPlayback retention and concurrency options must be positive.");
    }

    private static void EnsureOutsideStorage(string cacheRoot, string configuredStorageRoot, string contentRoot, string kind)
    {
        if (string.IsNullOrWhiteSpace(configuredStorageRoot)) return;
        var storageRoot = Path.GetFullPath(configuredStorageRoot, contentRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsWithin(cacheRoot, storageRoot) || IsWithin(storageRoot, cacheRoot))
            throw new InvalidOperationException($"Temporary playback cache must not overlap the {kind} storage root.");
    }

    private static bool IsWithin(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(candidate, root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsOwnedCacheFile(string name)
    {
        if (name.Length < 68 || name[..64].Any(character => !char.IsAsciiHexDigit(character))) return false;
        return string.Equals(name[64..], ".m4a", StringComparison.Ordinal)
            || name[64..].StartsWith(PartialMarker, StringComparison.Ordinal) && name.EndsWith(".m4a", StringComparison.Ordinal);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Temporary playback cache cannot use a symbolic link or junction directory.");
    }

    private sealed class CacheEntry(string path)
    {
        public string Path { get; } = path;
        public object Sync { get; } = new();
        public int ActiveLeases { get; set; }
        public DateTimeOffset? LastAccessUtc { get; set; }
    }

    private readonly record struct SourceStamp(long Length, long LastWriteTicks);

    private sealed class KeyLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
    }

    public void Dispose()
    {
        _transcodeSlots.Dispose();
        lock (_keyLockSync)
        {
            foreach (var keyLock in _keyLocks.Values) keyLock.Gate.Dispose();
            _keyLocks.Clear();
        }
    }
}

/// <summary>Periodically expires stale temporary renditions without touching permanent library assets.</summary>
public sealed class TemporaryPlaybackCleanupService(
    TemporaryPlaybackService playback,
    IOptions<TemporaryPlaybackOptions> options,
    ILogger<TemporaryPlaybackCleanupService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await playback.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.CleanupIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await playback.CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Temporary playback cache cleanup failed."); }
        }
    }
}
