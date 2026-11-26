using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>A durable task trigger; written in the same transaction as its business state.</summary>
[SugarTable("MessageOutboxEntries")]
public sealed class MessageOutboxEntry
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string QueueName { get; set; } = string.Empty;
    public Guid TaskId { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime AvailableAtUtc { get; set; } = DateTime.UtcNow;
    [SugarColumn(IsNullable = true)]
    public Guid? ClaimId { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? LeaseUntilUtc { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? SentAtUtc { get; set; }
    /// <summary>Exception type only; transport exception messages may contain credentials or payloads.</summary>
    [SugarColumn(IsNullable = true)]
    public string? LastError { get; set; }
}
