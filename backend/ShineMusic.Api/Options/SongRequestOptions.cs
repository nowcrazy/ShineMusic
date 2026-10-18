// 职责：点歌需求的运营配置（目前用于“已满足需求自动归档”）。
// 绑定配置节 "SongRequests"；归档仅改变公共列表可见性，不删除记录。

namespace ShineMusic.Api.Options;

/// <summary>点歌需求运营配置，对应配置节 "SongRequests"。</summary>
public sealed class SongRequestOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "SongRequests";

    /// <summary>是否启用自动归档后台任务。</summary>
    public bool ArchiveEnabled { get; set; } = true;

    /// <summary>已满足（fulfilled）的需求在多少天后自动归档（从公共池隐藏）；默认 30 天。</summary>
    public int ArchiveAfterDays { get; set; } = 30;

    /// <summary>后台归档扫描间隔（分钟）；默认 60 分钟。</summary>
    public int ArchiveScanIntervalMinutes { get; set; } = 60;
}
