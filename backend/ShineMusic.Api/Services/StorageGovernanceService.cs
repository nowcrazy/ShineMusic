// 职责：曲库存储治理服务。
// 数据库是存储的唯一事实来源：MediaAsset/Artwork 的 StorageKey 指向磁盘文件。
// 本服务围绕"数据库引用 vs 磁盘实体"的一致性提供三类能力：
//   1. AuditAsync    —— 对账审计：找出孤儿文件（磁盘有、库无引用，可安全回收）与缺失文件（库有引用、磁盘无，只报告）；
//   2. CleanupAsync  —— 回收审计发现的孤儿文件，并递归清理因此变空的目录（DismissItem 软删不删文件是孤儿的常见来源）；
//   3. DeleteTrackAsync —— 物理删除曲目：先删数据库行（无外键的互动表显式清理，其余级联），再删磁盘文件。
// 所有磁盘路径一律经 LibraryStoragePaths.TryResolve 校验，杜绝路径穿越。
//
// 设计取舍：先删库后删盘。数据库删除成功而磁盘删除失败时，文件成为孤儿（可由 CleanupAsync 后续回收），
// 不会出现"库已删但文件还在被引用播放"的不一致；反之若先删盘后删库失败，数据库将引用不存在的文件，危害更大。

using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;

namespace ShineMusic.Api.Services;

/// <summary>
/// 存储治理服务：审计磁盘与数据库的一致性，回收孤儿文件，并在删除曲目时联动清理磁盘。
/// </summary>
public sealed class StorageGovernanceService
{
    private readonly LibraryStoragePaths _libraryPaths;
    private readonly ILogger<StorageGovernanceService> _logger;

    /// <summary>构造服务；路径解析器为单例无状态配置，logger 用于记录删除失败的告警。</summary>
    public StorageGovernanceService(LibraryStoragePaths libraryPaths, ILogger<StorageGovernanceService> logger)
    {
        _libraryPaths = libraryPaths;
        _logger = logger;
    }

    /// <summary>
    /// 对账审计：汇总数据库引用的存储键与磁盘实际文件，输出孤儿/缺失/空目录明细。
    /// 审计只读，不修改任何状态。
    /// </summary>
    /// <param name="db">当前请求的数据库上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<StorageAuditDto> AuditAsync(MusicDbContext db, CancellationToken cancellationToken)
    {
        // 数据库引用集：正式曲库（LibraryRoot）下的文件只可能被 MediaAsset 与 Artwork 引用；
        // LibraryImportItem 的三个存储键都指向隔离区（QuarantineRoot），不参与本审计。
        var referencedKeys = await CollectReferencedKeysAsync(db, cancellationToken);
        var diskFiles = EnumerateDiskFiles();

        // Windows 文件系统不区分大小写、POSIX 区分，与 LibraryStoragePaths 的判定保持一致。
        var comparer = StringComparer.FromComparison(
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var referencedSet = new HashSet<string>(referencedKeys, comparer);
        var diskSet = new HashSet<string>(diskFiles.Select(file => file.Key), comparer);

        // 孤儿：磁盘存在但无任何数据库行引用（如软删曲目留下的残留）。
        var orphans = diskFiles
            .Where(file => !referencedSet.Contains(file.Key))
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => new StorageFileRefDto(file.Key, file.SizeBytes))
            .ToList();
        // 缺失：库有引用但磁盘无文件（通常为误删/磁盘故障，只报告不自动修复）。
        var missing = referencedKeys
            .Where(key => !diskSet.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new StorageFileRefDto(key, 0))
            .ToList();
        // 空目录：无文件且无子目录，通常是删除曲目后的残留。
        var emptyDirectories = EnumerateEmptyDirectories();

        return new StorageAuditDto(
            referencedKeys.Count,
            diskFiles.Count,
            missing.Count,
            orphans.Count,
            orphans.Sum(orphan => orphan.SizeBytes),
            emptyDirectories.Count,
            orphans,
            missing,
            emptyDirectories);
    }

    /// <summary>
    /// 回收审计发现的孤儿文件，并递归清理因此变空的目录。
    /// 幂等：重复调用在无孤儿时返回全 0 结果。
    /// </summary>
    public async Task<StorageCleanupDto> CleanupAsync(MusicDbContext db, CancellationToken cancellationToken)
    {
        var audit = await AuditAsync(db, cancellationToken);
        var deletedCount = 0;
        var deletedBytes = 0L;
        foreach (var orphan in audit.Orphans)
        {
            // 路径一律经 TryResolve 校验，防止异常存储键逃逸出曲库根目录。
            if (!_libraryPaths.TryResolve(orphan.StorageKey, out var path))
            {
                _logger.LogWarning("Storage cleanup skipped unresolvable key {StorageKey}.", orphan.StorageKey);
                continue;
            }
            try
            {
                if (!File.Exists(path))
                    continue;
                File.Delete(path);
                deletedCount++;
                deletedBytes += orphan.SizeBytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 单个文件删除失败不中断整体清理（如被其他进程占用），留待下次执行。
                _logger.LogWarning("Storage cleanup skipped {StorageKey}: {Message}", orphan.StorageKey, exception.Message);
            }
        }

        // 孤儿删除后目录可能变空，重新扫描并清理（删掉子目录会让父目录变空，循环直到稳定）。
        var removedDirectories = RemoveEmptyDirectories();
        return new StorageCleanupDto(deletedCount, deletedBytes, removedDirectories);
    }

    /// <summary>
    /// 物理删除曲目：数据库行与磁盘文件一并清理。
    /// 返回 null 表示曲目不存在。
    /// </summary>
    public async Task<TrackDeleteResultDto?> DeleteTrackAsync(MusicDbContext db, Guid trackId, CancellationToken cancellationToken)
    {
        var track = await db.Tracks
            .Include(item => item.MediaAssets)
            .Include(item => item.Artwork)
            .FirstOrDefaultAsync(item => item.Id == trackId, cancellationToken);
        if (track is null)
            return null;

        // 收集待删磁盘文件：曲目下所有媒体资产（原文件与转码产物均记录在 MediaAssets）。
        var filePaths = new List<string>();
        foreach (var asset in track.MediaAssets)
        {
            if (_libraryPaths.TryResolve(asset.StorageKey, out var path))
                filePaths.Add(path);
        }

        // 封面可能被其他曲目、专辑或编辑精选集合共享（引用语义为 SetNull），
        // 仅当没有任何其他引用时才删除封面：文件与数据库行一并清理，
        // 避免残留无引用行（其文件已删会在审计中显示为 missing）。
        var artworkDeleted = false;
        if (track.Artwork is not null)
        {
            var sharedByOtherTrack = await db.Tracks
                .AnyAsync(item => item.Id != trackId && item.ArtworkId == track.ArtworkId, cancellationToken);
            var sharedByAlbum = await db.Albums
                .AnyAsync(item => item.ArtworkId == track.ArtworkId, cancellationToken);
            var sharedByCollection = await db.EditorialCollections
                .AnyAsync(item => item.ArtworkId == track.ArtworkId, cancellationToken);
            if (!sharedByOtherTrack && !sharedByAlbum && !sharedByCollection &&
                _libraryPaths.TryResolve(track.Artwork.StorageKey, out var artworkPath))
            {
                filePaths.Add(artworkPath);
                db.Artworks.Remove(track.Artwork);
                artworkDeleted = true;
            }
        }

        // 显式清理无 TrackId 外键的用户互动行：Favorites/UserPlaylistTracks/UserTrackFeedbacks/
        // DailyRecommendationItems 的 TrackId 是普通列，删曲目不会级联，不清理会留下悬挂数据。
        // （ListeningEvents 无外键但作为历史分析日志保留；SongRequestRecord.MatchedTrackId 悬挂无害。）
        await db.Favorites.Where(item => item.TrackId == trackId).ExecuteDeleteAsync(cancellationToken);
        await db.UserPlaylistTracks.Where(item => item.TrackId == trackId).ExecuteDeleteAsync(cancellationToken);
        await db.UserTrackFeedbacks.Where(item => item.TrackId == trackId).ExecuteDeleteAsync(cancellationToken);
        await db.DailyRecommendationItems.Where(item => item.TrackId == trackId).ExecuteDeleteAsync(cancellationToken);

        // 删除曲目本体：MediaAssets/Lyrics/UserLibraryItems/TrackArtist/TrackGenre/CollectionTrack
        // 均配置了级联删除，随本行一并清除；Track→Artwork 为 SetNull，共享封面不受影响。
        db.Tracks.Remove(track);
        await db.SaveChangesAsync(cancellationToken);

        // 数据库删除成功后清理磁盘文件：先库后盘，库删失败不会误删文件。
        var deletedCount = 0;
        var deletedBytes = 0L;
        foreach (var path in filePaths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                var size = new FileInfo(path).Length;
                File.Delete(path);
                deletedCount++;
                deletedBytes += size;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Track file deletion skipped {Path}: {Message}", path, exception.Message);
            }
        }

        // 清理因此变空的目录（如 original/{trackId}/、playback/{trackId}/），
        // 未删掉的残留文件将留在审计的孤儿清单中，由 CleanupAsync 兜底。
        RemoveEmptyDirectories();

        return new TrackDeleteResultDto(trackId, track.Title, deletedCount, deletedBytes, artworkDeleted);
    }

    /// <summary>汇总数据库引用的全部存储键（MediaAsset + Artwork）。</summary>
    private static async Task<List<string>> CollectReferencedKeysAsync(MusicDbContext db, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        keys.AddRange(await db.MediaAssets.Select(asset => asset.StorageKey).ToListAsync(cancellationToken));
        keys.AddRange(await db.Artworks.Select(artwork => artwork.StorageKey).ToListAsync(cancellationToken));
        return keys;
    }

    /// <summary>枚举曲库根目录下的全部文件，返回（存储键, 字节数）列表；根目录不存在时返回空。</summary>
    /// <remarks>
    /// avatars/ 目录（用户头像）不属于曲库媒体资产、无数据库行引用，整体排除出对账，
    /// 否则头像文件会被误判为孤儿并在 CleanupAsync 中被回收。
    /// </remarks>
    private List<(string Key, long SizeBytes)> EnumerateDiskFiles()
    {
        var files = new List<(string, long)>();
        var root = _libraryPaths.Root;
        if (!Directory.Exists(root))
            return files;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var key = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (key.StartsWith(UserAvatarFiles.DirectoryKey, StringComparison.OrdinalIgnoreCase))
                continue; // 用户头像目录：不参与曲库存储对账
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // 枚举期间文件被并发删除时无法取大小，按 0 计入（不影响对账语义）。
                size = 0;
            }
            files.Add((key, size));
        }
        return files;
    }

    /// <summary>枚举曲库根目录下所有空目录（无文件且无子目录），返回存储键形式的相对路径。</summary>
    private List<string> EnumerateEmptyDirectories()
    {
        var emptyDirectories = new List<string>();
        var root = _libraryPaths.Root;
        if (!Directory.Exists(root))
            return emptyDirectories;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                emptyDirectories.Add(Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/'));
        }
        return emptyDirectories;
    }

    /// <summary>
    /// 循环删除空目录直到稳定：删掉子目录会让父目录变空，故需多轮；
    /// 若连续两轮空目录集合相同（说明删除全部失败，如权限问题），停止以避免死循环。
    /// </summary>
    private int RemoveEmptyDirectories()
    {
        var root = _libraryPaths.Root;
        if (!Directory.Exists(root))
            return 0;
        var removed = 0;
        var previous = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var current = new HashSet<string>(EnumerateEmptyDirectories(), StringComparer.Ordinal);
            if (current.Count == 0 || current.SetEquals(previous))
                break;
            foreach (var directoryKey in current)
            {
                if (!_libraryPaths.TryResolve(directoryKey, out var path))
                {
                    _logger.LogWarning("Empty directory cleanup skipped unresolvable key {DirectoryKey}.", directoryKey);
                    continue;
                }
                try
                {
                    Directory.Delete(path);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Empty directory cleanup skipped {DirectoryKey}: {Message}", directoryKey, exception.Message);
                }
            }
            previous = current;
        }
        return removed;
    }
}
