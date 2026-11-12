using ShineMusic.Model.Contracts;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class SourceSearchMatcherTests
{
    [Fact]
    public void Rejects_empty_query_and_unknown_mode()
    {
        Assert.False(SourceSearchMatcher.TryParse("   ", "title", out _, out var emptyError));
        Assert.NotEmpty(emptyError);

        Assert.False(SourceSearchMatcher.TryParse("周杰伦", "nope", out _, out var modeError));
        Assert.Contains("title", modeError);
    }

    [Fact]
    public void Splits_artist_track_on_ascii_and_fullwidth_separators()
    {
        Assert.True(SourceSearchMatcher.TryParse("周杰伦 - 晴天", "artist-track", out var criteria, out _));
        Assert.NotNull(criteria);
        Assert.Equal(SourceSearchMode.ArtistTrack, criteria!.Mode);
        Assert.Equal("周杰伦", criteria.Artist);
        Assert.Equal("晴天", criteria.Term);
        Assert.Equal("周杰伦 晴天", criteria.ProviderQuery);

        Assert.True(SourceSearchMatcher.TryParse("周杰伦－晴天", "artist-track", out var fullwidth, out _));
        Assert.Equal("周杰伦", fullwidth!.Artist);
    }

    [Theory]
    [InlineData("周杰伦")]
    [InlineData("- 晴天")]
    [InlineData("周杰伦 -")]
    public void Rejects_artist_track_without_both_sides(string query)
    {
        Assert.False(SourceSearchMatcher.TryParse(query, "artist-track", out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Filter_normalizes_fullwidth_punctuation_and_case()
    {
        Assert.True(SourceSearchMatcher.TryParse("ＡＢＣ　－　ｔｅｓｔ", "artist-track", out var criteria, out _));
        var item = new SourceSearchItemDto(
            "netease", "1", "TEST", ["abc"], null, null, [], null, null, null, "320k", false);
        var filtered = SourceSearchMatcher.Filter([item], criteria!);
        Assert.Single(filtered);
    }

    [Fact]
    public void Album_mode_matches_only_album_title()
    {
        Assert.True(SourceSearchMatcher.TryParse("范特西", "album", out var criteria, out _));
        var matching = new SourceSearchItemDto(
            "netease", "1", "晴天", ["周杰伦"], "范特西", null, [], null, null, null, "320k", false);
        var other = new SourceSearchItemDto(
            "netease", "2", "晴天", ["周杰伦"], "叶惠美", null, [], null, null, null, "320k", false);
        Assert.Single(SourceSearchMatcher.Filter([matching, other], criteria!));
    }
}
