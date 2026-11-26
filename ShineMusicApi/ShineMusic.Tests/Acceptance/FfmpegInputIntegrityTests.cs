using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using Xunit;

namespace ShineMusic.Tests.Acceptance;

public sealed class FfmpegInputIntegrityTests
{
    [FfmpegTheory]
    [InlineData(16)]
    [InlineData(24)]
    [Trait("Category", "FfmpegIntegration")]
    public async Task Flac_pcm_integrity_accepts_trailing_tags_but_rejects_missing_or_changed_samples(int bitsPerSample)
    {
        var bin = FfmpegFactAttribute.FindBin()!;
        var root = Path.Combine(Path.GetTempPath(), "shine-audio-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var valid = Path.Combine(root, "valid.flac");
            var damaged = Path.Combine(root, "damaged.flac");
            var ffmpeg = Path.Combine(bin, FfmpegFactAttribute.Executable("ffmpeg"));
            using var generate = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true
                }
            };
            foreach (var argument in new[] { "-nostdin", "-hide_banner", "-v", "error", "-y",
                         "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "flac",
                         "-sample_fmt", bitsPerSample == 16 ? "s16" : "s32",
                         "-bits_per_raw_sample", bitsPerSample.ToString(), valid })
                generate.StartInfo.ArgumentList.Add(argument);
            generate.Start();
            var errors = generate.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await generate.WaitForExitAsync(deadline.Token);
            Assert.True(generate.ExitCode == 0, await errors);

            var media = new FfmpegMediaService(Options.Create(new FfmpegOptions
            {
                ExecutablePath = ffmpeg,
                ProbeExecutablePath = Path.Combine(bin, FfmpegFactAttribute.Executable("ffprobe"))
            }), NullLogger<FfmpegMediaService>.Instance);
            Assert.Equal("flac", (await media.ProbeAsync(valid, default)).Codec);
            await media.ValidateCompleteAsync(valid, default);

            // Leave STREAMINFO/tag blocks intact: header probing still succeeds while frame
            // decoding must reject the input, matching damaged uploads with readable metadata.
            var bytes = await File.ReadAllBytesAsync(valid);
            Assert.Equal(bitsPerSample, (((bytes[20] & 1) << 4) | (bytes[21] >> 4)) + 1);

            // Standard ID3v1 tags and vendor-specific trailers are not decoded audio. Accept
            // them only because both the promised sample count and PCM checksum still match.
            foreach (var trailer in new[] { new byte[128], System.Text.Encoding.UTF8.GetBytes("vendor appended metadata") })
            {
                if (trailer.Length == 128) "TAG"u8.CopyTo(trailer);
                await File.WriteAllBytesAsync(damaged, bytes.Concat(trailer).ToArray());
                await media.ValidateCompleteAsync(damaged, default);
            }

            // Missing end frames and changed STREAMINFO checksums must never pass the fallback.
            await AssertCorruptAsync(media, damaged, bytes[..^100], root);
            var badChecksum = bytes.ToArray();
            badChecksum[26] ^= 1;
            await AssertCorruptAsync(media, damaged, badChecksum, root);

            // Even a clean decoder exit cannot excuse a mismatch in the promised sample count.
            var badSampleCount = bytes.ToArray();
            badSampleCount[25] ^= 1;
            await AssertCorruptAsync(media, damaged, badSampleCount, root);

            // No checksum means there is no proof that the audio is intact: retain strict decode.
            var uncheckedTrailer = bytes.Concat(new byte[128]).ToArray();
            uncheckedTrailer.AsSpan(26, 16).Clear();
            "TAG"u8.CopyTo(uncheckedTrailer.AsSpan(bytes.Length));
            await AssertCorruptAsync(media, damaged, uncheckedTrailer, root);
            var offset = 4; // fLaC magic
            bool last;
            do
            {
                last = (bytes[offset] & 0x80) != 0;
                var length = (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
                offset += 4 + length;
            } while (!last);
            bytes.AsSpan(offset, 32).Clear();
            await File.WriteAllBytesAsync(damaged, bytes);
            Assert.Equal("flac", (await media.ProbeAsync(damaged, default)).Codec);
            var failure = await Assert.ThrowsAsync<MediaProcessingException>(() => media.ValidateCompleteAsync(damaged, default));
            Assert.Equal(ErrorCodes.MediaCorrupt, failure.Code);
            Assert.DoesNotContain(root, ImportFormatting.SafeErrorMessage(failure.Code));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AssertCorruptAsync(FfmpegMediaService media, string path, byte[] bytes, string root)
    {
        await File.WriteAllBytesAsync(path, bytes);
        var failure = await Assert.ThrowsAsync<MediaProcessingException>(() => media.ValidateCompleteAsync(path, default));
        Assert.Equal(ErrorCodes.MediaCorrupt, failure.Code);
        Assert.DoesNotContain(root, ImportFormatting.SafeErrorMessage(failure.Code));
    }
}

internal sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute()
    {
        if (FfmpegFactAttribute.FindBin() is null)
            Skip = "Set SHINEMUSIC_TEST_FFMPEG_BIN, add ffmpeg and ffprobe to PATH, or place both in ShineMusicApi/tools/ffmpeg/bin to run media integration tests.";
    }
}

internal sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FindBin() is null)
            Skip = "Set SHINEMUSIC_TEST_FFMPEG_BIN, add ffmpeg and ffprobe to PATH, or place both in ShineMusicApi/tools/ffmpeg/bin to run media integration tests.";
    }

    internal static string Executable(string name) => TestFfmpeg.Executable(name);

    internal static string? FindBin() => TestFfmpeg.FindBin();
}
