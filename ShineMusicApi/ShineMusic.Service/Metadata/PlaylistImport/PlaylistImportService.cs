// 职责：歌单导入编排层——解析预览（逐曲匹配曲库）、导入落库（建歌单 + 已匹配插轨 +
// 未匹配建 pending 并触发多音源静默下载）、以及歌单详情读取时的 pending 懒结算
// （下载发布完成 → 转正为 UserPlaylistTrack；失败终态标记）。

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using SqlSugar;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>解析预览中的单条曲目（含曲库匹配结果）。</summary>
public sealed record PlaylistPreviewTrack(
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    int? DurationMs,
    bool Matched,
    Guid? MatchedTrackId);

/// <summary>解析预览结果。</summary>
public sealed record PlaylistParsePreview(
    string Platform,
    string Name,
    string? CoverUrl,
    int Total,
    IReadOnlyList<PlaylistPreviewTrack> Tracks);

/// <summary>导入结果汇总。</summary>
public sealed record PlaylistImportResult(Guid PlaylistId, int Total, int Matched, int Pending);

public sealed record PlaylistDownloadQueueResult(int Total, int Matched, int Queued);

/// <summary>重新补齐结果：转正 / 重建任务 / 仍无音源 / 保持不动 的分项数量。</summary>
public sealed record PlaylistRematchResult(
    Guid PlaylistId,
    int Total,
    int Promoted,
    int Retried,
    int NoSource,
    int Unchanged);

/// <summary>后台补齐单轮结果：结算（转正/终态）数、本轮处理数、成功建任务数、无音源数。</summary>
public sealed record PlaylistBackfillOutcome(int Settled, int Processed, int Queued, int NoSource);

/// <summary>歌单导入编排服务。</summary>
public sealed partial class PlaylistImportService(
    ISqlSugarClient db,
    IEnumerable<IPlaylistParser> parsers,
    IEnumerable<IMusicSourceProvider> providers,
    SourceDownloadService sourceDownloadService,
    PlaylistParseCache parseCache,
    IOptions<SourceDownloadOptions> options,
    ILogger<PlaylistImportService> logger)
{
    /// <summary>后台补齐时单曲之间的节流间隔：对音源平台搜索接口保持温和，避免批量触发限流。</summary>
    private static readonly TimeSpan BackfillThrottle = TimeSpan.FromMilliseconds(250);

    /// <summary>pending 状态常量。</summary>
    public static class PendingStatuses
    {
        public const string Pending = "pending";
        public const string Downloading = "downloading";
        public const string NoSource = "no_source";
        public const string Failed = "failed";
        /// <summary>
        /// 待人工审核：音频已下载完成，但自动评分低于自动发布阈值，条目停在 pending_review，
        /// 需管理员在「曲库导入」页发布后才会转正。此状态是「等审核」而非「等下载」，
        /// 不再参与补齐轮询，避免行永远停在 downloading 造成假死。
        /// </summary>
        public const string NeedsReview = "needs_review";
        /// <summary>
        /// 审核拒绝：音频已下载，但在「导入审核」页被人工拒绝（或处理失败），不会入库。
        /// 与 <see cref="Failed"/> 区分开——这不是音源问题，重试下载也不会改变结果。
        /// </summary>
        public const string Rejected = "rejected";
        /// <summary>用户选择「仅导入歌单」时的跳过态：不自动排队，可在「重新补齐」时手动排入下载队列。</summary>
        public const string Skipped = "skipped";
    }

    /// <summary>搜索阶段单源的结局，用于诊断展示（只记录，不改变候选筛选行为）。</summary>
    public static class SearchOutcomes
    {
        /// <summary>搜索命中标题+艺人，且已进入候选。</summary>
        public const string Matched = "matched";
        /// <summary>搜索有返回，但没有标题+艺人完全一致的条目。</summary>
        public const string NoMatch = "no_match";
        /// <summary>搜索超出单源预算被取消。</summary>
        public const string Timeout = "timeout";
        /// <summary>搜索抛异常。</summary>
        public const string Error = "error";
        /// <summary>命中标题+艺人，但可下载预判为不可下载，未进入候选。</summary>
        public const string Unavailable = "unavailable";
        /// <summary>曲名/艺人为空，未发起搜索。</summary>
        public const string Skipped = "skipped";
    }

    /// <summary>搜索阶段单源诊断（序列化到 SearchDiagnosticsJson）。</summary>
    private sealed record ProviderSearchDiagnostic(string Provider, string Outcome, int ResultCount, string? Message);

    /// <summary>诊断 JSON 统一 camelCase，便于与计划文档/前端约定的字段名一致。</summary>
    private static readonly JsonSerializerOptions DiagnosticsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>选择 parser 并解析歌单，逐曲匹配曲库后返回预览；解析结果写入缓存供导入复用。</summary>
    public async Task<PlaylistParsePreview> ParsePreviewAsync(string url, Guid userId, CancellationToken cancellationToken)
    {
        var (platform, parsed) = await ParseAsync(url, cancellationToken);
        parseCache.Set(userId, url, platform, parsed);

        var tracks = new List<PlaylistPreviewTrack>(parsed.Tracks.Count);
        foreach (var track in parsed.Tracks)
        {
            var matchedId = await MatchLibraryAsync(track.Title, track.Artists, track.DurationMs, cancellationToken, track.AlbumTitle);
            tracks.Add(new PlaylistPreviewTrack(
                track.Title, track.Artists, track.AlbumTitle, track.DurationMs,
                matchedId.HasValue, matchedId));
        }
        return new PlaylistParsePreview(platform, parsed.Name, parsed.CoverUrl, tracks.Count, tracks);
    }

    /// <summary>
    /// 导入歌单：建 UserPlaylist（立即生成）→ 已匹配曲目插 UserPlaylistTrack（保原顺序）→
    /// 未匹配曲目建 pending 行；downloadMissing 为 true 时由后台补齐任务异步搜索音源并建下载任务。
    /// </summary>
    /// <param name="url">外部平台歌单链接。</param>
    /// <param name="name">新歌单名称（留空用原歌单名）。</param>
    /// <param name="userId">所属用户。</param>
    /// <param name="downloadMissing">
    /// true（默认）＝加入下载队列：未匹配曲目置为 pending，后台 worker 逐首搜索音源并下载；
    /// false＝仅导入歌单：未匹配曲目置为 skipped，不占用带宽，之后可在「重新补齐」里手动排入队列。
    /// </param>
    /// <param name="cancellationToken">请求取消信号。</param>
    /// <remarks>
    /// 关键设计：本方法**不做任何音源搜索**。单曲「多源并行搜索 + 可下载预判」实测 25~50 秒，
    /// 200+ 首若同步执行需 1.5 小时以上，必然被前端请求超时掐断，导致歌单只落库前几首
    /// （曾出现「导入 200+ 首却只有 2 个下载任务」）。因此这里只做纯 DB 操作（毫秒级），
    /// 请求内完成落库即返回，补齐进度由 <see cref="BackfillPendingAsync"/> 在后台分轮推进。
    /// </remarks>
    public async Task<PlaylistImportResult> ImportAsync(
        string url, string? name, Guid userId, bool downloadMissing, CancellationToken cancellationToken)
    {
        // 优先复用预览阶段的解析结果：避免二次抓取平台接口（大歌单解析 20~60 秒）。
        if (!parseCache.TryGet(userId, url, out var parsed, out var platform))
            (platform, parsed) = await ParseAsync(url, cancellationToken);

        var playlistName = !string.IsNullOrWhiteSpace(name) ? name.Trim() : parsed.Name.Trim();
        if (playlistName.Length > 100)
            playlistName = playlistName[..100];

        var now = DateTime.UtcNow;
        var playlist = new UserPlaylist
        {
            UserId = userId,
            Name = playlistName,
            Description = parsed.Description is { Length: > 500 } ? parsed.Description[..500] : parsed.Description,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await db.Insertable(playlist).ExecuteCommandAsync(cancellationToken);

        var matchedCount = 0;
        var pendingCount = 0;
        for (var index = 0; index < parsed.Tracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = parsed.Tracks[index];
            var matchedId = await MatchLibraryAsync(track.Title, track.Artists, track.DurationMs, cancellationToken, track.AlbumTitle);
            if (matchedId.HasValue)
            {
                await TryInsertPlaylistTrackAsync(playlist.Id, matchedId.Value, index, cancellationToken);
                matchedCount++;
                continue;
            }

            // 未匹配：只落 pending 行（不在此搜索音源）。
            // 加入下载队列 → pending（后台 worker 接手）；仅导入歌单 → skipped（不占用带宽，可后续手动排入）。
            var pending = new UserPlaylistPendingTrack
            {
                PlaylistId = playlist.Id,
                Title = track.Title,
                ArtistsJson = JsonSerializer.Serialize(track.Artists),
                AlbumTitle = track.AlbumTitle,
                DurationMs = track.DurationMs,
                SourcePlatform = platform,
                SourceTrackId = track.SourceTrackId,
                SourceUrl = track.SourceUrl,
                SortOrder = index,
                Status = downloadMissing ? PendingStatuses.Pending : PendingStatuses.Skipped,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            await db.Insertable(pending).ExecuteCommandAsync(cancellationToken);
            pendingCount++;
        }

        return new PlaylistImportResult(playlist.Id, parsed.Tracks.Count, matchedCount, pendingCount);
    }

    /// <summary>只把外部歌单中尚未入库的曲目加入后台下载队列，不创建用户歌单。</summary>
    public async Task<PlaylistDownloadQueueResult> EnqueueDownloadsAsync(
        string url, Guid userId, CancellationToken cancellationToken)
    {
        if (!parseCache.TryGet(userId, url, out var parsed, out var platform))
            (platform, parsed) = await ParseAsync(url, cancellationToken);

        var now = DateTime.UtcNow;
        var matchedCount = 0;
        var queueItems = new List<PlaylistDownloadQueueItem>();
        foreach (var track in parsed.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await MatchLibraryAsync(
                    track.Title, track.Artists, track.DurationMs, cancellationToken, track.AlbumTitle) is not null)
            {
                matchedCount++;
                continue;
            }

            queueItems.Add(new PlaylistDownloadQueueItem
            {
                UserId = userId,
                Title = track.Title,
                ArtistsJson = JsonSerializer.Serialize(track.Artists),
                AlbumTitle = track.AlbumTitle,
                DurationMs = track.DurationMs,
                SourcePlatform = platform,
                SourceTrackId = track.SourceTrackId,
                SourceUrl = track.SourceUrl,
                Status = PendingStatuses.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        if (queueItems.Count > 0)
            await db.Insertable(queueItems).ExecuteCommandAsync(cancellationToken);

        return new PlaylistDownloadQueueResult(parsed.Tracks.Count, matchedCount, queueItems.Count);
    }

    /// <summary>
    /// 后台补齐单轮：先结算「下载中」的行（已发布 → 转正；失败终态 → 标记），
    /// 再按创建时间最老优先取一小批 status=pending 的行，逐条搜索音源并建下载任务。
    /// </summary>
    /// <param name="batchSize">本轮最多处理的 pending 行数。</param>
    /// <param name="timeBudget">本轮时间预算，超出即收尾（未处理的行下一轮继续）。</param>
    /// <param name="cancellationToken">宿主停止信号。</param>
    public async Task<PlaylistBackfillOutcome> BackfillPendingAsync(
        int batchSize, TimeSpan timeBudget, CancellationToken cancellationToken)
    {
        // ===== 1) 结算：把已下载完成/失败终态的 pending 行推进到下一状态 =====
        // 含 needs_review：这些行等的是管理员发布，发布成功后下一轮结算即可自动转正。
        // 含 failed / rejected：行被标记失败后，其导入条目仍可能被人工发布（例如先拒绝后重新发布），
        // 若不再结算就会永久卡住、曲目永远进不了歌单。
        var settlePlaylistIds = await db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.Status == PendingStatuses.Downloading
                || item.Status == PendingStatuses.NeedsReview
                || item.Status == PendingStatuses.Failed
                || item.Status == PendingStatuses.Rejected)
            .Select(item => item.PlaylistId)
            .Distinct()
            .Take(50)
            .ToListAsync(cancellationToken);
        var settled = 0;
        foreach (var playlistId in settlePlaylistIds)
        {
            try
            {
                await ResolvePendingAsync(playlistId, cancellationToken);
                settled++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 单个歌单结算失败不影响其余歌单与后续补齐。
            }
        }

        // ===== 2) 补齐：最老优先取批，逐条建下载任务 =====
        var rows = await db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.Status == PendingStatuses.Pending)
            .OrderBy(item => item.CreatedAtUtc)
            .Take(Math.Clamp(batchSize, 1, 100))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return new PlaylistBackfillOutcome(settled, 0, 0, 0);

        var playlistIds = rows.Select(item => item.PlaylistId).Distinct().ToList();
        var userByPlaylist = (await db.Queryable<UserPlaylist>()
                .Where(item => playlistIds.Contains(item.Id))
                .Select(item => new { item.Id, item.UserId })
                .ToListAsync(cancellationToken))
            .ToDictionary(item => item.Id, item => item.UserId);

        var stopwatch = Stopwatch.StartNew();
        int processed = 0, queued = 0, noSource = 0;
        foreach (var pending in rows)
        {
            if (cancellationToken.IsCancellationRequested || stopwatch.Elapsed >= timeBudget)
                break;

            processed++;
            if (!userByPlaylist.TryGetValue(pending.PlaylistId, out var userId))
            {
                // 歌单已被删除：pending 行成为孤儿，标记失败终态避免无限重试。
                await MarkPendingAsync(pending, PendingStatuses.Failed, cancellationToken);
                continue;
            }

            var track = new ParsedPlaylistTrack(
                pending.Title,
                DeserializeArtists(pending.ArtistsJson),
                pending.AlbumTitle,
                pending.DurationMs,
                pending.SourceTrackId,
                pending.SourceUrl);
            await TryStartSilentDownloadAsync(
                pending,
                track,
                userId,
                cancellationToken,
                // 批量补齐走更紧的搜索预算并跳过可下载预判：以吞吐优先，慢源交给下载阶段换源兜底。
                searchBudgetSeconds: Math.Clamp(options.Value.BackfillSearchBudgetSeconds, 3, 300),
                precheckDownloadable: false);
            pending.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(pending).ExecuteCommandAsync(cancellationToken);

            if (pending.Status == PendingStatuses.Downloading)
                queued++;
            else if (pending.Status == PendingStatuses.NoSource)
                noSource++;

            await Task.Delay(BackfillThrottle, cancellationToken);
        }

        return new PlaylistBackfillOutcome(settled, processed, queued, noSource);
    }

    /// <summary>后台处理独立加入下载队列的曲目，不创建或关联任何用户歌单。</summary>
    public async Task<(int Processed, int Queued, int NoSource)> ProcessDownloadQueueAsync(
        int batchSize, TimeSpan timeBudget, CancellationToken cancellationToken)
    {
        var rows = await db.Queryable<PlaylistDownloadQueueItem>()
            .Where(item => item.Status == PendingStatuses.Pending)
            .OrderBy(item => item.CreatedAtUtc)
            .Take(Math.Clamp(batchSize, 1, 100))
            .ToListAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        int processed = 0, queued = 0, noSource = 0;
        foreach (var row in rows)
        {
            if (cancellationToken.IsCancellationRequested || stopwatch.Elapsed >= timeBudget)
                break;

            processed++;
            var pending = new UserPlaylistPendingTrack
            {
                PlaylistId = Guid.Empty,
                Title = row.Title,
                ArtistsJson = row.ArtistsJson,
                AlbumTitle = row.AlbumTitle,
                DurationMs = row.DurationMs,
                SourcePlatform = row.SourcePlatform,
                SourceTrackId = row.SourceTrackId,
                SourceUrl = row.SourceUrl,
                Status = PendingStatuses.Pending
            };
            var track = new ParsedPlaylistTrack(
                row.Title,
                DeserializeArtists(row.ArtistsJson),
                row.AlbumTitle,
                row.DurationMs,
                row.SourceTrackId,
                row.SourceUrl);
            await TryStartSilentDownloadAsync(
                pending,
                track,
                row.UserId,
                cancellationToken,
                searchBudgetSeconds: Math.Clamp(options.Value.BackfillSearchBudgetSeconds, 3, 300),
                precheckDownloadable: false);

            row.Status = pending.Status;
            row.DownloadTaskId = pending.DownloadTaskId;
            row.SearchDiagnosticsJson = pending.SearchDiagnosticsJson;
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(row).ExecuteCommandAsync(cancellationToken);
            if (row.Status == PendingStatuses.Downloading)
                queued++;
            else if (row.Status == PendingStatuses.NoSource)
                noSource++;
            await Task.Delay(BackfillThrottle, cancellationToken);
        }

        return (processed, queued, noSource);
    }

    /// <summary>
    /// 未匹配/无音源曲目列表（只读）：合并「独立加入下载队列」与「导入歌单待补齐」两条链路，
    /// 仅返回 no_source / failed 两类，不会创建任何下载任务。
    /// </summary>
    public async Task<UnmatchedTrackListDto> ListUnmatchedAsync(
        string? status, string? keyword, int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Clamp(pageSize, 1, 1000);
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not null
            && normalizedStatus != PendingStatuses.NoSource
            && normalizedStatus != PendingStatuses.Failed)
            return new UnmatchedTrackListDto([], 0, effectivePage, effectivePageSize, 0, 0);

        // 计数不受关键词与分页影响，供前端标签页展示。
        var noSource = await db.Queryable<PlaylistDownloadQueueItem>()
                .Where(item => item.Status == PendingStatuses.NoSource).CountAsync(cancellationToken)
            + await db.Queryable<UserPlaylistPendingTrack>()
                .Where(item => item.Status == PendingStatuses.NoSource).CountAsync(cancellationToken);
        var taskFailed = await db.Queryable<PlaylistDownloadQueueItem>()
                .Where(item => item.Status == PendingStatuses.Failed).CountAsync(cancellationToken)
            + await db.Queryable<UserPlaylistPendingTrack>()
                .Where(item => item.Status == PendingStatuses.Failed).CountAsync(cancellationToken);

        var queueQuery = db.Queryable<PlaylistDownloadQueueItem>()
            .Where(item => item.Status == PendingStatuses.NoSource || item.Status == PendingStatuses.Failed);
        var pendingQuery = db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.Status == PendingStatuses.NoSource || item.Status == PendingStatuses.Failed);
        if (normalizedStatus is not null)
        {
            queueQuery = queueQuery.Where(item => item.Status == normalizedStatus);
            pendingQuery = pendingQuery.Where(item => item.Status == normalizedStatus);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim();
            queueQuery = queueQuery.Where(item => item.Title.Contains(term)
                || item.ArtistsJson.Contains(term)
                || (item.AlbumTitle != null && item.AlbumTitle.Contains(term)));
            pendingQuery = pendingQuery.Where(item => item.Title.Contains(term)
                || item.ArtistsJson.Contains(term)
                || (item.AlbumTitle != null && item.AlbumTitle.Contains(term)));
        }

        var queueItems = await queueQuery.OrderBy(item => item.CreatedAtUtc, OrderByType.Desc).ToListAsync(cancellationToken);
        var pendingItems = await pendingQuery.OrderBy(item => item.CreatedAtUtc, OrderByType.Desc).ToListAsync(cancellationToken);

        // 歌单名批量查回，避免逐行 N+1。
        var playlistIds = pendingItems.Select(item => item.PlaylistId).Distinct().ToList();
        var playlistNames = playlistIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.Queryable<UserPlaylist>()
                .Where(playlist => playlistIds.Contains(playlist.Id))
                .Select(playlist => new { playlist.Id, playlist.Name })
                .ToListAsync(cancellationToken))
                .ToDictionary(item => item.Id, item => item.Name);

        var rows = new List<UnmatchedTrackDto>(queueItems.Count + pendingItems.Count);
        rows.AddRange(queueItems.Select(item => new UnmatchedTrackDto(
            item.Id, "queue", item.Title, DeserializeArtists(item.ArtistsJson), item.AlbumTitle,
            item.SourcePlatform, null, item.Status,
            BuildUnmatchedReason(item.Status, item.SearchDiagnosticsJson), item.CreatedAtUtc)));
        rows.AddRange(pendingItems.Select(item => new UnmatchedTrackDto(
            item.Id, "playlist", item.Title, DeserializeArtists(item.ArtistsJson), item.AlbumTitle,
            item.SourcePlatform, playlistNames.GetValueOrDefault(item.PlaylistId), item.Status,
            BuildUnmatchedReason(item.Status, item.SearchDiagnosticsJson), item.CreatedAtUtc)));

        var ordered = rows.OrderByDescending(item => item.CreatedAtUtc).ToList();
        var pageItems = ordered
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToList();
        return new UnmatchedTrackListDto(pageItems, ordered.Count, effectivePage, effectivePageSize, noSource, taskFailed);
    }

    /// <summary>未匹配原因摘要：基于落库的逐源诊断生成中文说明；无诊断（历史数据）时给出保守提示。</summary>
    private static string BuildUnmatchedReason(string status, string? diagnosticsJson)
    {
        if (status == PendingStatuses.Failed)
            return "已创建下载任务，但下载阶段所有候选音源均失败；逐源失败明细见「音源抓歌」页的下载任务";

        var diagnostics = DeserializeDiagnostics(diagnosticsJson);
        if (diagnostics.Count == 0)
            return "历史数据未记录搜索诊断；可能为音源无匹配或搜索超时";

        if (diagnostics.All(item => item.Outcome == SearchOutcomes.Skipped))
            return "曲名或艺人为空，未发起音源搜索";

        var labels = diagnostics.Select(item => ProviderDisplayName(item.Provider)).Distinct().ToList();
        var noMatch = diagnostics
            .Where(item => item.Outcome == SearchOutcomes.NoMatch)
            .Select(item => ProviderDisplayName(item.Provider))
            .ToList();
        var unavailable = diagnostics
            .Where(item => item.Outcome == SearchOutcomes.Unavailable)
            .Select(item => ProviderDisplayName(item.Provider))
            .ToList();
        var timeouts = diagnostics.Where(item => item.Outcome == SearchOutcomes.Timeout).ToList();
        var errors = diagnostics.Where(item => item.Outcome == SearchOutcomes.Error).ToList();

        var parts = new List<string>
        {
            noMatch.Count > 0
                ? $"未匹配到标题+艺人完全一致的音源（已搜索：{string.Join("、", labels)}），未创建下载任务"
                : $"未找到可用的音源（已搜索：{string.Join("、", labels)}），未创建下载任务"
        };
        if (unavailable.Count > 0)
            parts.Add($"{string.Join("、", unavailable)} 命中但预判不可下载");
        foreach (var timeout in timeouts)
            parts.Add($"{ProviderDisplayName(timeout.Provider)} {timeout.Message ?? "搜索超时"}");
        foreach (var error in errors)
            parts.Add($"{ProviderDisplayName(error.Provider)} 搜索异常：{error.Message ?? "未知错误"}");
        return string.Join("；", parts);
    }

    /// <summary>音源标识 → 中文简称（用于诊断文案）。</summary>
    private static string ProviderDisplayName(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "netease" => "网易云",
        "kuwo" => "酷我",
        "kugou" => "酷狗",
        "migu" => "咪咕",
        "audius" => "Audius",
        "jamendo" => "Jamendo",
        "ytmusic" => "YouTube Music",
        _ => provider
    };

    /// <summary>
    /// 懒结算：歌单详情读取时调用，把下载已发布完成的 pending 行转正为正式曲目，
    /// 下载失败（含哈希重复后重匹配）的行更新为终态。
    /// </summary>
    public async Task ResolvePendingAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        var pendings = await db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.PlaylistId == playlistId
                && (item.Status == PendingStatuses.Pending
                    || item.Status == PendingStatuses.Downloading
                    || item.Status == PendingStatuses.NeedsReview))
            .ToListAsync(cancellationToken);
        if (pendings.Count == 0)
            return;

        foreach (var pending in pendings)
        {
            if (pending.DownloadTaskId is not { } taskId)
                continue;
            var task = await db.Queryable<ExternalDownloadTask>()
                .FirstAsync(candidate => candidate.Id == taskId, cancellationToken);
            if (task is null)
            {
                // 任务记录已被清理：退回 pending 让补齐重新排队（不能直接判失败，否则这首歌就永久丢了）。
                if (pending.Status != PendingStatuses.Pending)
                    await MarkPendingAsync(pending, PendingStatuses.Pending, cancellationToken);
                continue;
            }

            // 下载完成链路：task.ImportItemId → LibraryImportItem 的状态决定能否转正。
            if (task.ImportItemId is { } importItemId)
            {
                var importItem = await db.Queryable<LibraryImportItem>()
                    .Where(item => item.Id == importItemId)
                    .Select(item => new { item.Status, item.PublishedTrackId })
                    .FirstAsync(cancellationToken);

                if (importItem?.PublishedTrackId is { } trackId)
                {
                    await PromotePendingAsync(pending, trackId, cancellationToken);
                    continue;
                }

                switch (importItem?.Status)
                {
                    case LibraryImportItemStatuses.Rejected:
                    case LibraryImportItemStatuses.ProcessingFailed:
                        // 审核拒绝 / 处理失败：音频不会入库。标记 rejected（区别于音源失败的 failed），
                        // 重新下载不会改变结果，需重新排队才会重新走一遍。
                        if (pending.Status != PendingStatuses.Rejected)
                            await MarkPendingAsync(pending, PendingStatuses.Rejected, cancellationToken);
                        continue;
                    case LibraryImportItemStatuses.PendingReview:
                    case LibraryImportItemStatuses.PendingPublication:
                        // 音频已下载落地，但自动评分低于自动发布阈值，等管理员在「曲库导入」页发布。
                        // 这里标记 needs_review：不再重复下载，也不假装还在下载；发布后下一轮结算自动转正。
                        if (pending.Status != PendingStatuses.NeedsReview)
                            await MarkPendingAsync(pending, PendingStatuses.NeedsReview, cancellationToken);
                        continue;
                }
            }

            if (task.Status == ExternalDownloadTaskStatuses.Failed)
            {
                // 哈希重复终态：说明曲库已有同内容音频，按标题+艺人重匹配一次尝试直接转正。
                if (task.ErrorCode == ErrorCodes.SourceDuplicate)
                {
                    var artists = DeserializeArtists(pending.ArtistsJson);
                    var rematched = await MatchLibraryAsync(
                        pending.Title, artists, pending.DurationMs, cancellationToken, pending.AlbumTitle);
                    if (rematched.HasValue)
                    {
                        await PromotePendingAsync(pending, rematched.Value, cancellationToken);
                        continue;
                    }
                }
                await MarkPendingAsync(pending, PendingStatuses.Failed, cancellationToken);
                continue;
            }

            // 下载已完成却没有关联导入条目：既无法发布也无法转正，标记失败避免行永久卡在 downloading。
            if (task.Status == ExternalDownloadTaskStatuses.Completed)
            {
                await MarkPendingAsync(pending, PendingStatuses.Failed, cancellationToken);
                continue;
            }

            // 仍在流水线中（queued / downloading / import_queued）：标记 downloading，下一轮继续结算。
            if (pending.Status != PendingStatuses.Downloading)
                await MarkPendingAsync(pending, PendingStatuses.Downloading, cancellationToken);
        }
    }

    /// <summary>按 URL 选择解析器并解析；不支持的平台抛中文友好错误。返回平台标识与解析结果。</summary>
    private async Task<(string Platform, ParsedPlaylist Playlist)> ParseAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new PlaylistParseException("请粘贴歌单链接。");
        var parser = parsers.FirstOrDefault(candidate => candidate.CanHandle(url));
        if (parser is null)
            throw new PlaylistParseException("暂不支持该平台歌单，当前支持：网易云音乐、QQ 音乐、酷我音乐、酷狗音乐、汽水音乐、YouTube Music。");
        try
        {
            return (parser.Platform, await parser.ParseAsync(url.Trim(), cancellationToken));
        }
        catch (HttpRequestException exception)
        {
            throw new PlaylistParseException($"歌单解析失败（平台接口访问异常），请稍后重试。{exception.StatusCode}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlaylistParseException("歌单解析失败（平台接口响应超时），请稍后重试。");
        }
    }

    /// <summary>
    /// 曲库匹配：先按标题 + 艺人 + 专辑精确匹配；没有专辑命中时退回标题 + 艺人。
    /// 艺人始终为必需条件，避免同名异曲被直接转正。
    /// </summary>
    private async Task<Guid?> MatchLibraryAsync(
        string title,
        IReadOnlyList<string> artists,
        int? durationMs,
        CancellationToken cancellationToken,
        string? albumTitle = null)
    {
        var normalizedTitle = Normalize(title);
        var targetArtists = artists.Select(NormalizeLoose).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (normalizedTitle.Length == 0 || targetArtists.Count == 0)
            return null;

        var candidateTracks = await db.Queryable<Track>()
            .Where(track => track.PublishedAt != null && track.Title.ToLower() == normalizedTitle)
            .Select(track => new { track.Id, track.DurationMs, track.AlbumId })
            .ToListAsync(cancellationToken);
        if (candidateTracks.Count == 0)
            return null;
        var albumIds = candidateTracks
            .Where(candidate => candidate.AlbumId.HasValue)
            .Select(candidate => candidate.AlbumId!.Value)
            .Distinct()
            .ToList();
        var albumTitles = albumIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.Queryable<Album>()
                .Where(album => albumIds.Contains(album.Id))
                .Select(album => new { album.Id, album.Title })
                .ToListAsync(cancellationToken))
                .ToDictionary(album => album.Id, album => album.Title);
        var candidates = candidateTracks
            .Select(candidate => new
            {
                candidate.Id,
                candidate.DurationMs,
                AlbumTitle = candidate.AlbumId is { } albumId && albumTitles.TryGetValue(albumId, out var albumTitle)
                    ? albumTitle
                    : null
            })
            .ToList();

        // 艺人必须精确命中；不再用包含关系把不同艺人误认为同一人。
        var candidateIds = candidates.Select(candidate => candidate.Id).ToList();
        var artistRows = await db.Queryable<TrackArtist, Artist>((link, artist) => link.ArtistId == artist.Id)
            .Where((link, artist) => candidateIds.Contains(link.TrackId))
            .Select((link, artist) => new { link.TrackId, artist.Name })
            .ToListAsync(cancellationToken);
        var songAndArtistMatches = candidates.Where(candidate =>
        {
            var names = artistRows.Where(row => row.TrackId == candidate.Id)
                .Select(row => NormalizeLoose(row.Name)).ToList();
            return names.Any(targetArtists.Contains);
        }).ToList();
        var normalizedAlbum = NormalizeLoose(albumTitle);
        var albumMatches = normalizedAlbum.Length == 0
            ? []
            : songAndArtistMatches
                .Where(candidate => NormalizeLoose(candidate.AlbumTitle) == normalizedAlbum)
                .ToList();
        var matched = albumMatches.Count > 0 ? albumMatches : songAndArtistMatches;
        if (matched.Count == 0)
            return null;
        if (matched.Count == 1 || durationMs is null)
            return matched[0].Id;

        // 多候选：时长差 5 秒内取最接近者；都不在窗口内则取第一个标题/艺人命中项。
        return matched
            .Where(candidate => candidate.DurationMs.HasValue)
            .Select(candidate => (candidate.Id, Diff: Math.Abs(candidate.DurationMs!.Value - durationMs.Value)))
            .Where(entry => entry.Diff < 5000)
            .OrderBy(entry => entry.Diff)
            .Select(entry => (Guid?)entry.Id)
            .FirstOrDefault() ?? matched[0].Id;
    }

    /// <summary>
    /// 静默下载：候选必须匹配标题 + 艺人；有歌单专辑时优先要求专辑也匹配，
    /// 全部音源都没有专辑命中后才允许回退到标题 + 艺人。无命中直接标记 no_source，不建任务。
    /// </summary>
    private async Task TryStartSilentDownloadAsync(
        UserPlaylistPendingTrack pending, ParsedPlaylistTrack track, Guid userId, CancellationToken cancellationToken,
        int? searchBudgetSeconds = null, bool precheckDownloadable = true)
    {
        try
        {
            // 酷狗歌单：解析结果自带曲目 hash，直接建酷狗下载任务；酷狗不可用则回落全平台搜索。
            if (string.Equals(pending.SourcePlatform, "kugou", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(track.SourceTrackId)
                && await TryCreateDirectTaskAsync(pending, track, userId, cancellationToken, searchBudgetSeconds, precheckDownloadable))
                return;

            // 多音源搜索：并行搜所有启用 provider，单源超时不拖垮整体。
            var (candidates, diagnostics) = await SearchAllProvidersAsync(
                track, cancellationToken, searchBudgetSeconds, precheckDownloadable);
            if (candidates.Count == 0)
            {
                // 曲名带版本尾缀（"阿刁 - Live"）时，平台搜索结果可能因关键词过长而漏掉目标版本；
                // 用主标题（去括号说明与「- Live」尾缀）重搜一次，命中结果仍按原曲名/时长做匹配。
                var queryTitle = BuildQueryTitle(track.Title);
                if (queryTitle.Length > 0 && !string.Equals(queryTitle, track.Title.Trim(), StringComparison.Ordinal))
                {
                    var (retryCandidates, retryDiagnostics) = await SearchAllProvidersAsync(
                        track with { Title = queryTitle }, cancellationToken, searchBudgetSeconds, precheckDownloadable);
                    candidates = retryCandidates;
                    diagnostics = diagnostics.Concat(retryDiagnostics).ToList();
                    logger.LogInformation(
                        "Retried source search for {Title} by {Artists} with core title {CoreTitle}: {Count} candidates.",
                        track.Title, string.Join("/", track.Artists), queryTitle, candidates.Count);
                }
            }
            pending.SearchDiagnosticsJson = SerializeDiagnostics(diagnostics);
            if (candidates.Count == 0)
            {
                pending.Status = PendingStatuses.NoSource;
                logger.LogWarning(
                    "Playlist track {Title} by {Artists} (album {Album}) had no exact artist/title match after album-first fallback; no download task was created. Diagnostics: {Diagnostics}",
                    track.Title, string.Join("/", track.Artists), track.AlbumTitle, pending.SearchDiagnosticsJson);
                return;
            }

            // 按 SourceQualities.Rank() 排序：lossless → high → standard → unknown。
            var sorted = candidates
                .OrderBy(c => SourceQualities.Rank(c.Quality))
                .ToList();

            // 取音质最好的候选的主信息用于元数据快照。
            var primary = sorted[0];
            var metadata = new Dictionary<string, SourceSearchItemDto>
            {
                [primary.ProviderTrackId] = new SourceSearchItemDto(
                    primary.Provider, primary.ProviderTrackId, primary.Title, primary.Artists, primary.AlbumTitle,
                    primary.DurationMs, primary.Genres, primary.CoverUrl, primary.SourcePageUrl, primary.License,
                    primary.Quality, primary.IsLosslessAvailable, primary.ReleaseYear)
            };

            // 多候选换源模式：所有候选序列化进 CandidateProvidersJson，
            // 下载阶段 SourceDownloadJob 自动串行换源（第一源失败 → 换第二源 → ... → 全部失败标注原因）。
            var request = new CreateSourceDownloadTasksRequest
            {
                Provider = primary.Provider,
                ProviderTrackIds = [primary.ProviderTrackId],
                Metadata = metadata,
                Candidates = sorted.Select(c => new SourceSearchItemDto(
                    c.Provider, c.ProviderTrackId, c.Title, c.Artists, c.AlbumTitle,
                    c.DurationMs, c.Genres, c.CoverUrl, c.SourcePageUrl, c.License,
                    c.Quality, c.IsLosslessAvailable, c.ReleaseYear)).ToList()
            };
            var created = await sourceDownloadService.CreateTasksAsync(request, userId, cancellationToken);
            var taskRow = created.Created.Count > 0 ? created.Created[0] : null;
            taskRow ??= await db.Queryable<ExternalDownloadTask>().FirstAsync(
                    candidate => candidate.SourceProvider == primary.Provider && candidate.ProviderTrackId == primary.ProviderTrackId,
                    cancellationToken);
            if (taskRow is not null)
            {
                pending.DownloadTaskId = taskRow.Id;
                pending.Status = PendingStatuses.Downloading;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 静默下载是增强路径：任何单条失败都不阻塞导入，保持 pending 状态等待后续人工/重试。
            if (pending.Status == PendingStatuses.Downloading)
                return;
            pending.Status = PendingStatuses.Pending;
        }
    }

    /// <summary>
    /// 并行搜索所有启用音源；全局优先保留标题 + 艺人 + 专辑精确匹配，
    /// 只有一个专辑候选都没有时才回退到标题 + 艺人。之后再按时长、音质选候选。
    /// 支持音质阶梯的源先解析实际最高档，再用 CanDownloadAsync 过滤不可下载候选。
    /// </summary>
    private async Task<(List<SourceSearchResult> Candidates, List<ProviderSearchDiagnostic> Diagnostics)> SearchAllProvidersAsync(
        ParsedPlaylistTrack track, CancellationToken cancellationToken,
        int? budgetSecondsOverride = null, bool precheckDownloadable = true)
    {
        if (string.IsNullOrWhiteSpace(track.Title)
            || !track.Artists.Any(artist => !string.IsNullOrWhiteSpace(artist)))
        {
            return ([], [new ProviderSearchDiagnostic("all", SearchOutcomes.Skipped, 0, "曲名或艺人为空，未发起搜索")]);
        }

        var primaryArtist = track.Artists.First(artist => !string.IsNullOrWhiteSpace(artist));
        var query = $"{track.Title} {primaryArtist}";
        var budgetSeconds = Math.Clamp(budgetSecondsOverride ?? options.Value.SearchBudgetSeconds, 3, 300);
        var enabledProviders = providers.Where(p => p.IsEnabled).ToList();

        var searchTasks = enabledProviders.Select(async provider =>
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
            try
            {
                return (Provider: provider, Results: await provider.SearchAsync(query, 10, budgetCts.Token), Outcome: (string?)null, Message: (string?)null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Playlist source search timed out for provider {Provider}, track {Title} by {Artist}.",
                    provider.Provider, track.Title, primaryArtist);
                return (Provider: provider, Results: Array.Empty<SourceSearchResult>(), Outcome: (string?)SearchOutcomes.Timeout, Message: (string?)$"搜索超时（>{budgetSeconds} 秒）");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception,
                    "Playlist source search failed for provider {Provider}, track {Title} by {Artist}.",
                    provider.Provider, track.Title, primaryArtist);
                return (Provider: provider, Results: Array.Empty<SourceSearchResult>(), Outcome: (string?)SearchOutcomes.Error, Message: (string?)exception.Message);
            }
        });
        var providerResults = await Task.WhenAll(searchTasks);

        // 逐源诊断（只记录，不改变候选筛选）：超时/异常源以往被悄悄丢弃，这里显式落库。
        var songAndArtistMatches = providerResults
            .SelectMany(search => search.Results
                .Where(result => MatchesSongAndArtist(track, result))
                .Select(result => (search.Provider, Result: result)))
            .ToList();
        var normalizedAlbum = NormalizeLoose(track.AlbumTitle);
        List<(IMusicSourceProvider Provider, SourceSearchResult Result)> albumMatches = normalizedAlbum.Length == 0
            ? []
            : songAndArtistMatches
                .Where(candidate => NormalizeLoose(candidate.Result.AlbumTitle) == normalizedAlbum)
                .ToList();
        var fallbackMatches = new List<(IMusicSourceProvider Provider, SourceSearchResult Result)>();
        var selectedMatches = albumMatches.Count > 0 ? albumMatches : songAndArtistMatches;
        if (selectedMatches.Count == 0)
        {
            // 跨语言曲名兜底（如汽水 "BBIBBI" vs 网易云/酷狗原韩文 "삐삐"）：曲名对不上时，
            // 用「艺人 + 专辑 + 时长（±5 秒）」三元组确认同一曲目；三者必须同时命中，避免同名混淆。
            fallbackMatches = FindAlbumArtistDurationMatches(track, providerResults);
            if (fallbackMatches.Count == 0)
            {
                // 平台常给曲名加后缀（"背对背拥抱 (电视剧《爱情睡醒了》插曲)"、"阿刁 (歌手2018)"），
                // 标题全等会漏配；再以「标题包含 + 艺人 + 时长（±5 秒）」兜底，时长约束用于区分 Live/伴奏等版本。
                fallbackMatches = FindContainedTitleMatches(track, providerResults);
            }
            if (fallbackMatches.Count == 0)
            {
                // 版本标记差异（"阿刁 - Live" vs "阿刁 (歌手2018)"）：剥离括号说明与「- Live」等尾缀后比较主标题。
                fallbackMatches = FindCoreTitleMatches(track, providerResults);
            }
            selectedMatches = fallbackMatches;
            if (fallbackMatches.Count > 0)
            {
                logger.LogInformation(
                    "Matched {Title} by {Artist} via album/title fallback ({Count} candidates).",
                    track.Title, primaryArtist, fallbackMatches.Count);
            }
        }

        var diagnostics = providerResults.Select(outcome =>
        {
            if (outcome.Outcome is not null)
                return new ProviderSearchDiagnostic(outcome.Provider.Provider, outcome.Outcome, 0, outcome.Message);
            var matchedCount = outcome.Results.Count(result =>
                MatchesSongAndArtist(track, result)
                || fallbackMatches.Any(match => ReferenceEquals(match.Result, result)));
            return matchedCount > 0
                ? new ProviderSearchDiagnostic(outcome.Provider.Provider, SearchOutcomes.Matched, matchedCount, null)
                : new ProviderSearchDiagnostic(
                    outcome.Provider.Provider,
                    SearchOutcomes.NoMatch,
                    outcome.Results.Length,
                    outcome.Results.Length == 0 ? "未返回搜索结果" : $"返回 {outcome.Results.Length} 条结果，均未通过标题+艺人匹配");
        }).ToList();

        if (selectedMatches.Count == 0)
        {
            logger.LogWarning(
                "No source candidate matched artist {Artist} and title {Title}; album was {Album}.",
                primaryArtist, track.Title, track.AlbumTitle);
            return ([], diagnostics);
        }
        if (normalizedAlbum.Length > 0 && albumMatches.Count == 0)
        {
            logger.LogInformation(
                "No source candidate matched album {Album} for {Title} by {Artist}; falling back to exact artist/title matches.",
                track.AlbumTitle, track.Title, primaryArtist);
        }

        var bestByProvider = selectedMatches
            .GroupBy(candidate => candidate.Provider)
            .Select(group => (
                Provider: group.Key,
                Result: PickBestCandidate(group.Select(candidate => candidate.Result), track)!))
            .ToList();

        var candidateTasks = bestByProvider.Select(async selection =>
        {
            var provider = selection.Provider;
            var best = selection.Result;
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));

            // Search metadata cannot know whether a Kuwo track has an actual FLAC URL.
            // Resolve its quality ladder before cross-provider sorting; keep the conservative
            // search quality if this optional enrichment times out or fails.
            if (provider is IQualityLadderProvider ladder)
            {
                try
                {
                    var qualityCandidates = await ladder.ResolveDownloadCandidatesAsync(best.ProviderTrackId, budgetCts.Token);
                    if (qualityCandidates.Count > 0)
                    {
                        best = best with
                        {
                            Quality = qualityCandidates[0].Quality,
                            IsLosslessAvailable = qualityCandidates.Any(quality => quality.IsLossless)
                        };
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Playlist backfill quality lookup timed out for provider {Provider}, track {TrackId}; using search quality {Quality}.",
                        provider.Provider, best.ProviderTrackId, best.Quality);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "Playlist backfill quality lookup failed for provider {Provider}, track {TrackId}; using search quality {Quality}.",
                        provider.Provider, best.ProviderTrackId, best.Quality);
                }
            }

            // 批量补齐模式跳过可下载预判：预判会再消耗一份预算，且下载阶段的换源机制已能兜底，
            // 对「几百首排队」的批量场景性价比太低（单首耗时直接翻倍）。
            if (!precheckDownloadable)
                return best;

            // 轻量预判可下载性：不可下载的候选直接过滤（不浪费换源配额）。
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkCts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
            try
            {
                var canDownload = await provider.CanDownloadAsync(best.ProviderTrackId, checkCts.Token);
                return canDownload ? best : null;
            }
            catch
            {
                return best; // 预判失败时保守保留
            }
        });
        var selected = (await Task.WhenAll(candidateTasks))
            .Where(result => result is not null)
            .Select(result => result!)
            .ToList();

        // 命中标题+艺人、但被可下载预判过滤掉的源，诊断改为 unavailable（区别于"没搜到"）。
        var keptProviders = selected.Select(item => item.Provider).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalDiagnostics = diagnostics
            .Select(diagnostic => diagnostic.Outcome == SearchOutcomes.Matched
                && !keptProviders.Contains(diagnostic.Provider)
                ? diagnostic with { Outcome = SearchOutcomes.Unavailable, Message = "命中标题+艺人，但预判为不可下载，未进入候选" }
                : diagnostic)
            .ToList();
        return (selected, finalDiagnostics);
    }

    /// <summary>序列化搜索诊断；无诊断时返回 null（历史数据无该字段）。</summary>
    private static string? SerializeDiagnostics(IReadOnlyList<ProviderSearchDiagnostic> diagnostics)
        => diagnostics.Count == 0 ? null : JsonSerializer.Serialize(diagnostics, DiagnosticsJsonOptions);

    /// <summary>反序列化搜索诊断；缺失或格式异常时返回空列表。</summary>
    private static List<ProviderSearchDiagnostic> DeserializeDiagnostics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<ProviderSearchDiagnostic>>(json, DiagnosticsJsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// 酷狗直连建任务：用解析出的 hash 直接创建酷狗下载任务（元数据取自解析结果），
    /// 同时搜索其他启用音源作为换源候选——酷狗失败时自动按音质排序逐个尝试其他源。
    /// 成功返回 true；酷狗不可用/建任务失败返回 false 以回落全平台搜索。
    /// </summary>
    private async Task<bool> TryCreateDirectTaskAsync(
        UserPlaylistPendingTrack pending, ParsedPlaylistTrack track, Guid userId, CancellationToken cancellationToken,
        int? searchBudgetSeconds = null, bool precheckDownloadable = true)
    {
        var kugou = providers.FirstOrDefault(candidate => candidate.Provider == "kugou");
        if (kugou is null || !kugou.IsEnabled)
            return false;

        // 酷狗直连候选（hash 已知，零搜索错配）。
        var kugouCandidate = new SourceSearchItemDto(
            kugou.Provider, track.SourceTrackId!, track.Title, track.Artists, track.AlbumTitle,
            track.DurationMs, [], null, track.SourceUrl, null,
            SourceQualities.Standard, false);

        // 并行搜索其他启用音源，作为酷狗失败时的换源候选。
        var (otherCandidates, diagnostics) = await SearchAllProvidersAsync(track, cancellationToken, searchBudgetSeconds, precheckDownloadable);
        var allCandidates = new List<SourceSearchItemDto>();
        if (MatchesSongAndArtist(track, kugouCandidate))
        {
            allCandidates.Add(kugouCandidate);
            // 歌单直连的酷狗候选没有经过搜索阶段，补一条诊断说明它为何在候选里。
            diagnostics.Insert(0, new ProviderSearchDiagnostic(
                kugou.Provider, SearchOutcomes.Matched, 1, "歌单直连（曲目 hash 命中，无需搜索）"));
        }
        // 去重（避免酷狗直连和搜索结果重复），按音质排序。
        allCandidates.AddRange(otherCandidates
            .Where(c => !string.Equals(c.Provider, kugou.Provider, StringComparison.OrdinalIgnoreCase)
                || c.ProviderTrackId != track.SourceTrackId)
            .Select(c => new SourceSearchItemDto(
                c.Provider, c.ProviderTrackId, c.Title, c.Artists, c.AlbumTitle,
                c.DurationMs, c.Genres, c.CoverUrl, c.SourcePageUrl, c.License,
                c.Quality, c.IsLosslessAvailable, c.ReleaseYear)));
        pending.SearchDiagnosticsJson = SerializeDiagnostics(diagnostics);
        var normalizedAlbum = NormalizeLoose(track.AlbumTitle);
        List<SourceSearchItemDto> albumMatches = normalizedAlbum.Length == 0
            ? []
            : allCandidates.Where(candidate => NormalizeLoose(candidate.AlbumTitle) == normalizedAlbum).ToList();
        if (albumMatches.Count > 0)
        {
            allCandidates = albumMatches;
        }
        else if (normalizedAlbum.Length > 0 && allCandidates.Count > 0)
        {
            logger.LogInformation(
                "No source candidate matched album {Album} for {Title}; using exact artist/title fallback.",
                track.AlbumTitle, track.Title);
        }
        if (allCandidates.Count == 0)
        {
            pending.Status = PendingStatuses.NoSource;
            logger.LogWarning(
                "Kugou playlist track {Title} by {Artists} (album {Album}) had no exact artist/title match; no download task was created.",
                track.Title, string.Join("/", track.Artists), track.AlbumTitle);
            return true;
        }

        var sorted = allCandidates
            .OrderBy(c => SourceQualities.Rank(c.Quality))
            .ToList();

        var primary = sorted[0];
        var metadata = new Dictionary<string, SourceSearchItemDto>
        {
            [primary.ProviderTrackId] = primary
        };

        var request = new CreateSourceDownloadTasksRequest
        {
            Provider = primary.Provider,
            ProviderTrackIds = [primary.ProviderTrackId],
            Metadata = metadata,
            Candidates = sorted
        };
        var created = await sourceDownloadService.CreateTasksAsync(request, userId, cancellationToken);
        var taskRow = created.Created.Count > 0 ? created.Created[0] : null;
        taskRow ??= await db.Queryable<ExternalDownloadTask>().FirstAsync(
                candidate => candidate.SourceProvider == primary.Provider && candidate.ProviderTrackId == primary.ProviderTrackId,
                cancellationToken);
        if (taskRow is null)
            return false;
        pending.DownloadTaskId = taskRow.Id;
        pending.Status = PendingStatuses.Downloading;
        return true;
    }

    /// <summary>候选挑选：在已通过标题/艺人/专辑校验的结果中优先选时长差 3 秒内最接近者。</summary>
    private static SourceSearchResult? PickBestCandidate(IEnumerable<SourceSearchResult> results, ParsedPlaylistTrack track)
    {
        var candidates = results.ToList();
        if (candidates.Count == 0)
            return null;
        if (track.DurationMs is { } duration)
        {
            var withinWindow = candidates
                .Where(candidate => candidate.DurationMs.HasValue)
                .Select(candidate => (Candidate: candidate, Diff: Math.Abs(candidate.DurationMs!.Value - duration)))
                .Where(entry => entry.Diff < 3000)
                .OrderBy(entry => entry.Diff)
                .ToList();
            if (withinWindow.Count > 0)
                return withinWindow[0].Candidate;
        }
        return candidates[0];
    }

    private static bool MatchesSongAndArtist(ParsedPlaylistTrack track, SourceSearchResult candidate)
        => MatchesSongAndArtist(track.Title, track.Artists, candidate.Title, candidate.Artists);

    private static bool MatchesSongAndArtist(ParsedPlaylistTrack track, SourceSearchItemDto candidate)
        => MatchesSongAndArtist(track.Title, track.Artists, candidate.Title, candidate.Artists);

    private static bool MatchesSongAndArtist(
        string expectedTitle,
        IReadOnlyList<string> expectedArtists,
        string candidateTitle,
        IReadOnlyList<string> candidateArtists)
    {
        expectedTitle = NormalizeLoose(expectedTitle);
        if (expectedTitle.Length == 0 || NormalizeLoose(candidateTitle) != expectedTitle)
            return false;

        var targetArtists = expectedArtists
            .Select(NormalizeLoose)
            .Where(artist => artist.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return targetArtists.Count > 0
            && candidateArtists
                .Select(NormalizeLoose)
                .Any(targetArtists.Contains);
    }

    /// <summary>
    /// 跨语言曲名兜底匹配：部分平台对同一首歌使用不同语言的曲名（汽水 "BBIBBI" vs 韩文原名 "삐삐"），
    /// 标题比较必然失败。此时用「艺人 + 专辑 + 时长（±5 秒）」三元组确认，三者必须同时命中。
    /// </summary>
    private static List<(IMusicSourceProvider Provider, SourceSearchResult Result)> FindAlbumArtistDurationMatches(
        ParsedPlaylistTrack track,
        (IMusicSourceProvider Provider, SourceSearchResult[] Results, string? Outcome, string? Message)[] providerResults)
    {
        var targetAlbum = NormalizeLoose(track.AlbumTitle);
        var targetArtists = track.Artists
            .Select(NormalizeLoose)
            .Where(artist => artist.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (targetAlbum.Length == 0 || targetArtists.Count == 0 || track.DurationMs is not { } durationMs || durationMs <= 0)
            return [];

        return providerResults
            .SelectMany(search => search.Results
                .Where(result => result.DurationMs is { } candidateDuration
                    && Math.Abs(candidateDuration - durationMs) <= 5000
                    && NormalizeLoose(result.AlbumTitle) == targetAlbum
                    && result.Artists.Select(NormalizeLoose).Any(targetArtists.Contains))
                .Select(result => (search.Provider, Result: result)))
            .ToList();
    }

    /// <summary>
    /// 标题包含兜底：平台常给曲名追加后缀（"背对背拥抱 (电视剧《爱情睡醒了》插曲)"、"阿刁 (歌手2018)"），
    /// 标题全等会漏配。改为「一方标题包含另一方 + 艺人命中 + 时长 ±5 秒」，时长约束用于排除 Live/伴奏等版本。
    /// </summary>
    private static List<(IMusicSourceProvider Provider, SourceSearchResult Result)> FindContainedTitleMatches(
        ParsedPlaylistTrack track,
        (IMusicSourceProvider Provider, SourceSearchResult[] Results, string? Outcome, string? Message)[] providerResults)
    {
        var targetTitle = NormalizeLoose(track.Title);
        var targetArtists = track.Artists
            .Select(NormalizeLoose)
            .Where(artist => artist.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (targetTitle.Length < 2 || targetArtists.Count == 0 || track.DurationMs is not { } durationMs || durationMs <= 0)
            return [];

        return providerResults
            .SelectMany(search => search.Results
                .Where(result =>
                {
                    var title = NormalizeLoose(result.Title);
                    if (title.Length < 2 || !(title.Contains(targetTitle, StringComparison.Ordinal)
                            || targetTitle.Contains(title, StringComparison.Ordinal)))
                        return false;
                    return result.DurationMs is { } candidateDuration
                        && Math.Abs(candidateDuration - durationMs) <= 5000
                        && result.Artists.Select(NormalizeLoose).Any(targetArtists.Contains);
                })
                .Select(result => (search.Provider, Result: result)))
            .ToList();
    }

    /// <summary>
    /// 主标题兜底：剥离括号说明（"（歌手2018）"）与「 - Live」等尾缀后再比较主标题，
    /// 解决同曲不同版本标注（"阿刁 - Live" vs "阿刁 (歌手2018)"）；仍要求艺人命中且时长 ±5 秒。
    /// </summary>
    private static List<(IMusicSourceProvider Provider, SourceSearchResult Result)> FindCoreTitleMatches(
        ParsedPlaylistTrack track,
        (IMusicSourceProvider Provider, SourceSearchResult[] Results, string? Outcome, string? Message)[] providerResults)
    {
        var targetTitle = CoreTitle(track.Title);
        var targetArtists = track.Artists
            .Select(NormalizeLoose)
            .Where(artist => artist.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (targetTitle.Length < 2 || targetArtists.Count == 0 || track.DurationMs is not { } durationMs || durationMs <= 0)
            return [];

        return providerResults
            .SelectMany(search => search.Results
                .Where(result =>
                    CoreTitle(result.Title) == targetTitle
                    && result.DurationMs is { } candidateDuration
                    && Math.Abs(candidateDuration - durationMs) <= 5000
                    && result.Artists.Select(NormalizeLoose).Any(targetArtists.Contains))
                .Select(result => (search.Provider, Result: result)))
            .ToList();
    }

    /// <summary>主标题：去括号说明、去分隔符后的版本部分、去常见版本词，再松散归一化。</summary>
    private static string CoreTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return NormalizeLoose(BuildQueryTitle(value));
    }

    /// <summary>用于搜索的关键词主体：去掉括号说明（"（歌手2018）"）与「- Live」形式的版本尾缀，保留原始大小写。</summary>
    private static string BuildQueryTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;
        var text = BracketContentRegex().Replace(title.Normalize(NormalizationForm.FormKC), " ").Trim();
        var separatorIndex = text.IndexOfAny(['-', '－', '–', '—', '|', '/']);
        if (separatorIndex > 0)
            text = text[..separatorIndex].Trim();
        return text.Length == 0 ? title.Trim() : text;
    }

    /// <summary>转正：pending → UserPlaylistTrack（保留原 SortOrder），随后删除 pending 行。</summary>
    private async Task PromotePendingAsync(UserPlaylistPendingTrack pending, Guid trackId, CancellationToken cancellationToken)
    {
        var alreadyLinked = await db.Queryable<UserPlaylistTrack>()
            .AnyAsync(link => link.PlaylistId == pending.PlaylistId && link.TrackId == trackId, cancellationToken);
        if (!alreadyLinked)
        {
            await db.Insertable(new UserPlaylistTrack
            {
                PlaylistId = pending.PlaylistId,
                TrackId = trackId,
                SortOrder = pending.SortOrder,
                AddedAtUtc = DateTime.UtcNow
            }).ExecuteCommandAsync(cancellationToken);
        }
        await db.Deleteable<UserPlaylistPendingTrack>()
            .Where(item => item.Id == pending.Id)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>幂等插轨：同 (PlaylistId, TrackId) 已存在则跳过。</summary>
    private async Task TryInsertPlaylistTrackAsync(Guid playlistId, Guid trackId, int sortOrder, CancellationToken cancellationToken)
    {
        var exists = await db.Queryable<UserPlaylistTrack>()
            .AnyAsync(link => link.PlaylistId == playlistId && link.TrackId == trackId, cancellationToken);
        if (exists)
            return;
        await db.Insertable(new UserPlaylistTrack
        {
            PlaylistId = playlistId,
            TrackId = trackId,
            SortOrder = sortOrder,
            AddedAtUtc = DateTime.UtcNow
        }).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 重新补齐歌单的待处理曲目：对 pending / no_source / failed 以及「下载任务已丢失」的 downloading 行重跑匹配——
    /// 先按标题+艺人+时长匹配曲库直接转正；未命中则重置为 pending，交由后台补齐任务异步搜索建任务。
    /// </summary>
    /// <remarks>
    /// 与导入一致：本方法不做音源搜索，避免 200+ 首在请求线程内串行执行导致超时中断。
    /// 返回的 Retried 表示「已排入后台补齐队列」的数量。
    /// </remarks>
    public async Task<PlaylistRematchResult> RematchPendingAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        var playlist = await db.Queryable<UserPlaylist>()
            .FirstAsync(candidate => candidate.Id == playlistId, cancellationToken)
            ?? throw new InvalidOperationException("歌单不存在。");
        var pendings = await db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.PlaylistId == playlistId)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);

        int promoted = 0, retried = 0, noSource = 0, unchanged = 0;
        foreach (var pending in pendings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 下载中的行：任务仍在流水线就保持不动；任务已被删除则视为丢失，重新排队。
            if (pending.Status == PendingStatuses.Downloading && pending.DownloadTaskId is { } taskId)
            {
                var alive = await db.Queryable<ExternalDownloadTask>()
                    .AnyAsync(candidate => candidate.Id == taskId, cancellationToken);
                if (alive)
                {
                    unchanged++;
                    continue;
                }
            }

            // 待审核的行：音频已下载完成、等管理员发布。重跑一次曲库匹配，命中则直接转正；
            // 否则保持不动——重复下载只会产生重复导入条目。
            if (pending.Status == PendingStatuses.NeedsReview)
            {
                var reviewArtists = DeserializeArtists(pending.ArtistsJson);
                var reviewMatched = await MatchLibraryAsync(
                    pending.Title, reviewArtists, pending.DurationMs, cancellationToken, pending.AlbumTitle);
                if (reviewMatched.HasValue)
                {
                    await PromotePendingAsync(pending, reviewMatched.Value, cancellationToken);
                    promoted++;
                }
                else
                {
                    unchanged++;
                }
                continue;
            }

            var artists = DeserializeArtists(pending.ArtistsJson);
            var matched = await MatchLibraryAsync(
                pending.Title, artists, pending.DurationMs, cancellationToken, pending.AlbumTitle);
            if (matched.HasValue)
            {
                await PromotePendingAsync(pending, matched.Value, cancellationToken);
                promoted++;
                continue;
            }

            // 未命中曲库：重置为 pending 排队，后台补齐任务会搜索音源并建下载任务。
            pending.DownloadTaskId = null;
            pending.Status = PendingStatuses.Pending;
            pending.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(pending).ExecuteCommandAsync(cancellationToken);
            retried++;
        }

        return new PlaylistRematchResult(playlistId, pendings.Count, promoted, retried, noSource, unchanged);
    }

    /// <summary>更新 pending 行状态；状态未变化时跳过写库（结算每轮都会跑到，避免无意义 UPDATE）。</summary>
    private async Task MarkPendingAsync(UserPlaylistPendingTrack pending, string status, CancellationToken cancellationToken)
    {
        if (pending.Status == status)
            return;
        pending.Status = status;
        pending.UpdatedAtUtc = DateTime.UtcNow;
        await db.Updateable(pending).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>归一化：压缩连续空白 + trim + 小写，用于标题/艺人宽松比较。</summary>
    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex().Replace(value, " ").Trim().ToLowerInvariant();

    /// <summary>
    /// 更宽松的归一化：仅保留字母与数字（含 CJK），丢弃空格与全部标点。
    /// 各平台对空格/标点的写法不一致（"G.E.M. 邓紫棋" vs "G.E.M.邓紫棋"、
    /// "阿刁 - Live" vs "阿刁 (Live)"），严格归一化会把这些同一首歌判成不同曲目。
    /// 注意：查询曲库时仍用 <see cref="Normalize"/>（SQL 端按 Title 原值比较），此方法只用于跨源比较。
    /// </summary>
    private static string NormalizeLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static IReadOnlyList<string> DeserializeArtists(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>匹配各种括号说明：全/半角圆括号、方括号、直角括号。</summary>
    [GeneratedRegex(@"[（(【\[][^）)】\]]*[）)】\]]")]
    private static partial Regex BracketContentRegex();
}
