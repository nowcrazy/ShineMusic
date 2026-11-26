using System.Diagnostics;

namespace ShineMusic.Service.PlaylistImport;

public sealed partial class PlaylistImportService
{
    // Pending rows have sixteen persisted columns. Keep batches below even a
    // SQLite provider's conservative 999-parameter limit as well as PostgreSQL's.
    private const int PlaylistWriteBatchSize = 50;

    private void RecordPlaylistTransaction(string operation, Stopwatch stopwatch, int trackCount)
    {
        stopwatch.Stop();
        try
        {
            logger.LogDebug("Playlist {Operation} transaction completed in {TransactionElapsedMilliseconds} ms for {TrackCount} input rows",
                operation, stopwatch.Elapsed.TotalMilliseconds, trackCount);
        }
        catch
        {
            // Diagnostics cannot turn a committed import into a reported failure.
        }
    }

    private async Task InsertPlaylistRowsAsync<T>(IEnumerable<T> rows, CancellationToken cancellationToken)
        where T : class, new()
    {
        foreach (var batch in rows.Chunk(PlaylistWriteBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await db.Insertable(batch).ExecuteCommandAsync(cancellationToken);
        }
    }
}
