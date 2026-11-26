using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>Locks an import row while a final transaction verifies its execution lease.</summary>
internal static class ImportExecutionGuard
{
    internal static async Task LockAsync(ISqlSugarClient db, LibraryImportItem item, Guid? attemptId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = db.Updateable<LibraryImportItem>()
            .SetColumns(row => row.UpdatedAtUtc == now)
            .Where(row => row.Id == item.Id && !row.IsDeleted);
        if (attemptId is { } ownedAttempt)
        {
            var workerId = item.WorkerId;
            if (string.IsNullOrEmpty(workerId)) throw new Media.MediaLeaseLostException("import", item.Id);
            query = query.Where(row => row.WorkerId == workerId && row.AttemptId == ownedAttempt
                && row.LeaseUntilUtc != null && row.LeaseUntilUtc > now);
        }
        else
        {
            // Manual publication may only consume an unowned ready item. A concurrent
            // worker, manual publisher or completed publication invalidates its snapshot.
            var snapshotAttempt = item.AttemptId;
            query = query.Where(row => row.WorkerId == null && row.AttemptId == snapshotAttempt
                && row.Status == LibraryImportItemStatuses.ReadyToPublish);
        }
        if (await query.ExecuteCommandAsync(ct) != 1)
            throw new Media.MediaLeaseLostException("import", item.Id);
    }

    internal static async Task SaveAsync(ISqlSugarClient db, LibraryImportItem item, Guid? attemptId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = db.Updateable(item)
            .IgnoreColumns(row => new { row.WorkerId, row.AttemptId, row.LeaseUntilUtc, row.LastHeartbeatAtUtc, row.IsDeleted })
            .Where(row => row.Id == item.Id && !row.IsDeleted);
        if (attemptId is { } ownedAttempt)
        {
            var workerId = item.WorkerId;
            if (string.IsNullOrEmpty(workerId)) throw new Media.MediaLeaseLostException("import", item.Id);
            query = query.Where(row => row.WorkerId == workerId && row.AttemptId == ownedAttempt
                && row.LeaseUntilUtc != null && row.LeaseUntilUtc > now);
        }
        else
        {
            var snapshotAttempt = item.AttemptId;
            query = query.Where(row => row.WorkerId == null && row.AttemptId == snapshotAttempt
                && row.Status == LibraryImportItemStatuses.ReadyToPublish);
        }
        if (await query.ExecuteCommandAsync(ct) != 1)
            throw new Media.MediaLeaseLostException("import", item.Id);
    }
}
