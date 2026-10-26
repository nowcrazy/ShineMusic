// 职责：歌单导入编排层——解析预览（逐曲匹配曲库）、导入落库（建歌单 + 已匹配插轨 +
// 未匹配建 pending 并触发酷我静默下载）、以及歌单详情读取时的 pending 懒结算
// （下载发布完成 → 转正为 UserPlaylistTrack；失败终态标记）。

using System.Text.Json;
using System.Text.RegularExpressions;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Contracts;
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

/// <summary>歌单导入编排服务。</summary>
public sealed partial class PlaylistImportService(
    ISqlSugarClient db,
    IEnumerable<IPlaylistParser> parsers,
    IEnumerable<IMusicSourceProvider> providers,
    SourceDownloadService sourceDownloadService)
{
    /// <summary>pending 状态常量。</summary>
    public static class PendingStatuses
    {
        public const string Pending = "pending";
        public const string Downloading = "downloading";
        public const string NoSource = "no_source";
        public const string Failed = "failed";
    }

    /// <summary>选择 parser 并解析歌单，逐曲匹配曲库后返回预览。</summary>
    public async Task<PlaylistParsePreview> ParsePreviewAsync(string url, CancellationToken cancellationToken)
    {
        var (parser, parsed) = await ParseAsync(url, cancellationToken);
        var tracks = new List<PlaylistPreviewTrack>(parsed.Tracks.Count);
        foreach (var track in parsed.Tracks)
        {
            var matchedId = await MatchLibraryAsync(track.Title, track.Artists, track.DurationMs, cancellationToken);
            tracks.Add(new PlaylistPreviewTrack(
                track.Title, track.Artists, track.AlbumTitle, track.DurationMs,
                matchedId.HasValue, matchedId));
        }
        return new PlaylistParsePreview(parser.Platform, parsed.Name, parsed.CoverUrl, tracks.Count, tracks);
    }

    /// <summary>
    /// 导入歌单：建 UserPlaylist（立即生成）→ 已匹配曲目插 UserPlaylistTrack（保原顺序）→
    /// 未匹配曲目建 pending 行并用酷我音源搜索最佳候选触发静默下载（单条失败不影响整体）。
    /// </summary>
    public async Task<PlaylistImportResult> ImportAsync(string url, string? name, Guid userId, CancellationToken cancellationToken)
    {
        var (parser, parsed) = await ParseAsync(url, cancellationToken);
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
            var track = parsed.Tracks[index];
            var matchedId = await MatchLibraryAsync(track.Title, track.Artists, track.DurationMs, cancellationToken);
            if (matchedId.HasValue)
            {
                await TryInsertPlaylistTrackAsync(playlist.Id, matchedId.Value, index, cancellationToken);
                matchedCount++;
                continue;
            }

            var pending = new UserPlaylistPendingTrack
            {
                PlaylistId = playlist.Id,
                Title = track.Title,
                ArtistsJson = JsonSerializer.Serialize(track.Artists),
                AlbumTitle = track.AlbumTitle,
                DurationMs = track.DurationMs,
                SourcePlatform = parser.Platform,
                SourceTrackId = track.SourceTrackId,
                SourceUrl = track.SourceUrl,
                SortOrder = index,
                Status = PendingStatuses.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            await TryStartSilentDownloadAsync(pending, track, userId, cancellationToken);
            await db.Insertable(pending).ExecuteCommandAsync(cancellationToken);
            pendingCount++;
        }

        return new PlaylistImportResult(playlist.Id, parsed.Tracks.Count, matchedCount, pendingCount);
    }

    /// <summary>
    /// 懒结算：歌单详情读取时调用，把下载已发布完成的 pending 行转正为正式曲目，
    /// 下载失败（含哈希重复后重匹配）的行更新为终态。
    /// </summary>
    public async Task ResolvePendingAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        var pendings = await db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.PlaylistId == playlistId
                && (item.Status == PendingStatuses.Pending || item.Status == PendingStatuses.Downloading))
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
                await MarkPendingAsync(pending, PendingStatuses.Failed, cancellationToken);
                continue;
            }

            // 下载完成链路：task.ImportItemId → LibraryImportItem.PublishedTrackId 非空即已发布可播放。
            if (task.ImportItemId is { } importItemId)
            {
                var publishedTrackId = await db.Queryable<LibraryImportItem>()
                    .Where(item => item.Id == importItemId)
                    .Select(item => item.PublishedTrackId)
                    .FirstAsync(cancellationToken);
                if (publishedTrackId is { } trackId)
                {
                    await PromotePendingAsync(pending, trackId, cancellationToken);
                    continue;
                }
            }

            if (task.Status == ExternalDownloadTaskStatuses.Failed)
            {
                // 哈希重复终态：说明曲库已有同内容音频，按标题+艺人重匹配一次尝试直接转正。
                if (task.ErrorCode == ErrorCodes.SourceDuplicate)
                {
                    var artists = DeserializeArtists(pending.ArtistsJson);
                    var rematched = await MatchLibraryAsync(pending.Title, artists, pending.DurationMs, cancellationToken);
                    if (rematched.HasValue)
                    {
                        await PromotePendingAsync(pending, rematched.Value, cancellationToken);
                        continue;
                    }
                }
                await MarkPendingAsync(pending, PendingStatuses.Failed, cancellationToken);
                continue;
            }

            // 仍在流水线中：标记 downloading（completed 但未发布则保持原状，等待发布）。
            if (pending.Status != PendingStatuses.Downloading)
                await MarkPendingAsync(pending, PendingStatuses.Downloading, cancellationToken);
        }
    }

    /// <summary>按 URL 选择解析器并解析；不支持的平台抛中文友好错误。</summary>
    private async Task<(IPlaylistParser Parser, ParsedPlaylist Playlist)> ParseAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new PlaylistParseException("请粘贴歌单链接。");
        var parser = parsers.FirstOrDefault(candidate => candidate.CanHandle(url));
        if (parser is null)
            throw new PlaylistParseException("暂不支持该平台歌单，当前支持：网易云音乐、QQ 音乐、酷我音乐、YouTube Music。");
        try
        {
            return (parser, await parser.ParseAsync(url.Trim(), cancellationToken));
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
    /// 曲库匹配：归一化（trim + lower + 压缩空白）后标题相等的已发布曲目中，
    /// 要求任一艺人名归一化相等或互相包含；多候选取时长差 5 秒内最小者。
    /// </summary>
    private async Task<Guid?> MatchLibraryAsync(string title, IReadOnlyList<string> artists, int? durationMs, CancellationToken cancellationToken)
    {
        var normalizedTitle = Normalize(title);
        if (normalizedTitle.Length == 0)
            return null;

        var candidates = await db.Queryable<Track>()
            .Where(track => track.PublishedAt != null && track.Title.ToLower() == normalizedTitle)
            .Select(track => new { track.Id, track.DurationMs })
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return null;

        // 艺人过滤：候选曲目的艺人集合与目标艺人有任一「相等/包含」即视为匹配。
        var candidateIds = candidates.Select(candidate => candidate.Id).ToList();
        var artistRows = await db.Queryable<TrackArtist, Artist>((link, artist) => link.ArtistId == artist.Id)
            .Where((link, artist) => candidateIds.Contains(link.TrackId))
            .Select((link, artist) => new { link.TrackId, artist.Name })
            .ToListAsync(cancellationToken);
        var targetArtists = artists.Select(Normalize).Where(value => value.Length > 0).ToList();
        var matched = candidates.Where(candidate =>
        {
            var names = artistRows.Where(row => row.TrackId == candidate.Id)
                .Select(row => Normalize(row.Name)).ToList();
            if (targetArtists.Count == 0 || names.Count == 0)
                return true; // 任一侧无艺人信息时退化为标题匹配。
            return targetArtists.Any(target => names.Any(name =>
                name == target || name.Contains(target, StringComparison.Ordinal)
                || target.Contains(name, StringComparison.Ordinal)));
        }).ToList();
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

    /// <summary>静默下载：酷我音源搜索最佳候选并逐条建任务；搜不到/不可用则标记 no_source。</summary>
    private async Task TryStartSilentDownloadAsync(
        UserPlaylistPendingTrack pending, ParsedPlaylistTrack track, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var kuwo = providers.FirstOrDefault(candidate => candidate.Provider == "kuwo");
            if (kuwo is null || !kuwo.IsEnabled)
            {
                pending.Status = PendingStatuses.NoSource;
                return;
            }

            var query = track.Artists.Count > 0 ? $"{track.Title} {track.Artists[0]}" : track.Title;
            var results = await kuwo.SearchAsync(query, 5, cancellationToken);
            var best = PickBestCandidate(results, track);
            if (best is null)
            {
                pending.Status = PendingStatuses.NoSource;
                return;
            }

            var created = await sourceDownloadService.CreateTasksAsync("kuwo", [best.ProviderTrackId], userId, cancellationToken);
            var taskRow = created.Created.Count > 0 ? created.Created[0] : null;
            taskRow ??= await db.Queryable<ExternalDownloadTask>().FirstAsync(
                    candidate => candidate.Provider == "kuwo" && candidate.ProviderTrackId == best.ProviderTrackId,
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

    /// <summary>候选挑选：标题归一化相等优先，其次时长最接近（3 秒窗口内）。</summary>
    private static SourceSearchResult? PickBestCandidate(IReadOnlyCollection<SourceSearchResult> results, ParsedPlaylistTrack track)
    {
        if (results.Count == 0)
            return null;
        var normalizedTitle = Normalize(track.Title);
        var titleMatched = results
            .Where(candidate => Normalize(candidate.Title) == normalizedTitle)
            .ToList();
        var pool = titleMatched.Count > 0 ? titleMatched : results.ToList();
        if (track.DurationMs is { } duration)
        {
            var withinWindow = pool
                .Where(candidate => candidate.DurationMs.HasValue)
                .Select(candidate => (Candidate: candidate, Diff: Math.Abs(candidate.DurationMs!.Value - duration)))
                .Where(entry => entry.Diff < 3000)
                .OrderBy(entry => entry.Diff)
                .ToList();
            if (withinWindow.Count > 0)
                return withinWindow[0].Candidate;
        }
        // 无时长信息时只在标题精确命中的池子里取第一，避免错抓同名异曲。
        return titleMatched.Count > 0 ? pool[0] : null;
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

    /// <summary>更新 pending 行状态。</summary>
    private async Task MarkPendingAsync(UserPlaylistPendingTrack pending, string status, CancellationToken cancellationToken)
    {
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
