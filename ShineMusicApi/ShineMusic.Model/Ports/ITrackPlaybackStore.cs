// 职责：播放用例所需的曲库读取端口。
// 让接口层不再直接注入 ISqlSugarClient：Controller 只表达「要什么」，查询由基础设施实现。

namespace ShineMusic.Model.Ports;

/// <summary>曲目的可见性信息。</summary>
public sealed record TrackPlaybackVisibility(DateTimeOffset? PublishedAt, Guid? OwnerUserId);

/// <summary>已发布音频资产的位置与格式。</summary>
public sealed record PublishedAudioAsset(string StorageKey, string Container);

/// <summary>播放端点专用的曲库读模型。</summary>
public interface ITrackPlaybackStore
{
    /// <summary>读取曲目可见性；不存在返回 null。</summary>
    Task<TrackPlaybackVisibility?> GetVisibilityAsync(Guid trackId, CancellationToken cancellationToken);

    /// <summary>读取主已发布资产（优先 IsPrimary）；不存在返回 null。</summary>
    Task<PublishedAudioAsset?> GetPublishedAudioAsync(Guid trackId, CancellationToken cancellationToken);
}
