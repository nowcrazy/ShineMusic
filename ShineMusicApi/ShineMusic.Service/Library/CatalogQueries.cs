// 职责：曲库共享查询。
// 「已发布曲目 + 全量导航」「当前用户可访问曲目（公开 + 本人私有上传）」两处查询
// 被多个端点与推荐服务复用，下沉到 Service 层避免端点反向被业务服务依赖。
// 查询与水合分离：SqlSugar 不做 Include 联表加载，先按条件查出 Track 主行，
// 再调 HydrateTracksAsync 批量填充导航属性（内存组装，TrackDto 映射代码不变）。

using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>曲库共享查询构造器：统一附带转为 TrackDto 所需的全部导航属性。</summary>
public static class CatalogQueries
{
    /// <summary>已发布曲目（SqlSugar）。导航属性未填充，DTO 映射前须先调 <see cref="HydrateTracksAsync"/>。</summary>
    public static ISugarQueryable<Track> PublishedTracks(ISqlSugarClient db) =>
        db.Queryable<Track>().Where(track => track.PublishedAt.HasValue);

    /// <summary>当前用户可访问（公开 + 本人私有上传）的曲目（SqlSugar）。导航属性同样须经水合填充。</summary>
    public static ISugarQueryable<Track> AccessibleTracksWithDetails(ISqlSugarClient db, Guid userId) =>
        db.Queryable<Track>().Where(track => track.PublishedAt.HasValue || track.OwnerUserId == userId);

    /// <summary>
    /// 曲目导航属性批量水合：对一页 Track 主行，按 Id 集合批量查出关联表与维度表后在内存中填充
    /// （TrackArtists+Artist、TrackGenres+Genre、Album、Artwork、MediaAssets 仅已发布），
    /// 等价于一次性联表加载的 Include 链，填充后 CatalogController.ToDto 可直接使用。
    /// </summary>
    public static async Task HydrateTracksAsync(ISqlSugarClient db, List<Track> tracks, CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0)
            return;
        var trackIds = tracks.Select(track => track.Id).ToList();

        // —— 曲目-艺人关联 + 艺人维度 ——
        var trackArtists = await db.Queryable<TrackArtist>()
            .Where(link => trackIds.Contains(link.TrackId))
            .ToListAsync(cancellationToken);
        var artistIds = trackArtists.Select(link => link.ArtistId).Distinct().ToList();
        var artists = artistIds.Count == 0
            ? []
            : await db.Queryable<Artist>().Where(artist => artistIds.Contains(artist.Id)).ToListAsync(cancellationToken);
        var artistMap = artists.ToDictionary(artist => artist.Id);
        foreach (var link in trackArtists)
            if (artistMap.TryGetValue(link.ArtistId, out var artist))
                link.Artist = artist;
        var trackArtistLookup = trackArtists.ToLookup(link => link.TrackId);

        // —— 曲目-流派关联 + 流派维度 ——
        var trackGenres = await db.Queryable<TrackGenre>()
            .Where(link => trackIds.Contains(link.TrackId))
            .ToListAsync(cancellationToken);
        var genreIds = trackGenres.Select(link => link.GenreId).Distinct().ToList();
        var genres = genreIds.Count == 0
            ? []
            : await db.Queryable<Genre>().Where(genre => genreIds.Contains(genre.Id)).ToListAsync(cancellationToken);
        var genreMap = genres.ToDictionary(genre => genre.Id);
        foreach (var link in trackGenres)
            if (genreMap.TryGetValue(link.GenreId, out var genre))
                link.Genre = genre;
        var trackGenreLookup = trackGenres.ToLookup(link => link.TrackId);

        // —— 所属专辑 ——
        var albumIds = tracks.Where(track => track.AlbumId.HasValue).Select(track => track.AlbumId!.Value).Distinct().ToList();
        var albums = albumIds.Count == 0
            ? []
            : await db.Queryable<Album>().Where(album => albumIds.Contains(album.Id)).ToListAsync(cancellationToken);
        var albumMap = albums.ToDictionary(album => album.Id);

        // —— 曲目封面 ——
        var artworkIds = tracks.Where(track => track.ArtworkId.HasValue).Select(track => track.ArtworkId!.Value).Distinct().ToList();
        var artworks = artworkIds.Count == 0
            ? []
            : await db.Queryable<Artwork>().Where(artwork => artworkIds.Contains(artwork.Id)).ToListAsync(cancellationToken);
        var artworkMap = artworks.ToDictionary(artwork => artwork.Id);

        // —— 已发布媒体资产（过滤条件与原有查询一致） ——
        var mediaAssets = await db.Queryable<MediaAsset>()
            .Where(asset => trackIds.Contains(asset.TrackId) && asset.IsPublished)
            .ToListAsync(cancellationToken);
        var mediaLookup = mediaAssets.ToLookup(asset => asset.TrackId);

        foreach (var track in tracks)
        {
            track.TrackArtists = trackArtistLookup[track.Id].ToList();
            track.TrackGenres = trackGenreLookup[track.Id].ToList();
            track.Album = track.AlbumId.HasValue && albumMap.TryGetValue(track.AlbumId.Value, out var album) ? album : null;
            track.Artwork = track.ArtworkId.HasValue && artworkMap.TryGetValue(track.ArtworkId.Value, out var artwork) ? artwork : null;
            track.MediaAssets = mediaLookup[track.Id].ToList();
        }
    }
}
