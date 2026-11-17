// 职责：外部音源抓歌（Source Download）功能的配置选项。
// 对应配置节 "SourceDownload"：11 个音源的接入参数、下载行为限制，
// 以及「评分 ≥ 阈值自动直通入库」的闸门开关。
//
// 【安全默认值原则】2026-11-12 逐源实测后，只有 netease / migu 能稳定交付
// 正确曲目 + 合格音频；其余 9 个源默认禁用，Migu 默认 HQ，VIP 回退只保留 gdstudio。
// 这些值不是「示例」，而是配置缺失 / 环境覆盖失败时的最后一道防线：
// appsettings.json 与代码默认值必须同向，否则一次漏配就会静默回退到低质量链路。
// 重新启用任一源的判据见 docs/音源巡检与音质结论.md（逐源码率/时长/内容形态实测）。
// 注：禁用源的 Enabled 故意省略初始化器（隐式 false）——项目开启 CA1805 且警告即错误，
// 显式写 = false 会编译失败；ShineMusic.Tests/Rules/SourceDownloadSafeDefaultsTests.cs 锁定该语义。

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

    /// <summary>yt-dlp 子进程桥接配置（YouTube Music 音源）。</summary>
    public YtDlpSourceOptions YtDlp { get; set; } = new();

    /// <summary>酷我音乐音源配置（免密钥直链，未登录 MP3 最高 128k，部分曲目可取 FLAC）。</summary>
    public KuwoSourceOptions Kuwo { get; set; } = new();

    /// <summary>酷狗音乐音源配置（免密钥直链，免登录 128k）。</summary>
    public KugouSourceOptions Kugou { get; set; } = new();

    /// <summary>网易云音乐音源配置（免密钥，覆盖主流中文曲，320k MP3）。</summary>
    public NeteaseSourceOptions Netease { get; set; } = new();

    /// <summary>咪咕音乐音源配置（运营商版权曲库，含腾讯独家曲如周杰伦，支持无损）。</summary>
    public MiguSourceOptions Migu { get; set; } = new();

    /// <summary>汽水音乐（抖音音乐）音源配置（免密钥搜索/元数据；VIP 曲目匿名态仅试听片段）。</summary>
    public QishuiSourceOptions Qishui { get; set; } = new();

    /// <summary>QQ音乐音源配置（腾讯系曲库，匿名 128k m4a；Cookie 登录态可至 320k/无损）。</summary>
    public QqSourceOptions Qq { get; set; } = new();

    /// <summary>千千音乐（百度音乐）音源配置（公开 API 免登录，中文老歌覆盖）。</summary>
    public QianqianSourceOptions Qianqian { get; set; } = new();

    /// <summary>Bilibili（B站音频）音源配置（live/综艺现场/翻唱；下载经 yt-dlp 桥接自下载，复用 YtDlp 节代理/cookies）。</summary>
    public BilibiliSourceOptions Bilibili { get; set; } = new();

    /// <summary>下载行为限制（超时/体积/并发）。</summary>
    public SourceDownloadLimits Download { get; set; } = new();

    /// <summary>
    /// V2.0 Provider 核心架构（Phase 1）的附加配置节。
    /// 刻意放在 <c>SourceDownload:ProviderFramework</c> 下而不是平铺到 <c>SourceDownload</c> 根：
    /// 现有 11 个音源配置、SearchBudgetSeconds、AutoPublishThreshold 的语义必须保持不变，
    /// 平铺会带来同名 Key 与「看起来迁移了其实没迁移」的配置漂移。Phase 4/5 再决定是否收敛。
    /// 详见 docs/音源架构V2-Phase1-实施记录.md。
    /// </summary>
    public SourceProviderFrameworkOptions ProviderFramework { get; set; } = new();

    /// <summary>
    /// 聚合搜索的单源预算（秒）：任一源超过该时长即放弃该源（标注 providerErrors 超时），
    /// 其余源的结果照常返回；防止个别源（如不可达的 YouTube）挂起拖垮整体响应。默认 25 秒。
    /// </summary>
    public int SearchBudgetSeconds { get; set; } = 25;

    /// <summary>
    /// 歌单批量补齐（后台 worker）的单源预算（秒）。默认 8 秒。
    /// 批量补齐是「几百首排队」场景，追求吞吐而非单曲最优：预算收紧后慢源（如 yt-dlp 30s 超时）
    /// 会被快速放弃，改由下载阶段的换源机制兜底，避免一首歌把整轮补齐拖住。
    /// </summary>
    public int BackfillSearchBudgetSeconds { get; set; } = 8;

    /// <summary>评分自动入库总开关：false 时音源条目一律转人工审核。</summary>
    public bool AutoPublishEnabled { get; set; } = true;

    /// <summary>
    /// 自动入库评分阈值（0-100）：评分达到该值的音源条目跳过人工审核直接发布。
    /// 默认 90（V2.0 Phase 2 评分重构：身份40/音质40/元数据15/歌词5）。
    /// 身份 Rejected 或低码率（&lt;256kbps）的理论上限 = 60，
    /// 阈值必须高于该上限，否则低质量或身份不符的文件会重新获得自动发布资格（启动校验会拦截）。
    /// </summary>
    public int AutoPublishThreshold { get; set; } = 90;

    /// <summary>
    /// V2.0 Phase 2 评分权重（用户 2026-11-12 追加裁决）：四个维度**逐项单独配置**，
    /// 合计必须为 100，由 <c>ValidateOnStart</c> 拦截。数值调整须单独评审，本轮只做配置化不改默认值。
    /// 低质量 / 身份 Rejected 的理论上限由 <see cref="ScoreWeightsOptions.LowQualityCeiling"/> 动态推导，
    /// 不再依赖评分器里的硬编码常量。
    /// </summary>
    public ScoreWeightsOptions ScoreWeights { get; set; } = new();
}

/// <summary>
/// 自动评分的四维权重。每一项都是独立的配置键（<c>SourceDownload:ScoreWeights:Identity</c> 等），
/// 便于在不改代码的前提下单独调分值；<see cref="Total"/> 必须恒为 100。
/// </summary>
public sealed class ScoreWeightsOptions
{
    /// <summary>身份维度满分（Matched 得满分 / Ambiguous 半分 / Rejected 0）。</summary>
    public int Identity { get; set; } = 40;

    /// <summary>音质维度满分（无损满分；有损按 320kbps 绝对基准折算；低于 256kbps 记 0）。</summary>
    public int Quality { get; set; } = 40;

    /// <summary>元数据维度满分（标题 / 艺人 / 专辑 / 流派，按 5:5:3:2 的基准比例分配）。</summary>
    public int Metadata { get; set; } = 15;

    /// <summary>歌词维度满分（按歌词置信度线性折算）。</summary>
    public int Lyrics { get; set; } = 5;

    /// <summary>四项合计；启动校验要求恒等于 100。</summary>
    public int Total => Identity + Quality + Metadata + Lyrics;

    /// <summary>每项都必须非负，否则总分与上限推导失去意义。</summary>
    public bool AllNonNegative => Identity >= 0 && Quality >= 0 && Metadata >= 0 && Lyrics >= 0;

    /// <summary>
    /// 身份 Rejected（身份 0）或低码率（音质 0）条目能拿到的理论最高分：
    /// 取两个主维度的较大者 + 两个次要维度。默认 40+15+5 = 60。
    /// 自动发布阈值必须严格大于此值，否则质量闸门会被配置悄悄打开。
    /// </summary>
    public int LowQualityCeiling => Math.Max(Identity, Quality) + Metadata + Lyrics;
}

/// <summary>Audius 音源配置。</summary>
public sealed class AudiusSourceOptions
{
    /// <summary>是否启用该音源。默认禁用：实测 320k 命中《髮如雪》等错误曲目，见音源巡检结论。</summary>
    public bool Enabled { get; set; }

    /// <summary>API 基础地址（公共发现节点）。</summary>
    public string BaseUrl { get; set; } = "https://api.audius.co";

    /// <summary>调用方标识（Audius 要求所有请求携带 app_name 参数）。</summary>
    public string AppName { get; set; } = "ShineMusic";
}

/// <summary>Jamendo 音源配置。</summary>
public sealed class JamendoSourceOptions
{
    /// <summary>是否启用该音源。默认禁用：需 client_id，且实测未交付目标中文曲目。</summary>
    public bool Enabled { get; set; }

    /// <summary>API 基础地址。</summary>
    public string BaseUrl { get; set; } = "https://api.jamendo.com/v3.0";

    /// <summary>开发者 client_id（devportal.jamendo.com 免费注册获取）；敏感值经环境变量注入。</summary>
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>酷我音源配置。</summary>
public sealed class KuwoSourceOptions
{
    /// <summary>是否启用该音源。默认禁用：实测匿名态 purl 为空 / 11s 占位，见音源巡检结论。</summary>
    public bool Enabled { get; set; }

    /// <summary>可选 Cookie（登录态）：配置后可取 VIP/320k 资源，留空为匿名态。真实值由环境变量注入，勿提交仓库。</summary>
    public string Cookie { get; set; } = string.Empty;
}

/// <summary>酷狗音源配置。</summary>
public sealed class KugouSourceOptions
{
    /// <summary>是否启用该音源。默认禁用：实测 134s 是《晴天 (Live)》而非目标曲目，见音源巡检结论。</summary>
    public bool Enabled { get; set; }

    /// <summary>可选 Cookie（登录态）：配置后可取 VIP 资源，留空为匿名态。真实值由环境变量注入，勿提交仓库。</summary>
    public string Cookie { get; set; } = string.Empty;
}

/// <summary>网易云音乐音源配置。</summary>
public sealed class NeteaseSourceOptions
{
    /// <summary>是否启用该音源（免密钥，默认开启）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>可选 Cookie（登录态）：配置后可取 VIP 曲目直链，留空为匿名态。真实值由环境变量注入，勿提交仓库。</summary>
    public string Cookie { get; set; } = string.Empty;

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
    /// 默认只保留 gdstudio：2026-11 实测对 VIP 曲返回完整 320k / 无损。
    /// nextmusic / haitangw 已从默认链移除（前者不可控、后者实测 60s 预览且 403），
    /// 类型常量保留以兼容已有外部配置，但不再作为默认行为。
    /// </summary>
    public List<NeteaseVipFallbackEndpoint> Endpoints { get; set; } = new()
    {
        new() { Type = VipFallbackEndpointTypes.GdStudio, Url = "https://music-api.gdstudio.xyz/api.php?types=url&source=netease&id={id}&br={level}", Levels = ["320", "999"] },
    };

    /// <summary>单次回退请求超时（秒），默认 15s（第三方服务响应通常较快）。</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>VIP 回退端点类型常量。</summary>
public static class VipFallbackEndpointTypes
{
    /// <summary>nextmusic.toubiec.cn：共享 VIP 账号，POST 两步（先取 IP 再取 url），返回完整歌曲（支持 320k/无损）。</summary>
    public const string NextMusic = "nextmusic";

    /// <summary>musicapi.haitangw.net：GET 单步，返回 data.url（2026-10 实测仅 60s 预览片段，2026-11 实测 403；已移出默认链，仅保留兼容）。</summary>
    public const string HaiTangW = "haitangw";

    /// <summary>ZMusic 兼容官方格式：GET /song/url，参数 id 与 level，返回 data[0].url（部分实例仅预览）。</summary>
    public const string ZMusic = "zmusic";

    /// <summary>GD Studio（music-api.gdstudio.xyz）：GET 单步，br 档位（999=无损 flac/320），响应根级 url/br/size；2026-11 实测对 VIP 曲返回完整无损文件。</summary>
    public const string GdStudio = "gdstudio";
}

/// <summary>单个 VIP 回退端点配置。</summary>
public sealed class NeteaseVipFallbackEndpoint
{
    /// <summary>端点类型（见 <see cref="VipFallbackEndpointTypes"/>），决定请求/响应解析方式。默认 gdstudio（唯一实测可靠档）。</summary>
    public string Type { get; set; } = VipFallbackEndpointTypes.GdStudio;

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
    public string[] Levels { get; set; } = ["320", "192"];
}

/// <summary>咪咕音乐音源配置。</summary>
public sealed class MiguSourceOptions
{
    /// <summary>是否启用该音源（默认开启）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>最高下载音质：PQ=128k / HQ=320k / SQ=无损flac。默认 HQ，落地后须通过实测准入。</summary>
    public string ToneFlag { get; set; } = "HQ";

    /// <summary>可选 Cookie（登录态）：配置后可取 VIP 资源，留空为匿名态。真实值由环境变量注入，勿提交仓库。</summary>
    public string Cookie { get; set; } = string.Empty;
}

/// <summary>汽水音乐（Qishui / Luna）音源配置。</summary>
public sealed class QishuiSourceOptions
{
    /// <summary>是否启用该音源。默认禁用：实测 VIP 曲仅 30~60s 试听片段，见音源巡检结论。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 可选 Cookie（登录态）：匿名接口对 VIP/付费曲目只下发 30~60 秒试听片段，
    /// 配置登录 Cookie 后可由官方接口获得更完整的授权信息。真实值由环境变量注入，勿提交仓库。
    /// </summary>
    public string Cookie { get; set; } = string.Empty;

    /// <summary>搜索 API 基础地址（内部拼接 /track，需 Android 客户端 UA 与完整设备参数）。</summary>
    public string SearchBaseUrl { get; set; } = "https://api.qishui.com/luna/search";

    /// <summary>曲目详情 API 基础地址（分享页 SEO 数据源，无需签名）。</summary>
    public string SeoTrackBaseUrl { get; set; } = "https://beta-luna.douyin.com/luna/h5/seo_track";
}

/// <summary>QQ音乐音源配置。</summary>
public sealed class QqSourceOptions
{
    /// <summary>是否启用该音源。默认禁用：实测匿名态 purl 为空 / 11s 占位，见音源巡检结论。</summary>
    public bool Enabled { get; set; }

    /// <summary>可选 Cookie（登录态）：配置后可取 VIP/320k/无损资源，留空为匿名态（仅 128k m4a）。真实值由环境变量注入，勿提交仓库。</summary>
    public string Cookie { get; set; } = string.Empty;
}

/// <summary>千千音乐（百度音乐）音源配置。</summary>
public sealed class QianqianSourceOptions
{
    /// <summary>是否启用该音源。默认禁用：实测高码率 FLAC 命中《等晴天》等错误曲目，见音源巡检结论。</summary>
    public bool Enabled { get; set; }
}

/// <summary>Bilibili（B站音频）音源配置。</summary>
public sealed class BilibiliSourceOptions
{
    /// <summary>是否启用该音源。默认禁用：内容形态以 live / 综艺 / 翻唱为主，见音源巡检结论。</summary>
    public bool Enabled { get; set; }
}

/// <summary>yt-dlp 子进程桥接配置（YouTube Music 音源）。</summary>
public sealed class YtDlpSourceOptions
{
    /// <summary>是否启用 yt-dlp 系音源。默认禁用：需可用代理 / cookies / POT，见音源巡检结论。</summary>
    public bool Enabled { get; set; }

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
    /// 国内网络访问 YouTube 需要时配置；留空表示直连。
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

// ───────────────────────── V2.0 Phase 1：Provider 框架配置 ─────────────────────────
// 提案 §8「SourceDownload.json」的落地形态：加法式新增，不改任何既有键。
// 与提案示例的三处刻意偏差（均需后续阶段裁决，不在 Phase 1 偷偷改行为）：
//   1) 配置节下沉到 ProviderFramework，避免与既有 Netease/Migu/Download 键冲突；
//   2) QualityPolicy 只声明不强制（Phase 3 才接入），因为 MinimumQuality=320K /
//      AllowLossyFallback=false 与 §12 的「原文件缺失→192k 兼容副本合法发布」相冲突；
//   3) AutoPublish.Threshold=90 未落地：当前启动校验要求阈值 > 60 且默认 75，
//      改阈值属于评分重构（提案 §9），需与 D1「身份做前置闸门而非分数」一并裁决。

/// <summary>V2.0 Provider 框架总配置。</summary>
public sealed class SourceProviderFrameworkOptions
{
    /// <summary>
    /// 框架总开关。Phase 1 为纯加法：新服务只注册、不接管现有 SourceDownloadJob 下载链路，
    /// 因此关闭它不会影响既有功能；Phase 2 起接入调度时再把它变成真实闸门。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>搜索预算与并发策略。</summary>
    public SourceProviderSearchOptions Search { get; set; } = new();

    /// <summary>健康检查 / 熔断参数（Phase 5 消费，Phase 1 只绑定并校验）。</summary>
    public SourceProviderHealthCheckOptions HealthCheck { get; set; } = new();

    /// <summary>质量策略声明（Phase 3 消费并强制执行）。</summary>
    public SourceQualityPolicyOptions QualityPolicy { get; set; } = new();

    /// <summary>按平台标识索引的调度策略；未配置的平台使用默认值。</summary>
    public Dictionary<string, SourceProviderPolicyOptions> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>并发搜索预算：总预算与单源预算分离（提案 §4.3）。</summary>
public sealed class SourceProviderSearchOptions
{
    /// <summary>整轮搜索的总预算（秒）。默认 25，与既有 SearchBudgetSeconds 对齐但语义不同（总 vs 单源）。</summary>
    public int TotalBudgetSeconds { get; set; } = 25;

    /// <summary>单源默认超时（秒）。默认 8。</summary>
    public int ProviderTimeoutSeconds { get; set; } = 8;

    /// <summary>单源最大超时（秒）。默认 12；ProviderTimeoutSeconds 不得大于它。</summary>
    public int MaxProviderTimeoutSeconds { get; set; } = 12;

    /// <summary>最大并发 Provider 数。默认 5。</summary>
    public int MaxConcurrentProviders { get; set; } = 5;

    /// <summary>单源最多返回条目数。默认 20。</summary>
    public int MaxResultsPerProvider { get; set; } = 20;
}

/// <summary>Provider 健康检查 / 熔断参数（提案 §6；自动状态机属 Phase 5）。</summary>
public sealed class SourceProviderHealthCheckOptions
{
    /// <summary>是否启用健康记录。默认开启。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>巡检间隔（分钟）。默认 30。</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>连续失败阈值。默认 5 次。</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>首次熔断冷却（秒）。默认 60。</summary>
    public int RecoverySeconds { get; set; } = 60;

    /// <summary>再次熔断冷却上限（秒）。默认 300。</summary>
    public int MaxRecoverySeconds { get; set; } = 300;
}

/// <summary>
/// 原文件质量策略；由 QualityGate 与 AudioAdmissionPolicy 共同执行。
/// </summary>
public sealed class SourceQualityPolicyOptions
{
    /// <summary>常用目标质量。默认 320K，不为每首曲目持续追求无损。</summary>
    public string PreferredQuality { get; set; } = "320K";

    /// <summary>最低实测 MP3/AAC 码率。默认 192K；未知编码不准入。</summary>
    public string MinimumQuality { get; set; } = "192K";

    /// <summary>允许合格有损回退；不绕过实测编码和 192kbps 硬下限。</summary>
    public bool AllowLossyFallback { get; set; } = true;

    /// <summary>是否允许试听片段。默认 false。</summary>
    public bool AllowPreview { get; set; }

    /// <summary>是否要求完整音频。默认 true。</summary>
    public bool RequireFullTrack { get; set; } = true;

    /// <summary>是否要求录音室原版。默认 true。</summary>
    public bool RequireStudioVersion { get; set; } = true;

    /// <summary>最低匹配分（提案 §9 的评分重构，Phase 2/3 裁决后启用）。</summary>
    public int MinimumMatchScore { get; set; } = 90;

    /// <summary>最低元数据分。</summary>
    public int MinimumMetadataScore { get; set; } = 75;
}

/// <summary>单平台调度策略。Priority 只决定调度顺序，**不是**质量等级（提案 §8 注意事项 1）。</summary>
public sealed class SourceProviderPolicyOptions
{
    /// <summary>该平台在 V2 框架内是否参与调度；与既有 SourceDownload:Netease:Enabled 是「与」关系。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>调度优先级（值大者优先，相同则按 DI 注册顺序）。省略初始化器以避开 CA1805。</summary>
    public int Priority { get; set; }

    /// <summary>是否允许按平台曲目 Id 获取元数据；独立于搜索与下载。</summary>
    public bool MetadataEnabled { get; set; } = true;

    /// <summary>是否允许调度该平台的搜索能力。</summary>
    public bool SearchEnabled { get; set; } = true;

    /// <summary>是否允许调度该平台的音频资源解析与下载；不影响元数据获取。</summary>
    public bool DownloadEnabled { get; set; } = true;
}
