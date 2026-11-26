using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>消费者在确认失败消息前保存的持久化死信；重放不覆盖业务任务状态。</summary>
[SugarTable("MessageDeadLetters")]
public sealed class MessageDeadLetter
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string QueueName { get; set; } = string.Empty;
    /// <summary>仅原始消息体为完整的非空 Guid N 格式时解析，用于阻止补偿扫描无限重投毒消息。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? TaskId { get; set; }
    /// <summary>消息体最多保留 4096 字节；截断消息的 TaskId 为空且不可重放。</summary>
    [SugarColumn(ColumnDataType = "text")]
    public string BodyBase64 { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "text")]
    public string FailureReason { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime FailedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>已安排的人工重放次数；认领、outbox 与审计一并持久化。</summary>
    public int ReplayCount { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? LastReplayAtUtc { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? ReplayedAtUtc { get; set; }
    /// <summary>人工重放安排的 durable outbox 记录；发布确认由该记录跟踪。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? ReplayOutboxId { get; set; }
    [SugarColumn(IsNullable = true)]
    public Guid? ReplayLeaseId { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? ReplayLeaseUntilUtc { get; set; }
    /// <summary>固定错误码，不保存网络异常可能携带的连接参数。</summary>
    [SugarColumn(IsNullable = true)]
    public string? LastReplayError { get; set; }
}
