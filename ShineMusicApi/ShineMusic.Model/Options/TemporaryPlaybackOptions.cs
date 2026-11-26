namespace ShineMusic.Model.Options;

/// <summary>Limits for transient compatibility audio that is regenerated from the stored original.</summary>
public sealed class TemporaryPlaybackOptions
{
    public const string SectionName = "TemporaryPlayback";

    /// <summary>Private cache directory relative to the application content root.</summary>
    public string CacheDirectory { get; set; } = "data/temporary-playback";

    public int AacBitrateKbps { get; set; } = 192;
    public long MaxCacheBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long MaxEntryBytes { get; set; } = 500L * 1024 * 1024;
    public int IdleMinutes { get; set; } = 60;
    public int CleanupIntervalMinutes { get; set; } = 5;
    public int MaxConcurrentTranscodes { get; set; } = 2;
}
