// 存储治理相关对外契约：曲库磁盘对账（审计）、孤儿文件清理与曲目物理删除的结果 DTO。
// 治理对象是"数据库引用 vs 磁盘实体"的一致性：数据库（MediaAsset/Artwork 的 StorageKey）是唯一事实来源，
// 磁盘上的孤儿文件（无任何数据库行引用）可安全回收；缺失文件（库有行但磁盘无文件）只报告不自动处理。
namespace ShineMusic.Model.Contracts;

/// <summary>
/// 存储审计结果：曲库根目录下磁盘文件与数据库引用的对账明细。
/// </summary>
/// <param name="ReferencedFileCount">数据库引用的存储键数量（MediaAsset + Artwork）。</param>
/// <param name="TrackCount">有文件引用的曲目数（每首对应原件/播放件/封面等多个文件，与文件数对照可消除歧义）。</param>
/// <param name="DiskFileCount">磁盘上实际存在的文件数量（曲库根目录内）。</param>
/// <param name="MissingFileCount">缺失文件数：数据库有引用但磁盘不存在（通常是误删或磁盘故障，只报告不自动修复）。</param>
/// <param name="OrphanFileCount">孤儿文件数：磁盘存在但数据库无任何引用（可安全清理）。</param>
/// <param name="ReclaimableBytes">孤儿文件合计可回收字节数。</param>
/// <param name="EmptyDirectoryCount">空目录数量（无文件且无子目录，通常为删除残留）。</param>
/// <param name="Orphans">孤儿文件明细列表（含类型分类，便于前端预览）。</param>
/// <param name="Missing">缺失文件明细列表（含存储键与归属引用说明，便于人工排查）。</param>
/// <param name="EmptyDirectories">空目录相对路径列表（含末尾斜杠的存储键形式）。</param>
/// <param name="Groups">按曲目聚合的库内文件明细（音频 + 封面），末尾可能附带「其他引用」杂项组。</param>
public sealed record StorageAuditDto(
    int ReferencedFileCount,
    int TrackCount,
    int DiskFileCount,
    int MissingFileCount,
    int OrphanFileCount,
    long ReclaimableBytes,
    int EmptyDirectoryCount,
    IReadOnlyList<StorageOrphanFileDto> Orphans,
    IReadOnlyList<StorageMissingFileDto> Missing,
    IReadOnlyList<string> EmptyDirectories,
    IReadOnlyList<StorageFileGroupDto> Groups);

/// <summary>
/// 单个磁盘文件的引用信息（相对曲库根目录的存储键与大小）。
/// </summary>
/// <param name="StorageKey">相对存储键，形如 original/{trackId}/{assetId}.ext 或 artwork/{artworkId}.jpg。</param>
/// <param name="SizeBytes">文件大小（字节）。</param>
public sealed record StorageFileRefDto(string StorageKey, long SizeBytes);

/// <summary>孤儿文件明细：存储键与大小之外附带类型分类（audio/artwork/other），便于前端决定是否可预览。</summary>
/// <param name="StorageKey">相对存储键。</param>
/// <param name="SizeBytes">文件大小（字节）。</param>
/// <param name="Kind">文件类别：audio（音频）/ artwork（封面图片）/ other（其他，按扩展名推断）。</param>
public sealed record StorageOrphanFileDto(string StorageKey, long SizeBytes, string Kind);

/// <summary>缺失文件明细：库有引用但磁盘不存在，附带归属引用说明（哪些曲目/专辑/精选集在引用它）。</summary>
/// <param name="StorageKey">相对存储键。</param>
/// <param name="Kind">文件类别：audio / artwork / other。</param>
/// <param name="ReferencedBy">人类可读的归属引用列表，如「曲目《xxx》音频」「专辑《yyy》封面」。</param>
public sealed record StorageMissingFileDto(string StorageKey, string Kind, IReadOnlyList<string> ReferencedBy);

/// <summary>
/// 按曲目聚合的文件分组：一首曲目的全部关联文件（媒体资产 + 封面）放在一起展示。
/// TrackId 为 null 时是「其他引用」杂项组（仅被专辑/精选集引用或悬挂的封面记录）。
/// </summary>
/// <param name="TrackId">曲目 Id；null 表示非曲目分组。</param>
/// <param name="Title">分组标题（曲目标题或「其他引用」）。</param>
/// <param name="Artists">艺术家名（多位的拼接，无则 null）。</param>
/// <param name="AlbumTitle">所属专辑标题（单曲为 null）。</param>
/// <param name="DurationMs">曲目时长（毫秒）。</param>
/// <param name="IsPublished">曲目是否已发布（上架）。</param>
/// <param name="TotalBytes">组内在盘文件合计字节数。</param>
/// <param name="Files">组内文件明细。</param>
public sealed record StorageFileGroupDto(
    Guid? TrackId,
    string Title,
    string? Artists,
    string? AlbumTitle,
    long? DurationMs,
    bool IsPublished,
    long TotalBytes,
    IReadOnlyList<StorageGroupFileDto> Files);

/// <summary>分组内的单个文件明细。</summary>
/// <param name="StorageKey">相对存储键。</param>
/// <param name="SizeBytes">在盘字节数；缺失时为 0。</param>
/// <param name="Kind">文件类别：audio / artwork。</param>
/// <param name="Role">文件角色：primary（主资产）/ variant（转码产物）/ artwork（封面）。</param>
/// <param name="Detail">人类可读补充：音频为「容器 · 编码 · 码率」，封面为 MIME/共享说明，其他引用组为归属说明。</param>
/// <param name="OnDisk">文件是否在磁盘上存在（false 即缺失文件）。</param>
/// <param name="IsPublished">媒体资产发布状态；封面行为 null。</param>
public sealed record StorageGroupFileDto(
    string StorageKey,
    long SizeBytes,
    string Kind,
    string Role,
    string? Detail,
    bool OnDisk,
    bool? IsPublished);

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
