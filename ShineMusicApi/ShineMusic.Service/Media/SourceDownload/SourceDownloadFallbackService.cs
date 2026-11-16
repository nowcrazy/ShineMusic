// 职责：音源下载任务的「跨 provider 换源重试」——当指定音源平台下载失败后，
// 自动（或指定目标平台）在其他启用的音源上搜索同曲匹配并创建新的下载任务。
// 曲目身份匹配统一委托 TrackIdentityMatcher（唯一决策点）；本类不得再自带匹配实现。

using System.Text.Json;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;
using SqlSugar;

namespace ShineMusic.Service.SourceDownload;

/// <summary>换源重试结果（成功/失败带原因）。</summary>
public sealed class SwitchSourceResult
{
    public bool Success { get; init; }
    public SwitchSourceFailureKind? FailureKind { get; init; }
    public string? FailureMessage { get; init; }
    public SwitchSourceResponse? Response { get; init; }

    public static SwitchSourceResult Ok(SwitchSourceResponse response) =>
        new() { Success = true, Response = response };

    public static SwitchSourceResult Fail(SwitchSourceFailureKind kind, string message) =>
        new() { Success = false, FailureKind = kind, FailureMessage = message };
}

/// <summary>
/// 跨音源换源重试服务：对已失败的 ExternalDownloadTask，
/// 从其他启用的 provider 搜索同曲匹配并创建新下载任务。
/// </summary>
public sealed class SourceDownloadFallbackService(
    ISqlSugarClient db,
    ISearchAggregator searchAggregator,
    SourceDownloadService createService,
    ILogger<SourceDownloadFallbackService> logger)
{
    /// <summary>
    /// 自动换源优先级：能稳定给 320k/无损的源排在 128k 源与开放版权 UGC 源之前。
    /// IEnumerable&lt;IMusicSourceProvider&gt; 的顺序等于 DI 注册顺序（ModuleRegistrations 里
    /// audius/jamendo/kuwo/kugou 都排在 migu/netease 之前），原实现「首个标题命中即创建任务」，
    /// 于是失败重试常常落到 128k 的酷狗/酷我上。这里显式按音质能力排序。
    /// 依据：2026-11-12 docs/音源巡检补测.json（11 源 × 3 首正常录音室歌曲实测）。
    /// </summary>
    private static readonly Dictionary<string, int> ProviderQualityPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["migu"] = 0,       // SQ 无损 FLAC / HQ 320k，且含腾讯独家原曲
        ["netease"] = 1,    // 官方 320k；VIP 曲走 gdstudio 回退可得 FLAC
        ["qianqian"] = 2,   // rate=3000 可给 FLAC，但中文曲库常命中翻唱且 VIP 覆盖高
        ["qq"] = 3,         // 配置 Cookie 后可 320k/无损；匿名态基本不可用
        ["kuwo"] = 4,       // 匿名 128k，且多数曲目只回 11s 占位
        ["kugou"] = 5,      // 匿名 128k，且常命中 Live/黑胶等非录音室版本
        ["qishui"] = 6,     // 匿名 VIP 只给 30~60s 试听
        ["audius"] = 7,     // 开放版权，中文查询基本 0 命中或返回无关曲目
        ["jamendo"] = 8,
        ["bilibili"] = 9,   // UGC 视频音轨，非官方母带
        ["ytmusic"] = 10,
    };

    /// <summary>
    /// 对已失败任务执行换源重试。
    /// </summary>
    /// <param name="taskId">失败任务 Id。</param>
    /// <param name="userId">发起操作的管理员 Id（用于新任务审计）。</param>
    /// <param name="targetProvider">可选：指定目标 provider；留空则自动搜索所有其他启用源。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SwitchSourceResult> SwitchSourceAsync(
        Guid taskId,
        Guid userId,
        string? targetProvider,
        CancellationToken cancellationToken)
    {
        var task = await db.Queryable<ExternalDownloadTask>()
            .FirstAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null)
            return SwitchSourceResult.Fail(SwitchSourceFailureKind.TaskNotFound, $"Task {taskId} not found.");
        if (task.Status != ExternalDownloadTaskStatuses.Failed)
            return SwitchSourceResult.Fail(SwitchSourceFailureKind.NotFailed, "Only failed tasks can be retried with a different source.");

        // 从任务快照中取搜索关键词：标题 + 主艺人；任一缺失无法搜索。
        var expectedTitle = task.ExpectedTitle ?? task.Title;
        if (string.IsNullOrWhiteSpace(expectedTitle))
            return SwitchSourceResult.Fail(SwitchSourceFailureKind.InsufficientMetadata, "The task has no title to search with.");
        var primaryArtist = TryGetPrimaryArtist(task.ExpectedArtistsJson ?? task.ArtistsJson);

        // 构造搜索查询（标题 + 主艺人，无艺人时仅标题）。
        var query = string.IsNullOrWhiteSpace(primaryArtist)
            ? expectedTitle.Trim()
            : $"{expectedTitle.Trim()} {primaryArtist}";

        // 决定搜索哪些 provider：源清单以 V2 Registry 为准（Phase 2 用户裁决），
        // 排序仍按 ProviderQualityPriority —— A 轴修复，不能让「首个标题命中」把重试落到 128k 源。
        var candidates = searchAggregator.Providers
            .Where(descriptor => descriptor.ConfigEnabled
                && descriptor.SearchEnabled
                && descriptor.DownloadEnabled
                && descriptor.Declares(ProviderCapability.Resolve)
                && descriptor.Declares(ProviderCapability.Search)
                && !string.Equals(descriptor.ProviderName, task.SourceProvider, StringComparison.OrdinalIgnoreCase))
            .Select(descriptor => descriptor.ProviderName)
            .OrderBy(name => ProviderQualityPriority.GetValueOrDefault(name, 50))
            .ToList();
        if (!string.IsNullOrWhiteSpace(targetProvider))
        {
            var matchedProvider = candidates.FirstOrDefault(name =>
                string.Equals(name, targetProvider.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matchedProvider is null)
                return SwitchSourceResult.Fail(SwitchSourceFailureKind.ProviderUnavailable,
                    $"Provider '{targetProvider}' is not available or enabled.");
            candidates = [matchedProvider];
        }
        if (candidates.Count == 0)
            return SwitchSourceResult.Fail(SwitchSourceFailureKind.ProviderUnavailable,
                "No other enabled source providers are available to switch to.");

        // 一次并发扇出，超时 / 异常 / 健康熔断由 Orchestrator 收敛为逐源 outcome；
        // 再按音质优先级串行找首个匹配，保持「首个命中即建任务」的原有语义。
        var aggregated = await searchAggregator.SearchAsync(query, candidates, 10, cancellationToken);
        foreach (var outcome in aggregated.Outcomes
                     .OrderBy(item => ProviderQualityPriority.GetValueOrDefault(item.ProviderName, 50)))
        {
            if (outcome.ErrorCode is not null)
            {
                logger.LogWarning(
                    "Switch-source search failed for provider {Provider} and task {TaskId} ({ErrorCode}: {Message}); continuing with the next provider.",
                    outcome.ProviderName, taskId, outcome.ErrorCode, outcome.ErrorMessage);
                continue;
            }

            var matched = outcome.Results.FirstOrDefault(entry => MatchesCandidate(expectedTitle, primaryArtist, entry));
            if (matched is null)
                continue;

            // 找到了匹配！创建新任务。
            try
            {
                var metadata = new Dictionary<string, SourceSearchItemDto>
                {
                    [matched.ProviderTrackId] = new SourceSearchItemDto(
                        matched.Provider, matched.ProviderTrackId, matched.Title, matched.Artists,
                        matched.AlbumTitle, matched.DurationMs, matched.Genres, matched.CoverUrl,
                        matched.SourcePageUrl, matched.License, matched.Quality, matched.IsLosslessAvailable,
                        matched.ReleaseYear)
                };
                var result = await createService.CreateTasksAsync(new CreateSourceDownloadTasksRequest(
                    matched.Provider, [matched.ProviderTrackId], metadata,
                    ExpectedTrack: new SourceExpectedTrackDto(task.ExpectedTitle ?? task.Title!,
                        DeserializeList(task.ExpectedArtistsJson ?? task.ArtistsJson),
                        task.ExpectedAlbumTitle ?? task.AlbumTitle,
                        task.ExpectedTitle is not null ? task.ExpectedDurationMs : task.DurationMs)), userId, cancellationToken);

                if (result.Created.Count == 1)
                {
                    var newTask = result.Created[0];
                    var allArtists = DeserializeList(newTask.ArtistsJson);
                    return SwitchSourceResult.Ok(new SwitchSourceResponse(
                        newTask.Id,
                        newTask.SourceProvider,
                        newTask.ProviderTrackId,
                        newTask.Title,
                        allArtists,
                        AutoMatched: string.IsNullOrWhiteSpace(targetProvider),
                        Message: null));
                }
            }
            catch (SourceDuplicateException)
            {
                return SwitchSourceResult.Fail(SwitchSourceFailureKind.DuplicateTask,
                    $"A task for this track on provider '{outcome.ProviderName}' already exists.");
            }
            catch (SourceUnavailableException exception)
            {
                logger.LogWarning(
                    exception,
                    "Switch-source task creation failed for provider {Provider} and task {TaskId}; continuing with the next provider.",
                    outcome.ProviderName,
                    taskId);
                continue;
            }
        }

        return SwitchSourceResult.Fail(SwitchSourceFailureKind.NoMatchFound,
            $"No matching track found on any alternative source provider.");
    }

    // ====== 以下为任务快照解析辅助；曲目身份判定统一走 TrackIdentityMatcher（唯一决策点）。 ======

    /// <summary>
    /// 换源候选是否可接受：只负责把任务快照与搜索结果组装成 <see cref="TrackIdentity"/>，
    /// **判定逻辑全部委托** <see cref="TrackIdentityMatcher.Evaluate"/>。本类不得自带匹配实现。
    /// 标记 internal 以便测试直接锁定换源路径的判定口径（无需 mock 数据库 / provider）。
    /// </summary>
    internal static bool MatchesCandidate(string taskTitle, string? primaryArtist, SourceSearchResult entry)
    {
        var expected = new TrackIdentity(
            taskTitle,
            string.IsNullOrWhiteSpace(primaryArtist) ? [] : [primaryArtist]);
        var candidate = new TrackIdentity(entry.Title, entry.Artists, entry.DurationMs);
        return TrackIdentityMatcher.Evaluate(expected, candidate).Verdict == IdentityVerdict.Matched;
    }

    private static string? TryGetPrimaryArtist(string? artistsJson)
    {
        if (string.IsNullOrWhiteSpace(artistsJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<string[]>(artistsJson)
                ?.FirstOrDefault(artist => !string.IsNullOrWhiteSpace(artist));
        }
        catch (JsonException) { return null; }
    }

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
