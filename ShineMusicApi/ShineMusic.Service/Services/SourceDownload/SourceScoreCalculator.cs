// 职责：音源抓歌条目的「自动评分」计算器。
// 评分时机：导入流水线转码完成后（此时 codec/码率/歌词置信度/封面均已确定）。
// 评分模型（满分 100，权重与阈值见计划文档 source-download-service-plan.md）：
//   音质等级     50：无损 codec（flac/wav/aiff 系）=50；≥320k=40；256-319k=32；192-255k=24；<192k=10
//   元数据完整度 30：标题 6 + 艺人 6 + 专辑 4 + 发行年 3 + 流派可映射中文 mood 5 + 封面 6
//   歌词匹配度   20：LyricsConfidence × 20；无匹配 = 0
// 输出总分 + 分项明细 JSON（写 LibraryImportItem.AutoScore / AutoScoreBreakdownJson，
// 供人工审核页展示扣分原因；≥ SourceDownload:AutoPublishThreshold 时直通发布）。

using System.Text.Json;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;

namespace ShineMusic.Service.SourceDownload;

/// <summary>单个维度的评分明细。</summary>
/// <param name="Score">该维度得分。</param>
/// <param name="MaxScore">该维度满分。</param>
/// <param name="Reason">得分/扣分原因说明（人工可读）。</param>
public sealed record SourceScoreDimension(int Score, int MaxScore, string Reason);

/// <summary>评分结果：总分 + 三个维度的分项明细。</summary>
public sealed record SourceScoreResult(
    int Total,
    SourceScoreDimension Quality,
    SourceScoreDimension Metadata,
    SourceScoreDimension Lyrics)
{
    /// <summary>序列化为分项明细 JSON（存 AutoScoreBreakdownJson）。</summary>
    public string ToBreakdownJson() => JsonSerializer.Serialize(new
    {
        total = Total,
        quality = Quality,
        metadata = Metadata,
        lyrics = Lyrics
    });
}

/// <summary>音源条目自动评分器（无状态静态实现）。</summary>
public static class SourceScoreCalculator
{
    /// <summary>无损编码白名单（以 ffprobe 实测 codec/container 为准，不信任音源平台声明）。</summary>
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "alac", "wavpack", "ape", "tta"
    };

    private static readonly HashSet<string> LosslessContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "wav", "aiff", "aif", "wv", "ape"
    };

    /// <summary>对转码完成的导入条目计算自动评分（0-100）。</summary>
    public static SourceScoreResult Calculate(LibraryImportItem item)
    {
        var quality = ScoreQuality(item);
        var metadata = ScoreMetadata(item);
        var lyrics = ScoreLyrics(item);
        return new SourceScoreResult(quality.Score + metadata.Score + lyrics.Score, quality, metadata, lyrics);
    }

    /// <summary>音质维度（满分 50）：无损优先，其余按实测码率分档。</summary>
    private static SourceScoreDimension ScoreQuality(LibraryImportItem item)
    {
        if (IsLossless(item))
            return new SourceScoreDimension(50, 50, $"lossless codec ({item.Codec ?? item.Container})");

        var kbps = item.BitrateKbps;
        return kbps switch
        {
            null => new SourceScoreDimension(10, 50, "bitrate unknown"),
            >= 320 => new SourceScoreDimension(40, 50, $"{kbps}kbps ≥ 320k"),
            >= 256 => new SourceScoreDimension(32, 50, $"{kbps}kbps in 256-319k"),
            >= 192 => new SourceScoreDimension(24, 50, $"{kbps}kbps in 192-255k"),
            _ => new SourceScoreDimension(10, 50, $"{kbps}kbps < 192k")
        };
    }

    /// <summary>元数据完整度（满分 30）：六项加权累加，逐项记录扣分原因。</summary>
    private static SourceScoreDimension ScoreMetadata(LibraryImportItem item)
    {
        var score = 0;
        var missing = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Title)) score += 6; else missing.Add("title(-6)");
        if (DeserializeList(item.ArtistsJson).Count > 0) score += 6; else missing.Add("artists(-6)");
        if (!string.IsNullOrWhiteSpace(item.AlbumTitle)) score += 4; else missing.Add("album(-4)");
        if (item.ReleaseYear is > 0) score += 3; else missing.Add("year(-3)");

        // 流派必须能映射为中文 mood 标签才给分（保证「今日灵感」时段匹配可用）
        var genres = DeserializeList(item.GenresJson);
        if (genres.Count > 0 && GenreMoodMapper.MapFirst([.. genres]) is not null) score += 5;
        else missing.Add("genre-mood(-5)");

        if (!string.IsNullOrWhiteSpace(item.ArtworkStorageKey)) score += 6; else missing.Add("artwork(-6)");

        var reason = missing.Count == 0 ? "all fields present" : $"missing: {string.Join(", ", missing)}";
        return new SourceScoreDimension(score, 30, reason);
    }

    /// <summary>歌词匹配度（满分 20）：按匹配置信度线性折算。</summary>
    private static SourceScoreDimension ScoreLyrics(LibraryImportItem item)
    {
        if (item.LyricsConfidence is not > 0)
            return new SourceScoreDimension(0, 20, "no lyrics matched");
        var score = (int)Math.Round(item.LyricsConfidence.Value * 20, MidpointRounding.AwayFromZero);
        return new SourceScoreDimension(score, 20, $"confidence {item.LyricsConfidence.Value:0.##} × 20");
    }

    /// <summary>判定无损：codec 命中白名单，或 codec 是 PCM 系（wav/aiff 容器），或容器命中白名单。</summary>
    private static bool IsLossless(LibraryImportItem item)
    {
        if (item.Codec is { } codec)
        {
            if (LosslessCodecs.Contains(codec)) return true;
            if (codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return item.Container is { } container && LosslessContainers.Contains(container);
    }

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
