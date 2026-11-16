// 职责：Phase 6 验收测试——AudioValidator 音质验证器。
// 验收标准：
//   - FLAC 通过实际编码验证（真无损 FLAC 编码 → 通过）
//   - 320K 通过实际媒体信息验证（MP3 320kbps → 通过）
//   - 有损转无损伪装被拦截（FLAC 容器 + MP3 编码 → encoding_mismatch）
//   - 试听片段不自动入库（45s 文件 → preview_fragment）
//   - SHA-256 校验失败 → hash_mismatch

using ShineMusic.Service;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using Xunit;

namespace ShineMusic.Tests.Rules;

public class AudioValidatorTests
{
    // ─────────────────────── 通过场景 ───────────────────────

    [Fact]
    public void True_flac_lossless_passes()
    {
        // 验收：FLAC 通过实际编码验证
        var probed = new ProbedMedia("flac", "flac", 935, 269000, "flac", 44100, 16, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000, fileSizeBytes: 30_000_000);

        Assert.True(result.Accepted);
        Assert.NotNull(result.Quality);
        Assert.Equal(AudioQualityLevel.Lossless, result.Quality!.Level);
    }

    [Fact]
    public void Mp3_320k_passes()
    {
        // 验收：320K 通过实际媒体信息验证
        var probed = new ProbedMedia("mp3", "mp3", 320, 269000, "mp3", 44100, null, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000, fileSizeBytes: 10_000_000);

        Assert.True(result.Accepted);
        Assert.NotNull(result.Quality);
        Assert.Equal(AudioQualityLevel.High320K, result.Quality!.Level);
    }

    [Fact]
    public void Wav_pcm_passes_as_lossless()
    {
        var probed = new ProbedMedia("wav", "pcm_s16le", 1411, 269000, "wav", 44100, 16, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000, fileSizeBytes: 47_000_000);

        Assert.True(result.Accepted);
        Assert.Equal(AudioQualityLevel.Lossless, result.Quality!.Level);
    }

    [Fact]
    public void Alac_in_m4a_passes_as_lossless()
    {
        var probed = new ProbedMedia("m4a", "alac", 935, 269000, "mov,mp4,m4a", 44100, 16, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000, fileSizeBytes: 30_000_000);

        Assert.True(result.Accepted);
        Assert.Equal(AudioQualityLevel.Lossless, result.Quality!.Level);
    }

    // ─────────────────────── 有损转无损伪装 ───────────────────────

    [Fact]
    public void Mp3_in_flac_container_rejected()
    {
        // 验收：MP3 被重命名为 .flac（有损转无损伪装）
        var probed = new ProbedMedia("flac", "mp3", 128, 269000, "flac", 44100, null, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000);

        Assert.False(result.Accepted);
        Assert.Equal("encoding_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Aac_in_wav_container_rejected()
    {
        var probed = new ProbedMedia("wav", "aac", 256, 269000, "wav", 44100, null, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000);

        Assert.False(result.Accepted);
        Assert.Equal("encoding_mismatch", result.ErrorCode);
    }

    // ─────────────────────── 试听片段检测 ───────────────────────

    [Fact]
    public void Preview_fragment_with_large_expected_duration_rejected()
    {
        // 45s 文件相对 269s 的期望时长明显不足。
        var probed = new ProbedMedia("mp3", "mp3", 128, 45_000, "mp3", 44100, null, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000);

        Assert.False(result.Accepted);
        Assert.Equal("preview_fragment", result.ErrorCode);
    }

    [Fact]
    public void Preview_fragment_with_large_duration_deviation_rejected()
    {
        // 60s 文件 vs 269s 期望 → 偏差 209s > 15s 且 > 269*0.25=67s
        var probed = new ProbedMedia("flac", "flac", 935, 60_000, "flac", 44100, 16, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000);

        Assert.False(result.Accepted);
        Assert.Equal("preview_fragment", result.ErrorCode);
    }

    [Fact]
    public void Short_duration_without_expected_passes_if_over_30s()
    {
        // 35s 文件，无期望时长 → 不触发试听检测（超过 30s 绝对下限）
        var probed = new ProbedMedia("mp3", "mp3", 320, 35_000, "mp3", 44100, null, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: null);

        Assert.True(result.Accepted);
    }

    [Fact]
    public void Normal_duration_deviation_passes()
    {
        // 260s vs 269s → 偏差 9s < 15s → 正常范围
        var probed = new ProbedMedia("flac", "flac", 935, 260_000, "flac", 44100, 16, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000);

        Assert.True(result.Accepted);
    }

    // ─────────────────────── SHA-256 完整性 ───────────────────────

    [Fact]
    public void Hash_mismatch_rejected()
    {
        var probed = new ProbedMedia("flac", "flac", 935, 269000, "flac", 44100, 16, 2);

        var result = AudioValidator.Evaluate(probed,
            expectedSha256: "aaa", actualSha256: "bbb");

        Assert.False(result.Accepted);
        Assert.Equal("hash_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Hash_match_passes()
    {
        var probed = new ProbedMedia("flac", "flac", 935, 269000, "flac", 44100, 16, 2);

        var result = AudioValidator.Evaluate(probed,
            expectedSha256: "abc123", actualSha256: "ABC123"); // 大小写不敏感

        Assert.True(result.Accepted);
    }

    [Fact]
    public void Null_hash_skips_check()
    {
        var probed = new ProbedMedia("mp3", "mp3", 320, 269000, "mp3", 44100, null, 2);

        var result = AudioValidator.Evaluate(probed,
            expectedSha256: null, actualSha256: "abc");

        Assert.True(result.Accepted);
    }

    // ─────────────────────── 质量等级映射 ───────────────────────

    [Fact]
    public void Low_bitrate_mp3_maps_to_below320k()
    {
        var probed = new ProbedMedia("mp3", "mp3", 128, 269000, "mp3", 44100, null, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000);

        Assert.True(result.Accepted);
        Assert.Equal(AudioQualityLevel.Below320K, result.Quality!.Level);
    }

    [Fact]
    public void Quality_carries_full_probe_info()
    {
        var probed = new ProbedMedia("flac", "flac", 935, 269000, "flac", 44100, 16, 2);

        var result = AudioValidator.Evaluate(probed, expectedDurationMs: 269000, fileSizeBytes: 30_000_000);

        Assert.True(result.Accepted);
        var q = result.Quality!;
        Assert.Equal("flac", q.Codec);
        Assert.Equal(935, q.BitrateKbps);
        Assert.Equal(44100, q.SampleRateHz);
        Assert.Equal(16, q.BitsPerSample);
        Assert.Equal(2, q.Channels);
        Assert.Equal(269000, q.DurationMs);
        Assert.Equal(30_000_000, q.FileSizeBytes);
    }
}
