// GenreMoodMapper.cs
// 职责：把 iTunes 返回的英文流派（或其他来源的任意流派字符串）
// 归一化到应用内统一的中文 mood 标签（轻快 / 松弛 / 温柔 / 安静 / 活力）。
//
// 这个映射层是前端「今日灵感」卡片时段 mood 匹配的基础 —— 如果某首歌
// 的流派能正确映射到 mood，前端就能按时段氛围精准挑歌。

namespace ShineMusic.Service;

/// <summary>
/// 流派 → 应用内 mood 标签的归一化映射器。
/// 输入：任意流派字符串（英文 iTunes primaryGenreName / 中文 / 其他）
/// 输出：["轻快" | "松弛" | "温柔" | "安静" | "活力"] 或 原字符串
/// </summary>
public static class GenreMoodMapper
{
    // ↓ 中文 mood 标签常量，与前端 index.vue 的 mood 期望值保持一致
    public const string MoodLively   = "轻快";
    public const string MoodRelaxed  = "松弛";
    public const string MoodGentle   = "温柔";
    public const string MoodQuiet    = "安静";
    public const string MoodEnergetic = "活力";

    /// <summary>
    /// 按"关键字→mood"分组的映射表。
    /// 匹配规则：流派字符串包含某个关键字（忽略大小写）即归入对应 mood。
    /// 同一个流派可能命中多个关键字 —— 取先匹配到的（下表顺序即为优先级）。
    /// </summary>
    private static readonly (string[] Keywords, string Mood)[] Rules =
    {
        // —— 安静（深夜氛围，优先级最高：Ambient/New Age 不应该被误判为电子） ——
        (["ambient", "new age", "sleep", "meditation", "spiritual", "atmosphere", "bgm"], MoodQuiet),
        // —— 活力 ——
        (["rock", "metal", "punk", "hard", "heavy", "thrash", "death metal", "hip-hop", "rap",
          "trap", "drum and bass", "dnb", "breakbeat", "dubstep"], MoodEnergetic),
        // —— 温柔 ——
        (["ballad", "slow jam", "classical", "opera", "symphony", "concerto", "medieval",
          "baroque", "romantic", "chamber", "orchestral"], MoodGentle),
        // —— 松弛 ——
        (["jazz", "blues", "r&b", "soul", "folk", "acoustic", "easy listening",
          "lounge", "chill", "downtempo", "trip hop"], MoodRelaxed),
        // —— 轻快 ——
        (["pop", "dance", "electronic", "electropop", "house", "techno", "trance",
          "k-pop", "kpop", "c-pop", "j-pop", "jpop", "mandopop", "cantopop",
          "indie pop", "synth pop", "disco", "funk", "reggae", "ska"], MoodLively),
    };

    /// <summary>
    /// 从一个流派字符串归一化到 mood 标签。
    /// </summary>
    /// <param name="genre">原始流派字符串（可空）</param>
    /// <returns>匹配到的 mood 标签；完全没匹配时返回 null（由调用方决定 fallback 策略）</returns>
    public static string? Map(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return null;
        var lower = genre.ToLowerInvariant();

        foreach (var (keywords, mood) in Rules)
        {
            foreach (var kw in keywords)
            {
                if (lower.Contains(kw, StringComparison.Ordinal))
                    return mood;
            }
        }

        return null;
    }

    /// <summary>
    /// 从多个流派字符串中挑选一个最能代表整体风格的 mood。
    /// 策略：遍历每个流派都跑 Map，取第一个非 null 结果。
    /// 这样如果一个曲目同时有 "Pop" 和 "Rock"，会先匹配到 Pop → 轻快；
    /// 但如果流派列表里 "Rock" 排在前面，就会匹配到活力。
    /// </summary>
    public static string? MapFirst(params string[] genres)
    {
        foreach (var g in genres)
        {
            var mood = Map(g);
            if (mood is not null) return mood;
        }
        return null;
    }

    /// <summary>
    /// 批量把流派字符串列表转成 mood 列表（去重，保持出现顺序）。
    /// 用于 Track.Genres 回填 —— 可以同时存原流派和映射后的 mood。
    /// </summary>
    public static IEnumerable<string> MapAll(params string[] genres)
    {
        var seen = new HashSet<string>();
        foreach (var g in genres)
        {
            var mood = Map(g);
            if (mood is not null && seen.Add(mood))
                yield return mood;
        }
    }
}
