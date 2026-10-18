// 歌词自动匹配配置：绑定 "Lyrics" 配置节。
// 控制导入流水线在解析完元数据后是否自动补全歌词，以及外部歌词源（当前为 LRCLIB）的调用参数。
namespace ShineMusic.Api.Options;

/// <summary>
/// 歌词自动匹配选项，对应配置节 "Lyrics"。
/// </summary>
public sealed class LyricsOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Lyrics";

    /// <summary>
    /// 是否启用外部歌词源自动匹配（内嵌标签与旁挂 .lrc 始终优先尝试，不受此开关影响）。
    /// 关闭后，没有内嵌/旁挂歌词的条目只会被标记 lyrics_missing，由人工补充。
    /// </summary>
    public bool AutoFetchEnabled { get; set; } = true;

    /// <summary>外部歌词源标识，当前仅支持 "lrclib"。</summary>
    public string Provider { get; set; } = "lrclib";

    /// <summary>外部歌词源 API 根地址（不含末尾斜杠）。</summary>
    public string BaseUrl { get; set; } = "https://lrclib.net/api";

    /// <summary>单次外部请求超时（秒）；超时视为未命中，不影响导入流程。</summary>
    public int TimeoutSeconds { get; set; } = 6;

    /// <summary>
    /// 精确匹配时允许的时长偏差（秒）。LRCLIB 的 get 接口对时长敏感，
    /// 实测约 ±5 秒内可命中，超出会返回 404，需要走 search 兜底。
    /// </summary>
    public int DurationToleranceSeconds { get; set; } = 5;

    /// <summary>
    /// search 兜底时的最低相似度阈值（0~1）。低于该值的候选一律丢弃，
    /// 避免把同名不同版本（Live/Remix）的歌词错误写入。
    /// </summary>
    public double MinScore { get; set; } = 0.6;

    /// <summary>候选同时存在逐行时间轴与纯文本时，是否优先选择带时间轴的版本。</summary>
    public bool PreferSynced { get; set; } = true;

    /// <summary>
    /// 是否把繁体歌词转换为简体。默认关闭：完整的繁简映射需要 OpenCC 词典，
    /// 简单字表转换会误伤（如「著」在「著作」中不应转「着」），故保留原文并交给人工判断。
    /// </summary>
    public bool ConvertTraditionalToSimplified { get; set; }

    /// <summary>调用外部歌词源时使用的 User-Agent（LRCLIB 要求标识调用方）。</summary>
    public string UserAgent { get; set; } = "ShineMusic/1.0";
}
