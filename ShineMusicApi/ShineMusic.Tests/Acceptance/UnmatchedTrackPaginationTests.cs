using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using SqlSugar;
using Xunit;
using Xunit.Abstractions;

namespace ShineMusic.Tests.Acceptance;

[Collection("Source download database acceptance")]
public sealed class UnmatchedTrackPaginationTests(ITestOutputHelper output)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SourceDownloadAcceptanceFixture Fixture()
    {
        var fixture = new SourceDownloadAcceptanceFixture();
        fixture.Db.CodeFirst.InitTables<PlaylistDownloadQueueItem, UserPlaylistPendingTrack, UserPlaylist>();
        fixture.SearchProviders.Clear();
        fixture.Legacy.Clear();
        return fixture;
    }

    private static PlaylistImportService Service(SourceDownloadAcceptanceFixture f) => new(
        f.Db, [], f.Legacy, f.Aggregator(), new SourceDownloadService(f.Db, f.Legacy, f.Bus, f.Access()),
        new PlaylistParseCache(), Options.Create(f.Options), NullLogger<PlaylistImportService>.Instance);

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:x12}");

    private static PlaylistDownloadQueueItem Queue(int id, string status = "no_source", DateTime? created = null) => new()
    {
        Id = Id(id), UserId = Id(999), Title = "普通曲目", ArtistsJson = "[\"普通艺人\"]",
        AlbumTitle = "普通专辑", SourcePlatform = "test", Status = status, CreatedAtUtc = created ?? Epoch,
        UpdatedAtUtc = Epoch
    };

    private static UserPlaylistPendingTrack Pending(int id, Guid playlistId, string status = "no_source", DateTime? created = null) => new()
    {
        Id = Id(id), PlaylistId = playlistId, Title = "普通曲目", ArtistsJson = "[\"普通艺人\"]",
        AlbumTitle = "普通专辑", SourcePlatform = "test", Status = status, CreatedAtUtc = created ?? Epoch,
        UpdatedAtUtc = Epoch
    };

    [Theory]
    [InlineData("title")]
    [InlineData("artists")]
    [InlineData("album")]
    public async Task Keyword_matches_each_contract_field_in_both_origins_and_preserves_global_counts(string field)
    {
        using var f = Fixture();
        var playlist = new UserPlaylist { Id = Id(100), UserId = Id(999), Name = "目标歌单" };
        f.Db.Insertable(playlist).ExecuteCommand();
        var queue = Queue(1);
        var pending = Pending(2, playlist.Id, "failed");
        if (field == "title") queue.Title = pending.Title = "前缀针尾缀";
        // The existing API filters the stored JSON text; keep the searched text
        // literal rather than changing it into JSON \u escapes in this fixture.
        if (field == "artists") queue.ArtistsJson = pending.ArtistsJson = "[\"前缀针尾缀\",\"另一位艺人\"]";
        if (field == "album") queue.AlbumTitle = pending.AlbumTitle = "前缀针尾缀";
        f.Db.Insertable(new[] { queue, Queue(3, "failed"), Queue(5, "downloading") }).ExecuteCommand();
        f.Db.Insertable(new[] { pending, Pending(4, playlist.Id), Pending(6, playlist.Id, "pending") }).ExecuteCommand();

        var service = Service(f);
        var result = await service.ListUnmatchedAsync(null, " 针 ", 1, 20, default);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.NoSource);
        Assert.Equal(2, result.TaskFailed);
        Assert.Equal(new[] { "playlist", "queue" }, result.Items.Select(row => row.Origin).OrderBy(origin => origin));
        var actualQueue = Assert.Single(result.Items, row => row.Origin == "queue");
        var actualPending = Assert.Single(result.Items, row => row.Origin == "playlist");
        Assert.Equal(queue.Title, actualQueue.Title);
        Assert.Equal(pending.Title, actualPending.Title);
        Assert.Equal(JsonSerializer.Deserialize<string[]>(queue.ArtistsJson), actualQueue.Artists);
        Assert.Equal(JsonSerializer.Deserialize<string[]>(pending.ArtistsJson), actualPending.Artists);
        Assert.Equal(queue.AlbumTitle, actualQueue.AlbumTitle);
        Assert.Equal(pending.AlbumTitle, actualPending.AlbumTitle);
        Assert.Equal("test", actualQueue.SourcePlatform);
        Assert.Equal("test", actualPending.SourcePlatform);
        Assert.Null(actualQueue.PlaylistName);
        Assert.Equal(playlist.Name, actualPending.PlaylistName);
        Assert.NotEmpty(actualQueue.Reason);
        Assert.NotEmpty(actualPending.Reason);

        var failed = await service.ListUnmatchedAsync(" FAILED ", "针", 1, 1, default);
        Assert.Equal(pending.Id, Assert.Single(failed.Items).Id);
        Assert.Equal(1, failed.Total);
        Assert.Equal(2, failed.NoSource);
        Assert.Equal(2, failed.TaskFailed);
        var noSourcePage = await service.ListUnmatchedAsync("no_source", null, 2, 1, default);
        Assert.Single(noSourcePage.Items);
        Assert.Equal(2, noSourcePage.Total);
        Assert.Equal(2, noSourcePage.NoSource);
        Assert.Equal(2, noSourcePage.TaskFailed);
        Assert.Empty(f.Bus.Messages);
        Assert.Equal(0, f.Db.Queryable<ExternalDownloadTask>().Count());
    }

    [Fact]
    public async Task Equal_timestamps_use_id_then_origin_for_stable_pages_and_preserve_cross_origin_duplicates()
    {
        using var f = Fixture();
        var playlist = new UserPlaylist { Id = Id(100), UserId = Id(999), Name = "稳定排序" };
        f.Db.Insertable(playlist).ExecuteCommand();
        f.Db.Insertable(new[] { Queue(2), Queue(1), Queue(4, created: Epoch.AddDays(1)), Queue(5, created: Epoch.AddDays(-1)) }).ExecuteCommand();
        f.Db.Insertable(new[] { Pending(2, playlist.Id), Pending(1, playlist.Id) }).ExecuteCommand();
        var expected = new[]
        {
            (Id(4), "queue"), (Id(1), "playlist"), (Id(1), "queue"),
            (Id(2), "playlist"), (Id(2), "queue"), (Id(5), "queue")
        };
        var service = Service(f);
        for (var repetition = 0; repetition < 2; repetition++)
        {
            var received = new List<(Guid, string)>();
            for (var page = 1; page <= expected.Length; page++)
            {
                var result = await service.ListUnmatchedAsync(null, null, page, 1, default);
                Assert.Equal(expected.Length, result.Total);
                var row = Assert.Single(result.Items);
                received.Add((row.Id, row.Origin));
            }
            Assert.Equal(expected, received);
        }
    }

    [Fact]
    public async Task Empty_invalid_and_out_of_range_requests_preserve_page_clamping_contract()
    {
        using var f = Fixture();
        var service = Service(f);
        var empty = await service.ListUnmatchedAsync(" ", " ", -4, 0, default);
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Total);
        Assert.Equal(0, empty.NoSource);
        Assert.Equal(0, empty.TaskFailed);
        Assert.Equal(1, empty.Page);
        Assert.Equal(1, empty.PageSize);

        f.Db.Insertable(new[] { Queue(1), Queue(2, "failed"), Queue(3, "pending") }).ExecuteCommand();
        var invalid = await service.ListUnmatchedAsync("downloading", null, 0, 5000, default);
        Assert.Empty(invalid.Items);
        Assert.Equal(0, invalid.Total);
        Assert.Equal(0, invalid.NoSource);
        Assert.Equal(0, invalid.TaskFailed);
        Assert.Equal(1, invalid.Page);
        Assert.Equal(1000, invalid.PageSize);
        var clamped = await service.ListUnmatchedAsync(null, null, int.MinValue, -10, default);
        Assert.Single(clamped.Items);
        Assert.Equal(1, clamped.Page);
        Assert.Equal(1, clamped.PageSize);
        Assert.Equal(2, clamped.Total);
        var largePageSize = await service.ListUnmatchedAsync(null, null, 1, int.MaxValue, default);
        Assert.Equal(1000, largePageSize.PageSize);
        Assert.Equal(2, largePageSize.Items.Count);
        var beyondEnd = await service.ListUnmatchedAsync(null, null, int.MaxValue, 1000, default);
        Assert.Empty(beyondEnd.Items);
        Assert.Equal(2, beyondEnd.Total);
        Assert.Equal(int.MaxValue, beyondEnd.Page);
    }

    [Fact]
    public async Task Playlist_names_are_only_read_for_the_returned_page()
    {
        using var f = Fixture();
        var playlists = Enumerable.Range(1, 3).Select(i => new UserPlaylist { Id = Id(100 + i), UserId = Id(999), Name = $"歌单{i}" }).ToArray();
        f.Db.Insertable(playlists).ExecuteCommand();
        f.Db.Insertable(playlists.Select((playlist, index) => Pending(index + 1, playlist.Id, created: Epoch.AddDays(-index))).ToArray()).ExecuteCommand();
        var service = Service(f);
        using var capture = new SelectCapture(f.Db);
        var page = await service.ListUnmatchedAsync(null, null, 2, 1, default);
        Assert.Equal("歌单2", Assert.Single(page.Items).PlaylistName);
        Assert.Equal(3, page.Total);
        var nameQueries = capture.Statements.Where(statement => IsPlaylistLookup(statement.Sql)).ToArray();
        Assert.NotEmpty(nameQueries);
        var returnedNameRows = capture.CountReaderRows(nameQueries);
        Assert.Equal(1, returnedNameRows);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public async Task Large_history_returns_a_bounded_database_page_and_reports_sqlite_observations(int total)
    {
        using var f = Fixture();
        var playlists = Enumerable.Range(1, 4).Select(i => new UserPlaylist { Id = Id(200_000 + i), UserId = Id(999), Name = $"规模歌单{i}" }).ToArray();
        f.Db.Insertable(playlists).ExecuteCommand();
        f.Db.Ado.BeginTran();
        try
        {
            for (var start = 0; start < total / 2; start += 100)
            {
                var queue = new List<PlaylistDownloadQueueItem>(100);
                var pending = new List<UserPlaylistPendingTrack>(100);
                for (var i = start; i < Math.Min(start + 100, total / 2); i++)
                {
                    var status = i % 2 == 0 ? "no_source" : "failed";
                    queue.Add(Queue(i * 2 + 1, status, Epoch.AddSeconds(i)));
                    pending.Add(Pending(i * 2 + 2, playlists[i % playlists.Length].Id, status, Epoch.AddSeconds(i)));
                }
                f.Db.Insertable(queue).ExecuteCommand();
                f.Db.Insertable(pending).ExecuteCommand();
            }
            f.Db.Ado.CommitTran();
        }
        catch
        {
            f.Db.Ado.RollbackTran();
            throw;
        }

        const int pageSize = 20;
        const int samples = 20;
        var service = Service(f);
        await service.ListUnmatchedAsync(null, null, 17, pageSize, default); // Exclude initial JIT/model warmup.
        using var capture = new SelectCapture(f.Db);
        var durations = new List<double>();
        var allocations = new List<long>();
        for (var sample = 0; sample < samples; sample++)
        {
            capture.Clear();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            var result = await service.ListUnmatchedAsync(null, null, 17, pageSize, default);
            stopwatch.Stop();
            allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            durations.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.Equal(pageSize, result.Items.Count);
            Assert.Equal(total, result.Total);
            Assert.Equal(total / 2, result.NoSource);
            Assert.Equal(total / 2, result.TaskFailed);
        }

        var statements = capture.Statements.ToArray();
        Assert.Contains(statements, statement => Regex.IsMatch(statement.Sql, @"\bUNION\s+ALL\b", RegexOptions.IgnoreCase));
        Assert.Contains(statements, statement => Regex.IsMatch(statement.Sql, @"\b(LIMIT|OFFSET|ROW_NUMBER)\b", RegexOptions.IgnoreCase));
        // COUNT(*) returns one scalar row, not 'count' rows. Replaying the captured
        // SELECTs with the real reader measures actual result-set cardinality.
        // Replay is outside the timed/allocation samples, with no intervening writes.
        var returnedRows = capture.CountReaderRows(statements);
        var nameRows = capture.CountReaderRows(statements.Where(statement => IsPlaylistLookup(statement.Sql)).ToArray());
        Assert.InRange(nameRows, 0, pageSize);
        var scalarQueries = statements.Count(statement => Regex.IsMatch(statement.Sql, @"\bCOUNT\s*\(", RegexOptions.IgnoreCase));
        Assert.InRange(returnedRows, pageSize, pageSize + nameRows + scalarQueries);
        durations.Sort();
        allocations.Sort();
        output.WriteLine("SQLite observational sample only; this is not a PostgreSQL/network/concurrent-load benchmark.");
        output.WriteLine($"history={total:N0}; page=17; pageSize={pageSize}; samples={samples}; SELECT statements={statements.Length}; captured SELECT replay reader rows={returnedRows}; playlist lookup reader rows={nameRows}; scalar count result rows={scalarQueries}");
        output.WriteLine($"warm elapsed ms median={durations[samples / 2]:F3}, observed P95 of {samples}={durations[(int)Math.Ceiling(samples * 0.95) - 1]:F3}; process allocation delta bytes median={allocations[samples / 2]:N0}, max={allocations[^1]:N0}");
        output.WriteLine("Allocation uses process-wide GC.GetTotalAllocatedBytes across awaits; no absolute latency/allocation threshold or acceleration claim is asserted.");
        foreach (var statement in statements)
            output.WriteLine("Captured SELECT: " + statement.Sql);
        var pageQuery = Assert.Single(statements, statement =>
            Regex.IsMatch(statement.Sql, @"\bUNION\s+ALL\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(statement.Sql, @"\b(LIMIT|OFFSET|ROW_NUMBER)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(statement.Sql, @"\bCOUNT\s*\(", RegexOptions.IgnoreCase));
        foreach (var planRow in capture.ExplainQueryPlan(pageQuery))
            output.WriteLine("SQLite EXPLAIN QUERY PLAN: " + planRow);
        Assert.Empty(f.Bus.Messages);
    }

    private static bool IsPlaylistLookup(string sql) => Regex.IsMatch(sql, @"\buser_playlists\b", RegexOptions.IgnoreCase);

    private sealed record CapturedSelect(string Sql, SugarParameter[] Parameters);

    private sealed class SelectCapture : IDisposable
    {
        private readonly SqlSugarClient _db;
        private readonly Action<string, SugarParameter[]> _record;
        public List<CapturedSelect> Statements { get; } = [];

        public SelectCapture(SqlSugarClient db)
        {
            _db = db;
            _record = (sql, parameters) =>
            {
                var trimmed = sql.TrimStart();
                if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) return;
                Statements.Add(new CapturedSelect(sql, parameters.Select(parameter => new SugarParameter(parameter.ParameterName, parameter.Value)
                {
                    DbType = parameter.DbType, Size = parameter.Size
                }).ToArray()));
            };
            // This helper owns the newly-created fixture's otherwise empty AOP hook.
            // AopProvider.OnLogExecuting is write-only in this SqlSugar version.
            db.Aop.OnLogExecuting = _record;
        }

        public void Clear() => Statements.Clear();

        public int CountReaderRows(IReadOnlyList<CapturedSelect> statements)
        {
            _db.Aop.OnLogExecuting = null;
            try
            {
                var count = 0;
                foreach (var statement in statements)
                {
                    using var reader = _db.Ado.GetDataReader(statement.Sql, statement.Parameters);
                    while (reader.Read()) count++;
                }
                return count;
            }
            finally { _db.Aop.OnLogExecuting = _record; }
        }

        public IReadOnlyList<string> ExplainQueryPlan(CapturedSelect statement)
        {
            var rows = new List<string>();
            using var reader = _db.Ado.GetDataReader("EXPLAIN QUERY PLAN " + statement.Sql, statement.Parameters);
            while (reader.Read())
                rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
            return rows;
        }

        public void Dispose() => _db.Aop.OnLogExecuting = null;
    }
}
