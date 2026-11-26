namespace ShineMusic.Model.Contracts;

public sealed record LibraryWorkflowPageDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record LibraryWorkflowResolveRequestDto(string Decision, string? Reason = null);

public sealed record PrivateOriginalStorageDto(long UsedBytes, long LimitBytes, long? RemainingBytes, int OriginalCount);

public sealed record LibraryWorkflowSettingsDto(
    int PrivateReviewDays,
    int PublicReviewDays,
    int RecycleDays,
    int ReminderHours,
    int Mp3MinimumKbps,
    int AacLcMinimumKbps,
    int OpusMinimumKbps,
    int VorbisMinimumKbps);

public sealed record LibraryWorkflowReviewDto(
    Guid Id,
    string Kind,
    string Status,
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    long? DurationMs,
    string? OriginalContainer,
    string? OriginalCodec,
    int? OriginalBitrateKbps,
    int? SampleRateHz,
    int? BitsPerSample,
    int? Channels,
    string? CodecProfile,
    DateTime ExpiresAtUtc,
    Guid? SourceTrackId,
    Guid? ExistingTrackId,
    string? ExistingTrackTitle,
    IReadOnlyList<string> ExistingTrackArtists,
    string? ExistingCodec,
    int? ExistingBitrateKbps,
    string ReviewReason,
    string? ResolutionReason);

public sealed record LibraryWorkflowTrackDto(
    Guid TrackId,
    string Title,
    IReadOnlyList<string> Artists,
    bool IsPublic,
    DateTimeOffset AddedAtUtc,
    DateTime? RecycleExpiresAtUtc);
