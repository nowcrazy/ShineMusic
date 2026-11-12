// 职责：后台任务分组开关（P5）。
// 模块化单体阶段所有 HostedService 跑在 Web 进程里；把 Media 下载/转码/存储治理
// 单独部署时，Web 进程切到 WebOnly、Worker 进程固定 MediaOnly，两边互不重复消费队列。

namespace ShineMusic.Api.Extensions;

/// <summary>当前进程要承载的后台任务集合。</summary>
public enum HostedServiceMode
{
    /// <summary>全部 7 个后台任务（默认，保持单体部署行为不变）。</summary>
    All,

    /// <summary>仅媒体组 4 个：音源下载消费、导入转码消费、两者的重试扫描。</summary>
    MediaOnly,

    /// <summary>仅非媒体组 3 个：歌词补齐、点歌归档、歌单待处理补齐。Web 进程独立部署时使用。</summary>
    NonMediaOnly,

    /// <summary>不承载任何后台任务（便于只跑 HTTP 面的诊断/迁移场景）。</summary>
    None
}

/// <summary>把配置节 Worker:Mode 解析为 <see cref="HostedServiceMode"/>；未知值 fail-safe 回落到 All。</summary>
public static class HostedServiceModeParser
{
    /// <summary>解析配置值。</summary>
    public static HostedServiceMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "media" => HostedServiceMode.MediaOnly,
        "web" => HostedServiceMode.NonMediaOnly,
        "none" => HostedServiceMode.None,
        _ => HostedServiceMode.All
    };
}
