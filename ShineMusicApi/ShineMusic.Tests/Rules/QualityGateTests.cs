using ShineMusic.Model.Domain;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using Xunit;

namespace ShineMusic.Tests.Rules;

public class QualityGateTests
{
    private static QualityGate Create(string minimum = "192K", bool fallback = true)
    {
        var options = new SourceDownloadOptions();
        options.ProviderFramework.QualityPolicy.MinimumQuality = minimum;
        options.ProviderFramework.QualityPolicy.AllowLossyFallback = fallback;
        return new QualityGate(Microsoft.Extensions.Options.Options.Create(options));
    }

    [Fact]
    public void Default_chain_targets_320_first_and_retains_lossless()
    {
        Assert.Equal([AudioQualityLevel.High320K, AudioQualityLevel.Lossless, AudioQualityLevel.Below320K],
            Create().GetAllowedQualityChain());
        Assert.False(Create().IsPreviewAllowed);
    }

    [Theory]
    [InlineData("mp3", "mp3", 192, true)]
    [InlineData("mp3", "mp3", 256, true)]
    [InlineData("mp3", "mp3", 320, true)]
    [InlineData("aac", "m4a", 192, true)]
    [InlineData("aac", "aac", 256, true)]
    [InlineData("aac", "m4a", 320, true)]
    [InlineData("flac", "flac", null, true)]
    [InlineData("alac", "m4a", null, true)]
    [InlineData("pcm_s16le", "wav", null, true)]
    [InlineData("mp3", "mp3", 128, false)]
    [InlineData("aac", "m4a", 191, false)]
    [InlineData("aac", "m4a", null, false)]
    [InlineData("aac", "m4a", 0, false)]
    [InlineData("unknown", "flac", 1411, false)]
    [InlineData(null, "wav", 1411, false)]
    [InlineData("opus", "ogg", 320, false)]
    [InlineData("mp3", "flac", 320, false)]
    public void Shared_policy_and_gate_use_measured_codec_and_bitrate(string? codec, string container, int? bitrate, bool accepted)
    {
        Assert.Equal(accepted, AudioAdmissionPolicy.IsAcceptable(codec, container, bitrate));
        Assert.Equal(accepted, Create().IsAcceptableForLibrary(codec, container, bitrate));
        Assert.Equal(accepted, AudioAdmissionPolicy.GetRejectionReason(codec, container, bitrate) is null);
    }

    [Theory]
    [InlineData(AudioQualityLevel.Unknown)]
    [InlineData(AudioQualityLevel.Below320K)]
    [InlineData(AudioQualityLevel.High320K)]
    [InlineData(AudioQualityLevel.Lossless)]
    public void Display_level_alone_never_proves_admission(AudioQualityLevel level) =>
        Assert.False(Create().IsAcceptableForLibrary(level));

    [Theory]
    [InlineData(128, false)]
    [InlineData(192, true)]
    [InlineData(256, true)]
    [InlineData(320, true)]
    [InlineData(null, true)]
    public void Candidate_prefilter_keeps_qualified_and_unknown_candidates(int? bitrate, bool allowed) =>
        Assert.Equal(allowed, Create().IsCandidateAllowed(false, bitrate));

    [Theory]
    [InlineData("320K", true)]
    [InlineData("128K", true)]
    [InlineData("Below320K", true)]
    [InlineData("192K", false)]
    [InlineData("Lossless", true)]
    public void Configuration_cannot_open_the_whole_below320_bucket(string minimum, bool fallback)
    {
        var gate = Create(minimum, fallback);
        Assert.False(gate.IsAcceptableForLibrary("mp3", "mp3", 128));
        Assert.False(gate.IsAcceptableForLibrary("mp3", "mp3", 192));
    }
}
