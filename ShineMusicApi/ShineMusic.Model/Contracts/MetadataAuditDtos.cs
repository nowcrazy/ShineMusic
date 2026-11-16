namespace ShineMusic.Model.Contracts;

/// <summary>字段来源与冲突；ValueJson与CandidateJson存储字段值，允许人工锁定空值。</summary>
public sealed record MetadataFieldAuditDto(string Field, string? ValueJson, string Source,
    string? SourceId, DateTime ObservedAtUtc, bool ManualLocked,
    string? CandidateJson = null, string? CandidateSource = null, string? Reason = null);
