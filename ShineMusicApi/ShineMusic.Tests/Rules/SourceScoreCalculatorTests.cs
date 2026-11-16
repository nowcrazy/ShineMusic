using ShineMusic.Model.Entities;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

/// <summary>自动评分决定「≥阈值直通发布」，重构时权重口径不能漂移。</summary>
/// <remarks>
/// V2.0 Phase 2 评分模型（满分 100，阈值 90）：
///   身份 40（Matched=40/Ambiguous=20/Rejected=0/未知=40）
///   音质 40（无损=40/320k=40/低于256k=0/未知=20）
///   元数据 15（标题5+艺人5+专辑3+流派2）
///   歌词 5（confidence×5）
/// </remarks>
public sealed class SourceScoreCalculatorTests
{
    private static LibraryImportItem NewItem() => new()
    {
        DisplayName = "test.m4a",
        SourceStorageKey = "sourcedl/test.m4a",
        ContentHash = "hash"
    };

    [Fact]
    public void Lossless_source_gets_full_quality_score()
    {
        var item = NewItem();
        item.Codec = "flac";
        item.Container = "flac";
        item.BitrateKbps = 900;

        var result = SourceScoreCalculator.Calculate(item);
        Assert.Equal(40, result.Quality.Score);
        Assert.Equal(40, result.Quality.MaxScore);
        Assert.Equal(40, result.Identity.MaxScore);
        Assert.Equal(15, result.Metadata.MaxScore);
        Assert.Equal(5, result.Lyrics.MaxScore);
    }

    [Fact]
    public void Unknown_bitrate_is_scored_twenty_not_zero()
    {
        var item = NewItem();
        item.Codec = "aac";
        item.BitrateKbps = null;

        Assert.Equal(20, SourceScoreCalculator.Calculate(item).Quality.Score);
    }

    [Fact]
    public void Low_bitrate_source_cannot_auto_publish_even_with_perfect_metadata()
    {
        // V2.0 Phase 2：<256kbps 音质=0，身份维度无参数调用默认 Matched=40，
        // 总分 = 40(身份) + 0(音质) + 15(元数据) + 5(歌词) = 60 < 90 阈值。
        var item = NewItem();
        item.SourceProvider = "kuwo";
        item.Codec = "mp3";
        item.BitrateKbps = 128;
        item.Title = "晴天";
        item.ArtistsJson = "[\"周杰伦\"]";
        item.AlbumTitle = "叶惠美";
        item.GenresJson = "[\"Pop\"]";
        item.DurationMs = 269000;
        item.LyricsConfidence = 1.0;

        var result = SourceScoreCalculator.Calculate(item);
        Assert.Equal(0, result.Quality.Score);
        Assert.Equal(60, result.Total);
        Assert.Contains("高质量门槛", result.Quality.Reason);
    }

    [Fact]
    public void High_bitrate_lossy_source_scores_by_absolute_reference()
    {
        var item = NewItem();
        item.SourceProvider = "migu";
        item.Codec = "mp3";
        item.BitrateKbps = 320;

        Assert.Equal(40, SourceScoreCalculator.Calculate(item).Quality.Score);

        item.BitrateKbps = 256;
        Assert.Equal(32, SourceScoreCalculator.Calculate(item).Quality.Score);
    }

    [Fact]
    public void Lyrics_confidence_scales_to_five_points()
    {
        var item = NewItem();
        item.Codec = "flac";
        item.LyricsConfidence = 0.5;
        // 0.5 × 5 = 2.5 → round away from zero → 3
        Assert.Equal(3, SourceScoreCalculator.Calculate(item).Lyrics.Score);

        item.LyricsConfidence = null;
        Assert.Equal(0, SourceScoreCalculator.Calculate(item).Lyrics.Score);
    }

    [Fact]
    public void Fully_populated_item_scores_one_hundred_and_breakdown_is_json()
    {
        // 无身份判定参数时身份维度默认 Matched=40。
        // 无损40 + 身份40 + 元数据15 + 歌词5 = 100。
        var item = NewItem();
        item.Codec = "flac";
        item.Title = "晴天";
        item.ArtistsJson = "[\"周杰伦\"]";
        item.AlbumTitle = "叶惠美";
        item.GenresJson = "[\"Pop\"]";
        item.DurationMs = 269000;
        item.LyricsConfidence = 1.0;

        var result = SourceScoreCalculator.Calculate(item);
        Assert.Equal(100, result.Total);
        Assert.Contains("\"total\":100", result.ToBreakdownJson());
    }

    [Fact]
    public void Identity_rejected_drops_total_below_threshold()
    {
        // 身份 Rejected=0 + 无损40 + 元数据15 + 歌词5 = 60 < 90 阈值。
        var item = NewItem();
        item.Codec = "flac";
        item.Title = "晴天";
        item.ArtistsJson = "[\"周杰伦\"]";
        item.AlbumTitle = "叶惠美";
        item.GenresJson = "[\"Pop\"]";
        item.LyricsConfidence = 1.0;

        var rejected = new IdentityDecision(
            IdentityVerdict.Rejected, TrackIdentityMatcher.ReasonVersionMismatch, "候选含 Live 装饰");
        var result = SourceScoreCalculator.Calculate(item, rejected);
        Assert.Equal(0, result.Identity.Score);
        Assert.Equal(60, result.Total);
    }

    [Fact]
    public void Identity_ambiguous_drops_total_below_threshold()
    {
        // 身份 Ambiguous=20 + 无损40 + 元数据15 + 歌词5 = 80 < 90 阈值。
        var item = NewItem();
        item.Codec = "flac";
        item.Title = "晴天";
        item.ArtistsJson = "[\"周杰伦\"]";
        item.AlbumTitle = "叶惠美";
        item.GenresJson = "[\"Pop\"]";
        item.LyricsConfidence = 1.0;

        var ambiguous = new IdentityDecision(
            IdentityVerdict.Ambiguous, TrackIdentityMatcher.ReasonAmbiguous, "时长偏差 8s > 5s");
        var result = SourceScoreCalculator.Calculate(item, ambiguous);
        Assert.Equal(20, result.Identity.Score);
        Assert.Equal(80, result.Total);
    }

    [Fact]
    public void Identity_matched_with_320k_scores_one_hundred()
    {
        // 身份 Matched=40 + 320k 音质40 + 元数据15 + 歌词5 = 100 ≥ 90 阈值。
        var item = NewItem();
        item.Codec = "mp3";
        item.BitrateKbps = 320;
        item.Title = "晴天";
        item.ArtistsJson = "[\"周杰伦\"]";
        item.AlbumTitle = "叶惠美";
        item.GenresJson = "[\"Pop\"]";
        item.LyricsConfidence = 1.0;

        var matched = new IdentityDecision(
            IdentityVerdict.Matched, TrackIdentityMatcher.ReasonMatched);
        var result = SourceScoreCalculator.Calculate(item, matched);
        Assert.Equal(40, result.Identity.Score);
        Assert.Equal(100, result.Total);
    }
}
