// 职责：外部音源抓歌管理端点（仅管理员，受功能开关 SourceDownload 控制）。
// 提供「搜索音源 → 创建下载任务 → 跟踪任务 → 重试失败任务」的闭环；
// 下载与入库的实际执行由 RabbitMQ 消费端（SourceDownloadJob + LibraryImportJob）完成。

using System.Text.Json;
using ShineMusic.Service.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.SourceDownload;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>Handles administrator searches, download tasks, and retries for external music sources.</summary>
[ApiController]
[Route("api/v1/admin/source-downloads")]
[Authorize(Roles = "admin")]
[FeatureGate(nameof(FeatureOptions.SourceDownload))]
[Tags("Admin Source Download")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class SourceDownloadController : ControllerBase
{
    /// <summary>搜索音源平台：provider=all 时并行聚合各启用源，单源失败不拖垮整体（providerErrors 标注）。</summary>
    [HttpGet("search", Name = "SearchMusicSources")]
    public async Task<IResult> Search(
        [FromQuery] string? query,
        [FromQuery] string? provider,
        [FromQuery] int? limit,
        IEnumerable<IMusicSourceProvider> providers,
        IOptions<SourceDownloadOptions> options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ApiResults.ValidationError(HttpContext, "query", "A search query is required.");
        var effectiveLimit = Math.Clamp(limit ?? 20, 1, 50);
        var providerFilter = string.IsNullOrWhiteSpace(provider) ? "all" : provider.Trim().ToLowerInvariant();

        var candidates = providers
            .Where(candidate => providerFilter == "all" || candidate.Provider == providerFilter)
            .ToList();
        if (providerFilter != "all" && candidates.Count == 0)
            return ApiResults.ValidationError(HttpContext, "provider", $"Unknown source provider '{providerFilter}'.");

        var results = new List<SourceSearchItemDto>();
        var errors = new List<SourceProviderErrorDto>();
        var enabledCandidates = candidates.Where(candidate => candidate.IsEnabled).ToList();
        // 未启用的源不视为错误，仅当显式指定该源时给出提示。
        if (providerFilter != "all" && candidates.Count > 0 && enabledCandidates.Count == 0)
            errors.Add(new SourceProviderErrorDto(providerFilter, candidates[0].DisabledReason ?? "Source is disabled."));

        // 单源搜索预算：任一源超时即放弃该源（标注 providerErrors），其余源结果照常返回，
        // 防止不可达源（如国内网络下的 YouTube/yt-dlp）挂起拖垮整体响应。
        var budgetSeconds = Math.Clamp(options.Value.SearchBudgetSeconds, 5, 300);
        // 并行搜索各启用源；单源异常收敛为 providerErrors。
        var searchTasks = enabledCandidates.Select(async candidate =>
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
            try
            {
                var items = await candidate.SearchAsync(query.Trim(), effectiveLimit, budgetCts.Token);
                return (Provider: candidate.Provider, Items: items, Error: (string?)null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 预算到期（非客户端取消）：该源标注超时，不拖垮整体；yt-dlp 系平台提示代理配置
                var hint = candidate.Provider is "ytmusic" or "soundcloud"
                    ? "（国内网络不可直连，可在 appsettings 的 SourceDownload:YtDlp:Proxy 配置代理）"
                    : string.Empty;
                return (Provider: candidate.Provider, Items: Array.Empty<SourceSearchResult>(), Error: (string?)$"搜索超时（>{budgetSeconds} 秒），已跳过该源{hint}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return (Provider: candidate.Provider, Items: Array.Empty<SourceSearchResult>(), Error: (string?)exception.Message);
            }
        });
        foreach (var outcome in await Task.WhenAll(searchTasks))
        {
            if (outcome.Error is not null)
                errors.Add(new SourceProviderErrorDto(outcome.Provider, outcome.Error));
            results.AddRange(outcome.Items.Select(item => new SourceSearchItemDto(
                item.Provider, item.ProviderTrackId, item.Title, item.Artists, item.AlbumTitle,
                item.DurationMs, item.Genres, item.CoverUrl, item.SourcePageUrl, item.License,
                item.Quality, item.IsLosslessAvailable)));
        }
        return Results.Ok(new SourceSearchResponse(results, errors));
    }

    /// <summary>创建下载任务（202 Accepted）：按 (Provider, ProviderTrackId) 去重，重复项进 skippedDuplicates。</summary>
    [HttpPost("tasks", Name = "CreateSourceDownloadTasks")]
    public async Task<IResult> CreateTasks(
        [FromBody] CreateSourceDownloadTasksRequest request,
        ISqlSugarClient db,
        SourceDownloadService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Provider))
            return ApiResults.ValidationError(HttpContext, "provider", "A source provider is required.");
        if (request.ProviderTrackIds is null || request.ProviderTrackIds.Count == 0)
            return ApiResults.ValidationError(HttpContext, "providerTrackIds", "At least one track id is required.");
        if (request.ProviderTrackIds.Count > 20)
            return ApiResults.ValidationError(HttpContext, "providerTrackIds", "At most 20 tracks can be queued per request.");
        if (request.ProviderTrackIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128))
            return ApiResults.ValidationError(HttpContext, "providerTrackIds", "Track ids must be non-empty and at most 128 characters.");
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        try
        {
            var result = await service.CreateTasksAsync(request.Provider.Trim().ToLowerInvariant(), request.ProviderTrackIds, userId, cancellationToken);
            // SqlSugar 版审计写入立即执行 INSERT（任务的创建写入已在 SourceDownloadService 内各自提交）。
            AdminAudit.Add(HttpContext, db, "source_download.task_created", "source_download_task", result.Created.Count > 0 ? result.Created[0].Id : Guid.Empty,
                new { provider = request.Provider, created = result.Created.Count, skipped = result.SkippedDuplicates.Count });
            return Results.Accepted(value: new CreateSourceDownloadTasksResultDto(
                result.Created.Select(task => ToDto(task)).ToList(), result.SkippedDuplicates));
        }
        catch (SourceUnavailableException exception)
        {
            return ApiResults.Error(HttpContext, StatusCodes.Status409Conflict, ErrorCodes.SourceUnavailable, exception.Message);
        }
    }

    /// <summary>任务列表：按状态筛选 + 分页（默认按创建时间倒序）。</summary>
    [HttpGet("tasks", Name = "ListSourceDownloadTasks")]
    public async Task<IResult> ListTasks(
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(1, page ?? 1);
        var effectivePageSize = Math.Clamp(pageSize ?? 20, 1, 100);
        var query = db.Queryable<ExternalDownloadTask>();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = query.Where(task => task.Status == normalized);
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(task => task.CreatedAtUtc)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);
        var hasLyricsByTask = await ResolveHasLyricsAsync(db, items, cancellationToken);
        return Results.Ok(new ExternalDownloadTaskListDto(
            items.Select(task => ToDto(task, hasLyricsByTask.GetValueOrDefault(task.Id))).ToList(),
            total, effectivePage, effectivePageSize));
    }

    /// <summary>任务详情。</summary>
    [HttpGet("tasks/{taskId:guid}", Name = "GetSourceDownloadTask")]
    public async Task<IResult> GetTask(Guid taskId, ISqlSugarClient db, CancellationToken cancellationToken)
    {
        var task = await db.Queryable<ExternalDownloadTask>()
            .FirstAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null) return ApiResults.NotFound(HttpContext);
        var hasLyricsByTask = await ResolveHasLyricsAsync(db, [task], cancellationToken);
        return Results.Ok(ToDto(task, hasLyricsByTask.GetValueOrDefault(task.Id)));
    }

    /// <summary>重试失败任务：仅 failed 状态可重试，重置为 queued 并重新投递。</summary>
    [HttpPost("tasks/{taskId:guid}/retry", Name = "RetrySourceDownloadTask")]
    public async Task<IResult> RetryTask(
        Guid taskId,
        ISqlSugarClient db,
        IRabbitMessageBus messageBus,
        CancellationToken cancellationToken)
    {
        var task = await db.Queryable<ExternalDownloadTask>().FirstAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null)
            return ApiResults.NotFound(HttpContext);
        if (task.Status != ExternalDownloadTaskStatuses.Failed)
            return ApiResults.Error(HttpContext, StatusCodes.Status409Conflict, ErrorCodes.ResourceConflict, "Only failed tasks can be retried.");

        task.Status = ExternalDownloadTaskStatuses.Queued;
        task.ErrorCode = null;
        task.ErrorMessage = null;
        // 手动重试给新一轮完整自动退避额度。
        task.AutoRetryCount = 0;
        task.NextRetryAtUtc = null;
        task.UpdatedAtUtc = DateTime.UtcNow;
        // 状态重置 + 审计同一事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(task)
                .UpdateColumns(item => new { item.Status, item.ErrorCode, item.ErrorMessage, item.AutoRetryCount, item.NextRetryAtUtc, item.UpdatedAtUtc })
                .ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "source_download.task_retried", "source_download_task", task.Id);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        await messageBus.PublishAsync(QueueNames.Downloads, task.Id, cancellationToken);
        var hasLyricsByTask = await ResolveHasLyricsAsync(db, [task], cancellationToken);
        return Results.Ok(ToDto(task, hasLyricsByTask.GetValueOrDefault(task.Id)));
    }

    /// <summary>实体 → DTO 映射（ArtistsJson 反序列化为数组；hasLyrics 由调用方按 ImportItemId→PublishedTrackId→Lyrics 解析后传入）。</summary>
    private static ExternalDownloadTaskDto ToDto(ExternalDownloadTask task, bool? hasLyrics = null) => new(
        task.Id,
        task.Provider,
        task.ProviderTrackId,
        task.Title,
        DeserializeList(task.ArtistsJson),
        task.AlbumTitle,
        task.DurationMs,
        task.Quality,
        task.License,
        task.SourcePageUrl,
        task.CoverUrl,
        task.Status,
        task.AutoScore,
        task.AutoPublished,
        task.ErrorCode,
        task.ErrorMessage,
        task.AutoRetryCount,
        task.NextRetryAtUtc,
        task.ImportItemId,
        task.CreatedAtUtc,
        task.UpdatedAtUtc,
        hasLyrics);

    /// <summary>
    /// 批量解析任务的歌词有无：ImportItemId → LibraryImportItem.PublishedTrackId → Lyrics 存在性。
    /// 未关联曲目的任务不出现在结果中（DTO 上 HasLyrics 保持 null）。
    /// </summary>
    private static async Task<Dictionary<Guid, bool>> ResolveHasLyricsAsync(
        ISqlSugarClient db, IReadOnlyCollection<ExternalDownloadTask> tasks, CancellationToken cancellationToken)
    {
        var importItemIds = tasks
            .Where(task => task.ImportItemId.HasValue)
            .Select(task => task.ImportItemId!.Value)
            .Distinct()
            .ToList();
        if (importItemIds.Count == 0) return [];

        var importItems = await db.Queryable<LibraryImportItem>()
            .Where(item => importItemIds.Contains(item.Id) && item.PublishedTrackId != null)
            .Select(item => new { item.Id, TrackId = item.PublishedTrackId!.Value })
            .ToListAsync(cancellationToken);
        var trackIdByItem = importItems.ToDictionary(entry => entry.Id, entry => entry.TrackId);
        if (trackIdByItem.Count == 0) return [];

        var trackIds = trackIdByItem.Values.Distinct().ToList();
        var tracksWithLyrics = (await db.Queryable<Lyric>()
                .Where(lyric => trackIds.Contains(lyric.TrackId))
                .Select(lyric => lyric.TrackId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var result = new Dictionary<Guid, bool>();
        foreach (var task in tasks)
        {
            if (task.ImportItemId is { } itemId && trackIdByItem.TryGetValue(itemId, out var trackId))
                result[task.Id] = tracksWithLyrics.Contains(trackId);
        }
        return result;
    }

    /// <summary>把 JSON 数组字符串解析为字符串列表；解析失败返回空列表。</summary>
    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
