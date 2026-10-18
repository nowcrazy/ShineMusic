using ShineMusic.Api.Options;
using ShineMusic.Api.Services;
using ShineMusic.Api.Tests.Domain;

namespace ShineMusic.Api.Tests.Services;

/// <summary>
/// 点歌需求 ↔ 待上架曲目的匹配校验器测试。
///
/// 锁定的行为：标题或艺人任一明显不一致即 <see cref="MatchVerdictKind.Mismatch"/>，
/// 轻微差异在 <see cref="TitleArtistMatchOptions.MaxEditDistance"/> 内判 FuzzyMatch 供调用方处置。
/// </summary>
public sealed class SongRequestMatchValidatorTests
{
    private static readonly TitleArtistMatchOptions Default = new() { MaxEditDistance = 1, Normalize = true };

    [Fact]
    public void Validate_标题艺人均一致_判Match且距离为0()
    {
        var request = RequestFixture.Create("天下", "张杰");

        var verdict = SongRequestMatchValidator.Validate("天下", ["张杰"], request, Default);

        Assert.Equal(MatchVerdictKind.Match, verdict.Kind);
        Assert.Equal(0, verdict.TitleDistance);
        Assert.Equal(0, verdict.ArtistDistance);
        Assert.Equal("天下 - 张杰", verdict.TrackDisplay);
        Assert.Equal("天下 - 张杰", verdict.RequestDisplay);
    }

    [Fact]
    public void Validate_标题错配_判Mismatch()
    {
        // 审计中记录的典型张冠李戴：同一艺人，但歌名完全不同。
        var request = RequestFixture.Create("天下", "张杰");

        var verdict = SongRequestMatchValidator.Validate("你是最美的太阳", ["张杰"], request, Default);

        Assert.Equal(MatchVerdictKind.Mismatch, verdict.Kind);
        Assert.True(verdict.TitleDistance > Default.MaxEditDistance, "标题距离应超过阈值");
    }

    [Fact]
    public void Validate_艺人错配_即使标题一致也判Mismatch()
    {
        var request = RequestFixture.Create("青花瓷", "周杰伦");

        var verdict = SongRequestMatchValidator.Validate("青花瓷", ["费玉清"], request, Default);

        Assert.Equal(MatchVerdictKind.Mismatch, verdict.Kind);
    }

    [Fact]
    public void Validate_错别字在阈值内_判FuzzyMatch()
    {
        var request = RequestFixture.Create("七里香", "周杰伦");

        var verdict = SongRequestMatchValidator.Validate("七裡香", ["周杰伦"], request, Default);

        Assert.Equal(MatchVerdictKind.FuzzyMatch, verdict.Kind);
        Assert.True(verdict.TitleDistance > 0);
        Assert.True(verdict.TitleDistance <= Default.MaxEditDistance);
    }

    [Fact]
    public void Validate_多艺人任一命中_判Match()
    {
        var request = RequestFixture.Create("稻香", "周杰伦");

        var verdict = SongRequestMatchValidator.Validate("稻香", ["费玉清", "周杰伦"], request, Default);

        Assert.Equal(MatchVerdictKind.Match, verdict.Kind);
    }

    [Fact]
    public void Validate_开启归一化时_忽略大小写与内部空白()
    {
        var request = RequestFixture.Create("zhang jie tian xia", "zhang jie");

        var verdict = SongRequestMatchValidator.Validate("ZhangJieTianXia", ["ZhangJie"], request, Default);

        Assert.Equal(MatchVerdictKind.Match, verdict.Kind);
    }

    [Fact]
    public void Validate_关闭归一化时_大小写与空白差异都会拉开距离()
    {
        var options = new TitleArtistMatchOptions { MaxEditDistance = 0, Normalize = false };
        var request = RequestFixture.Create("Tian Xia", "Zhang Jie");

        var verdict = SongRequestMatchValidator.Validate("TianXia", ["ZhangJie"], request, options);

        Assert.Equal(MatchVerdictKind.Mismatch, verdict.Kind);
    }

    [Fact]
    public void Validate_曲目标题为空_判Mismatch且展示串降级()
    {
        var request = RequestFixture.Create("天下", "张杰");

        var verdict = SongRequestMatchValidator.Validate(null, ["张杰"], request, Default);

        Assert.Equal(MatchVerdictKind.Mismatch, verdict.Kind);
        Assert.Equal(-1, verdict.TitleDistance);
        Assert.Equal("(未填写标题)", verdict.TrackDisplay);
    }

    [Fact]
    public void Validate_需求侧缺少标题或艺人_判Mismatch而非误放行()
    {
        var request = RequestFixture.Create("天下", "张杰");

        Assert.Equal(MatchVerdictKind.Mismatch,
            SongRequestMatchValidator.Validate("天下", ["张杰"], request.With(title: ""), Default).Kind);
        Assert.Equal(MatchVerdictKind.Mismatch,
            SongRequestMatchValidator.Validate("天下", ["张杰"], request.With(artistName: "  "), Default).Kind);
    }

    [Fact]
    public void Validate_最大距离为0时_一字之差即判Mismatch()
    {
        var options = new TitleArtistMatchOptions { MaxEditDistance = 0, Normalize = true };
        var request = RequestFixture.Create("七里香", "周杰伦");

        var verdict = SongRequestMatchValidator.Validate("七裡香", ["周杰伦"], request, options);

        Assert.Equal(MatchVerdictKind.Mismatch, verdict.Kind);
        Assert.Equal(1, verdict.TitleDistance);
    }

    [Fact]
    public void Validate_曲目无艺人信息_距离报告负一()
    {
        var request = RequestFixture.Create("天下", "张杰");

        var verdict = SongRequestMatchValidator.Validate("天下", [], request, Default);

        Assert.Equal(MatchVerdictKind.Mismatch, verdict.Kind);
        Assert.Equal(-1, verdict.ArtistDistance);
    }
}
