using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ShineMusic.Model.Entities;
using ShineMusic.Service.PlaylistImport;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class PlaylistBatchScaleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    [InlineData(1000)]
    public async Task Preview_import_and_enqueue_match_the_retained_legacy_reference_with_chunk_bounded_sql(int size)
    {
        using var f = PlaylistBatchTestData.Fixture();
        var titles = Enumerable.Range(0, size).Where(index => index % 2 == 0).Select(index => $"Catalog song {index}").ToArray();
        var catalog = PlaylistBatchTestData.SeedCatalog(f, titles);
        var parsed = new ParsedPlaylist("scale " + size, null, null,
            Enumerable.Range(0, size).Select(index => PlaylistBatchTestData.Input(
                index % 2 == 0 ? $"Catalog song {index}" : $"Unmatched song {index}", index.ToString(CultureInfo.InvariantCulture))).ToArray());
        var log = new TransactionLog();
        var service = PlaylistBatchTestData.Service(f, parsed, logger: log);

        // Warm matching/JIT/model paths without adding any playlist or queue rows.
        await service.ParsePreviewAsync(PlaylistBatchTestData.Url, Guid.Empty, default);
        await LegacyMatchAsync(f, parsed.Tracks[0]);
        var (legacyPreview, oldPreview) = await MeasureAsync(f, () => LegacyPreviewAsync(f, parsed), () => null);
        var (preview, newPreview) = await MeasureAsync(f,
            () => service.ParsePreviewAsync(PlaylistBatchTestData.Url, Guid.Empty, default), () => null);
        Assert.Equal(legacyPreview, preview.Tracks.Select(track => track.MatchedTrackId));
        Assert.Equal(size / 2, preview.Tracks.Count(track => track.Matched));

        double? oldImportTransaction = null;
        var (legacyImport, oldImport) = await MeasureAsync(f,
            () => LegacyImportAsync(f, parsed, milliseconds => oldImportTransaction = milliseconds), () => oldImportTransaction);
        log.LastTransactionMilliseconds = null;
        var (imported, newImport) = await MeasureAsync(f,
            () => service.ImportAsync(PlaylistBatchTestData.Url, null, Guid.Empty, false, default), () => log.LastTransactionMilliseconds);
        Assert.Equal((legacyImport.Total, legacyImport.Matched, legacyImport.Pending), (imported.Total, imported.Matched, imported.Pending));
        Assert.Equal(size / 2, f.Db.Queryable<UserPlaylistTrack>().Where(row => row.PlaylistId == imported.PlaylistId).Count());
        Assert.Equal(size / 2, f.Db.Queryable<UserPlaylistPendingTrack>().Where(row => row.PlaylistId == imported.PlaylistId).Count());
        Assert.Equal(
            f.Db.Queryable<UserPlaylistTrack>().Where(row => row.PlaylistId == legacyImport.PlaylistId).OrderBy(row => row.SortOrder).ToList().Select(row => (row.TrackId, row.SortOrder)),
            f.Db.Queryable<UserPlaylistTrack>().Where(row => row.PlaylistId == imported.PlaylistId).OrderBy(row => row.SortOrder).ToList().Select(row => (row.TrackId, row.SortOrder)));

        var (legacyQueue, oldQueue) = await MeasureAsync(f, () => LegacyEnqueueAsync(f, parsed), () => null);
        log.LastTransactionMilliseconds = null;
        var (queued, newQueue) = await MeasureAsync(f,
            () => service.EnqueueDownloadsAsync(PlaylistBatchTestData.Url, Guid.Empty, default), () => log.LastTransactionMilliseconds);
        Assert.Equal((legacyQueue.Total, legacyQueue.Matched, legacyQueue.Queued), (queued.Total, queued.Matched, queued.Queued));

        // These are structural SQL bounds, not timing thresholds. The title,
        // album and artist reads grow in 400-key chunks; writes use 50-row chunks.
        var matchChunks = (size + 399) / 400;
        foreach (var observation in new[] { newPreview, newImport, newQueue })
            Assert.InRange(observation.Selects, 1, matchChunks * 3 + 2);
        Assert.InRange(newImport.Inserts, 1, 1 + ((size / 2 + 49) / 50) * 2);
        Assert.InRange(newQueue.Inserts, 1, (size / 2 + 49) / 50);
        Assert.True(oldPreview.Selects >= size);
        Assert.True(oldImport.Selects >= size);
        Assert.True(oldQueue.Selects >= size);
        Assert.True(newPreview.Selects < oldPreview.Selects);
        Assert.True(newImport.Selects < oldImport.Selects);
        Assert.True(newQueue.Selects < oldQueue.Selects);
        Assert.Empty(f.Bus.Messages);
        Assert.Equal(0, f.Db.Queryable<ExternalDownloadTask>().Count());

        var legacySettlementId = SeedSettlement(f, catalog, size, "legacy settlement");
        var batchSettlementId = SeedSettlement(f, catalog, size, "batch settlement");
        var (_, oldSettlement) = await MeasureAsync(f, async () =>
        {
            await LegacyResolveFixedScenarioAsync(f, legacySettlementId);
            return true;
        }, () => null);
        log.LastTransactionMilliseconds = null;
        var (_, newSettlement) = await MeasureAsync(f, async () =>
        {
            await service.ResolvePendingAsync(batchSettlementId, default);
            return true;
        }, () => log.LastTransactionMilliseconds);
        var oldLinks = f.Db.Queryable<UserPlaylistTrack>().Where(row => row.PlaylistId == legacySettlementId)
            .OrderBy(row => row.SortOrder).ToList();
        var newLinks = f.Db.Queryable<UserPlaylistTrack>().Where(row => row.PlaylistId == batchSettlementId)
            .OrderBy(row => row.SortOrder).ToList();
        Assert.Equal(size / 2, newLinks.Count);
        Assert.Equal(oldLinks.Select(row => (row.TrackId, row.SortOrder)), newLinks.Select(row => (row.TrackId, row.SortOrder)));
        var oldPending = f.Db.Queryable<UserPlaylistPendingTrack>().Where(row => row.PlaylistId == legacySettlementId)
            .OrderBy(row => row.SortOrder).ToList();
        var newPending = f.Db.Queryable<UserPlaylistPendingTrack>().Where(row => row.PlaylistId == batchSettlementId)
            .OrderBy(row => row.SortOrder).ToList();
        Assert.Equal(size / 2, newPending.Count);
        Assert.Equal(oldPending.Select(row => (row.SortOrder, row.SourceTrackId, row.Status)),
            newPending.Select(row => (row.SortOrder, row.SourceTrackId, row.Status)));
        Assert.All(newPending, row => Assert.Equal("needs_review", row.Status));
        var settlementReadBound = 1 + 2 * ((size + 399) / 400) + (size / 2 + 399) / 400;
        var settlementInsertBound = (size / 2 + 49) / 50;
        var settlementUpdateBound = 1 + (size / 2 + 99) / 100;
        Assert.InRange(newSettlement.Selects, 1, settlementReadBound);
        Assert.InRange(newSettlement.Inserts, 1, settlementInsertBound);
        Assert.Equal(size / 2, newSettlement.Deletes); // Per-row fencing is intentional.
        Assert.InRange(newSettlement.Updates, 1, settlementUpdateBound);
        Assert.InRange(newSettlement.Commands, size / 2,
            settlementReadBound + settlementInsertBound + settlementUpdateBound + size / 2);
        Assert.True(oldSettlement.Selects >= size * 2);
        Assert.True(newSettlement.Selects < oldSettlement.Selects);
        Assert.True(newSettlement.Commands < oldSettlement.Commands);

        output.WriteLine($"SQLite synthetic size={size}, published matches={size / 2}, unmatched={size / 2}; one observation per workflow after matching warmup.");
        output.WriteLine("Legacy reference is the pre-batch per-input matcher and import/enqueue loops retained below in test code, not a second execution of removed production code.");
        output.WriteLine("Both variants read the same synthetic catalog; the extra playlists/queue rows they create do not participate in catalog matching. No source provider search is enabled.");
        Print("preview", oldPreview, newPreview);
        Print("import", oldImport, newImport);
        Print("enqueue", oldQueue, newQueue);
        Print("settlement", oldSettlement, newSettlement);
        output.WriteLine("Settlement baseline is restricted to this seeded scenario: half of completed tasks reference a published import, half reference pending_review; its retained test loop reads each task/import and writes each link/delete/status individually. It is not a reproduction of every old settlement branch. The batch implementation intentionally retains one fenced DELETE per promoted row.");
        output.WriteLine("Transaction milliseconds are available only when measured around BeginTranAsync through successful CommitTranAsync (legacy test stopwatch / production structured diagnostic). N/A means no separate accurate observation; legacy enqueue used one bulk command without an explicit service transaction.");
        output.WriteLine("Total times include AOP instrumentation and local SQLite I/O. These single samples do not establish PostgreSQL latency, network effects, P95, concurrency behavior or an acceleration multiple. No timing threshold is asserted.");
    }

    private void Print(string workflow, Observation legacy, Observation current)
    {
        static string Milliseconds(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A";
        output.WriteLine($"{workflow}: legacy SQL={legacy.Commands} (SELECT={legacy.Selects}, INSERT={legacy.Inserts}, UPDATE={legacy.Updates}, DELETE={legacy.Deletes}), totalMs={legacy.ElapsedMilliseconds:F3}, transactionMs={Milliseconds(legacy.TransactionMilliseconds)}; batch SQL={current.Commands} (SELECT={current.Selects}, INSERT={current.Inserts}, UPDATE={current.Updates}, DELETE={current.Deletes}), totalMs={current.ElapsedMilliseconds:F3}, transactionMs={Milliseconds(current.TransactionMilliseconds)}");
    }

    private sealed record Observation(int Commands, int Selects, int Inserts, int Updates, int Deletes, double ElapsedMilliseconds, double? TransactionMilliseconds);

    private static async Task<(T Value, Observation Observation)> MeasureAsync<T>(SourceDownloadAcceptanceFixture f, Func<Task<T>> action, Func<double?> transaction)
    {
        var commands = 0;
        var selects = 0;
        var inserts = 0;
        var updates = 0;
        var deletes = 0;
        f.Db.Aop.OnLogExecuting = (sql, parameters) =>
        {
            commands++;
            var start = sql.TrimStart();
            if (start.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) selects++;
            if (start.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)) inserts++;
            if (start.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)) updates++;
            if (start.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) deletes++;
        };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var value = await action();
            stopwatch.Stop();
            return (value, new Observation(commands, selects, inserts, updates, deletes, stopwatch.Elapsed.TotalMilliseconds, transaction()));
        }
        finally { f.Db.Aop.OnLogExecuting = null; }
    }

    private sealed class TransactionLog : ILogger<PlaylistImportService>
    {
        public double? LastTransactionMilliseconds { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            foreach (var value in values)
                if (value.Key == "TransactionElapsedMilliseconds" && value.Value is not null)
                    LastTransactionMilliseconds = Convert.ToDouble(value.Value, CultureInfo.InvariantCulture);
        }
    }

    // Retained A10 baseline: the former MatchLibraryAsync query/selection rules.
    // Keep it deliberately per input; sharing a cached or batched candidate set
    // here would erase the database round trips being compared.
    private static async Task<Guid?> LegacyMatchAsync(SourceDownloadAcceptanceFixture f, ParsedPlaylistTrack input)
    {
        var title = Regex.Replace(input.Title, @"\s+", " ").Trim().ToLowerInvariant();
        var artists = input.Artists.Select(Loose).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (title.Length == 0 || artists.Count == 0) return null;
        var candidates = await f.Db.Queryable<Track>()
            .Where(track => track.PublishedAt != null && track.Title.ToLower() == title)
            .Select(track => new { track.Id, track.DurationMs, track.AlbumId }).ToListAsync();
        if (candidates.Count == 0) return null;
        var albumIds = candidates.Where(track => track.AlbumId.HasValue).Select(track => track.AlbumId!.Value).Distinct().ToList();
        var albums = albumIds.Count == 0 ? new Dictionary<Guid, string>()
            : (await f.Db.Queryable<Album>().Where(album => albumIds.Contains(album.Id))
                .Select(album => new { album.Id, album.Title }).ToListAsync()).ToDictionary(album => album.Id, album => album.Title);
        var ids = candidates.Select(track => track.Id).ToList();
        var artistRows = await f.Db.Queryable<TrackArtist, Artist>((link, artist) => link.ArtistId == artist.Id)
            .Where((link, artist) => ids.Contains(link.TrackId))
            .Select((link, artist) => new { link.TrackId, artist.Name }).ToListAsync();
        var matchedArtists = candidates.Where(candidate => artistRows.Where(row => row.TrackId == candidate.Id)
            .Select(row => Loose(row.Name)).Any(artists.Contains)).ToList();
        var albumTitle = Loose(input.AlbumTitle);
        var matchedAlbums = albumTitle.Length == 0 ? [] : matchedArtists.Where(candidate =>
            candidate.AlbumId is { } albumId && albums.TryGetValue(albumId, out var value) && Loose(value) == albumTitle).ToList();
        var eligible = matchedAlbums.Count > 0 ? matchedAlbums : matchedArtists;
        if (input.DurationMs is null) return eligible.Count == 1 ? eligible[0].Id : null;
        return eligible.Where(candidate => candidate.DurationMs.HasValue)
            .Select(candidate => (candidate.Id, Difference: Math.Abs(candidate.DurationMs!.Value - input.DurationMs.Value)))
            .Where(candidate => candidate.Difference < 5000).OrderBy(candidate => candidate.Difference)
            .Select(candidate => (Guid?)candidate.Id).FirstOrDefault();
    }

    private static string Loose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private static async Task<IReadOnlyList<Guid?>> LegacyPreviewAsync(SourceDownloadAcceptanceFixture f, ParsedPlaylist parsed)
    {
        var matches = new List<Guid?>();
        foreach (var track in parsed.Tracks) matches.Add(await LegacyMatchAsync(f, track));
        return matches;
    }

    private static async Task<PlaylistImportResult> LegacyImportAsync(SourceDownloadAcceptanceFixture f, ParsedPlaylist parsed, Action<double> transactionTime)
    {
        var playlist = new UserPlaylist { UserId = Guid.Empty, Name = parsed.Name, ImportSnapshotJson = JsonSerializer.Serialize(parsed) };
        var matched = 0;
        var pending = 0;
        var transaction = Stopwatch.StartNew();
        await f.Db.Ado.BeginTranAsync();
        try
        {
            await f.Db.Insertable(playlist).ExecuteCommandAsync();
            for (var index = 0; index < parsed.Tracks.Count; index++)
            {
                var track = parsed.Tracks[index];
                var id = await LegacyMatchAsync(f, track);
                if (id.HasValue)
                {
                    if (!await f.Db.Queryable<UserPlaylistTrack>().AnyAsync(row => row.PlaylistId == playlist.Id && row.TrackId == id.Value))
                        await f.Db.Insertable(new UserPlaylistTrack { PlaylistId = playlist.Id, TrackId = id.Value, SortOrder = index, AddedAtUtc = DateTime.UtcNow }).ExecuteCommandAsync();
                    matched++;
                }
                else
                {
                    await f.Db.Insertable(new UserPlaylistPendingTrack
                    {
                        PlaylistId = playlist.Id, Title = track.Title, ArtistsJson = JsonSerializer.Serialize(track.Artists),
                        AlbumTitle = track.AlbumTitle, DurationMs = track.DurationMs, SourcePlatform = "batch-test",
                        SourceTrackId = track.SourceTrackId, SourceUrl = track.SourceUrl, SortOrder = index, Status = "skipped"
                    }).ExecuteCommandAsync();
                    pending++;
                }
            }
            await f.Db.Ado.CommitTranAsync();
            transaction.Stop();
            transactionTime(transaction.Elapsed.TotalMilliseconds);
            return new PlaylistImportResult(playlist.Id, parsed.Tracks.Count, matched, pending);
        }
        catch { await f.Db.Ado.RollbackTranAsync(); throw; }
    }

    private static async Task<PlaylistDownloadQueueResult> LegacyEnqueueAsync(SourceDownloadAcceptanceFixture f, ParsedPlaylist parsed)
    {
        var matched = 0;
        var rows = new List<PlaylistDownloadQueueItem>();
        foreach (var track in parsed.Tracks)
        {
            if (await LegacyMatchAsync(f, track) is not null) { matched++; continue; }
            rows.Add(new PlaylistDownloadQueueItem
            {
                UserId = Guid.Empty, Title = track.Title, ArtistsJson = JsonSerializer.Serialize(track.Artists),
                AlbumTitle = track.AlbumTitle, DurationMs = track.DurationMs, SourcePlatform = "batch-test",
                SourceTrackId = track.SourceTrackId, SourceUrl = track.SourceUrl, Status = "pending"
            });
        }
        if (rows.Count > 0) await f.Db.Insertable(rows).ExecuteCommandAsync();
        return new PlaylistDownloadQueueResult(parsed.Tracks.Count, matched, rows.Count);
    }

    private static Guid SeedSettlement(SourceDownloadAcceptanceFixture f, IReadOnlyList<Track> catalog, int size, string name)
    {
        var playlist = new UserPlaylist { UserId = Guid.Empty, Name = name };
        var imports = new List<LibraryImportItem>();
        var tasks = new List<ExternalDownloadTask>();
        var pending = new List<UserPlaylistPendingTrack>();
        for (var index = 0; index < size; index++)
        {
            var published = index % 2 == 0;
            var import = new LibraryImportItem
            {
                DisplayName = "settlement.flac", SourceStorageKey = "settlement.flac", ContentHash = Guid.NewGuid().ToString("N"),
                Status = published ? LibraryImportItemStatuses.Published : LibraryImportItemStatuses.PendingReview,
                PublishedTrackId = published ? catalog[index / 2].Id : null
            };
            var task = new ExternalDownloadTask
            {
                SourceProvider = "batch-test", ProviderTrackId = Guid.NewGuid().ToString("N"),
                Status = ExternalDownloadTaskStatuses.Completed, ImportItemId = import.Id
            };
            imports.Add(import);
            tasks.Add(task);
            pending.Add(new UserPlaylistPendingTrack
            {
                PlaylistId = playlist.Id, Title = "settlement " + index, ArtistsJson = "[]", SourcePlatform = "batch-test",
                SourceTrackId = index.ToString(CultureInfo.InvariantCulture), SortOrder = index,
                Status = "downloading", DownloadTaskId = task.Id,
                // SQLite stores bulk literals as text: avoid trailing-zero
                // fraction spellings differing from DateTime parameter binding.
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc).AddSeconds(index),
                UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc).AddSeconds(index)
            });
        }
        f.Db.Ado.BeginTran();
        try
        {
            f.Db.Insertable(playlist).ExecuteCommand();
            foreach (var chunk in imports.Chunk(50)) f.Db.Insertable(chunk).ExecuteCommand();
            foreach (var chunk in tasks.Chunk(50)) f.Db.Insertable(chunk).ExecuteCommand();
            foreach (var chunk in pending.Chunk(50)) f.Db.Insertable(chunk).ExecuteCommand();
            f.Db.Ado.CommitTran();
        }
        catch { f.Db.Ado.RollbackTran(); throw; }
        return playlist.Id;
    }

    // Deliberately limited old-loop reference for SeedSettlement, not a clone of
    // the full historical resolver or its error/retry/duplicate-source branches.
    private static async Task LegacyResolveFixedScenarioAsync(SourceDownloadAcceptanceFixture f, Guid playlistId)
    {
        var rows = await f.Db.Queryable<UserPlaylistPendingTrack>().Where(row => row.PlaylistId == playlistId)
            .OrderBy(row => row.SortOrder).ToListAsync();
        foreach (var row in rows)
        {
            var taskId = row.DownloadTaskId!.Value;
            var task = await f.Db.Queryable<ExternalDownloadTask>().FirstAsync(item => item.Id == taskId)
                ?? throw new InvalidOperationException("The fixed settlement fixture requires a task.");
            var importId = task.ImportItemId!.Value;
            var import = await f.Db.Queryable<LibraryImportItem>().FirstAsync(item => item.Id == importId)
                ?? throw new InvalidOperationException("The fixed settlement fixture requires an import item.");
            if (import.PublishedTrackId is { } trackId)
            {
                if (!await f.Db.Queryable<UserPlaylistTrack>().AnyAsync(link => link.PlaylistId == playlistId && link.TrackId == trackId))
                    await f.Db.Insertable(new UserPlaylistTrack
                    {
                        PlaylistId = playlistId, TrackId = trackId, SortOrder = row.SortOrder, AddedAtUtc = DateTime.UtcNow
                    }).ExecuteCommandAsync();
                await f.Db.Deleteable<UserPlaylistPendingTrack>().Where(item => item.Id == row.Id).ExecuteCommandAsync();
            }
            else
            {
                Assert.Equal(LibraryImportItemStatuses.PendingReview, import.Status);
                await f.Db.Updateable<UserPlaylistPendingTrack>().SetColumns(item => item.Status == "needs_review")
                    .SetColumns(item => item.UpdatedAtUtc == DateTime.UtcNow).Where(item => item.Id == row.Id).ExecuteCommandAsync();
            }
        }
    }
}
