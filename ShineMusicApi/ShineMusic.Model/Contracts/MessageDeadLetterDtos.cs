namespace ShineMusic.Model.Contracts;

/// <summary>仅管理员可见的失败消息及人工重放记录。</summary>
public sealed record MessageDeadLetterDto(
    Guid Id,
    string QueueName,
    Guid? TaskId,
    string BodyBase64,
    string FailureReason,
    DateTimeOffset FailedAtUtc,
    int ReplayCount,
    DateTimeOffset? LastReplayAtUtc,
    DateTimeOffset? ReplayedAtUtc,
    Guid? ReplayOutboxId,
    DateTimeOffset? ReplayLeaseUntilUtc,
    string? LastReplayError,
    bool CanReplay);

public sealed record MessageDeadLetterListDto(
    IReadOnlyList<MessageDeadLetterDto> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>已达到自动投递次数上限的待处理 outbox 消息；sent 历史不在此列表。</summary>
public sealed record FailedMessageOutboxDto(
    Guid Id,
    string QueueName,
    Guid TaskId,
    int Attempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset AvailableAtUtc,
    string? LastError);

public sealed record FailedMessageOutboxListDto(
    IReadOnlyList<FailedMessageOutboxDto> Items,
    int Total,
    int Page,
    int PageSize);
