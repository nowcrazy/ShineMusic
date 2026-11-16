// 职责：曲目身份判定的**唯一决策点**（无 IO 纯函数）。
//
// 2026-11-12 §8 第 0 步：把 SourceDownloadJob / SourceDownloadFallbackService 各持一份的
// TitleMatches / ArtistsMatch / NormalizeTitle 收归一处，配合 SingleDecisionPointTests 反射断言。
//
// 2026-11-12 V2.0 Phase 2 / §8 第 2 步（用户「合并实施」）：扩展为**三态规则表**。
// 规则来源：docs/音源质量稳定性策略.md §6.1（平衡口径，已拍板）：
//   - 标题：归一化后完全相等；括号内容先分类再决定是否容忍；
//   - Rejected 词：live/现场/翻唱/cover/伴奏/卡拉OK/karaoke/instrumental/黑胶版/dj/remix/纯音乐/片段/试听；
//   - Ambiguous 词：Remastered/Deluxe/Acoustic/重制/不插电，以及所有未识别括号说明；
//   - 歌单原曲自带相同装饰时，同装饰候选可判 Matched（保留「部分用户确实想收 Live」的召回）；
//   - 艺人：精确元素匹配 > 子串命中(Ambiguous) > 完全不同(Rejected)；
//   - 时长：≤5s Matched / 无数据或中间区间 Ambiguous / 偏差 >25% 且 >15s Rejected。
//
// 【裁决记录】用户本轮选「评分制取代硬闸门」，D1「身份做前置闸门」被修订：
// 三态仍由本类产出并记录（结果可追溯），但不再由 DeliveryContractVerifier 硬阻断；
// 发布决策改由 SourceScoreCalculator 的身份 40 分维度 + 90 分阈值决定。
// 本类不感知评分、不感知发布，只负责把判定说清楚。

namespace ShineMusic.Service.SourceDownload;

/// <summary>
/// 身份判定三态。<see cref="Ambiguous"/> 必须显式转人工，绝不折叠成通过。
/// </summary>
public enum IdentityVerdict
{
    Matched,
    Ambiguous,
    Rejected,
}

/// <summary>标题里版本装饰的分类结果。</summary>
public enum TrackVersionKind
{
    /// <summary>无装饰。</summary>
    None,

    /// <summary>不确定装饰（Remastered / Deluxe / Acoustic / 未识别括号说明）→ 待审。</summary>
    AmbiguousDecoration,

    /// <summary>明确错误版本（Live / 翻唱 / 伴奏 / 试听 ...）→ 拒绝。</summary>
    RejectedVersion,
}

/// <summary>曲目身份快照。<see cref="Artists"/> 为空表示「无艺人信息」，此时跳过艺人校验。</summary>
public sealed record TrackIdentity(
    string Title,
    IReadOnlyList<string> Artists,
    long? DurationMs = null,
    string? AlbumTitle = null,
    string? Isrc = null);

/// <summary>
/// 身份判定结论。<see cref="ReasonCode"/> 供审核页与 WarningsJson 记录；
/// <see cref="ExpectedTitle"/> / <see cref="CandidateTitle"/> 供审核页展示「期望 vs 实际」对照。
/// </summary>
public sealed record IdentityDecision(
    IdentityVerdict Verdict,
    string ReasonCode,
    string? Detail = null,
    string? ExpectedTitle = null,
    string? CandidateTitle = null);

/// <summary>标题解析结果：去掉装饰后的核心标题 + 装饰分类 + 原始装饰文本。</summary>
internal sealed record TitleAnalysis(string Core, TrackVersionKind VersionKind, string? Decoration);

/// <summary>艺人匹配级别（精确元素 / 子串命中 / 完全不同）。</summary>
internal enum ArtistMatchKind
{
    None,
    Partial,
    Exact,
}

/// <summary>曲目身份判定器：全仓唯一允许持有标题/艺人归一化与匹配实现的地方。</summary>
public static class TrackIdentityMatcher
{
    public const string ReasonMatched = "identity_matched";
    public const string ReasonTitleMismatch = "title_mismatch";
    public const string ReasonArtistMismatch = "artist_mismatch";
    public const string ReasonVersionMismatch = "version_mismatch";
    public const string ReasonDurationMismatch = "duration_mismatch";
    public const string ReasonAmbiguous = "identity_ambiguous";

    /// <summary>明确错误版本：歌单原曲未带相同装饰时直接 Rejected。</summary>
    private static readonly string[] RejectedDecorationWords =
    [
        "live", "现场", "翻唱", "cover", "伴奏", "卡拉ok", "karaoke",
        "instrumental", "黑胶版", "dj", "remix", "纯音乐", "片段", "试听",
        "club mix", "clubmix", "变速", "加速", "降速",
    ];

    /// <summary>不确定版本：归 Ambiguous，交人工确认（评审确认，不直接拒绝以免误伤）。</summary>
    private static readonly string[] AmbiguousDecorationWords =
    [
        "remastered", "deluxe", "acoustic", "重制", "不插电",
    ];

    private static readonly char[] DashSeparators = ['-', '–', '—'];

    /// <summary>
    /// 判定候选曲目是否与期望身份一致。调用方不得自带匹配实现。
    /// </summary>
    /// <param name="expected">歌单原曲 / 任务快照身份；<c>Artists</c> 第一项为主艺人。</param>
    /// <param name="candidate">搜索结果 / 实际下载候选身份。</param>
    public static IdentityDecision Evaluate(TrackIdentity expected, TrackIdentity candidate)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(candidate);

        var expectedAnalysis = AnalyzeTitle(expected.Title);
        var candidateAnalysis = AnalyzeTitle(candidate.Title);
        var context = (Expected: expected.Title, Candidate: candidate.Title);

        // —— 标题：归一化后必须完全相等 ——
        if (!TitleMatches(expected.Title, candidate.Title))
            return new IdentityDecision(IdentityVerdict.Rejected, ReasonTitleMismatch,
                $"期望「{expected.Title}」与候选「{candidate.Title}」归一化后不完全相等。",
                context.Expected, context.Candidate);

        // —— 版本装饰：Rejected 优先，且歌单原曲自带相同装饰时放行 ——
        var sameDecoration = !string.IsNullOrWhiteSpace(expectedAnalysis.Decoration)
                             && string.Equals(expectedAnalysis.Decoration, candidateAnalysis.Decoration,
                                 StringComparison.OrdinalIgnoreCase);
        if (candidateAnalysis.VersionKind == TrackVersionKind.RejectedVersion && !sameDecoration)
            return new IdentityDecision(IdentityVerdict.Rejected, ReasonVersionMismatch,
                $"候选含明确非录音室版本装饰「{candidateAnalysis.Decoration}」，歌单原曲未带相同装饰。",
                context.Expected, context.Candidate);

        // —— 艺人：精确元素匹配 / 子串命中 / 完全不同 ——
        var primaryArtist = expected.Artists.FirstOrDefault(artist => !string.IsNullOrWhiteSpace(artist));
        var artistKind = primaryArtist is null
            ? ArtistMatchKind.Exact
            : ArtistsMatch(candidate.Artists, primaryArtist);
        if (artistKind == ArtistMatchKind.None)
            return new IdentityDecision(IdentityVerdict.Rejected, ReasonArtistMismatch,
                $"主艺人「{primaryArtist}」与候选艺人列表完全不同。",
                context.Expected, context.Candidate);

        // —— 时长：硬性拒绝优先，其余按不确定处理 ——
        var ambiguousReasons = new List<string>();
        if (expected.DurationMs is > 0 && candidate.DurationMs is > 0)
        {
            var diffMs = Math.Abs(expected.DurationMs.Value - candidate.DurationMs.Value);
            if (diffMs > expected.DurationMs.Value * 0.25 && diffMs > 15_000)
                return new IdentityDecision(IdentityVerdict.Rejected, ReasonDurationMismatch,
                    $"时长偏差 {diffMs / 1000}s 超过 25% 且 >15s，疑似试听片段或错误版本。",
                    context.Expected, context.Candidate);
            if (diffMs > 5_000)
                ambiguousReasons.Add($"时长偏差 {diffMs / 1000}s > 5s");
        }
        else if (expected.DurationMs is > 0)
        {
            ambiguousReasons.Add("候选缺少时长数据，无法确认完整性");
        }

        // —— 汇总：任何不确定都降级为 Ambiguous，绝不折叠成 Matched ——
        if (candidateAnalysis.VersionKind == TrackVersionKind.AmbiguousDecoration && !sameDecoration)
            ambiguousReasons.Add($"候选含不确定版本装饰「{candidateAnalysis.Decoration}」");
        if (expectedAnalysis.VersionKind != TrackVersionKind.None
            && candidateAnalysis.VersionKind == TrackVersionKind.None)
            ambiguousReasons.Add("歌单原曲带版本装饰而候选未标注，版本无法确认");
        if (artistKind == ArtistMatchKind.Partial)
            ambiguousReasons.Add($"主艺人「{primaryArtist}」仅子串命中，未精确匹配");

        if (ambiguousReasons.Count > 0)
            return new IdentityDecision(IdentityVerdict.Ambiguous, ReasonAmbiguous,
                string.Join("；", ambiguousReasons), context.Expected, context.Candidate);

        return new IdentityDecision(IdentityVerdict.Matched, ReasonMatched, null,
            context.Expected, context.Candidate);
    }

    /// <summary>
    /// 标题核心匹配：分类并剥离版本装饰后必须**完全相等**才算匹配。
    /// 任何非装饰的多余字符都会使匹配失败（《等晴天》≠《晴天》）。
    /// </summary>
    internal static bool TitleMatches(string taskTitle, string? entryTitle)
    {
        var left = NormalizeTitle(taskTitle);
        var right = NormalizeTitle(entryTitle);
        if (left.Length == 0 || right.Length == 0)
            return false;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 艺人匹配：归一化后**精确元素相等**为 Exact；仅互含为 Partial；都不命中为 None。
    /// 第 0 步的互含语义已按 §6.1 升级为精确元素匹配，子串命中不再直接放行。
    /// </summary>
    internal static ArtistMatchKind ArtistsMatch(IReadOnlyList<string> artists, string primaryArtist)
    {
        var expected = NormalizeArtist(primaryArtist);
        if (expected.Length == 0)
            return ArtistMatchKind.None;

        var partial = false;
        foreach (var artist in artists)
        {
            var actual = NormalizeArtist(artist);
            if (actual.Length == 0)
                continue;
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return ArtistMatchKind.Exact;
            if (actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
                || expected.Contains(actual, StringComparison.OrdinalIgnoreCase))
                partial = true;
        }
        return partial ? ArtistMatchKind.Partial : ArtistMatchKind.None;
    }

    // Artist parentheses are part of the name, not recording-version decorations.
    private static string NormalizeArtist(string value)
        => string.Concat(FoldWidth(value).Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// 归一化标题：全半角统一 → 分类并剥离括号装饰与连字符版本尾缀 → 去空白。
    /// 与第 0 步「无条件剥离所有括号」不同：装饰内容会先分类，供 Evaluate 产出三态。
    /// </summary>
    internal static string NormalizeTitle(string? title) => AnalyzeTitle(title).Core;

    /// <summary>
    /// 解析标题，返回核心标题、装饰分类与装饰文本。
    /// 括号支持 ()（）[]［］【】{}｛｝（全角先折叠为半角）；连字符尾缀支持 - – —。
    /// </summary>
    internal static TitleAnalysis AnalyzeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new TitleAnalysis(string.Empty, TrackVersionKind.None, null);

        var folded = FoldWidth(title);
        var builder = new System.Text.StringBuilder(folded.Length);
        var inner = new System.Text.StringBuilder();
        var decorations = new List<string>();
        var depth = 0;

        foreach (var ch in folded)
        {
            if (ch is '(' or '[' or '{')
            {
                depth++;
                inner.Clear();
                continue;
            }
            if (ch is ')' or ']' or '}')
            {
                if (depth > 0)
                    depth--;
                if (inner.Length > 0)
                    decorations.Add(inner.ToString().Trim());
                inner.Clear();
                continue;
            }
            if (depth > 0)
            {
                inner.Append(ch);
                continue;
            }
            if (char.IsWhiteSpace(ch) || ch == '\u00A0')
                continue;
            builder.Append(ch);
        }

        var core = builder.ToString();
        var dashIndex = core.LastIndexOfAny(DashSeparators);
        if (dashIndex > 0)
        {
            var suffix = core[(dashIndex + 1)..];
            if (IsKnownDecoration(suffix))
            {
                decorations.Add(suffix);
                core = core[..dashIndex];
            }
        }

        var kind = TrackVersionKind.None;
        var primaryDecoration = (string?)null;
        foreach (var decoration in decorations)
        {
            var current = ClassifyDecoration(decoration);
            if (current == TrackVersionKind.None)
                continue;
            if (current == TrackVersionKind.RejectedVersion)
            {
                kind = TrackVersionKind.RejectedVersion;
                primaryDecoration = decoration;
                break;
            }
            if (kind != TrackVersionKind.RejectedVersion)
            {
                kind = TrackVersionKind.AmbiguousDecoration;
                primaryDecoration ??= decoration;
            }
        }

        return new TitleAnalysis(core, kind, primaryDecoration is null ? null : FoldWidth(primaryDecoration).Trim());
    }

    private static TrackVersionKind ClassifyDecoration(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TrackVersionKind.None;
        var normalized = FoldWidth(text).ToLowerInvariant();
        if (RejectedDecorationWords.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
            return TrackVersionKind.RejectedVersion;
        // Ambiguous 词与「未识别括号说明」同档：都交人工确认。
        if (AmbiguousDecorationWords.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
            return TrackVersionKind.AmbiguousDecoration;
        return TrackVersionKind.AmbiguousDecoration;
    }

    private static bool IsKnownDecoration(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var normalized = FoldWidth(text).ToLowerInvariant();
        return RejectedDecorationWords.Any(word => normalized.Contains(word, StringComparison.Ordinal))
               || AmbiguousDecorationWords.Any(word => normalized.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>全角 ASCII / 全角空格折叠为半角，避免「（Ｌｉｖｅ）」绕过词表。</summary>
    private static string FoldWidth(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\u3000')
                chars[i] = ' ';
            else if (chars[i] is >= '\uFF01' and <= '\uFF5E')
                chars[i] = (char)(chars[i] - 0xFEE0);
        }
        return new string(chars);
    }
}
