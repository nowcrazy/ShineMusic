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

using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using SqlSugar;

namespace ShineMusic.Service;

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
    /// 对账审计：汇总数据库引用的存储键与磁盘实际文件，输出孤儿/缺失/空目录明细，
    /// 并按曲目分组输出库内文件（音频 + 封面）供治理页关联查看。审计只读，不修改任何状态。
    /// </summary>
    /// <param name="db">SqlSugar 客户端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<StorageAuditDto> AuditAsync(ISqlSugarClient db, CancellationToken cancellationToken)
    {
        // Windows 文件系统不区分大小写、POSIX 区分，与 LibraryStoragePaths 的判定保持一致。
        var comparer = StringComparer.FromComparison(
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        var diskFiles = EnumerateDiskFiles();
        var diskSet = new HashSet<string>(diskFiles.Select(file => file.Key), comparer);
        var diskSizeByKey = new Dictionary<string, long>(comparer);
        foreach (var file in diskFiles)
            diskSizeByKey[file.Key] = file.SizeBytes;

        // 一次取齐对账与分组所需的全部行（曲库量级小，内存聚合换取清晰的分组结构）。
        // 数据库引用集：正式曲库（LibraryRoot）下的文件只可能被 MediaAsset 与 Artwork 引用；
        // LibraryImportItem 的三个存储键都指向隔离区（QuarantineRoot），不参与本审计。
        var assets = await db.Queryable<MediaAsset>().ToListAsync(cancellationToken);
        var artworks = await db.Queryable<Artwork>().ToListAsync(cancellationToken);
        var tracks = await db.Queryable<Track>().ToListAsync(cancellationToken);
        var albums = await db.Queryable<Album>().ToListAsync(cancellationToken);
        var collections = await db.Queryable<EditorialCollection>().ToListAsync(cancellationToken);
        // 曲目-艺术家关联（左连接取名字），供分组标题展示。
        var artistRows = await db.Queryable<TrackArtist>()
            .LeftJoin<Artist>((link, artist) => link.ArtistId == artist.Id)
            .Select((link, artist) => new { link.TrackId, ArtistName = artist.Name })
            .ToListAsync(cancellationToken);

        var referencedKeys = new List<string>(assets.Count + artworks.Count);
        referencedKeys.AddRange(assets.Select(asset => asset.StorageKey));
        referencedKeys.AddRange(artworks.Select(artwork => artwork.StorageKey));
        var referencedSet = new HashSet<string>(referencedKeys, comparer);

        // 孤儿：磁盘存在但无任何数据库行引用（如软删曲目留下的残留）。
        var orphans = diskFiles
            .Where(file => !referencedSet.Contains(file.Key))
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => new StorageOrphanFileDto(file.Key, file.SizeBytes, StorageFileKinds.Classify(file.Key)))
            .ToList();

        // 缺失：库有引用但磁盘无文件（通常为误删/磁盘故障，只报告不自动修复）；附归属引用说明便于排查。
        var trackById = tracks.ToDictionary(track => track.Id);
        var assetByKey = assets
            .GroupBy(asset => asset.StorageKey, comparer)
            .ToDictionary(group => group.Key, group => group.First(), comparer);
        var artworkByKey = artworks
            .GroupBy(artwork => artwork.StorageKey, comparer)
            .ToDictionary(group => group.Key, group => group.First(), comparer);
        var missing = referencedSet
            .Where(key => !diskSet.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new StorageMissingFileDto(
                key,
                StorageFileKinds.Classify(key),
                DescribeKeyOwners(key, assetByKey, artworkByKey, trackById, tracks, albums, collections)))
            .ToList();

        // 空目录：无文件且无子目录，通常是删除曲目后的残留。
        var emptyDirectories = EnumerateEmptyDirectories();

        // 按曲目分组的库内文件明细（音频 + 封面关联在一起），末尾附「其他引用」杂项组。
        var artistsByTrack = artistRows
            .GroupBy(row => row.TrackId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.ArtistName).Where(name => !string.IsNullOrEmpty(name)).Cast<string>().ToList());
        var groups = BuildTrackGroups(tracks, albums, collections, assets, artworks, artistsByTrack, diskSet, diskSizeByKey);

        return new StorageAuditDto(
            referencedKeys.Count,
            diskFiles.Count,
            missing.Count,
            orphans.Count,
            orphans.Sum(orphan => orphan.SizeBytes),
            emptyDirectories.Count,
            orphans,
            missing,
            emptyDirectories,
            groups);
    }

    /// <summary>
    /// 回收审计发现的孤儿文件，并递归清理因此变空的目录。
    /// 幂等：重复调用在无孤儿时返回全 0 结果。
    /// </summary>
    public async Task<StorageCleanupDto> CleanupAsync(ISqlSugarClient db, CancellationToken cancellationToken)
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
    public async Task<TrackDeleteResultDto?> DeleteTrackAsync(ISqlSugarClient db, Guid trackId, CancellationToken cancellationToken)
    {
        var track = await db.Queryable<Track>().FirstAsync(item => item.Id == trackId, cancellationToken);
        if (track is null)
            return null;
        // SqlSugar 不做 Include：媒体资产与封面各取一次。
        var trackAssets = await db.Queryable<MediaAsset>()
            .Where(asset => asset.TrackId == trackId)
            .ToListAsync(cancellationToken);
        var trackArtwork = track.ArtworkId.HasValue
            ? await db.Queryable<Artwork>().FirstAsync(artwork => artwork.Id == track.ArtworkId.Value, cancellationToken)
            : null;

        // 收集待删磁盘文件：曲目下所有媒体资产（原文件与转码产物均记录在 MediaAssets）。
        var filePaths = new List<string>();
        foreach (var asset in trackAssets)
        {
            if (_libraryPaths.TryResolve(asset.StorageKey, out var path))
                filePaths.Add(path);
        }

        // 封面可能被其他曲目、专辑或编辑精选集合共享（引用语义为 SetNull），
        // 仅当没有任何其他引用时才删除封面：文件与数据库行一并清理，
        // 避免残留无引用行（其文件已删会在审计中显示为 missing）。
        var artworkDeleted = false;
        if (trackArtwork is not null)
        {
            var sharedByOtherTrack = await db.Queryable<Track>()
                .AnyAsync(item => item.Id != trackId && item.ArtworkId == track.ArtworkId, cancellationToken);
            var sharedByAlbum = await db.Queryable<Album>()
                .AnyAsync(item => item.ArtworkId == track.ArtworkId, cancellationToken);
            var sharedByCollection = await db.Queryable<EditorialCollection>()
                .AnyAsync(item => item.ArtworkId == track.ArtworkId, cancellationToken);
            if (!sharedByOtherTrack && !sharedByAlbum && !sharedByCollection &&
                _libraryPaths.TryResolve(trackArtwork.StorageKey, out var artworkPath))
            {
                filePaths.Add(artworkPath);
                artworkDeleted = true;
            }
        }

        // SqlSugar CodeFirst 不建外键（无数据库级联），故显式清理全部子行：
        // 无 TrackId 外键的用户互动行（Favorites/UserPlaylistTracks/UserTrackFeedbacks/
        // DailyRecommendationItems，不清理会留下悬挂数据），以及原先由 EF 级联处理的
        // MediaAssets/Lyrics/UserLibraryItems/TrackArtist/TrackGenre/CollectionTrack。
        // （ListeningEvents 无外键但作为历史分析日志保留；SongRequestRecord.MatchedTrackId 悬挂无害。）
        var commentIds = await db.Queryable<TrackComment>()
            .Where(comment => comment.TrackId == trackId)
            .Select(comment => comment.Id)
            .ToListAsync(cancellationToken);

        // 数据库删除（子行 → 曲目 → 封面）同事务提交，等价原先单次 SaveChanges 的原子性。
        await db.Ado.BeginTranAsync();
        try
        {
            if (commentIds.Count > 0)
            {
                await db.Deleteable<TrackCommentLike>()
                    .Where(like => commentIds.Contains(like.CommentId))
                    .ExecuteCommandAsync(cancellationToken);
                await db.Deleteable<TrackComment>()
                    .Where(comment => comment.TrackId == trackId)
                    .ExecuteCommandAsync(cancellationToken);
            }
            await db.Deleteable<Favorite>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<UserPlaylistTrack>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<UserTrackFeedback>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<DailyRecommendationItem>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<MediaAsset>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<Lyric>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<UserLibraryItem>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<TrackArtist>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<TrackGenre>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<CollectionTrack>().Where(item => item.TrackId == trackId).ExecuteCommandAsync(cancellationToken);
            await db.Deleteable<Track>().Where(item => item.Id == trackId).ExecuteCommandAsync(cancellationToken);
            if (artworkDeleted && trackArtwork is not null)
                await db.Deleteable<Artwork>().Where(item => item.Id == trackArtwork.Id).ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

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

    /// <summary>汇总缺失存储键的归属引用说明（哪些对象在引用它），供治理页排查。</summary>
    private static IReadOnlyList<string> DescribeKeyOwners(
        string key,
        IReadOnlyDictionary<string, MediaAsset> assetByKey,
        IReadOnlyDictionary<string, Artwork> artworkByKey,
        IReadOnlyDictionary<Guid, Track> trackById,
        List<Track> tracks,
        List<Album> albums,
        List<EditorialCollection> collections)
    {
        var owners = new List<string>();
        if (assetByKey.TryGetValue(key, out var asset))
            owners.Add(trackById.TryGetValue(asset.TrackId, out var track) ? $"曲目《{track.Title}》音频" : "曲目音频（曲目已删除）");
        if (artworkByKey.TryGetValue(key, out var artwork))
            owners.AddRange(DescribeArtworkOwners(artwork, tracks, albums, collections));
        if (owners.Count == 0)
            owners.Add("未知引用");
        return owners;
    }

    /// <summary>列出一封封面记录的全部引用方（曲目/专辑/精选集）；无引用时返回悬挂说明。</summary>
    private static List<string> DescribeArtworkOwners(
        Artwork artwork,
        List<Track> tracks,
        List<Album> albums,
        List<EditorialCollection> collections)
    {
        var owners = new List<string>();
        owners.AddRange(tracks.Where(item => item.ArtworkId == artwork.Id).Select(item => $"曲目《{item.Title}》封面"));
        owners.AddRange(albums.Where(item => item.ArtworkId == artwork.Id).Select(item => $"专辑《{item.Title}》封面"));
        owners.AddRange(collections.Where(item => item.ArtworkId == artwork.Id).Select(item => $"精选集《{item.Title}》封面"));
        if (owners.Count == 0)
            owners.Add("封面记录（未被任何对象引用）");
        return owners;
    }

    /// <summary>
    /// 按曲目聚合库内文件：每首曲目的媒体资产（主资产在前）与封面放在同一组；
    /// 未被任何曲目认领的封面（仅专辑/精选集引用或悬挂记录）归入末尾的「其他引用」组。
    /// </summary>
    private static List<StorageFileGroupDto> BuildTrackGroups(
        List<Track> tracks,
        List<Album> albums,
        List<EditorialCollection> collections,
        List<MediaAsset> assets,
        List<Artwork> artworks,
        IReadOnlyDictionary<Guid, List<string>> artistsByTrack,
        HashSet<string> diskSet,
        IReadOnlyDictionary<string, long> diskSizeByKey)
    {
        var albumTitleById = albums.ToDictionary(album => album.Id, album => album.Title);
        var artworkById = artworks.ToDictionary(artwork => artwork.Id);
        var assetsByTrack = assets.GroupBy(asset => asset.TrackId).ToDictionary(group => group.Key, group => group.ToList());
        // 封面只认领一次：被多个对象共享的封面归入（按标题序）首个引用它的曲目组，避免分组重复计数。
        var artworkClaimed = new HashSet<Guid>();
        var groups = new List<StorageFileGroupDto>();

        foreach (var track in tracks.OrderBy(track => track.Title, StringComparer.Ordinal))
        {
            var files = new List<StorageGroupFileDto>();
            if (assetsByTrack.TryGetValue(track.Id, out var trackAssets))
            {
                foreach (var asset in trackAssets
                    .OrderByDescending(asset => asset.IsPrimary)
                    .ThenBy(asset => asset.StorageKey, StringComparer.Ordinal))
                {
                    var onDisk = diskSet.Contains(asset.StorageKey);
                    var detail = $"{asset.Container} · {asset.Codec}";
                    if (asset.BitrateKbps is > 0)
                        detail += $" · {asset.BitrateKbps}kbps";
                    files.Add(new StorageGroupFileDto(
                        asset.StorageKey,
                        onDisk ? diskSizeByKey.GetValueOrDefault(asset.StorageKey) : 0,
                        "audio",
                        asset.IsPrimary ? "primary" : "variant",
                        detail,
                        onDisk,
                        asset.IsPublished));
                }
            }
            if (track.ArtworkId is { } artworkId
                && artworkById.TryGetValue(artworkId, out var artwork)
                && artworkClaimed.Add(artworkId))
            {
                var onDisk = diskSet.Contains(artwork.StorageKey);
                var referenceCount = tracks.Count(candidate => candidate.ArtworkId == artworkId)
                    + albums.Count(album => album.ArtworkId == artworkId)
                    + collections.Count(collection => collection.ArtworkId == artworkId);
                files.Add(new StorageGroupFileDto(
                    artwork.StorageKey,
                    onDisk ? diskSizeByKey.GetValueOrDefault(artwork.StorageKey) : 0,
                    "artwork",
                    "artwork",
                    referenceCount > 1 ? $"{artwork.ContentType} · 共享封面" : artwork.ContentType,
                    onDisk,
                    null));
            }
            groups.Add(new StorageFileGroupDto(
                track.Id,
                track.Title,
                artistsByTrack.TryGetValue(track.Id, out var names) && names.Count > 0 ? string.Join(" / ", names) : null,
                track.AlbumId is { } albumId && albumTitleById.TryGetValue(albumId, out var albumTitle) ? albumTitle : null,
                track.DurationMs,
                track.PublishedAt.HasValue,
                files.Sum(file => file.SizeBytes),
                files));
        }

        // 「其他引用」杂项组：未被曲目认领的封面（仅专辑/精选集引用，或悬挂的封面记录）。
        var otherFiles = artworks
            .Where(artwork => !artworkClaimed.Contains(artwork.Id))
            .OrderBy(artwork => artwork.StorageKey, StringComparer.Ordinal)
            .Select(artwork =>
            {
                var onDisk = diskSet.Contains(artwork.StorageKey);
                return new StorageGroupFileDto(
                    artwork.StorageKey,
                    onDisk ? diskSizeByKey.GetValueOrDefault(artwork.StorageKey) : 0,
                    "artwork",
                    "artwork",
                    string.Join("；", DescribeArtworkOwners(artwork, tracks, albums, collections)),
                    onDisk,
                    null);
            })
            .ToList();
        if (otherFiles.Count > 0)
        {
            groups.Add(new StorageFileGroupDto(
                null,
                "其他引用",
                "专辑/精选集封面等",
                null,
                null,
                true,
                otherFiles.Sum(file => file.SizeBytes),
                otherFiles));
        }
        return groups;
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
            if (key.StartsWith(UserAvatarFiles.DirectoryKey, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(UserThemeFiles.DirectoryKey, StringComparison.OrdinalIgnoreCase))
                continue; // 用户头像/主题壁纸目录：不参与曲库存储对账
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

/// <summary>
/// 存储文件类型推断与预览 Content-Type 映射：按扩展名把存储键分为 音频/封面/其他 三类，
/// 并为治理页文件预览端点提供浏览器可识别的 MIME 类型。
/// </summary>
public static class StorageFileKinds
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "flac", "wav", "m4a", "aac", "ogg", "opus", "wma", "aiff", "ape" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "webp", "gif", "bmp", "svg", "avif" };

    /// <summary>按扩展名分类存储键：audio / artwork / other。</summary>
    public static string Classify(string storageKey)
    {
        var extension = Path.GetExtension(storageKey);
        if (extension.Length <= 1)
            return "other";
        var ext = extension[1..];
        if (AudioExtensions.Contains(ext))
            return "audio";
        if (ImageExtensions.Contains(ext))
            return "artwork";
        return "other";
    }

    /// <summary>预览端点的响应 Content-Type：图片/音频给出具体 MIME，其余按二进制流处理。</summary>
    public static string ContentTypeFor(string storageKey)
    {
        var extension = Path.GetExtension(storageKey);
        if (extension.Length <= 1)
            return "application/octet-stream";
        return extension[1..].ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "svg" => "image/svg+xml",
            "avif" => "image/avif",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "m4a" => "audio/mp4",
            "aac" => "audio/aac",
            "ogg" or "opus" => "audio/ogg",
            "wma" => "audio/x-ms-wma",
            _ => "application/octet-stream",
        };
    }
}
