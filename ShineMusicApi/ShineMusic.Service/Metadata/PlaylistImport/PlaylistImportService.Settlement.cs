using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service.PlaylistImport;

public sealed partial class PlaylistImportService
{
    private const int SettlementReadBatchSize = 400;
    private const int SettlementWriteBatchSize = 100;

    private sealed class SettlementTaskSnapshot
    {
        public SettlementTaskSnapshot() { }
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ErrorCode { get; set; }
        public Guid? ImportItemId { get; set; }
    }

    private sealed class SettlementImportSnapshot
    {
        public SettlementImportSnapshot() { }
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public Guid? PublishedTrackId { get; set; }
    }

    private sealed record SettlementStatusChange(UserPlaylistPendingTrack Pending, string Status, bool ClearTask = false);
    private sealed record SettlementPromotion(UserPlaylistPendingTrack Pending, Guid TrackId);

    /// <summary>Settle one playlist using batched task/import snapshots and a single fallback matching pass.</summary>
    public async Task ResolvePendingAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        var pendings = await db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.PlaylistId == playlistId
                && (item.Status == PendingStatuses.Pending
                    || item.Status == PendingStatuses.Downloading
                    || item.Status == PendingStatuses.Failed
                    || item.Status == PendingStatuses.Rejected
                    || item.Status == PendingStatuses.NeedsReview))
            .OrderBy(item => item.SortOrder)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (pendings.Count == 0) return;

        var tasks = await LoadSettlementTasksAsync(pendings, cancellationToken);
        var imports = await LoadSettlementImportsAsync(tasks.Values, cancellationToken);
        var statuses = new List<SettlementStatusChange>();
        var promotions = new List<SettlementPromotion>();
        var duplicates = new List<UserPlaylistPendingTrack>();

        foreach (var pending in pendings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.DownloadTaskId is not { } taskId) continue;
            if (!tasks.TryGetValue(taskId, out var task))
            {
                if (pending.Status != PendingStatuses.Pending)
                    statuses.Add(new(pending, PendingStatuses.Pending));
                continue;
            }

            if (task.ImportItemId is { } importId && imports.TryGetValue(importId, out var import))
            {
                // A later manual publication also settles previously rejected/failed rows.
                if (import.PublishedTrackId is { } trackId)
                {
                    promotions.Add(new(pending, trackId));
                    continue;
                }
                var status = import.Status switch
                {
                    LibraryImportItemStatuses.IdentityRejected or LibraryImportItemStatuses.Rejected => PendingStatuses.Rejected,
                    LibraryImportItemStatuses.ProcessingFailed => PendingStatuses.Failed,
                    LibraryImportItemStatuses.ReadyToPublish or LibraryImportItemStatuses.PendingReview
                        or LibraryImportItemStatuses.PendingPublication => PendingStatuses.NeedsReview,
                    _ => null
                };
                if (status is not null)
                {
                    if (pending.Status != status) statuses.Add(new(pending, status));
                    continue;
                }
            }

            if (task.Status == ExternalDownloadTaskStatuses.Failed)
            {
                if (task.ErrorCode == ErrorCodes.SourceDuplicate) duplicates.Add(pending);
                else if (pending.Status != PendingStatuses.Failed) statuses.Add(new(pending, PendingStatuses.Failed));
            }
            else if (task.Status == ExternalDownloadTaskStatuses.Completed)
            {
                // A completed task without a published/import-review outcome cannot settle successfully.
                if (pending.Status != PendingStatuses.Failed) statuses.Add(new(pending, PendingStatuses.Failed));
            }
            else if (pending.Status != PendingStatuses.Downloading)
            {
                statuses.Add(new(pending, PendingStatuses.Downloading));
            }
        }

        if (duplicates.Count > 0)
        {
            var matched = await MatchLibraryBatchAsync(duplicates.Select(ToSettlementTrack).ToList(), cancellationToken);
            for (var index = 0; index < duplicates.Count; index++)
            {
                if (matched[index] is { } trackId) promotions.Add(new(duplicates[index], trackId));
                else if (duplicates[index].Status != PendingStatuses.Failed)
                    statuses.Add(new(duplicates[index], PendingStatuses.Failed));
            }
        }

        await ApplySettlementPromotionsAsync(playlistId, promotions, cancellationToken);
        await ApplySettlementStatusesAsync(statuses, cancellationToken);
    }

    /// <summary>Rematching performs no source-network work; unmatched rows return to the background queue.</summary>
    public async Task<PlaylistRematchResult> RematchPendingAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        if (!await db.Queryable<UserPlaylist>().AnyAsync(item => item.Id == playlistId, cancellationToken))
            throw new InvalidOperationException("歌单不存在。");
        await ResolvePendingAsync(playlistId, cancellationToken);
        var pendings = await db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.PlaylistId == playlistId)
            .OrderBy(item => item.SortOrder)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (pendings.Count == 0) return new(playlistId, 0, 0, 0, 0, 0);

        var tasks = await LoadSettlementTasksAsync(pendings, cancellationToken);
        var eligible = pendings.Where(pending => !(pending.Status == PendingStatuses.Downloading
            && pending.DownloadTaskId is { } taskId && tasks.TryGetValue(taskId, out var task)
            && (task.Status == ExternalDownloadTaskStatuses.Queued
                || task.Status == ExternalDownloadTaskStatuses.Downloading
                || task.Status == ExternalDownloadTaskStatuses.ImportQueued))).ToList();
        var matched = await MatchLibraryBatchAsync(eligible.Select(ToSettlementTrack).ToList(), cancellationToken);
        var promotions = new List<SettlementPromotion>();
        var statuses = new List<SettlementStatusChange>();
        for (var index = 0; index < eligible.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = eligible[index];
            if (matched[index] is { } trackId) promotions.Add(new(pending, trackId));
            else if (pending.Status != PendingStatuses.NeedsReview)
                statuses.Add(new(pending, PendingStatuses.Pending, ClearTask: true));
        }

        var promoted = await ApplySettlementPromotionsAsync(playlistId, promotions, cancellationToken);
        var retried = await ApplySettlementStatusesAsync(statuses, cancellationToken);
        // Snapshot conflicts are left untouched and must not count as successfully requeued/promoted.
        return new(playlistId, pendings.Count, promoted, retried, 0, pendings.Count - promoted - retried);
    }

    private static ParsedPlaylistTrack ToSettlementTrack(UserPlaylistPendingTrack pending) =>
        new(pending.Title, DeserializeArtists(pending.ArtistsJson), pending.AlbumTitle,
            pending.DurationMs, pending.SourceTrackId, pending.SourceUrl);

    private async Task<Dictionary<Guid, SettlementTaskSnapshot>> LoadSettlementTasksAsync(
        IReadOnlyList<UserPlaylistPendingTrack> pendings, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, SettlementTaskSnapshot>();
        foreach (var ids in pendings.Where(item => item.DownloadTaskId.HasValue)
                     .Select(item => item.DownloadTaskId!.Value).Distinct().Chunk(SettlementReadBatchSize))
        {
            var rows = await db.Queryable<ExternalDownloadTask>().Where(item => ids.Contains(item.Id))
                .Select(item => new SettlementTaskSnapshot
                {
                    Id = item.Id, Status = item.Status, ErrorCode = item.ErrorCode, ImportItemId = item.ImportItemId
                }).ToListAsync(cancellationToken);
            foreach (var row in rows) result[row.Id] = row;
        }
        return result;
    }

    private async Task<Dictionary<Guid, SettlementImportSnapshot>> LoadSettlementImportsAsync(
        IEnumerable<SettlementTaskSnapshot> tasks, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, SettlementImportSnapshot>();
        foreach (var ids in tasks.Where(item => item.ImportItemId.HasValue)
                     .Select(item => item.ImportItemId!.Value).Distinct().Chunk(SettlementReadBatchSize))
        {
            var rows = await db.Queryable<LibraryImportItem>().Where(item => ids.Contains(item.Id))
                .Select(item => new SettlementImportSnapshot
                {
                    Id = item.Id, Status = item.Status, PublishedTrackId = item.PublishedTrackId
                }).ToListAsync(cancellationToken);
            foreach (var row in rows) result[row.Id] = row;
        }
        return result;
    }

    private async Task<int> ApplySettlementStatusesAsync(
        IReadOnlyList<SettlementStatusChange> changes, CancellationToken cancellationToken)
    {
        var applied = 0;
        foreach (var group in changes.GroupBy(change => (change.Status, change.ClearTask)))
        foreach (var chunk in group.Chunk(SettlementWriteBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshots = Expressionable.Create<UserPlaylistPendingTrack>();
            foreach (var change in chunk)
            {
                var pending = change.Pending;
                snapshots.Or(item => item.Id == pending.Id && item.PlaylistId == pending.PlaylistId
                    && item.Status == pending.Status && item.DownloadTaskId == pending.DownloadTaskId
                    && item.UpdatedAtUtc == pending.UpdatedAtUtc);
            }
            var status = group.Key.Status;
            var updatedAt = DateTime.UtcNow;
            var update = db.Updateable<UserPlaylistPendingTrack>()
                .SetColumns(item => item.Status == status)
                .SetColumns(item => item.UpdatedAtUtc == updatedAt)
                .Where(snapshots.ToExpression());
            if (group.Key.ClearTask) update = update.SetColumns(item => item.DownloadTaskId == (Guid?)null);
            applied += await update.ExecuteCommandAsync(cancellationToken);
        }
        return applied;
    }

    /// <summary>
    /// Claim each snapshot by a fenced delete, then insert missing links together in the same transaction.
    /// The playlist-row write serializes settlers for different pending rows targeting the same track.
    /// </summary>
    private async Task<int> ApplySettlementPromotionsAsync(Guid playlistId,
        IReadOnlyList<SettlementPromotion> promotions, CancellationToken cancellationToken)
    {
        if (promotions.Count == 0) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        var ownsTransaction = db.Ado.Transaction is null;
        if (ownsTransaction) await db.Ado.BeginTranAsync();
        try
        {
            // No content or modification timestamp changes; PostgreSQL/SQLite hold the row write lock to commit.
            var playlistExists = await db.Updateable<UserPlaylist>()
                .SetColumns(item => item.UpdatedAtUtc == item.UpdatedAtUtc)
                .Where(item => item.Id == playlistId).ExecuteCommandAsync(cancellationToken);
            if (playlistExists == 0)
            {
                if (ownsTransaction) await db.Ado.CommitTranAsync();
                return 0; // A concurrently deleted playlist must never acquire orphan links.
            }
            var linked = new HashSet<Guid>();
            foreach (var ids in promotions.Select(item => item.TrackId).Distinct().Chunk(SettlementReadBatchSize))
            {
                var existing = await db.Queryable<UserPlaylistTrack>()
                    .Where(item => item.PlaylistId == playlistId && ids.Contains(item.TrackId))
                    .Select(item => item.TrackId).ToListAsync(cancellationToken);
                linked.UnionWith(existing);
            }
            var links = new List<UserPlaylistTrack>();
            var promoted = 0;
            foreach (var promotion in promotions.OrderBy(item => item.Pending.SortOrder).ThenBy(item => item.Pending.Id))
            {
                var pending = promotion.Pending;
                var deleted = await db.Deleteable<UserPlaylistPendingTrack>()
                    .Where(item => item.Id == pending.Id && item.PlaylistId == playlistId
                        && item.Status == pending.Status && item.DownloadTaskId == pending.DownloadTaskId
                        && item.UpdatedAtUtc == pending.UpdatedAtUtc)
                    .ExecuteCommandAsync(cancellationToken);
                if (deleted == 0) continue;
                promoted += deleted;
                if (linked.Add(promotion.TrackId))
                    links.Add(new UserPlaylistTrack
                    {
                        PlaylistId = playlistId, TrackId = promotion.TrackId,
                        SortOrder = pending.SortOrder, AddedAtUtc = DateTime.UtcNow
                    });
            }
            await InsertPlaylistRowsAsync(links, cancellationToken);
            if (ownsTransaction) await db.Ado.CommitTranAsync();
            return promoted;
        }
        catch
        {
            if (ownsTransaction) await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    /// <summary>Single-row compatibility for orphan handling in the background backfill loop.</summary>
    private async Task MarkPendingAsync(UserPlaylistPendingTrack pending, string status, CancellationToken cancellationToken)
    {
        if (pending.Status == status) return;
        var applied = await ApplySettlementStatusesAsync([new(pending, status)], cancellationToken);
        if (applied > 0) pending.Status = status;
    }
}
