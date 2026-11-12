// 职责：音源下载任务的「跨 provider 换源重试」——当指定音源平台下载失败后，
// 自动（或指定目标平台）在其他启用的音源上搜索同曲匹配并创建新的下载任务。
// 匹配逻辑与 SourceDownloadJob 的跨源佐证保持一致（归一化标题 + 艺人互含）。

using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
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
    IEnumerable<IMusicSourceProvider> providers,
    SourceDownloadService createService,
    IOptions<SourceDownloadOptions> options,
    ILogger<SourceDownloadFallbackService> logger)
{
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
        if (string.IsNullOrWhiteSpace(task.Title))
            return SwitchSourceResult.Fail(SwitchSourceFailureKind.InsufficientMetadata, "The task has no title to search with.");
        var primaryArtist = TryGetPrimaryArtist(task.ArtistsJson);

        // 构造搜索查询（标题 + 主艺人，无艺人时仅标题）。
        var query = string.IsNullOrWhiteSpace(primaryArtist)
            ? task.Title.Trim()
            : $"{task.Title.Trim()} {primaryArtist}";

        // 决定搜索哪些 provider。
        var candidates = providers
            .Where(p => p.IsEnabled
                && p.Provider != task.SourceProvider
                && !string.Equals(p.Provider, "ytmusic", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrWhiteSpace(targetProvider))
        {
            var matchedProvider = candidates.FirstOrDefault(p =>
                string.Equals(p.Provider, targetProvider.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matchedProvider is null)
                return SwitchSourceResult.Fail(SwitchSourceFailureKind.ProviderUnavailable,
                    $"Provider '{targetProvider}' is not available or enabled.");
            candidates = new List<IMusicSourceProvider> { matchedProvider };
        }
        if (candidates.Count == 0)
            return SwitchSourceResult.Fail(SwitchSourceFailureKind.ProviderUnavailable,
                "No other enabled source providers are available to switch to.");

        // yt-dlp 源搜索较慢，使用其配置的搜索预算；其他源使用较短的独立预算。
        foreach (var provider in candidates)
        {
            SourceSearchResult? matched = null;
            try
            {
                using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var budgetSeconds = string.Equals(provider.Provider, "ytmusic", StringComparison.OrdinalIgnoreCase)
                    ? Math.Clamp(options.Value.YtDlp.SearchTimeoutSeconds, 10, 300)
                    : 15;
                budgetCts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
                var results = await provider.SearchAsync(query, 10, budgetCts.Token);
                matched = results.FirstOrDefault(entry =>
                    TitleMatches(task.Title!, entry.Title)
                    && (string.IsNullOrWhiteSpace(primaryArtist) || ArtistsMatch(entry.Artists, primaryArtist)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Switch-source search timed out for provider {Provider} and task {TaskId}; continuing with the next provider.",
                    provider.Provider,
                    taskId);
                continue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Switch-source search failed for provider {Provider} and task {TaskId}; continuing with the next provider.",
                    provider.Provider,
                    taskId);
                continue;
            }

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
                var result = await createService.CreateTasksAsync(
                    matched.Provider, [matched.ProviderTrackId], metadata, userId, cancellationToken);

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
                    $"A task for this track on provider '{provider.Provider}' already exists.");
            }
            catch (SourceUnavailableException exception)
            {
                logger.LogWarning(
                    exception,
                    "Switch-source task creation failed for provider {Provider} and task {TaskId}; continuing with the next provider.",
                    provider.Provider,
                    taskId);
                continue;
            }
        }

        return SwitchSourceResult.Fail(SwitchSourceFailureKind.NoMatchFound,
            $"No matching track found on any alternative source provider.");
    }

    // ====== 以下匹配辅助方法与 SourceDownloadJob 保持一致（归一化标题互含 + 艺人互含）。 ======

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

    private static bool TitleMatches(string taskTitle, string? entryTitle)
    {
        var left = NormalizeTitle(taskTitle);
        var right = NormalizeTitle(entryTitle);
        if (left.Length < 2 || right.Length < 2) return false;
        return left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ArtistsMatch(IReadOnlyList<string> artists, string primaryArtist)
    {
        var expected = NormalizeTitle(primaryArtist);
        if (expected.Length < 2) return false;
        return artists.Any(artist =>
        {
            var actual = NormalizeTitle(artist);
            return actual.Length >= 2
                && (actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
                    || expected.Contains(actual, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var builder = new System.Text.StringBuilder(title.Length);
        var depth = 0;
        foreach (var ch in title)
        {
            if (ch is '(' or '（' or '[' or '［' or '【' or '{' or '｛') depth++;
            else if (ch is ')' or '）' or ']' or '］' or '】' or '}' or '｝') { if (depth > 0) depth--; }
            else if (depth == 0 && !char.IsWhiteSpace(ch) && ch != '\u00A0') builder.Append(ch);
        }
        return builder.ToString();
    }

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
