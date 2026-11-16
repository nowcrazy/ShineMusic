namespace ShineMusic.Model.Contracts;

/// <summary>逐条保留来源与处理阶段，便于排查导入未完成原因。</summary>
public sealed record PlaylistImportProgressItemDto(
    int Position, string Title, string SourcePlatform, string? SourceTrackId, string? SourceUrl,
    string Status, Guid? DownloadTaskId, string? DownloadStatus, Guid? ImportItemId,
    string? ImportStatus, string? ErrorCode, string? ErrorMessage, string? SearchDiagnosticsJson);

/// <summary>原始快照与当前歌单计数；重复曲目合并后两者可能不同，快照不丢失。</summary>
public sealed record PlaylistImportProgressDto(Guid PlaylistId, int? OriginalCount, int LinkedCount,
    int PendingCount, string? OriginalSnapshotJson, IReadOnlyList<PlaylistImportProgressItemDto> Items);
