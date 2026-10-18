// 服务端音频转码与 FFmpeg 可执行文件配置选项：绑定 "Transcoding" 与 "Ffmpeg" 两个配置节。
namespace ShineMusic.Api.Options;

/// <summary>
/// 导入音频的统一转码目标参数，对应配置节 "Transcoding"；
/// 当前转码管线只实现了 aac_m4a 一种输出，启动时校验目标格式与取值范围。
/// </summary>
public sealed class TranscodingOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Transcoding";

    /// <summary>转码目标格式标识，目前仅支持固定值 "aac_m4a"（AAC 音频封装在 MP4/M4A 容器中）。</summary>
    public string TargetFormat { get; set; } = "aac_m4a";

    /// <summary>转码目标码率，单位 kbps，取值范围 96～320，默认 192。</summary>
    public int BitrateKbps { get; set; } = 192;

    /// <summary>
    /// 转码后台服务允许同时执行的 ffmpeg 任务数，取值范围 1～4，默认 1；
    /// 上限设为 4 是为了限制转码对服务器 CPU 的占用。
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 1;
}

/// <summary>
/// FFmpeg/ffprobe 可执行文件定位与任务超时配置，对应配置节 "Ffmpeg"。
/// </summary>
public sealed class FfmpegOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Ffmpeg";

    /// <summary>ffmpeg 可执行文件路径；默认 "ffmpeg" 表示直接从系统 PATH 中查找。</summary>
    public string ExecutablePath { get; set; } = "ffmpeg";

    /// <summary>ffprobe 可执行文件路径，用于探测音频元数据；默认 "ffprobe" 表示从 PATH 中查找。</summary>
    public string ProbeExecutablePath { get; set; } = "ffprobe";

    /// <summary>单个媒体探测/转码任务的超时时间，单位分钟，默认 30；超时后任务判定失败。</summary>
    public int TimeoutMinutes { get; set; } = 30;
}