// 职责：定义消息队列监控相关的 DTO。
// 挂载点：MessageQueueStatusService（填充）→ MessageQueueAdminController（输出）→ 前端 admin-mq.ts（消费）。
// 队列统计来源：RabbitMQ Management API；业务任务明细来源：对应 PostgreSQL 任务表。

namespace ShineMusic.Model.Contracts;

/// <summary>RabbitMQ 单个队列的运行状态。</summary>
/// <param name="Name">队列名（vhost 内唯一）。</param>
/// <param name="Role">业务角色说明（导入/下载队列等），未登记的队列回退为"未登记队列"。</param>
/// <param name="State">运行状态（running / down / idle 等，来自 Management API）。</param>
/// <param name="MessagesReady">待消费消息数（积压）。</param>
/// <param name="MessagesUnacknowledged">已投递未确认数（正在处理中）。</param>
/// <param name="Consumers">在线消费者数量。</param>
/// <param name="Prefetch">消费端预取数（QoS），同时作为该通道的并行处理上限。</param>
/// <param name="Concurrency">实际并行处理条数 = 在线消费者数 × 预取数。</param>
/// <param name="Mode">消费模式：serial 串行 / parallel 并行 / offline 无消费者。</param>
/// <param name="PublishRate">每秒发布速率；队列从未有流量时 Management API 不返回该字段，为 null。</param>
/// <param name="DeliverRate">每秒投递速率；同上，无流量历史时为 null。</param>
public sealed record MessageQueueStatusDto(
    string Name,
    string Role,
    string State,
    long MessagesReady,
    long MessagesUnacknowledged,
    int Consumers,
    int Prefetch,
    int Concurrency,
    string Mode,
    double? PublishRate,
    double? DeliverRate);

/// <summary>队列中的具体业务任务；DetailsJson 不包含隔离存储内部路径或文件哈希。</summary>
public sealed record MessageQueueTaskDto(
    Guid Id,
    string QueueName,
    string State,
    string Status,
    string? Title,
    string? ArtistsJson,
    string? AlbumTitle,
    string? OriginalFileName,
    string? Provider,
    string? ProviderTrackId,
    string? SourcePageUrl,
    long? FileSizeBytes,
    string? Container,
    string? Codec,
    int? BitrateKbps,
    int RetryCount,
    DateTimeOffset? NextRetryAtUtc,
    string? ErrorCode,
    string? ErrorMessage,
    string DetailsJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>队列业务任务明细及匹配筛选条件的总数。</summary>
public sealed record MessageQueueTaskListDto(
    string QueueName,
    string State,
    int Total,
    int Page,
    int Limit,
    IReadOnlyList<MessageQueueTaskDto> Items);

/// <summary>当前生效或排期生效的个性化推荐策略参数。</summary>
public sealed record RecommendationStrategyValuesDto(
    int DailyTrackCount,
    double ArtistAffinityWeight,
    double PlayCountWeight,
    int PlayCountCap,
    double SkipPenalty,
    double FavoriteBoost,
    double LikeBoost,
    double DislikePenalty,
    int NonRepeatDays,
    double RecentRecommendationPenalty,
    int NewTrackBoostDays,
    double NewTrackBoostMax);

/// <summary>管理员读取推荐策略时返回当前策略及下一生效版本。</summary>
public sealed record RecommendationStrategyAdminDto(
    RecommendationStrategyValuesDto Active,
    RecommendationStrategyValuesDto? Scheduled,
    DateOnly? ScheduledEffectiveDate);

/// <summary>管理员提交的个性化推荐策略；所有数值均由数据注解限制有效范围。</summary>
public sealed record UpdateRecommendationStrategyRequest
{
    [System.ComponentModel.DataAnnotations.Range(5, 50)]
    public int DailyTrackCount { get; init; } = 30;

    [System.ComponentModel.DataAnnotations.Range(0, 20)]
    public double ArtistAffinityWeight { get; init; } = 2.5;

    [System.ComponentModel.DataAnnotations.Range(0, 20)]
    public double PlayCountWeight { get; init; } = 3;

    [System.ComponentModel.DataAnnotations.Range(1, 100)]
    public int PlayCountCap { get; init; } = 20;

    [System.ComponentModel.DataAnnotations.Range(0, 50)]
    public double SkipPenalty { get; init; } = 4;

    [System.ComponentModel.DataAnnotations.Range(0, 50)]
    public double FavoriteBoost { get; init; } = 6;

    [System.ComponentModel.DataAnnotations.Range(0, 50)]
    public double LikeBoost { get; init; } = 8;

    [System.ComponentModel.DataAnnotations.Range(0, 100)]
    public double DislikePenalty { get; init; } = 40;

    [System.ComponentModel.DataAnnotations.Range(0, 30)]
    public int NonRepeatDays { get; init; } = 7;

    [System.ComponentModel.DataAnnotations.Range(0, 100)]
    public double RecentRecommendationPenalty { get; init; } = 25;

    [System.ComponentModel.DataAnnotations.Range(1, 365)]
    public int NewTrackBoostDays { get; init; } = 90;

    [System.ComponentModel.DataAnnotations.Range(0, 20)]
    public double NewTrackBoostMax { get; init; } = 3;
}
