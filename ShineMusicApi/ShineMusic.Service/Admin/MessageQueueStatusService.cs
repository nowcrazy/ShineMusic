using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service.Messaging;

namespace ShineMusic.Service;

/// <summary>
/// RabbitMQ 队列状态查询：经 Management HTTP API（默认 15672 端口）读取全部队列的
/// 积压（ready）、处理中（unacked）、在线消费者与收发速率，供管理后台监控页展示。
/// 具体下载/导入任务则从 PostgreSQL 业务记录读取，RabbitMQ 消息本身只携带任务 Id。
/// 连接信息（地址/凭据）由 DI 注册的 rabbitmq-management HttpClient 统一装配。
/// 插件未启用或网络不通时底层抛 HttpRequestException，由控制器统一转成 502 响应。
/// </summary>
public sealed class MessageQueueStatusService(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> sourceDownloadOptions,
    IOptions<TranscodingOptions> transcodingOptions,
    ISqlSugarClient db,
    ILogger<MessageQueueStatusService> logger)
{
    public const string HttpClientName = "rabbitmq-management";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DetailJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>已知队列的业务角色说明；未登记的队列回退为"未登记队列"。</summary>
    private static readonly Dictionary<string, string> KnownRoles = new(StringComparer.Ordinal)
    {
        [QueueNames.Downloads] = "音源下载（SourceDownloadJob）",
        [QueueNames.Imports] = "曲库导入（LibraryImportJob）",
    };

    /// <summary>查询全部队列状态：已知业务队列排前，其后按积压从多到少。</summary>
    public async Task<List<MessageQueueStatusDto>> GetQueuesAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var entries = await client.GetFromJsonAsync<List<ManagementQueue>>("api/queues", JsonOptions, cancellationToken) ?? [];
        logger.LogDebug("RabbitMQ management returned {Count} queues.", entries.Count);

        return entries
            .Where(queue => !string.IsNullOrWhiteSpace(queue.Name))
            .Select(queue => Build(queue))
            .OrderBy(queue => KnownRoles.ContainsKey(queue.Name) ? 0 : 1)
            .ThenByDescending(queue => queue.MessagesReady)
            .ThenBy(queue => queue.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>从业务表查询指定队列当前等待或处理中的任务，消息本身只携带任务 Id。</summary>
    public async Task<MessageQueueTaskListDto> GetTasksAsync(
        string queueName,
        string state,
        int limit,
        int page,
        CancellationToken cancellationToken)
    {
        return queueName switch
        {
            QueueNames.Downloads => await GetDownloadTasksAsync(state, limit, page, cancellationToken),
            QueueNames.Imports => await GetImportTasksAsync(state, limit, page, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(queueName), queueName, "Unsupported message queue.")
        };
    }

    private async Task<MessageQueueTaskListDto> GetDownloadTasksAsync(
        string state,
        int limit,
        int page,
        CancellationToken cancellationToken)
    {
        var query = db.Queryable<ExternalDownloadTask>()
            .Where(task =>
                task.Status == ExternalDownloadTaskStatuses.Queued ||
                task.Status == ExternalDownloadTaskStatuses.Downloading);
        if (state == "waiting")
            query = query.Where(task => task.Status == ExternalDownloadTaskStatuses.Queued);
        else if (state == "processing")
            query = query.Where(task => task.Status == ExternalDownloadTaskStatuses.Downloading);

        var total = await query.CountAsync(cancellationToken);
        var tasks = await query
            .OrderBy(task => task.CreatedAtUtc)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var items = tasks.Select(task => new MessageQueueTaskDto(
            task.Id,
            QueueNames.Downloads,
            task.Status == ExternalDownloadTaskStatuses.Queued ? "waiting" : "processing",
            task.Status,
            task.Title,
            task.ArtistsJson,
            task.AlbumTitle,
            null,
            task.SourceProvider,
            task.ProviderTrackId,
            task.SourcePageUrl,
            null,
            null,
            null,
            null,
            task.AutoRetryCount,
            task.NextRetryAtUtc.HasValue ? ToUtcOffset(task.NextRetryAtUtc.Value) : null,
            task.ErrorCode,
            task.ErrorMessage,
            JsonSerializer.Serialize(new
            {
                task.AudioQualityTier,
                task.License,
                task.DurationMs,
                task.AttemptIndex,
                task.CandidateProvidersJson,
                task.AttemptErrorsJson
            }, DetailJsonOptions),
            ToUtcOffset(task.CreatedAtUtc),
            ToUtcOffset(task.UpdatedAtUtc))).ToList();

        return new MessageQueueTaskListDto(QueueNames.Downloads, state, total, page, limit, items);
    }

    private async Task<MessageQueueTaskListDto> GetImportTasksAsync(
        string state,
        int limit,
        int page,
        CancellationToken cancellationToken)
    {
        var query = db.Queryable<LibraryImportItem>()
            .Where(item => !item.IsDeleted)
            .Where(item =>
                item.Status == LibraryImportItemStatuses.Uploaded ||
                item.Status == LibraryImportItemStatuses.Validating ||
                item.Status == LibraryImportItemStatuses.ParsingMetadata ||
                item.Status == LibraryImportItemStatuses.Transcoding);
        if (state == "waiting")
            query = query.Where(item => item.Status == LibraryImportItemStatuses.Uploaded);
        else if (state == "processing")
            query = query.Where(item =>
                item.Status == LibraryImportItemStatuses.Validating ||
                item.Status == LibraryImportItemStatuses.ParsingMetadata ||
                item.Status == LibraryImportItemStatuses.Transcoding);

        var total = await query.CountAsync(cancellationToken);
        var tasks = await query
            .OrderBy(item => item.CreatedAtUtc)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var items = tasks.Select(item => new MessageQueueTaskDto(
            item.Id,
            QueueNames.Imports,
            item.Status == LibraryImportItemStatuses.Uploaded ? "waiting" : "processing",
            item.Status,
            item.Title,
            item.ArtistsJson,
            item.AlbumTitle,
            item.DisplayName,
            item.SourceProvider,
            null,
            null,
            item.SizeBytes,
            item.Container,
            item.Codec,
            item.BitrateKbps,
            item.ProcessingRetryCount,
            null,
            item.ErrorCode,
            item.ErrorMessage,
            JsonSerializer.Serialize(new
            {
                item.BatchId,
                item.TrackNumber,
                item.DiscNumber,
                item.ReleaseYear,
                item.GenresJson,
                item.WarningsJson
            }, DetailJsonOptions),
            ToUtcOffset(item.CreatedAtUtc),
            ToUtcOffset(item.UpdatedAtUtc))).ToList();

        return new MessageQueueTaskListDto(QueueNames.Imports, state, total, page, limit, items);
    }

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>
    /// 组装单个队列状态。消费端把 ConsumerDispatchConcurrency 设为 prefetch，
    /// 因此并行度 = 在线消费者数 × 预取数；预取为 1 且单消费者时即严格串行。
    /// </summary>
    private MessageQueueStatusDto Build(ManagementQueue queue)
    {
        var consumers = queue.Consumers;
        var prefetch = PrefetchFor(queue.Name);
        var concurrency = consumers <= 0 ? 0 : consumers * prefetch;
        var mode = concurrency <= 0 ? "offline" : concurrency == 1 ? "serial" : "parallel";
        return new MessageQueueStatusDto(
            queue.Name,
            KnownRoles.GetValueOrDefault(queue.Name, "未登记队列"),
            string.IsNullOrWhiteSpace(queue.State) ? "unknown" : queue.State,
            queue.MessagesReady,
            queue.MessagesUnacknowledged,
            consumers,
            prefetch,
            concurrency,
            mode,
            queue.Stats?.Publish?.Rate,
            queue.Stats?.Deliver?.Rate);
    }

    /// <summary>取该队列消费端配置的预取数；未登记队列回退为 1。</summary>
    private int PrefetchFor(string queueName)
    {
        if (queueName == QueueNames.Downloads)
            return Math.Clamp(sourceDownloadOptions.Value.Download.MaxConcurrentDownloads, 1, 4);
        if (queueName == QueueNames.Imports)
            return Math.Clamp(transcodingOptions.Value.MaxConcurrentJobs, 1, 4);
        return 1;
    }

    /// <summary>Management API /api/queues 响应条目（只取监控页需要的字段）。</summary>
    private sealed record ManagementQueue(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("messages_ready")] long MessagesReady,
        [property: JsonPropertyName("messages_unacknowledged")] long MessagesUnacknowledged,
        [property: JsonPropertyName("consumers")] int Consumers,
        [property: JsonPropertyName("message_stats")] ManagementStats? Stats);

    private sealed record ManagementStats(
        [property: JsonPropertyName("publish_details")] ManagementRate? Publish,
        [property: JsonPropertyName("deliver_details")] ManagementRate? Deliver);

    private sealed record ManagementRate([property: JsonPropertyName("rate")] double Rate);
}
