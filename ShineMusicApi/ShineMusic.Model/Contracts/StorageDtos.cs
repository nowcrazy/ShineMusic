// 存储治理相关对外契约：曲库磁盘对账（审计）、孤儿文件清理与曲目物理删除的结果 DTO。
// 治理对象是"数据库引用 vs 磁盘实体"的一致性：数据库（MediaAsset/Artwork 的 StorageKey）是唯一事实来源，
// 磁盘上的孤儿文件（无任何数据库行引用）可安全回收；缺失文件（库有行但磁盘无文件）只报告不自动处理。
namespace ShineMusic.Model.Contracts;

/// <summary>
/// 存储审计结果：曲库根目录下磁盘文件与数据库引用的对账明细。
/// </summary>
/// <param name="ReferencedFileCount">数据库引用的存储键数量（MediaAsset + Artwork）。</param>
/// <param name="DiskFileCount">磁盘上实际存在的文件数量（曲库根目录内）。</param>
/// <param name="MissingFileCount">缺失文件数：数据库有引用但磁盘不存在（通常是误删或磁盘故障，只报告不自动修复）。</param>
/// <param name="OrphanFileCount">孤儿文件数：磁盘存在但数据库无任何引用（可安全清理）。</param>
/// <param name="ReclaimableBytes">孤儿文件合计可回收字节数。</param>
/// <param name="EmptyDirectoryCount">空目录数量（无文件且无子目录，通常为删除残留）。</param>
/// <param name="Orphans">孤儿文件明细列表。</param>
/// <param name="Missing">缺失文件明细列表（含存储键，便于人工排查）。</param>
/// <param name="EmptyDirectories">空目录相对路径列表（含末尾斜杠的存储键形式）。</param>
public sealed record StorageAuditDto(
    int ReferencedFileCount,
    int DiskFileCount,
    int MissingFileCount,
    int OrphanFileCount,
    long ReclaimableBytes,
    int EmptyDirectoryCount,
    IReadOnlyList<StorageFileRefDto> Orphans,
    IReadOnlyList<StorageFileRefDto> Missing,
    IReadOnlyList<string> EmptyDirectories);

/// <summary>
/// 单个磁盘文件的引用信息（相对曲库根目录的存储键与大小）。
/// </summary>
/// <param name="StorageKey">相对存储键，形如 original/{trackId}/{assetId}.ext 或 artwork/{artworkId}.jpg。</param>
/// <param name="SizeBytes">文件大小（字节）。</param>
public sealed record StorageFileRefDto(string StorageKey, long SizeBytes);

/// <summary>
/// 孤儿文件清理的执行结果。
/// </summary>
/// <param name="DeletedOrphanCount">实际删除的孤儿文件数量。</param>
/// <param name="DeletedBytes">实际删除释放的字节数。</param>
/// <param name="RemovedEmptyDirectoryCount">连带移除的空目录数量（孤儿删除后递归发现的）。</param>
public sealed record StorageCleanupDto(int DeletedOrphanCount, long DeletedBytes, int RemovedEmptyDirectoryCount);

/// <summary>
/// 曲目物理删除的执行结果：删除数据库行并同步清理磁盘文件。
/// </summary>
/// <param name="TrackId">被删除曲目的标识。</param>
/// <param name="Title">被删除曲目的标题（删除前快照，便于审计追溯）。</param>
/// <param name="DeletedFileCount">实际删除的磁盘文件数量（媒体资产 + 可能连带删除的封面）。</param>
/// <param name="DeletedBytes">实际删除释放的字节数。</param>
/// <param name="ArtworkDeleted">封面是否随曲目一并删除（数据库行与文件；被其他曲目/专辑/精选集共享的封面会保留）。</param>
public sealed record TrackDeleteResultDto(Guid TrackId, string Title, int DeletedFileCount, long DeletedBytes, bool ArtworkDeleted);
