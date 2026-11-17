// 职责：音源抓歌条目的「自动评分」计算器。
// 评分时机：导入流水线转码完成后（此时 codec/码率/歌词置信度/封面均已确定）。
//
// V2.0 Phase 2 评分模型（满分 100，裁决③）：
//   身份维度     40：Matched=40 / Ambiguous=20 / Rejected=0 / 未知(无期望身份)=40
//   音质等级     40：无损=40；有损 = min(实测码率, 320) / 320 × 40；码率未知=20；<256kbps=0
//   元数据       15：标题5 + 艺人5 + 专辑3 + 流派2
//   歌词          5：LyricsConfidence × 5；无匹配 = 0
// 自动发布阈值 90（裁决③）：身份 Matched + 无损 + 元数据齐全 + 歌词齐 = 100；
//   身份 Ambiguous + 无损 = 80 < 90 → 转人工审核；
//   身份 Rejected + 无损 = 60 < 90 → 转人工审核。
//
// 2026-11-12 变更（Phase 2 评分重构）：
//   - 三维（音质40/元数据34+6/歌词20）→ 四维（身份40/音质40/元数据15/歌词5）；
//   - 阈值 75 → 90；封面加分移除（元数据从 40 降到 15，封面不再纳入评分）；
//   - 身份维度对接 TrackIdentityMatcher 三态，由调用方传入 IdentityDecision；
//   - LowQualityAutoScoreCeiling 语义从「低码率上限」改为「身份 Rejected 上限」（恰好仍为 60）。

using System.Text.Json;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;

namespace ShineMusic.Service.SourceDownload;

/// <summary>单个维度的评分明细。</summary>
/// <param name="Score">该维度得分。</param>
/// <param name="MaxScore">该维度满分。</param>
/// <param name="Reason">得分/扣分原因说明（人工可读）。</param>
public sealed record SourceScoreDimension(int Score, int MaxScore, string Reason);

/// <summary>评分结果：总分 + 四个维度的分项明细。</summary>
public sealed record SourceScoreResult(
    int Total,
    SourceScoreDimension Identity,
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
        identity = Identity,
        quality = Quality,
        metadata = Metadata,
        lyrics = Lyrics
    }, BreakdownJsonOptions);
}

/// <summary>音源条目自动评分器（无状态静态实现）。</summary>
public static class SourceScoreCalculator
{
    /// <summary>绝对音质基准：320kbps 有损视为满分。</summary>
    private const int ReferenceBitrateKbps = 320;

    /// <summary>
    /// 自动发布的最低音质门槛。低于此值的有损文件音质维度记 0 分。
    /// </summary>
    private const int HighQualityFloorKbps = 256;

    /// <summary>
    /// **默认权重**下身份 Rejected（或低质量）条目的理论总分上限 = 40 + 15 + 5 = 60。
    /// 保留给历史测试与文档引用；权重配置化后，生产启动校验改用
    /// <see cref="ScoreWeightsOptions.LowQualityCeiling"/> 按当前配置动态推导，
    /// 避免「改了配置权重、常量没跟着改」导致防线静默失效。
    /// </summary>
    public const int LowQualityAutoScoreCeiling = 60;

    /// <summary>无损编码白名单（以 ffprobe 实测 codec/container 为准，不信任音源平台声明）。</summary>
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "alac", "wavpack", "ape", "tta"
    };

    private static readonly HashSet<string> LosslessContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "wav", "aiff", "aif", "wv", "ape"
    };

    /// <summary>
    /// 对转码完成的导入条目计算自动评分（0-100）。
    /// 无身份判定参数时身份维度默认 Matched=40（向后兼容：管理员手动上传无期望身份）。
    /// </summary>
    public static SourceScoreResult Calculate(LibraryImportItem item)
        => Calculate(item, identityDecision: null, weights: null);

    /// <summary>
    /// 对转码完成的导入条目计算自动评分（0-100），含身份维度，权重取默认配置。
    /// 调用方应先反查期望身份（ExternalDownloadTask 快照或 SongRequestRecord），
    /// 调 <see cref="TrackIdentityMatcher.Evaluate"/> 取得 <paramref name="identityDecision"/> 后传入。
    /// </summary>
    /// <param name="item">转码完成的导入条目。</param>
    /// <param name="identityDecision">身份判定结论；null 视为 Matched（无期望身份的自评场景）。</param>
    public static SourceScoreResult Calculate(LibraryImportItem item, IdentityDecision? identityDecision)
        => Calculate(item, identityDecision, weights: null);

    /// <summary>
    /// 生产入口：按 <c>SourceDownload:ScoreWeights</c> 的四项配置分别给分，合计恒为 100。
    /// 各维度内部仍按既有基准比例折算（身份 40 制 / 音质 40 制 / 元数据 15 制 / 歌词 5 制），
    /// 因此默认权重下逐分与配置化之前完全一致。
    /// </summary>
    /// <param name="item">转码完成的导入条目。</param>
    /// <param name="identityDecision">身份判定结论；null 视为 Matched。</param>
    /// <param name="weights">评分权重；null 时使用代码默认值（离线测试与历史调用兼容）。</param>
    public static SourceScoreResult Calculate(
        LibraryImportItem item, IdentityDecision? identityDecision, ScoreWeightsOptions? weights)
    {
        ArgumentNullException.ThrowIfNull(item);
        var effective = weights ?? new ScoreWeightsOptions();
        var identity = ScoreIdentity(identityDecision, effective.Identity);
        var quality = ScoreQuality(item, effective.Quality);
        var metadata = ScoreMetadata(item, effective.Metadata);
        var lyrics = ScoreLyrics(item, effective.Lyrics);
        return new SourceScoreResult(
            identity.Score + quality.Score + metadata.Score + lyrics.Score,
            identity, quality, metadata, lyrics);
    }

    /// <summary>把基准满分制下的分数按配置满分缩放；默认权重时零开销原样返回。</summary>
    private static int Scale(int raw, int baseMax, int max) =>
        baseMax == max
            ? raw
            : (int)Math.Round((double)raw * max / Math.Max(1, baseMax), MidpointRounding.AwayFromZero);

    /// <summary>身份维度：Matched=满分 / Ambiguous=半分 / Rejected=0 / 未知=满分。</summary>
    private static SourceScoreDimension ScoreIdentity(IdentityDecision? decision, int max)
    {
        if (decision is null)
            return new SourceScoreDimension(max, max, "no expected identity (self-evaluation: matched)");
        return decision.Verdict switch
        {
            IdentityVerdict.Matched => new SourceScoreDimension(max, max, $"identity matched ({decision.ReasonCode})"),
            IdentityVerdict.Ambiguous => new SourceScoreDimension(Scale(20, 40, max), max, $"identity ambiguous ({decision.ReasonCode}): {decision.Detail}"),
            IdentityVerdict.Rejected => new SourceScoreDimension(0, max, $"identity rejected ({decision.ReasonCode}): {decision.Detail}"),
            _ => new SourceScoreDimension(max, max, "unknown verdict, defaulting to matched"),
        };
    }

    /// <summary>音质维度：无损直接满分；有损按绝对码率折算；低于高质量门槛记 0。</summary>
    private static SourceScoreDimension ScoreQuality(LibraryImportItem item, int max)
    {
        if (IsLossless(item))
            return new SourceScoreDimension(max, max, $"lossless codec ({item.Codec ?? item.Container})");

        var kbps = item.BitrateKbps;
        if (kbps is not > 0)
            return new SourceScoreDimension(Scale(20, 40, max), max, "bitrate unknown");

        if (kbps.Value < HighQualityFloorKbps)
            return new SourceScoreDimension(0, max,
                $"{kbps.Value}kbps < {HighQualityFloorKbps}kbps 高质量门槛：不参与自动发布（可人工审核上架）");

        var effective = Math.Min(kbps.Value, ReferenceBitrateKbps);
        var baseScore = (int)Math.Round(40.0 * effective / ReferenceBitrateKbps, MidpointRounding.AwayFromZero);
        return new SourceScoreDimension(Scale(baseScore, 40, max), max,
            $"{effective}kbps / {ReferenceBitrateKbps}kbps（绝对音质基准）");
    }

    /// <summary>
    /// 元数据维度：标题5 + 艺人5 + 专辑3 + 流派2（基准 15 制，按配置满分等比缩放）。
    /// 封面不再纳入评分（Phase 2 重构：封面是加分项而非必填，不应影响自动发布决策）。
    /// </summary>
    private static SourceScoreDimension ScoreMetadata(LibraryImportItem item, int max)
    {
        var score = 0;
        var missing = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Title) && !item.Title.Contains('\uFFFD')) score += 5; else missing.Add("标题缺失或无效(-5)");
        if (DeserializeList(item.ArtistsJson).Count > 0) score += 5; else missing.Add("艺人(-5)");
        if (!string.IsNullOrWhiteSpace(item.AlbumTitle)) score += 3; else missing.Add("专辑(-3)");
        if (DeserializeList(item.GenresJson).Count > 0) score += 2; else missing.Add("流派(-2)");

        var reason = missing.Count > 0
            ? $"缺失: {string.Join(", ", missing)}"
            : "必填齐全";

        return new SourceScoreDimension(Scale(score, 15, max), max, reason);
    }

    /// <summary>歌词匹配度：按匹配置信度线性折算到配置满分。</summary>
    private static SourceScoreDimension ScoreLyrics(LibraryImportItem item, int max)
    {
        if (item.LyricsConfidence is not > 0 || !double.IsFinite(item.LyricsConfidence.Value))
            return new SourceScoreDimension(0, max, "no valid lyrics confidence");
        var confidence = Math.Clamp(item.LyricsConfidence.Value, 0, 1);
        var score = (int)Math.Round(confidence * max, MidpointRounding.AwayFromZero);
        return new SourceScoreDimension(score, max, $"confidence {confidence:0.##} × {max}");
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
            return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value) && !value.Contains('\uFFFD')).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
