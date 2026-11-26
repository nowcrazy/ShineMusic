using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>A pending upload version or a public nomination; never a second effective catalog version.</summary>
[SugarTable("LibraryWorkflowReviews")]
public sealed class LibraryWorkflowReview
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "private";
    public string Status { get; set; } = "pending";
    public Guid OwnerUserId { get; set; }
    public Guid ImportItemId { get; set; }
    [SugarColumn(IsNullable = true)]
    public Guid? SourceTrackId { get; set; }
    [SugarColumn(IsNullable = true)]
    public Guid? ExistingTrackId { get; set; }
    [SugarColumn(IsNullable = true)]
    public Guid? PublicTrackId { get; set; }
    public string OriginalStorageKey { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string ReviewReason { get; set; } = "quality_uncertain";
    [SugarColumn(IsNullable = true, ColumnDataType = "text")]
    public string? ResolutionReason { get; set; }
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime ExpiresAtUtc { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? ReminderSentAtUtc { get; set; }
}

