// 职责：IArtworkVisibilityStore 的 SqlSugar 实现。
// 查询形状与改造前 ArtworkController.GetArtwork 的内联查询完全一致：
// 主键条件与 OR 可见性组必须拆成两条链式 Where，否则 SqlSugar 会丢掉 && 产生语法错误。

using ShineMusic.Model.Entities;
using ShineMusic.Model.Ports;
using SqlSugar;

namespace ShineMusic.Repository.Persistence;

/// <summary>基于 SqlSugar 的封面可见性读模型实现。</summary>
public sealed class SqlSugarArtworkVisibilityStore(ISqlSugarClient db) : IArtworkVisibilityStore
{
    /// <inheritdoc />
    public async Task<PublicArtworkAsset?> GetPublicAsync(Guid artworkId, CancellationToken cancellationToken)
    {
        var artwork = await db.Queryable<Artwork>()
            .Where(item => item.Id == artworkId)
            .Where(item =>
                SqlFunc.Subqueryable<Track>().Where(track => track.ArtworkId == item.Id && track.PublishedAt.HasValue).Any() ||
                SqlFunc.Subqueryable<Album>().Where(album => album.ArtworkId == item.Id &&
                    SqlFunc.Subqueryable<Track>().Where(track => track.AlbumId == album.Id && track.PublishedAt.HasValue).Any()).Any() ||
                SqlFunc.Subqueryable<EditorialCollection>().Where(collection => collection.ArtworkId == item.Id && collection.IsPublished).Any())
            .Select(item => new { item.StorageKey, item.ContentType })
            .FirstAsync(cancellationToken);
        return artwork is null ? null : new PublicArtworkAsset(artwork.StorageKey, artwork.ContentType);
    }
}
