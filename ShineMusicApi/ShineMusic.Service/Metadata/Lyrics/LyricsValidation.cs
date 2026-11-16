using System.Globalization;
using System.Text.RegularExpressions;
using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Service.Lyrics;

/// <summary>Hard identity and timeline gates. Scores only rank candidates passing these gates.</summary>
public static class LyricsValidation
{
    public static bool ValidateIdentity(LyricsQuery query, string source, string? id, LyricsCandidateInfo candidate)
    {
        if (string.IsNullOrWhiteSpace(query.Title) || !query.Artists.Any(a => !string.IsNullOrWhiteSpace(a)) ||
            string.IsNullOrWhiteSpace(candidate.Title) || string.IsNullOrWhiteSpace(candidate.Artist))
            return Reject(query, source, id, "identity_unconfirmed");
        if (!double.IsFinite(candidate.DurationSeconds) || candidate.DurationSeconds <= 0)
            return Reject(query, source, id, "duration_unconfirmed");
        if (query.DurationMs is > 0 && Math.Abs(query.DurationMs.Value / 1000d - candidate.DurationSeconds) > 5)
            return Reject(query, source, id, "duration_mismatch");
        var artists = new[] { candidate.Artist }.Concat(candidate.Artist.Split(['/', '&', ',', '、', '，', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var decision = TrackIdentityMatcher.Evaluate(
            new TrackIdentity(query.Title, query.Artists, query.DurationMs),
            new TrackIdentity(candidate.Title, artists, (long)(candidate.DurationSeconds * 1000)));
        return decision.Verdict == IdentityVerdict.Matched || Reject(query, source, id, decision.ReasonCode);
    }

    public static bool Reject(LyricsQuery query, string source, string? id, string reason)
    {
        query.Diagnostics.Add(new(source, id, reason));
        return false;
    }

    public static bool ValidateContent(LyricsQuery query, string source, string? id, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Reject(query, source, id, "no_lyrics");
        if (content.Length > 20000) return Reject(query, source, id, "content_too_large");
        var text = Regex.Replace(content, @"\[[^\]\r\n]*\]", "").Trim();
        if (text.Length == 0) return Reject(query, source, id, "no_lyrics");
        if (Regex.IsMatch(text, @"^(纯音乐[，,]?\s*请欣赏|纯音乐|此歌曲为没有填词的纯音乐|暂无歌词|暂时没有歌词|无歌词|no lyrics|instrumental)[。.!\s]*$", RegexOptions.IgnoreCase))
            return Reject(query, source, id, "instrumental_or_no_lyrics");
        double offset = 0;
        var stamps = new List<double>();
        foreach (Match tag in Regex.Matches(content, @"\[([^\]\r\n]*)\]"))
        {
            var value = tag.Groups[1].Value;
            if (value.StartsWith("offset", StringComparison.OrdinalIgnoreCase))
            {
                if (!Regex.IsMatch(value, @"^offset:[+-]?\d+$", RegexOptions.IgnoreCase) ||
                    !double.TryParse(value[7..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset) || !double.IsFinite(offset))
                    return Reject(query, source, id, "invalid_timestamp");
            }
            else if (Regex.IsMatch(value, @"^[+\-\d]") || (value.Contains(':') && !Regex.IsMatch(value, @"^(ar|ti|al|by|re|ve|length|au|la|tool):", RegexOptions.IgnoreCase)))
            {
                var stamp = Regex.Match(value, @"^(\d+):([0-5]\d)(?:[.:](\d{1,3}))?$");
                if (!stamp.Success || !double.TryParse(stamp.Groups[1].Value, out var minutes) || !double.IsFinite(minutes))
                    return Reject(query, source, id, "invalid_timestamp");
                var seconds = double.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = stamp.Groups[3].Success ? double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                stamps.Add(minutes * 60 + seconds + fraction);
            }
        }
        // Detect unclosed numeric/offset tags as well as complete malformed tags.
        if (Regex.IsMatch(content, @"\[(?:[+\-\d]|offset)[^\]\r\n]*(?:\r?\n|$)", RegexOptions.IgnoreCase))
            return Reject(query, source, id, "invalid_timestamp");
        if (stamps.Any(s => !double.IsFinite(s + offset / 1000) || s + offset / 1000 < 0 ||
            (query.DurationMs is > 0 && s + offset / 1000 > query.DurationMs.Value / 1000d + 5)))
            return Reject(query, source, id, "timeline_out_of_bounds");
        if (stamps.Count == 0) query.Diagnostics.Add(new(source, id, "plain_text"));
        return true;
    }
}
