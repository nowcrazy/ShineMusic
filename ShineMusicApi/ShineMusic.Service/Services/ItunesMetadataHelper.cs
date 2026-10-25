// ItunesMetadataHelper.cs
// 职责：把外部源返回的信号归一化为曲库可直接使用的字段——
//   1) Region：地区分类（华语 / 欧美 / 日韩 / 其他），用于推荐算法的“区域亲和度”；
//   2) Explicit：是否含成人内容，用于内容过滤与推荐降权。
//
// 关键纠正（2026-10）：iTunes Search API 返回的 country 是「响应请求的商店国家」，
// 不指定 country 参数时默认走 US 商店，于是所有歌曲都会返回 USA。
// 早期版本用它推导 Region，导致华语歌一律被判成“欧美”。现在改为：
//   流派关键词（Mandopop / J-Pop / K-Pop…）→ 艺人名文字体系（汉字/假名/谚文/拉丁）
//   → 商店代码（仅作最弱兜底，且明确不把 US 商店当作“欧美”的证据）。
// 这套规则仍是启发式：汉字名无法区分中日艺人，真正的国籍需要 MusicBrainz 之类的权威源。

namespace ShineMusic.Service;

/// <summary>
/// iTunes 元数据归一化辅助：地区分类推导；显式标记解析。
/// </summary>
public static class ItunesMetadataHelper
{
    /// <summary>
    /// 地区分类常量：与前端/推荐算法约定的固定取值，避免散落硬编码字符串。
    /// </summary>
    public static class Regions
    {
        /// <summary>华语（普通话/国语及粤语等）。</summary>
        public const string Chinese = "华语";
        /// <summary>欧美（英语及欧洲大陆主流）。</summary>
        public const string Western = "欧美";
        /// <summary>日韩（日本 / 韩国）。</summary>
        public const string Jk = "日韩";
        /// <summary>其他（可识别但未归入上述三类）。</summary>
        public const string Other = "其他";
    }

    // 流派 → 地区：只收录“语言/地域指向明确”的流派，Pop/Rock 这类跨语种流派不参与判定，
    // 否则会把 Mandopop 之外的所有 Pop 都误判成欧美。
    private static readonly (string[] Keywords, string Region)[] GenreRules =
    {
        (["mandopop", "cantopop", "c-pop", "taiwanese pop", "hokkien", "cantonese"], Regions.Chinese),
        (["j-pop", "jpop", "japanese", "anime", "enka", "city pop", "kayokyoku"], Regions.Jk),
        (["k-pop", "kpop", "korean"], Regions.Jk),
    };

    // 商店代码 → 地区：仅作最弱兜底。注意 US 商店是 Search API 的默认值，
    // 它只说明“这首歌能在美国商店搜到”，不说明艺人来自欧美，因此不参与判定。
    private static readonly Dictionary<string, string> CountryToRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        // 华语：中国大陆/港澳台及东南亚主要华语发行地
        ["CHN"] = Regions.Chinese, ["TWN"] = Regions.Chinese, ["HKG"] = Regions.Chinese,
        ["MAC"] = Regions.Chinese, ["SGP"] = Regions.Chinese, ["MYS"] = Regions.Chinese,
        // 日韩
        ["JPN"] = Regions.Jk, ["KOR"] = Regions.Jk,
        // 欧美：欧洲主要市场（US 被排除，理由见上）
        ["GBR"] = Regions.Western, ["IRL"] = Regions.Western,
        ["FRA"] = Regions.Western, ["DEU"] = Regions.Western, ["ITA"] = Regions.Western,
        ["ESP"] = Regions.Western, ["NLD"] = Regions.Western, ["SWE"] = Regions.Western,
        ["DNK"] = Regions.Western, ["NOR"] = Regions.Western, ["CHE"] = Regions.Western,
        ["AUT"] = Regions.Western, ["BEL"] = Regions.Western, ["PRT"] = Regions.Western,
        ["FIN"] = Regions.Western, ["POL"] = Regions.Western,
    };

    /// <summary>
    /// 推导地区分类。判定顺序：流派关键词 → 艺人名文字体系 → 商店代码。
    /// </summary>
    /// <param name="countryCode">上架商店代码（可空）；仅在流派与艺人名都无法判定时兜底。</param>
    /// <param name="genre">外部源返回的流派（如 Mandopop / J-Pop / Pop）。</param>
    /// <param name="artistName">艺人名，用于按文字体系（汉字/假名/谚文/拉丁）判断语种区域。</param>
    /// <returns>华语/欧美/日韩/其他；全部信号都缺失时返回 null（未知，不臆测）。</returns>
    public static string? DeriveRegion(string? countryCode, string? genre = null, string? artistName = null)
    {
        // 1) 流派：语言指向最明确
        if (!string.IsNullOrWhiteSpace(genre))
        {
            var lower = genre.Trim().ToLowerInvariant();
            foreach (var (keywords, region) in GenreRules)
            {
                if (keywords.Any(keyword => lower.Contains(keyword, StringComparison.Ordinal)))
                    return region;
            }
        }

        // 2) 艺人名文字体系：假名/谚文 → 日韩；汉字 → 华语；拉丁字母 → 欧美
        var scriptRegion = DeriveRegionFromScript(artistName);
        if (scriptRegion is not null)
            return scriptRegion;

        // 3) 商店代码（最弱信号）
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            var code = countryCode.Trim();
            if (code.Length > 0 && CountryToRegion.TryGetValue(code, out var region))
                return region;
        }

        return null;
    }

    /// <summary>
    /// 按艺人名的书写体系推断地区：假名或谚文 → 日韩；汉字 → 华语；纯拉丁字母 → 欧美。
    /// 混合书写（如「周杰倫 Jay Chou」）按“先出现的决定性字符”判定，汉字优先。
    /// </summary>
    private static string? DeriveRegionFromScript(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return null;

        var hasHan = false;
        var hasLatin = false;
        foreach (var ch in artistName)
        {
            // 平假名 / 片假名
            if (ch is >= '\u3040' and <= '\u30FF') return Regions.Jk;
            // 谚文
            if (ch is >= '\uAC00' and <= '\uD7AF') return Regions.Jk;
            // CJK 统一汉字
            if (ch is >= '\u4E00' and <= '\u9FFF') hasHan = true;
            // 拉丁字母
            else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z') hasLatin = true;
        }

        if (hasHan) return Regions.Chinese;
        if (hasLatin) return Regions.Western;
        return null;
    }

    /// <summary>
    /// 由 iTunes trackExplicitness 解析是否为成人内容。
    /// </summary>
    /// <param name="explicitness">explicit / cleaned / notExplicit。</param>
    /// <returns>explicit→true；cleaned/notExplicit→false；空或未知值→null。</returns>
    public static bool? ParseExplicit(string? explicitness)
    {
        if (string.IsNullOrWhiteSpace(explicitness)) return null;
        return explicitness.Trim().ToLowerInvariant() switch
        {
            "explicit" => true,
            "cleaned" or "notexplicit" => false,
            _ => null
        };
    }
}
