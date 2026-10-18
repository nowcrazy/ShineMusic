// 职责：校验「待上架曲目」与「点歌需求」是否为同一首歌（标题 + 艺人），
// 用于堵住“张杰-天下 被错配成 你是最美的太阳”这类张冠李戴。
// 采用归一化比较 + Levenshtein 编辑距离容忍微小差异（错别字/空白），
// 结果分为 Match / FuzzyMatch / Mismatch 三档，交由调用方按全局配置决定放行或阻断。

using ShineMusic.Api.Data;
using ShineMusic.Api.Options;

namespace ShineMusic.Api.Services;

/// <summary>匹配结论档位。</summary>
public enum MatchVerdictKind
{
    /// <summary>标题与艺人均完全一致。</summary>
    Match,
    /// <summary>存在微小差异（编辑距离在阈值内），可放行但建议留痕。</summary>
    FuzzyMatch,
    /// <summary>明显不一致，属于错配。</summary>
    Mismatch
}

/// <summary>匹配校验结果。</summary>
/// <param name="Kind">结论档位。</param>
/// <param name="TrackDisplay">待上架曲目的展示串（标题 - 艺人）。</param>
/// <param name="RequestDisplay">点歌需求的展示串（标题 - 艺人）。</param>
/// <param name="TitleDistance">标题编辑距离；完全一致为 0。</param>
/// <param name="ArtistDistance">艺人编辑距离；完全一致为 0，无艺人信息为 -1。</param>
public sealed record MatchVerdict(MatchVerdictKind Kind, string TrackDisplay, string RequestDisplay, int TitleDistance, int ArtistDistance);

/// <summary>点歌需求与曲目的匹配校验器。</summary>
public static class SongRequestMatchValidator
{
    /// <summary>
    /// 校验待上架曲目是否与点歌需求一致。
    /// </summary>
    /// <param name="trackTitle">待上架曲目标题（可能为空）。</param>
    /// <param name="trackArtists">待上架曲目艺人列表。</param>
    /// <param name="request">点歌需求记录。</param>
    /// <param name="options">匹配参数（最大编辑距离、是否归一化）。</param>
    public static MatchVerdict Validate(
        string? trackTitle,
        IEnumerable<string> trackArtists,
        SongRequestRecord request,
        TitleArtistMatchOptions options)
    {
        var aggressive = options.Normalize;
        var maxDistance = Math.Max(0, options.MaxEditDistance);

        var normalizedTrackTitle = Normalize(trackTitle, aggressive);
        var normalizedRequestTitle = Normalize(request.Title, aggressive);
        var titleResult = Compare(normalizedTrackTitle, normalizedRequestTitle, maxDistance);

        var artistResult = CompareArtists(trackArtists, request.ArtistName, aggressive, maxDistance);

        var kind = titleResult.Kind == MatchVerdictKind.Mismatch || artistResult.Kind == MatchVerdictKind.Mismatch
            ? MatchVerdictKind.Mismatch
            : titleResult.Kind == MatchVerdictKind.FuzzyMatch || artistResult.Kind == MatchVerdictKind.FuzzyMatch
                ? MatchVerdictKind.FuzzyMatch
                : MatchVerdictKind.Match;

        var artists = trackArtists.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        var trackDisplay = string.IsNullOrWhiteSpace(trackTitle)
            ? "(未填写标题)"
            : artists.Length == 0
                ? trackTitle!.Trim()
                : $"{trackTitle.Trim()} - {string.Join("/", artists)}";

        return new MatchVerdict(
            kind,
            trackDisplay,
            $"{request.Title} - {request.ArtistName}",
            titleResult.Distance,
            artistResult.Distance);
    }

    /// <summary>比较两个已归一化的字符串：相等→Match，编辑距离≤阈值→FuzzyMatch，否则→Mismatch。</summary>
    private static (MatchVerdictKind Kind, int Distance) Compare(string left, string right, int maxDistance)
    {
        if (left.Length == 0 || right.Length == 0)
            return (MatchVerdictKind.Mismatch, -1);
        if (string.Equals(left, right, StringComparison.Ordinal))
            return (MatchVerdictKind.Match, 0);
        var distance = Levenshtein(left, right);
        return (distance <= maxDistance ? MatchVerdictKind.FuzzyMatch : MatchVerdictKind.Mismatch, distance);
    }

    /// <summary>在多个艺人中取与需求艺人最接近的一个进行比较（任一匹配即视为一致）。</summary>
    private static (MatchVerdictKind Kind, int Distance) CompareArtists(
        IEnumerable<string> trackArtists,
        string requestArtist,
        bool aggressive,
        int maxDistance)
    {
        var target = Normalize(requestArtist, aggressive);
        if (target.Length == 0)
            return (MatchVerdictKind.Mismatch, -1);

        var best = int.MaxValue;
        foreach (var artist in trackArtists)
        {
            var normalized = Normalize(artist, aggressive);
            if (normalized.Length == 0)
                continue;
            if (string.Equals(normalized, target, StringComparison.Ordinal))
                return (MatchVerdictKind.Match, 0);
            var distance = Levenshtein(normalized, target);
            if (distance < best)
                best = distance;
        }

        if (best == int.MaxValue)
            return (MatchVerdictKind.Mismatch, -1);
        return (best <= maxDistance ? MatchVerdictKind.FuzzyMatch : MatchVerdictKind.Mismatch, best);
    }

    /// <summary>归一化：去首尾空白并转大写；aggressive 时再移除内部空白，容忍“张 杰 / 张杰”这类差异。</summary>
    private static string Normalize(string? value, bool aggressive)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim().ToUpperInvariant();
        if (!aggressive)
            return trimmed;
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (!char.IsWhiteSpace(ch))
                builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>计算 Levenshtein 编辑距离（滚动数组，空间 O(min(n,m))）。</summary>
    private static int Levenshtein(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        // 以较短串作为列，降低内存占用。
        if (left.Length > right.Length)
            (left, right) = (right, left);

        var previous = new int[left.Length + 1];
        var current = new int[left.Length + 1];
        for (var i = 0; i <= left.Length; i++)
            previous[i] = i;

        for (var j = 1; j <= right.Length; j++)
        {
            current[0] = j;
            for (var i = 1; i <= left.Length; i++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[i] = Math.Min(
                    Math.Min(current[i - 1] + 1, previous[i] + 1),
                    previous[i - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[left.Length];
    }
}
