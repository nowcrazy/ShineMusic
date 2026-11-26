namespace ShineMusic.Model.Options;

/// <summary>Private upload confirmation, public nomination and recycling policies.</summary>
public sealed class LibraryWorkflowOptions
{
    public const string SectionName = "LibraryWorkflow";
    public int PrivateReviewDays { get; set; } = 7;
    public int PublicReviewDays { get; set; } = 30;
    public int RecycleDays { get; set; } = 7;
    public int ReminderHours { get; set; } = 24;
    /// <summary>Per-user retained private original bytes; zero preserves unlimited storage.</summary>
    public long MaxPrivateOriginalBytesPerUser { get; set; }
    public int Mp3MinimumKbps { get; set; } = 256;
    public int AacLcMinimumKbps { get; set; } = 256;
    public int OpusMinimumKbps { get; set; } = 192;
    public int VorbisMinimumKbps { get; set; } = 256;
}
