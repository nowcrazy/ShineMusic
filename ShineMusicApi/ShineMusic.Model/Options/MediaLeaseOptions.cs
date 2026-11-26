namespace ShineMusic.Model.Options;

/// <summary>媒体任务租约配置：控制 Worker 认领、心跳和失效恢复的时间窗口。</summary>
public sealed class MediaLeaseOptions
{
    public const string SectionName = "MediaLease";

    /// <summary>一次任务认领的租约时长（秒）。</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Worker 续租心跳间隔（秒）。</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>每轮租约恢复最多处理的任务数。</summary>
    public int RecoveryBatchSize { get; set; } = 100;
}
