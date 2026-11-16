// 职责：用户 2026-11-12 追加裁决「评分每一项分值单独写进配置文件、总分 100」的回归锁。
// 数值本身仍为 40/40/15/5（用户选择「先不改，单独评审」），这里锁的是
// 「四项可独立配置 + 合计 100 + 低质量上限随配置动态推导 + 缩放不改变默认逐分」。

using Microsoft.Extensions.Configuration;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class ScoreWeightsTests
{
    private static LibraryImportItem FullItem() => new()
    {
        DisplayName = "test.flac",
        SourceStorageKey = "sourcedl/test.flac",
        ContentHash = "hash",
        Title = "晴天",
        ArtistsJson = "[\"周杰伦\"]",
        AlbumTitle = "叶惠美",
        GenresJson = "[\"流行\"]",
        DurationMs = 269000,
        LyricsConfidence = 1.0,
        Container = "flac",
        Codec = "flac",
        BitrateKbps = 900,
    };

    [Fact]
    public void Default_weights_sum_to_one_hundred_and_derive_the_sixty_point_ceiling()
    {
        var weights = new ScoreWeightsOptions();

        Assert.Equal(40, weights.Identity);
        Assert.Equal(40, weights.Quality);
        Assert.Equal(15, weights.Metadata);
        Assert.Equal(5, weights.Lyrics);
        Assert.Equal(100, weights.Total);
        Assert.Equal(SourceScoreCalculator.LowQualityAutoScoreCeiling, weights.LowQualityCeiling);
    }

    [Fact]
    public void Config_section_binds_each_weight_independently()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SourceDownload:ScoreWeights:Identity"] = "35",
                ["SourceDownload:ScoreWeights:Quality"] = "35",
                ["SourceDownload:ScoreWeights:Metadata"] = "20",
                ["SourceDownload:ScoreWeights:Lyrics"] = "10",
            })
            .Build();
        var options = new SourceDownloadOptions();
        configuration.GetSection(SourceDownloadOptions.SectionName).Bind(options);

        Assert.Equal(35, options.ScoreWeights.Identity);
        Assert.Equal(35, options.ScoreWeights.Quality);
        Assert.Equal(20, options.ScoreWeights.Metadata);
        Assert.Equal(10, options.ScoreWeights.Lyrics);
        Assert.Equal(100, options.ScoreWeights.Total);
    }

    [Fact]
    public void Custom_weights_change_each_dimension_max_score_but_keep_total_at_one_hundred()
    {
        var weights = new ScoreWeightsOptions
        {
            Identity = 35, Quality = 35, Metadata = 20, Lyrics = 10,
        };

        var result = SourceScoreCalculator.Calculate(FullItem(), identityDecision: null, weights);

        Assert.Equal(35, result.Identity.MaxScore);
        Assert.Equal(35, result.Quality.MaxScore);
        Assert.Equal(20, result.Metadata.MaxScore);
        Assert.Equal(10, result.Lyrics.MaxScore);
        Assert.Equal(35, result.Identity.Score);
        Assert.Equal(35, result.Quality.Score);
        Assert.Equal(20, result.Metadata.Score);
        Assert.Equal(10, result.Lyrics.Score);
        Assert.Equal(100, result.Total);
    }

    [Fact]
    public void Default_weights_keep_the_original_per_dimension_scores()
    {
        var result = SourceScoreCalculator.Calculate(FullItem());

        Assert.Equal(40, result.Identity.Score);
        Assert.Equal(40, result.Quality.Score);
        Assert.Equal(15, result.Metadata.Score);
        Assert.Equal(5, result.Lyrics.Score);
        Assert.Equal(100, result.Total);
    }

    [Fact]
    public void Low_quality_ceiling_follows_the_configured_weights()
    {
        var weights = new ScoreWeightsOptions
        {
            Identity = 50, Quality = 30, Metadata = 15, Lyrics = 5,
        };
        var item = FullItem();
        item.Codec = "mp3";
        item.Container = "mp3";
        item.BitrateKbps = 128;

        var result = SourceScoreCalculator.Calculate(item, identityDecision: null, weights);

        Assert.Equal(0, result.Quality.Score);
        Assert.Equal(70, weights.LowQualityCeiling);
        Assert.True(result.Total <= weights.LowQualityCeiling);
    }

    [Fact]
    public void A_rejected_identity_stays_below_the_dynamically_derived_ceiling()
    {
        var weights = new ScoreWeightsOptions
        {
            Identity = 50, Quality = 30, Metadata = 15, Lyrics = 5,
        };
        var rejected = new IdentityDecision(
            IdentityVerdict.Rejected, TrackIdentityMatcher.ReasonVersionMismatch, "候选含 Live 装饰");

        var result = SourceScoreCalculator.Calculate(FullItem(), rejected, weights);

        Assert.Equal(0, result.Identity.Score);
        Assert.Equal(50, result.Total); // 0 + 音质30 + 元数据15 + 歌词5
        Assert.True(result.Total <= weights.LowQualityCeiling);
    }
}
