// 职责：音源抓歌条目的「自动评分」计算器。
// 评分时机：导入流水线转码完成后（此时 codec/码率/歌词置信度/封面均已确定）。
// 评分模型（满分 100，权重与阈值见计划文档 source-download-service-plan.md）：
//   音质等级     40：按「来源相对」折算 —— 无损=40；有损 = 实测码率 / 该渠道可得上限 × 40
//                     （酷我上限 128k，故 128k 即满分；不因渠道能力差而永久压低分数）；码率未知=20
//   元数据必填   34：标题 10 + 艺人 10 + 专辑 7 + 流派 4 + 时长有效 3
//   加分项        6：封面（抓不到不扣分，只是拿不到该加分）
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
    /// <summary>序列化命名统一为 camelCase，便于前端直接消费（历史 PascalCase 数据仍可解析）。</summary>
    private static readonly JsonSerializerOptions BreakdownJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>序列化为分项明细 JSON（存 AutoScoreBreakdownJson）。</summary>
    public string ToBreakdownJson() => JsonSerializer.Serialize(new
    {
        total = Total,
        quality = Quality,
        metadata = Metadata,
        lyrics = Lyrics
    }, BreakdownJsonOptions);
}

/// <summary>音源条目自动评分器（无状态静态实现）。</summary>
public static class SourceScoreCalculator
{
    /// <summary>各渠道可得的码率上限（kbps）。评分按「相对该上限」折算，避免低码率渠道被整体判死。</summary>
    private static readonly Dictionary<string, int> ProviderBitrateCeilings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kuwo"] = 128
    };

    private const int DefaultBitrateCeiling = 320;

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

    /// <summary>音质维度（满分 40）：无损直接满分；有损按实测码率相对该渠道上限折算。</summary>
    private static SourceScoreDimension ScoreQuality(LibraryImportItem item)
    {
        if (IsLossless(item))
            return new SourceScoreDimension(40, 40, $"lossless codec ({item.Codec ?? item.Container})");

        var kbps = item.BitrateKbps;
        if (kbps is not > 0)
            return new SourceScoreDimension(20, 40, "bitrate unknown");

        var ceiling = ProviderBitrateCeilings.GetValueOrDefault(item.SourceProvider ?? string.Empty, DefaultBitrateCeiling);
        var effective = Math.Min(kbps.Value, ceiling);
        var score = (int)Math.Round(40.0 * effective / ceiling, MidpointRounding.AwayFromZero);
        return new SourceScoreDimension(score, 40, $"{effective}kbps / {ceiling}kbps（来源可得上限）");
    }

    /// <summary>
    /// 元数据维度（满分 40）：必填项 34 分（标题/艺人/专辑/流派/时长），封面 6 分为加分项。
    /// 封面抓不到只损失加分，不计为「缺失扣分」。
    /// </summary>
    private static SourceScoreDimension ScoreMetadata(LibraryImportItem item)
    {
        var score = 0;
        var missing = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Title)) score += 10; else missing.Add("标题(-10)");
        if (DeserializeList(item.ArtistsJson).Count > 0) score += 10; else missing.Add("艺人(-10)");
        if (!string.IsNullOrWhiteSpace(item.AlbumTitle)) score += 7; else missing.Add("专辑(-7)");
        if (DeserializeList(item.GenresJson).Count > 0) score += 4; else missing.Add("流派(-4)");
        if (item.DurationMs is > 0) score += 3; else missing.Add("时长(-3)");

        var hasArtwork = !string.IsNullOrWhiteSpace(item.ArtworkStorageKey);
        if (hasArtwork) score += 6;

        var reason = missing.Count > 0
            ? $"缺失: {string.Join(", ", missing)}"
            : "必填齐全";
        if (!hasArtwork)
            reason += missing.Count > 0 ? "；封面加分未得" : "；封面加分未得(+6)";

        return new SourceScoreDimension(score, 40, reason);
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
