using ShineMusic.Model.Entities;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

/// <summary>自动评分决定「≥阈值直通发布」，重构时权重口径不能漂移。</summary>
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
        Assert.Equal(40, result.Metadata.MaxScore);
        Assert.Equal(20, result.Lyrics.MaxScore);
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
    public void Kuwo_uses_128k_as_its_relative_ceiling()
    {
        var item = NewItem();
        item.SourceProvider = "kuwo";
        item.Codec = "mp3";
        item.BitrateKbps = 128;

        Assert.Equal(40, SourceScoreCalculator.Calculate(item).Quality.Score);
    }

    [Fact]
    public void Lyrics_confidence_scales_to_twenty_points()
    {
        var item = NewItem();
        item.Codec = "flac";
        item.LyricsConfidence = 0.5;
        Assert.Equal(10, SourceScoreCalculator.Calculate(item).Lyrics.Score);

        item.LyricsConfidence = null;
        Assert.Equal(0, SourceScoreCalculator.Calculate(item).Lyrics.Score);
    }

    [Fact]
    public void Fully_populated_item_scores_one_hundred_and_breakdown_is_json()
    {
        var item = NewItem();
        item.Codec = "flac";
        item.Title = "晴天";
        item.ArtistsJson = "[\"周杰伦\"]";
        item.AlbumTitle = "叶惠美";
        item.GenresJson = "[\"Pop\"]";
        item.DurationMs = 269000;
        item.ArtworkStorageKey = "artwork/1.jpg";
        item.LyricsConfidence = 1.0;

        var result = SourceScoreCalculator.Calculate(item);
        Assert.Equal(100, result.Total);
        Assert.Contains("\"total\":100", result.ToBreakdownJson());
    }
}
