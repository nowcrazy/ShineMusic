// 职责：自动发布（评分直通）的领域规则。
// 音源抓歌与管理员手动导入各有一套开关与阈值，但「达标即直通、否则转人工审核」是同一条规则。
// 后台 sweep 的 SqlSugar 查询是同一策略的 SQL 投影，改动时必须两处同步。

namespace ShineMusic.Model.Domain;

/// <summary>两类导入来源各自的自动发布开关与分数阈值。</summary>
public sealed record AutoPublishThresholds(
    bool SourceEnabled,
    int SourceThreshold,
    bool LibraryEnabled,
    int LibraryThreshold);

/// <summary>自动发布策略：不感知数据库、不感知 HttpContext。</summary>
public static class AutoPublishPolicy
{
    /// <summary>
    /// 判断评分达标且对应来源的开关已开启。<paramref name="hasSourceProvider"/> 为 true 表示音源抓歌条目，
    /// 否则为管理员手动导入条目；null 分数永远不直通。
    /// </summary>
    public static bool ShouldAutoPublish(bool hasSourceProvider, int? score, AutoPublishThresholds thresholds)
    {
        if (score is null) return false;
        return hasSourceProvider
            ? thresholds.SourceEnabled && score.Value >= thresholds.SourceThreshold
            : thresholds.LibraryEnabled && score.Value >= thresholds.LibraryThreshold;
    }
}
