// 职责：歌单导入编排层——解析预览（逐曲匹配曲库）、导入落库（建歌单 + 已匹配插轨 +
// 未匹配建 pending 并触发多音源静默下载）、以及歌单详情读取时的 pending 懒结算
// （下载发布完成 → 转正为 UserPlaylistTrack；失败终态标记）。

using System.Diagnostics;
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
        var targetArtists = artists.Select(Normalize).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
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
                .Select(row => Normalize(row.Name)).ToList();
            return names.Any(targetArtists.Contains);
        }).ToList();
        var normalizedAlbum = Normalize(albumTitle);
        var albumMatches = normalizedAlbum.Length == 0
            ? []
            : songAndArtistMatches
                .Where(candidate => Normalize(candidate.AlbumTitle) == normalizedAlbum)
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
            var candidates = await SearchAllProvidersAsync(track, cancellationToken, searchBudgetSeconds, precheckDownloadable);
            if (candidates.Count == 0)
            {
                pending.Status = PendingStatuses.NoSource;
                logger.LogWarning(
                    "Playlist track {Title} by {Artists} (album {Album}) had no exact artist/title match after album-first fallback; no download task was created.",
                    track.Title, string.Join("/", track.Artists), track.AlbumTitle);
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
                    candidate => candidate.Provider == primary.Provider && candidate.ProviderTrackId == primary.ProviderTrackId,
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
    private async Task<List<SourceSearchResult>> SearchAllProvidersAsync(
        ParsedPlaylistTrack track, CancellationToken cancellationToken,
        int? budgetSecondsOverride = null, bool precheckDownloadable = true)
    {
        if (string.IsNullOrWhiteSpace(track.Title)
            || !track.Artists.Any(artist => !string.IsNullOrWhiteSpace(artist)))
        {
            return [];
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
                return (Provider: provider, Results: await provider.SearchAsync(query, 10, budgetCts.Token));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Playlist source search timed out for provider {Provider}, track {Title} by {Artist}.",
                    provider.Provider, track.Title, primaryArtist);
                return (Provider: provider, Results: Array.Empty<SourceSearchResult>());
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception,
                    "Playlist source search failed for provider {Provider}, track {Title} by {Artist}.",
                    provider.Provider, track.Title, primaryArtist);
                return (Provider: provider, Results: Array.Empty<SourceSearchResult>());
            }
        });
        var providerResults = await Task.WhenAll(searchTasks);
        var songAndArtistMatches = providerResults
            .SelectMany(search => search.Results
                .Where(result => MatchesSongAndArtist(track, result))
                .Select(result => (search.Provider, Result: result)))
            .ToList();
        var normalizedAlbum = Normalize(track.AlbumTitle);
        List<(IMusicSourceProvider Provider, SourceSearchResult Result)> albumMatches = normalizedAlbum.Length == 0
            ? []
            : songAndArtistMatches
                .Where(candidate => Normalize(candidate.Result.AlbumTitle) == normalizedAlbum)
                .ToList();
        var selectedMatches = albumMatches.Count > 0 ? albumMatches : songAndArtistMatches;
        if (selectedMatches.Count == 0)
        {
            logger.LogWarning(
                "No source candidate matched artist {Artist} and title {Title}; album was {Album}.",
                primaryArtist, track.Title, track.AlbumTitle);
            return [];
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
        return (await Task.WhenAll(candidateTasks))
            .Where(result => result is not null)
            .Select(result => result!)
            .ToList();
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
        var otherCandidates = await SearchAllProvidersAsync(track, cancellationToken, searchBudgetSeconds, precheckDownloadable);
        var allCandidates = new List<SourceSearchItemDto>();
        if (MatchesSongAndArtist(track, kugouCandidate))
            allCandidates.Add(kugouCandidate);
        // 去重（避免酷狗直连和搜索结果重复），按音质排序。
        allCandidates.AddRange(otherCandidates
            .Where(c => !string.Equals(c.Provider, kugou.Provider, StringComparison.OrdinalIgnoreCase)
                || c.ProviderTrackId != track.SourceTrackId)
            .Select(c => new SourceSearchItemDto(
                c.Provider, c.ProviderTrackId, c.Title, c.Artists, c.AlbumTitle,
                c.DurationMs, c.Genres, c.CoverUrl, c.SourcePageUrl, c.License,
                c.Quality, c.IsLosslessAvailable, c.ReleaseYear)));
        var normalizedAlbum = Normalize(track.AlbumTitle);
        List<SourceSearchItemDto> albumMatches = normalizedAlbum.Length == 0
            ? []
            : allCandidates.Where(candidate => Normalize(candidate.AlbumTitle) == normalizedAlbum).ToList();
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
                candidate => candidate.Provider == primary.Provider && candidate.ProviderTrackId == primary.ProviderTrackId,
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
        expectedTitle = Normalize(expectedTitle);
        if (expectedTitle.Length == 0 || Normalize(candidateTitle) != expectedTitle)
            return false;

        var targetArtists = expectedArtists
            .Select(Normalize)
            .Where(artist => artist.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return targetArtists.Count > 0
            && candidateArtists
                .Select(Normalize)
                .Any(targetArtists.Contains);
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

    private static IReadOnlyList<string> DeserializeArtists(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
