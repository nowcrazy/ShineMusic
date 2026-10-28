// 职责：外部音源抓歌（Source Download）功能的配置选项。
// 对应配置节 "SourceDownload"：Audius/Jamendo 两个开放版权平台的接入参数、
// 下载行为限制，以及「评分 ≥ 阈值自动直通入库」的闸门开关。

namespace ShineMusic.Model.Options;

/// <summary>外部音源抓歌功能配置。</summary>
public sealed class SourceDownloadOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "SourceDownload";

    /// <summary>Audius 平台接入配置（免密钥，仅需 AppName 标识）。</summary>
    public AudiusSourceOptions Audius { get; set; } = new();

    /// <summary>Jamendo 平台接入配置（需免费注册的 client_id；未配置时保持禁用）。</summary>
    public JamendoSourceOptions Jamendo { get; set; } = new();

    /// <summary>yt-dlp 子进程桥接配置（YouTube Music / SoundCloud 音源共用）。</summary>
    public YtDlpSourceOptions YtDlp { get; set; } = new();

    /// <summary>酷我音乐音源配置（免密钥直链，音质 320k）。</summary>
    public KuwoSourceOptions Kuwo { get; set; } = new();

    /// <summary>酷狗音乐音源配置（免密钥直链，免登录 128k）。</summary>
    public KugouSourceOptions Kugou { get; set; } = new();

    /// <summary>网易云音乐音源配置（免密钥，覆盖主流中文曲，320k MP3）。</summary>
    public NeteaseSourceOptions Netease { get; set; } = new();

    /// <summary>咪咕音乐音源配置（运营商版权曲库，含腾讯独家曲如周杰伦，支持无损）。</summary>
    public MiguSourceOptions Migu { get; set; } = new();

    /// <summary>下载行为限制（超时/体积/并发）。</summary>
    public SourceDownloadLimits Download { get; set; } = new();

    /// <summary>
    /// 聚合搜索的单源预算（秒）：任一源超过该时长即放弃该源（标注 providerErrors 超时），
    /// 其余源的结果照常返回；防止个别源（如不可达的 YouTube）挂起拖垮整体响应。默认 25 秒。
    /// </summary>
    public int SearchBudgetSeconds { get; set; } = 25;

    /// <summary>评分自动入库总开关：false 时音源条目一律转人工审核。</summary>
    public bool AutoPublishEnabled { get; set; } = true;

    /// <summary>自动入库评分阈值（0-100）：评分达到该值的音源条目跳过人工审核直接发布。</summary>
    public int AutoPublishThreshold { get; set; } = 70;
}

/// <summary>Audius 音源配置。</summary>
public sealed class AudiusSourceOptions
{
    /// <summary>是否启用该音源。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>API 基础地址（公共发现节点）。</summary>
    public string BaseUrl { get; set; } = "https://api.audius.co";

    /// <summary>调用方标识（Audius 要求所有请求携带 app_name 参数）。</summary>
    public string AppName { get; set; } = "ShineMusic";
}

/// <summary>Jamendo 音源配置。</summary>
public sealed class JamendoSourceOptions
{
    /// <summary>是否启用该音源（默认关闭，配置 ClientId 后打开）。</summary>
    public bool Enabled { get; set; }

    /// <summary>API 基础地址。</summary>
    public string BaseUrl { get; set; } = "https://api.jamendo.com/v3.0";

    /// <summary>开发者 client_id（devportal.jamendo.com 免费注册获取）；敏感值经环境变量注入。</summary>
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>酷我音源配置。</summary>
public sealed class KuwoSourceOptions
{
    /// <summary>是否启用该音源（免密钥，默认开启）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>酷狗音源配置。</summary>
public sealed class KugouSourceOptions
{
    /// <summary>是否启用该音源（免密钥，默认开启）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>网易云音乐音源配置。</summary>
public sealed class NeteaseSourceOptions
{
    /// <summary>是否启用该音源（免密钥，默认开启）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>搜索 API 基础地址（公开接口免登录）。</summary>
    public string SearchBaseUrl { get; set; } = "https://music.163.com/api/search/get";

    /// <summary>曲目详情 API 基础地址。</summary>
    public string DetailBaseUrl { get; set; } = "https://music.163.com/api/v3/song/detail";

    /// <summary>播放地址 API 基础地址（返回音频直链）。</summary>
    public string PlayerUrlBaseUrl { get; set; } = "https://music.163.com/api/song/enhance/player/url";

    /// <summary>下载码率（kbps）：320000=320k，999000=无损（需 VIP）。默认 320k。</summary>
    public int Bitrate { get; set; } = 320000;

    /// <summary>VIP 曲目回退解析配置（官方 API 对 fee≠0 曲目返回空 url 时启用）。</summary>
    public NeteaseVipFallbackOptions VipFallback { get; set; } = new();
}

/// <summary>
/// 网易云 VIP 曲目回退解析配置。
/// 思路借鉴 musicdl / UnblockNeteaseMusic：官方接口对 VIP 曲返回空 url 时，
/// 回退到第三方「解锁」端点获取下载直链。第三方端点可能使用共享 VIP 账号或音源替换，
/// 可用性随服务方运营变动，故做成可配置、可关闭、多端点链式兜底。
/// </summary>
public sealed class NeteaseVipFallbackOptions
{
    /// <summary>是否启用 VIP 回退解析（默认开启）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 回退端点链（按顺序尝试，首个返回有效完整直链的为准）。
    /// 默认优先 nextmusic（共享 VIP 账号，返回完整 320k/无损），haitangw 兜底（仅 60s 预览）。
    /// </summary>
    public List<NeteaseVipFallbackEndpoint> Endpoints { get; set; } = new()
    {
        new() { Type = VipFallbackEndpointTypes.NextMusic, BaseUrl = "https://nextmusic.toubiec.cn", Levels = ["exhigh", "standard"] },
        new() { Type = VipFallbackEndpointTypes.HaiTangW, Url = "https://musicapi.haitangw.net/music/wy.php?id={id}&level={level}&type=json", Levels = ["320", "128"] },
    };

    /// <summary>单次回退请求超时（秒），默认 15s（第三方服务响应通常较快）。</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>VIP 回退端点类型常量。</summary>
public static class VipFallbackEndpointTypes
{
    /// <summary>nextmusic.toubiec.cn：共享 VIP 账号，POST 两步（先取 IP 再取 url），返回完整歌曲（支持 320k/无损）。</summary>
    public const string NextMusic = "nextmusic";

    /// <summary>musicapi.haitangw.net：GET 单步，返回 data.url（2026-10 实测仅 60s 预览片段，作兜底）。</summary>
    public const string HaiTangW = "haitangw";

    /// <summary>ZMusic 兼容官方格式：GET /song/url，参数 id 与 level，返回 data[0].url（部分实例仅预览）。</summary>
    public const string ZMusic = "zmusic";
}

/// <summary>单个 VIP 回退端点配置。</summary>
public sealed class NeteaseVipFallbackEndpoint
{
    /// <summary>端点类型（见 <see cref="VipFallbackEndpointTypes"/>），决定请求/响应解析方式。</summary>
    public string Type { get; set; } = VipFallbackEndpointTypes.HaiTangW;

    /// <summary>
    /// GET 型端点的完整 URL 模板（含 {id}/{level} 占位符）。
    /// haitangw / zmusic 使用此字段。
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// POST 型端点的基础地址（不含路径）。
    /// nextmusic 使用此字段，内部拼接 /api/ip、/api/getSongUrl。
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>尝试的音质等级（按顺序，首个返回有效 url 的为准）。</summary>
    public string[] Levels { get; set; } = ["320", "128"];
}

/// <summary>咪咕音乐音源配置。</summary>
public sealed class MiguSourceOptions
{
    /// <summary>是否启用该音源（默认开启）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>下载音质：PQ=128k / HQ=320k / SQ=无损flac。默认 HQ（320k MP3）。</summary>
    public string ToneFlag { get; set; } = "HQ";
}

/// <summary>yt-dlp 子进程桥接配置（YouTube Music / SoundCloud 音源共用）。</summary>
public sealed class YtDlpSourceOptions
{
    /// <summary>是否启用 yt-dlp 系音源（false 时 ytmusic/soundcloud 均不可用）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// yt-dlp 可执行文件路径：相对路径按应用内容根目录解析。
    /// 缺省 ../tools/yt-dlp.exe —— dev（内容根=ShineMusicApi/ShineMusic.Api）与 publish（内容根=ShineMusicApi/publish）
    /// 两种布局下都解析到仓库的 ShineMusicApi/tools/yt-dlp.exe。
    /// </summary>
    public string ExecutablePath { get; set; } = "../tools/yt-dlp.exe";

    /// <summary>可选 cookies 文件路径（YouTube 触发风控 403/登录校验时使用；留空表示不携带）。</summary>
    public string CookiesFile { get; set; } = string.Empty;

    /// <summary>
    /// 可选 HTTP/SOCKS 代理（传给 yt-dlp --proxy，如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080）。
    /// 国内网络访问 YouTube/SoundCloud 需要配置；留空表示直连。
    /// </summary>
    public string Proxy { get; set; } = string.Empty;

    /// <summary>
    /// 传给 yt-dlp --js-runtimes 的 JS 运行时（YouTube 签名/挑战解密必需，2026 年起无运行时则拿不到音频格式）。
    /// 默认 node（本机 Node ≥22 即可）；留空则不传该参数。
    /// </summary>
    public string JsRuntimes { get; set; } = "node";

    /// <summary>
    /// bgutil PO Token 插件目录（传给 yt-dlp --plugin-dirs）。YouTube CDN 直链 403 时需要 POT；
    /// 目录不存在则跳过（优雅降级）。默认 ../tools/yt-dlp-plugins。
    /// </summary>
    public string PotPluginDir { get; set; } = "../tools/yt-dlp-plugins";

    /// <summary>
    /// bgutil POT 生成脚本的 server 目录（传给 youtubepot-bgutilscript:server_home）。
    /// 目录不存在则跳过。默认 ../tools/bgutil-pot/server（需先 npm ci 再 npx tsc 编译）。
    /// </summary>
    public string PotServerHome { get; set; } = "../tools/bgutil-pot/server";

    /// <summary>
    /// YouTube 下载用的 player_client 列表（逗号分隔，作为独立 --extractor-args 传递）。
    /// 默认客户端不会触发 POT 流程导致 CDN 403；mweb 会触发 gvs POT 请求。留空用 yt-dlp 默认集。
    /// </summary>
    public string PlayerClients { get; set; } = "mweb,web_music";

    /// <summary>单次搜索子进程超时（秒）：scsearch 每条约 7s（实测 5 条 44s），放宽到 120s 覆盖默认 limit=20 的最坏情况。</summary>
    public int SearchTimeoutSeconds { get; set; } = 120;

    /// <summary>单曲元数据抓取超时（秒），默认 30s。</summary>
    public int MetadataTimeoutSeconds { get; set; } = 30;
}

/// <summary>下载行为限制。</summary>
public sealed class SourceDownloadLimits
{
    /// <summary>单文件下载超时（秒），无损原文件体积大，默认放宽到 120s。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>单文件体积上限（字节），默认与上传单文件上限一致（250MB）。</summary>
    public long MaxFileSizeBytes { get; set; } = 262144000;

    /// <summary>并发下载任务数（1-4）；默认 1，避免触发平台限流。</summary>
    public int MaxConcurrentDownloads { get; set; } = 1;
}
