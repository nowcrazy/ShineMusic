namespace ShineMusic.Service.SourceProviders.Abstractions;

/// <summary>当前环境真实巡检证据，不能外推到所有歌曲、登录态或网络环境。</summary>
public sealed record ProviderVerification(string Scope, string Metadata, string Search, string Download);

/// <summary>匿名实测结果覆盖历史能力描述；未知能力保持 Unknown，不假定不支持。</summary>
public static class VerifiedProviderCapabilities
{
    /// <summary>2026-11-16巡检：平台Cookie均未配置，Jamendo有client_id，yt-dlp有cookies文件且无显式代理。</summary>
    public const string Scope = "2026-11-16；当前网络；平台匿名态；中文三曲与开放曲库样本；非全曲库保证。";

    /// <summary>安全、无凭据的证据摘要。</summary>
    public static ProviderVerification? Evidence(string provider) => provider.ToLowerInvariant() switch
    {
        "migu" => new(Scope, "三曲详情返回；封面在详情中补齐，发行年份缺失。", "三曲有结果，录音室与Live须区分。", "晴天269.75s/935kbps FLAC、富士山下267.17s/767kbps FLAC；月亮代表我的心各档失败。"),
        "netease" => new(Scope, "已知ID晴天186016、富士山下65766均返回正确标题与艺人。", "三查询有结果，但前列未匹配指定原唱；不能把返回条目当精确命中。", "已知ID富士山下258.90s/1568kbps FLAC；晴天受限；无关候选曾返回128K，下载后须校验。"),
        "kuwo" => new(Scope, "三曲详情返回标题、艺人、专辑、时长、封面。", "三曲均精确命中。", "三曲均未返回可下载资源；不推断登录态能力。"),
        "qq" => new(Scope, "兼容name与嵌套album后，三曲真实详情均返回。", "三曲均精确命中。", "匿名vkey无地址；补测真实media_mid、FLAC/320K/128K及GET/POST三曲18项均104003，下载保持关闭。"),
        "kugou" => new(Scope, "修复搜索请求后两项详情返回fileName、时长；第三查询为空；详情无专辑封面。", "Android UA+Referer恢复结果；富士山下匹配、晴天候选为不同版本，第三查询为空。", "取得134.05秒128K MP3（非目标版本）；另一曲权限受限；高质量下载未验证，禁用自动下载。"),
        "qishui" => new(Scope, "未取得有效公开ID独立验证，保持未知。", "三次HTTP成功但响应为空，当前搜索不可用。", "未取得有效ID，未验证完整下载。"),
        "qianqian" => new(Scope, "候选详情返回标题、艺人、专辑、时长和封面。", "三查询有结果，但候选为周深、左兆丽等其他歌曲/演唱者。", "一候选未得到有效音频、其余两项VIP；未验证完整高质量下载。"),
        "audius" => new(Scope, "详情可返回标题、作者、时长、封面、流派、许可；专辑可缺失。", "英文自身曲库可搜索，中文原唱目标未匹配。", "Must B Love320.24s、Piano Fala76.59s，均320K MP3；中文候选节点拒绝；未验证无损原文件。"),
        "jamendo" => new(Scope, "自身曲库详情含标题、艺人、专辑、时长、封面、许可、年份；流派样本为空。", "love/piano有结果，中文晴天无结果。", "Tu amor me hace grande230.95s/611kbps FLAC、Etude Op.28 112.40s/494kbps FLAC。"),
        "bilibili" => new(Scope, "三样本两次返回视频详情；作者是UP主，不是歌曲演唱者保证。", "三查询均有视频结果，不能直接视为录音室同曲。", "yt-dlp实测HTTP412，当前未取得音频。"),
        "ytmusic" => new(Scope, "三次单曲详情均超30秒预算。", "三查询返回视频条目，flat搜索艺人缺失。", "取得269.72秒WAV；这是有损音轨提取PCM，1411kbps不证明上游无损；不启用高质量下载。"),
        _ => null
    };

    /// <summary>覆盖已实现能力的当前验证状态，不凭空新增接口能力。</summary>
    public static IReadOnlyList<ProviderCapabilityDescriptor> Apply(
        string provider, IReadOnlyList<ProviderCapabilityDescriptor> declared)
    {
        var evidence = Evidence(provider);
        if (evidence is null) return declared;
        return declared.Select(item =>
        {
            var status = VerifiedState(provider.ToLowerInvariant(), item.Capability);
            if (status is null) return item;
            var note = item.Capability switch
            {
                ProviderCapability.Search => evidence.Search,
                ProviderCapability.Metadata => evidence.Metadata,
                _ => evidence.Download
            };
            return item with { Status = status.Value, Note = Scope + " " + note };
        }).ToArray();
    }

    private static ProviderStatus? VerifiedState(string provider, ProviderCapability capability)
    {
        if (capability == ProviderCapability.Search)
            return provider == "qishui" ? ProviderStatus.Unavailable
                : provider is "kuwo" or "qq" ? ProviderStatus.Available : ProviderStatus.Degraded;
        if (capability == ProviderCapability.Metadata)
            return provider == "qishui" ? ProviderStatus.Unknown
                : provider == "ytmusic" ? ProviderStatus.Unavailable
                : provider == "kuwo" ? ProviderStatus.Available : ProviderStatus.Degraded;
        if (capability is ProviderCapability.Resolve or ProviderCapability.Download320 or ProviderCapability.DownloadLossless)
        {
            if (provider == "qishui") return ProviderStatus.Unknown;
            if (provider is "qq" or "kuwo" or "qianqian" or "bilibili") return ProviderStatus.Unavailable;
            if (capability == ProviderCapability.DownloadLossless && provider == "audius") return ProviderStatus.Unknown;
            if (capability == ProviderCapability.Download320 && provider == "jamendo") return ProviderStatus.Unknown;
            if ((capability is ProviderCapability.Download320 or ProviderCapability.DownloadLossless) && (provider is "kugou" or "ytmusic"))
                return ProviderStatus.Unknown;
            return ProviderStatus.Degraded;
        }
        return null;
    }
}
