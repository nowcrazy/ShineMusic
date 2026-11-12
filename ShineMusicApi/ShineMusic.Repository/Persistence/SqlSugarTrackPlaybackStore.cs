// 职责：ITrackPlaybackStore 的 SqlSugar 实现。
// 查询形状与改造前 CatalogController.StreamTrack 内联的两条查询保持一致，
// 这样接口层瘦化的同时响应语义不变。

using ShineMusic.Model.Entities;
using ShineMusic.Model.Ports;
using SqlSugar;

namespace ShineMusic.Repository.Persistence;

/// <summary>基于 SqlSugar 的播放读模型实现。</summary>
public sealed class SqlSugarTrackPlaybackStore(ISqlSugarClient db) : ITrackPlaybackStore
{
    /// <inheritdoc />
    public async Task<TrackPlaybackVisibility?> GetVisibilityAsync(Guid trackId, CancellationToken cancellationToken)
    {
        var row = await db.Queryable<Track>()
            .Where(track => track.Id == trackId)
            .Select(track => new { track.PublishedAt, track.OwnerUserId })
            .FirstAsync(cancellationToken);
        return row is null ? null : new TrackPlaybackVisibility(row.PublishedAt, row.OwnerUserId);
    }

    /// <inheritdoc />
    public async Task<PublishedAudioAsset?> GetPublishedAudioAsync(Guid trackId, CancellationToken cancellationToken)
    {
        var row = await db.Queryable<MediaAsset>()
            .Where(asset => asset.TrackId == trackId && asset.IsPublished)
            .OrderByDescending(asset => asset.IsPrimary)
            .Select(asset => new { asset.StorageKey, asset.Container })
            .FirstAsync(cancellationToken);
        return row is null ? null : new PublishedAudioAsset(row.StorageKey, row.Container);
    }
}
