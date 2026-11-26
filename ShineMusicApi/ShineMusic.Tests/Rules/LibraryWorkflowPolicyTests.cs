using ShineMusic.Model.Domain;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class LibraryWorkflowPolicyTests
{
    [Theory]
    [InlineData("mp3", "", 256, true)]
    [InlineData("mp3", "", 255, false)]
    [InlineData("aac", "LC", 256, true)]
    [InlineData("aac", "HE-AAC", 512, false)]
    [InlineData("opus", "", 192, true)]
    [InlineData("vorbis", "", 255, false)]
    [InlineData("opus", "", 320, true)]
    public void Nomination_uses_original_codec_threshold_and_profile(string codec, string profile, int bitrate, bool expected)
    {
        var item = Candidate(codec, bitrate, profile);
        Assert.Equal(expected, LibraryWorkflowPolicy.CanNominate(item, new LibraryWorkflowOptions()));
    }

    [Fact]
    public void Nomination_rejects_missing_duration_or_non_stereo_lossy_media()
    {
        var item = Candidate("mp3", 320, "");
        Assert.True(LibraryWorkflowPolicy.CanNominate(item, new LibraryWorkflowOptions()));
        item.DurationMs = null;
        Assert.False(LibraryWorkflowPolicy.CanNominate(item, new LibraryWorkflowOptions()));
        item.DurationMs = 180000;
        item.Channels = 1;
        Assert.False(LibraryWorkflowPolicy.CanNominate(item, new LibraryWorkflowOptions()));
    }

    [Fact]
    public void Comparison_only_equates_same_bytes_and_never_orders_different_codecs_by_bitrate()
    {
        var existing = new MediaAsset { ContentHash = "same", IsOriginal = true, Codec = "flac", BitrateKbps = 900 };
        var incoming = Candidate("mp3", 320, "");
        incoming.ContentHash = "different";
        Assert.Equal(OriginalQualityComparison.Unknown, LibraryWorkflowPolicy.Compare(incoming, existing));
        incoming.ContentHash = "same";
        Assert.Equal(OriginalQualityComparison.Equal, LibraryWorkflowPolicy.Compare(incoming, existing));
    }

    private static LibraryImportItem Candidate(string codec, int bitrate, string profile) => new()
    {
        DisplayName = "candidate.audio",
        SourceStorageKey = "uploads/candidate.audio",
        ContentHash = "candidate-hash",
        Title = "Sample",
        ArtistsJson = "[\"Artist\"]",
        DurationMs = 180000,
        Codec = codec,
        CodecProfile = profile,
        Container = codec == "aac" ? "m4a" : codec,
        Channels = 2,
        SampleRateHz = 44100,
        BitrateKbps = bitrate
    };
}
