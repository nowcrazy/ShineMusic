using System.Text;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;

namespace ShineMusic.Model.Domain;

public enum OriginalQualityComparison { Unknown, Equal, Better, Worse }

/// <summary>Nomination and replacement are separate decisions; codec labels are not provenance evidence.</summary>
public static class LibraryWorkflowPolicy
{
    public static bool CanNominate(LibraryImportItem item, LibraryWorkflowOptions options)
    {
        if (item.DurationMs is null or <= 0 || string.IsNullOrWhiteSpace(item.Title)
            || string.IsNullOrWhiteSpace(item.ArtistsJson) || item.ArtistsJson == "[]") return false;
        if (AudioAdmissionPolicy.IsLosslessCodec(item.Codec)) return true;
        if (item.Channels != 2 || item.SampleRateHz is null or < 32000) return false;
        var minimum = item.Codec?.ToLowerInvariant() switch
        {
            "mp3" => options.Mp3MinimumKbps,
            "aac" when IsAacLc(item.CodecProfile) => options.AacLcMinimumKbps,
            "opus" => options.OpusMinimumKbps,
            "vorbis" => options.VorbisMinimumKbps,
            _ => int.MaxValue
        };
        return item.BitrateKbps is > 0 && item.BitrateKbps >= minimum;
    }

    public static bool IsAacLc(string? profile) => profile?.Trim().ToLowerInvariant() is "lc" or "aac lc" or "aac-lc";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    public static OriginalQualityComparison Compare(LibraryImportItem incoming, MediaAsset? existing)
    {
        if (existing is null || !existing.IsOriginal) return OriginalQualityComparison.Unknown;
        if (incoming.ContentHash == existing.ContentHash) return OriginalQualityComparison.Equal;
        var incomingLossless = AudioAdmissionPolicy.IsLosslessCodec(incoming.Codec);
        var existingLossless = AudioAdmissionPolicy.IsLosslessCodec(existing.Codec);
        // A nominally lossless encoding can contain an earlier lossy source. Cross-codec
        // replacement therefore needs the owner's/admin's explicit comparison.
        if (!string.Equals(incoming.Codec, existing.Codec, StringComparison.OrdinalIgnoreCase)
            || incoming.Channels is null || existing.Channels is null || incoming.Channels != existing.Channels
            || incoming.SampleRateHz is null || existing.SampleRateHz is null || incoming.SampleRateHz != existing.SampleRateHz)
            return OriginalQualityComparison.Unknown;
        if (incomingLossless || existingLossless)
        {
            if (incoming.BitsPerSample is null || existing.BitsPerSample is null) return OriginalQualityComparison.Unknown;
            return incoming.BitsPerSample == existing.BitsPerSample
                ? OriginalQualityComparison.Equal : OriginalQualityComparison.Unknown;
        }
        if (!string.Equals(incoming.CodecProfile, existing.CodecProfile, StringComparison.OrdinalIgnoreCase)
            || incoming.BitrateKbps is null or <= 0 || existing.BitrateKbps is null or <= 0)
            return OriginalQualityComparison.Unknown;
        return incoming.BitrateKbps.Value.CompareTo(existing.BitrateKbps.Value) switch
        {
            > 0 => OriginalQualityComparison.Better,
            < 0 => OriginalQualityComparison.Worse,
            _ => OriginalQualityComparison.Equal
        };
    }
}
