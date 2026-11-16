using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

/// <summary>
/// 锁定「曲目身份匹配」正确性闸门（V2 Phase 2 / §8 第 2 步三态规则表）。
/// 历史文件名/类名保留以避免无谓的增删噪音；实际锁定的是唯一决策点
/// <see cref="TrackIdentityMatcher"/> 与换源重试谓词
/// <see cref="SourceDownloadFallbackService.MatchesCandidate"/>。
///
/// 第 0 步口径：标题归一化后完全相等、艺人互含、只产 Matched/Rejected。
/// 第 2 步口径（本文件当前版本）：装饰词分档、艺人精确元素匹配、时长校验、Ambiguous 绝不折叠成通过。
/// 规则来源 docs/音源质量稳定性策略.md §6.1（平衡口径，已拍板）。
/// </summary>
public sealed class SourceDownloadJobMatchingTests
{
    private static SourceSearchResult Entry(string title, string artist = "周杰伦") =>
        new("migu", "track-1", title, [artist], null, 240_000, [], null, null, null, "HQ", false);

    private static IdentityDecision Evaluate(
        string expectedTitle,
        string candidateTitle,
        string[]? expectedArtists = null,
        string[]? candidateArtists = null,
        long? expectedDurationMs = null,
        long? candidateDurationMs = null) =>
        TrackIdentityMatcher.Evaluate(
            new TrackIdentity(expectedTitle, expectedArtists ?? ["周杰伦"], expectedDurationMs),
            new TrackIdentity(candidateTitle, candidateArtists ?? ["周杰伦"], candidateDurationMs));

    // ─────────────────────── 标题核心匹配 ───────────────────────

    [Theory]
    [InlineData("晴天", "晴天")]
    [InlineData("晴天", "晴天 (Live)")]                // 装饰在 core 层被剥离；是否放行由 Evaluate 的三态决定
    [InlineData("晴天", "晴天（Live版）")]
    [InlineData("晴天", "晴天 [DJ版]")]
    [InlineData("晴天", "晴天 (Live) (Feat. 某人)")]
    public void TitleMatches_equal_after_normalization(string taskTitle, string entryTitle)
        => Assert.True(TrackIdentityMatcher.TitleMatches(taskTitle, entryTitle));

    [Theory]
    [InlineData("晴天", "等晴天")]                      // 完全不同歌曲、恰好以核心结尾 → 必须拒绝
    [InlineData("晴天", "Remastered 晴天")]             // 前缀装饰不在括号内，不剥离
    [InlineData("晴天", "晴天们")]
    [InlineData("晴天", "雨天的晴天")]
    public void TitleMatches_rejects_different_song(string taskTitle, string entryTitle)
        => Assert.False(TrackIdentityMatcher.TitleMatches(taskTitle, entryTitle));

    [Fact]
    public void TitleMatches_rejects_empty_or_different_entry_but_accepts_single_character()
    {
        Assert.False(TrackIdentityMatcher.TitleMatches("晴天", null));
        Assert.False(TrackIdentityMatcher.TitleMatches("晴天", "天"));
        Assert.False(TrackIdentityMatcher.TitleMatches("", ""));
        Assert.True(TrackIdentityMatcher.TitleMatches("晴", "晴"));
    }

    [Theory]
    [InlineData("晴天 (Live)", "晴天")]
    [InlineData("周杰伦（Jay）", "周杰伦")]
    [InlineData("  晴天 　", "晴天")]
    [InlineData("晴天 - Remastered", "晴天")]          // 连字符版本尾缀也参与分类
    public void NormalizeTitle_strips_decorations(string input, string expected)
        => Assert.Equal(expected, TrackIdentityMatcher.NormalizeTitle(input));

    [Fact]
    public void NormalizeTitle_returns_empty_for_null_or_whitespace()
        => Assert.Equal(string.Empty, TrackIdentityMatcher.NormalizeTitle(null));

    // ─────────────────────── 三态：Matched ───────────────────────

    [Fact]
    public void Evaluate_returns_matched_for_exact_identity()
    {
        var decision = Evaluate("晴天", "晴天");

        Assert.Equal(IdentityVerdict.Matched, decision.Verdict);
        Assert.Equal(TrackIdentityMatcher.ReasonMatched, decision.ReasonCode);
    }

    [Fact]
    public void Evaluate_exposes_expected_and_candidate_titles_for_review_ui()
    {
        var decision = Evaluate("晴天", "等晴天");

        Assert.Equal("晴天", decision.ExpectedTitle);
        Assert.Equal("等晴天", decision.CandidateTitle);
    }

    [Fact]
    public void Evaluate_returns_title_mismatch_reason()
    {
        var decision = Evaluate("晴天", "等晴天");

        Assert.Equal(IdentityVerdict.Rejected, decision.Verdict);
        Assert.Equal(TrackIdentityMatcher.ReasonTitleMismatch, decision.ReasonCode);
    }

    [Fact]
    public void Evaluate_returns_artist_mismatch_reason()
    {
        var decision = Evaluate("晴天", "晴天", candidateArtists: ["周深"]);

        Assert.Equal(IdentityVerdict.Rejected, decision.Verdict);
        Assert.Equal(TrackIdentityMatcher.ReasonArtistMismatch, decision.ReasonCode);
    }

    [Fact]
    public void Evaluate_skips_artist_check_when_expected_artist_missing()
    {
        var decision = Evaluate("晴天", "晴天", expectedArtists: [], candidateArtists: ["周深"]);

        Assert.Equal(IdentityVerdict.Matched, decision.Verdict);
    }

    // ─────────────────────── 三态：Rejected 版本装饰 ───────────────────────

    [Theory]
    [InlineData("晴天 (Live)")]
    [InlineData("晴天（现场版）")]
    [InlineData("晴天 (翻唱)")]
    [InlineData("晴天 (Cover)")]
    [InlineData("晴天 (伴奏)")]
    [InlineData("晴天 (卡拉OK)")]
    [InlineData("晴天 (Karaoke)")]
    [InlineData("晴天 (Instrumental)")]
    [InlineData("晴天 (黑胶版)")]
    [InlineData("晴天 (DJ版)")]
    [InlineData("晴天 (Remix)")]
    [InlineData("晴天 (纯音乐)")]
    [InlineData("晴天 (片段)")]
    [InlineData("晴天 (试听)")]
    public void Rejected_decoration_words_are_rejected(string candidateTitle)
    {
        var decision = Evaluate("晴天", candidateTitle);

        Assert.Equal(IdentityVerdict.Rejected, decision.Verdict);
        Assert.Equal(TrackIdentityMatcher.ReasonVersionMismatch, decision.ReasonCode);
    }

    [Fact]
    public void Full_width_decoration_cannot_bypass_wordlist()
    {
        var decision = Evaluate("晴天", "晴天（Ｌｉｖｅ）");

        Assert.Equal(IdentityVerdict.Rejected, decision.Verdict);
    }

    [Fact]
    public void Expected_live_but_candidate_unmarked_is_ambiguous()
    {
        var decision = Evaluate("晴天 (Live)", "晴天");

        Assert.Equal(IdentityVerdict.Ambiguous, decision.Verdict);
        Assert.Equal(TrackIdentityMatcher.ReasonAmbiguous, decision.ReasonCode);
    }

    // ─────────────────────── 三态：Ambiguous 版本装饰 ───────────────────────

    [Theory]
    [InlineData("晴天 (Remastered)")]
    [InlineData("晴天 (Deluxe)")]
    [InlineData("晴天 (Acoustic)")]
    [InlineData("晴天 (重制版)")]
    [InlineData("晴天 (不插电)")]
    [InlineData("晴天 - Remastered")]
    [InlineData("晴天 (电视剧XX插曲)")]                 // 未识别括号说明 → 待审
    [InlineData("晴天 (电影XX主题曲)")]
    [InlineData("晴天 (歌手2018)")]
    public void Ambiguous_decorations_are_deferred_not_folded_into_pass(string candidateTitle)
    {
        var decision = Evaluate("晴天", candidateTitle);

        Assert.Equal(IdentityVerdict.Ambiguous, decision.Verdict);
        Assert.Equal(TrackIdentityMatcher.ReasonAmbiguous, decision.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(decision.Detail));
    }

    [Fact]
    public void Expected_track_with_same_explicit_decoration_is_matched()
    {
        // 「歌单原曲本身就是 Live」：以期望身份为准，同装饰候选可判 Matched。
        var decision = Evaluate("晴天 (Live)", "晴天 (Live)");

        Assert.Equal(IdentityVerdict.Matched, decision.Verdict);
    }

    [Fact]
    public void Hyphen_suffix_decoration_is_classified()
    {
        Assert.Equal(TrackVersionKind.AmbiguousDecoration,
            TrackIdentityMatcher.AnalyzeTitle("晴天 - Remastered").VersionKind);
        Assert.Equal(TrackVersionKind.RejectedVersion,
            TrackIdentityMatcher.AnalyzeTitle("晴天 - Live").VersionKind);
    }

    // ─────────────────────── 三态：艺人精确元素匹配 ───────────────────────

    [Fact]
    public void Artist_exact_element_is_matched()
    {
        var decision = Evaluate("晴天", "晴天", candidateArtists: ["费玉清", "周杰伦"]);

        Assert.Equal(IdentityVerdict.Matched, decision.Verdict);
    }

    [Fact]
    public void Artist_partial_substring_is_ambiguous()
    {
        // 第 0 步的互含语义会把「周杰伦们」当成周杰伦；第 2 步改为精确元素匹配。
        var decision = Evaluate("晴天", "晴天", candidateArtists: ["周杰伦们"]);

        Assert.Equal(IdentityVerdict.Ambiguous, decision.Verdict);
        Assert.Contains("子串", decision.Detail);
    }

    [Fact]
    public void Artist_completely_different_is_rejected()
        => Assert.Equal(IdentityVerdict.Rejected,
            Evaluate("晴天", "晴天", candidateArtists: ["周深"]).Verdict);

    // ─────────────────────── 三态：时长 ───────────────────────

    [Fact]
    public void Duration_within_5_seconds_is_matched()
        => Assert.Equal(IdentityVerdict.Matched,
            Evaluate("晴天", "晴天", expectedDurationMs: 269_000, candidateDurationMs: 272_000).Verdict);

    [Fact]
    public void Duration_between_5s_and_25_percent_is_ambiguous()
        => Assert.Equal(IdentityVerdict.Ambiguous,
            Evaluate("晴天", "晴天", expectedDurationMs: 269_000, candidateDurationMs: 289_000).Verdict);

    [Fact]
    public void Duration_over_25_percent_and_15s_is_rejected()
    {
        // 45 秒试听片段冒充 269 秒完整歌曲。
        var decision = Evaluate("晴天", "晴天", expectedDurationMs: 269_000, candidateDurationMs: 45_000);

        Assert.Equal(IdentityVerdict.Rejected, decision.Verdict);
        Assert.Equal(TrackIdentityMatcher.ReasonDurationMismatch, decision.ReasonCode);
    }

    [Fact]
    public void Missing_candidate_duration_is_ambiguous()
        => Assert.Equal(IdentityVerdict.Ambiguous,
            Evaluate("晴天", "晴天", expectedDurationMs: 269_000, candidateDurationMs: null).Verdict);

    // ─────────────────────── 换源重试路径（影子副本实锤的回归闸门） ───────────────────────

    [Fact]
    public void Switch_source_path_rejects_different_song_with_same_artist()
        => Assert.False(SourceDownloadFallbackService.MatchesCandidate(
            "晴天", "周杰伦", Entry("等晴天", "周杰伦")));

    [Fact]
    public void Switch_source_path_accepts_exact_title()
        => Assert.True(SourceDownloadFallbackService.MatchesCandidate("晴天", "周杰伦", Entry("晴天")));

    [Fact]
    public void Switch_source_path_rejects_live_version()
        => Assert.False(SourceDownloadFallbackService.MatchesCandidate(
            "晴天", "周杰伦", Entry("晴天 (Live)")));

    [Fact]
    public void Switch_source_path_rejects_ambiguous_version()
        => Assert.False(SourceDownloadFallbackService.MatchesCandidate(
            "晴天", "周杰伦", Entry("晴天 (Remastered)")));

    [Fact]
    public void Switch_source_path_skips_artist_check_when_artist_missing()
        => Assert.True(SourceDownloadFallbackService.MatchesCandidate("晴天", null, Entry("晴天", "周深")));
}
