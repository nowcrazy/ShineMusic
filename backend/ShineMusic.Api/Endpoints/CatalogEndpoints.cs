using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>Serves published catalog content and media streams.</summary>
[ApiController]
[Route("api/v1")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class CatalogController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 100;

    [HttpGet("tracks", Name = "GetTracks")]
    [Tags("Tracks")]
    public async Task<IResult> GetTracks(
        MusicDbContext db,
        string? q,
        Guid? artistId,
        Guid? albumId,
        string? genre,
        string? sort,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = PublishedTracks(db);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(track =>
                EF.Functions.Like(track.Title, $"%{term}%") ||
                (track.Album != null && EF.Functions.Like(track.Album.Title, $"%{term}%")) ||
                track.TrackArtists.Any(link => EF.Functions.Like(link.Artist.Name, $"%{term}%")));
        }
        if (artistId.HasValue)
            query = query.Where(track => track.TrackArtists.Any(link => link.ArtistId == artistId.Value));
        if (albumId.HasValue)
            query = query.Where(track => track.AlbumId == albumId.Value);
        if (!string.IsNullOrWhiteSpace(genre))
            query = query.Where(track => track.TrackGenres.Any(link => link.Genre.Name == genre.Trim()));

        var total = await query.CountAsync(cancellationToken);
        query = sort?.ToLowerInvariant() switch
        {
            null or "recent" => query.OrderByDescending(track => track.PublishedAt).ThenBy(track => track.Title),
            "title" => query.OrderBy(track => track.Title).ThenBy(track => track.Id),
            "duration" => query.OrderBy(track => track.DurationMs).ThenBy(track => track.Title),
            _ => null!
        };
        if (query is null)
            return ApiResults.ValidationError(HttpContext, "sort", "sort must be recent, title, or duration.");

        var tracks = await query.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<TrackDto>(tracks.Select(ToDto).ToArray(), pageNumber, size, total));
    }

    [HttpGet("tracks/{trackId:guid}", Name = "GetTrack")]
    [Tags("Tracks")]
    public async Task<IResult> GetTrack(MusicDbContext db, Guid trackId, CancellationToken cancellationToken)
    {
        var track = await PublishedTracks(db).FirstOrDefaultAsync(track => track.Id == trackId, cancellationToken);
        if (track is null)
        {
            // 未公开曲目（用户上传的私有曲目）：仅上传者本人与管理员可查看详情。
            var privateTrack = await db.Tracks.AsNoTracking()
                .Include(candidate => candidate.TrackArtists).ThenInclude(link => link.Artist)
                .Include(candidate => candidate.TrackGenres).ThenInclude(link => link.Genre)
                .Include(candidate => candidate.Album)
                .Include(candidate => candidate.Artwork)
                .Include(candidate => candidate.MediaAssets.Where(asset => asset.IsPublished))
                .FirstOrDefaultAsync(candidate => candidate.Id == trackId && candidate.PublishedAt == null, cancellationToken);
            if (privateTrack is null || !PrivateTrackAccess.CanAccess(privateTrack.PublishedAt, privateTrack.OwnerUserId, HttpContext.User))
                return ApiResults.NotFound(HttpContext);
            track = privateTrack;
        }
        return Results.Ok(ToDto(track));
    }

    [HttpGet("tracks/{trackId:guid}/stream", Name = "StreamTrack")]
    [Tags("Tracks")]
    public async Task<IResult> StreamTrack(
        MusicDbContext db,
        LibraryStoragePaths storagePaths,
        IOptions<FeatureOptions> featureOptions,
        Guid trackId,
        CancellationToken cancellationToken)
    {
        if (!featureOptions.Value.AnonymousPlayback && HttpContext.User.Identity?.IsAuthenticated != true)
            return Results.Json(new ApiErrorResponse(new ApiErrorDetail(
                ErrorCodes.AuthenticationRequired, "Authentication is required.", HttpContext.TraceIdentifier)),
                statusCode: StatusCodes.Status401Unauthorized);

        // 私有曲目（用户上传、未公开）的可见性判断：上传者本人与管理员可播放，其他人按 404 处理。
        var track = await db.Tracks.AsNoTracking()
            .Where(candidate => candidate.Id == trackId)
            .Select(candidate => new { candidate.PublishedAt, candidate.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (track is null || !PrivateTrackAccess.CanAccess(track.PublishedAt, track.OwnerUserId, HttpContext.User))
            return ApiResults.NotFound(HttpContext);

        var asset = await db.MediaAssets.AsNoTracking()
            .Where(asset => asset.TrackId == trackId && asset.IsPublished)
            .OrderByDescending(asset => asset.IsPrimary)
            .Select(asset => new { asset.StorageKey, asset.Container })
            .FirstOrDefaultAsync(cancellationToken);
        if (asset is null || !storagePaths.TryResolve(asset.StorageKey, out var filePath) || !System.IO.File.Exists(filePath))
            return ApiResults.NotFound(HttpContext);

        var contentType = GetAudioContentType(asset.Container);
        if (contentType is null)
        {
            return Results.Json(
                new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.UnsupportedMediaFormat, "The media format is not supported.", HttpContext.TraceIdentifier)),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // ETag + Last-Modified + 短缓存：重复播放/回拖进度可命中客户端缓存（弱网下少一次整段重下）。
        // 已发布曲目的音频文件是不可变的，1 小时缓存不会出现明显陈旧。
        var fileInfo = new FileInfo(filePath);
        var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc);
        var entityTag = new EntityTagHeaderValue($"\"{fileInfo.Length:x}-{fileInfo.LastWriteTimeUtc.Ticks:x}\"");
        HttpContext.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.File(filePath, contentType, lastModified: lastModified, entityTag: entityTag, enableRangeProcessing: true);
    }

    [HttpGet("albums", Name = "GetAlbums")]
    [Tags("Albums")]
    public async Task<IResult> GetAlbums(
        MusicDbContext db,
        string? q,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        IQueryable<Album> query = db.Albums.AsNoTracking()
            .Where(album => album.Tracks.Any(track => track.PublishedAt != null))
            .Include(album => album.AlbumArtists).ThenInclude(link => link.Artist)
            .Include(album => album.Artwork)
            .Include(album => album.Tracks.Where(track => track.PublishedAt != null));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(album => EF.Functions.Like(album.Title, $"%{term}%") ||
                album.AlbumArtists.Any(link => EF.Functions.Like(link.Artist.Name, $"%{term}%")));
        }

        var total = await query.CountAsync(cancellationToken);
        var albums = await query.OrderBy(album => album.Title).ThenBy(album => album.Id)
            .Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = albums.Select(album => new AlbumSummaryDto(album.Id, album.Title)).ToArray();
        return Results.Ok(new PageResult<AlbumSummaryDto>(items, pageNumber, size, total));
    }

    [HttpGet("albums/{albumId:guid}", Name = "GetAlbum")]
    [Tags("Albums")]
    public async Task<IResult> GetAlbum(MusicDbContext db, Guid albumId, CancellationToken cancellationToken)
    {
        var album = await db.Albums.AsNoTracking()
            .Where(album => album.Id == albumId && album.Tracks.Any(track => track.PublishedAt != null))
            .Include(album => album.AlbumArtists).ThenInclude(link => link.Artist)
            .Include(album => album.Artwork)
            .Include(album => album.Tracks.Where(track => track.PublishedAt != null))
                .ThenInclude(track => track.TrackArtists).ThenInclude(link => link.Artist)
            .Include(album => album.Tracks.Where(track => track.PublishedAt != null))
                .ThenInclude(track => track.TrackGenres).ThenInclude(link => link.Genre)
            .Include(album => album.Tracks.Where(track => track.PublishedAt != null))
                .ThenInclude(track => track.Artwork)
            .Include(album => album.Tracks.Where(track => track.PublishedAt != null))
                .ThenInclude(track => track.MediaAssets.Where(asset => asset.IsPublished))
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
            return ApiResults.NotFound(HttpContext);

        var tracks = album.Tracks.OrderBy(track => track.DiscNumber).ThenBy(track => track.TrackNumber).ThenBy(track => track.Title)
            .Select(ToDto).ToArray();
        return Results.Ok(new AlbumDetailDto(
            album.Id,
            album.Title,
            album.ReleaseYear,
            album.AlbumArtists.Select(link => new ArtistSummaryDto(link.Artist.Id, link.Artist.Name)).ToArray(),
            ArtworkUrl(album.ArtworkId),
            tracks));
    }

    [HttpGet("artists", Name = "GetArtists")]
    [Tags("Artists")]
    public async Task<IResult> GetArtists(
        MusicDbContext db,
        string? q,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = db.Artists.AsNoTracking()
            .Where(artist => artist.TrackArtists.Any(link => link.Track.PublishedAt != null));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(artist => EF.Functions.Like(artist.Name, $"%{term}%"));
        }

        var total = await query.CountAsync(cancellationToken);
        var artists = await query.OrderBy(artist => artist.Name).ThenBy(artist => artist.Id)
            .Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = artists.Select(artist => new ArtistSummaryDto(artist.Id, artist.Name)).ToArray();
        return Results.Ok(new PageResult<ArtistSummaryDto>(items, pageNumber, size, total));
    }

    [HttpGet("artists/{artistId:guid}", Name = "GetArtist")]
    [Tags("Artists")]
    public async Task<IResult> GetArtist(MusicDbContext db, Guid artistId, CancellationToken cancellationToken)
    {
        var artist = await db.Artists.AsNoTracking()
            .Where(artist => artist.Id == artistId && artist.TrackArtists.Any(link => link.Track.PublishedAt != null))
            .Include(artist => artist.AlbumArtists).ThenInclude(link => link.Album)
                .ThenInclude(album => album.Tracks.Where(track => track.PublishedAt != null))
            .Include(artist => artist.TrackArtists.Where(link => link.Track.PublishedAt != null)).ThenInclude(link => link.Track)
                .ThenInclude(track => track.TrackArtists).ThenInclude(link => link.Artist)
            .Include(artist => artist.TrackArtists.Where(link => link.Track.PublishedAt != null)).ThenInclude(link => link.Track)
                .ThenInclude(track => track.TrackGenres).ThenInclude(link => link.Genre)
            .Include(artist => artist.TrackArtists.Where(link => link.Track.PublishedAt != null)).ThenInclude(link => link.Track)
                .ThenInclude(track => track.Album)
            .Include(artist => artist.TrackArtists.Where(link => link.Track.PublishedAt != null)).ThenInclude(link => link.Track)
                .ThenInclude(track => track.Artwork)
            .Include(artist => artist.TrackArtists.Where(link => link.Track.PublishedAt != null)).ThenInclude(link => link.Track)
                .ThenInclude(track => track.MediaAssets.Where(asset => asset.IsPublished))
            .FirstOrDefaultAsync(cancellationToken);
        if (artist is null)
            return ApiResults.NotFound(HttpContext);

        var albums = artist.AlbumArtists.Select(link => link.Album)
            .Where(album => album.Tracks.Any(track => track.PublishedAt != null))
            .DistinctBy(album => album.Id)
            .OrderBy(album => album.Title)
            .Select(album => new AlbumSummaryDto(album.Id, album.Title)).ToArray();
        var tracks = artist.TrackArtists.Select(link => link.Track).OrderBy(track => track.Title).Select(ToDto).ToArray();
        return Results.Ok(new ArtistDetailDto(artist.Id, artist.Name, artist.Biography, albums, tracks));
    }

    [HttpGet("genres", Name = "GetGenres")]
    [Tags("Genres")]
    public async Task<IResult> GetGenres(MusicDbContext db, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = db.Genres.AsNoTracking()
            .Where(genre => genre.TrackGenres.Any(link => link.Track.PublishedAt != null))
            .OrderBy(genre => genre.Name)
            .Select(genre => genre.Name);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<string>(items, pageNumber, size, total));
    }

    [HttpGet("collections", Name = "GetCollections")]
    [Tags("Collections")]
    [FeatureGate(nameof(FeatureOptions.EditorialCollections))]
    public async Task<IResult> GetCollections(MusicDbContext db, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        // 注意：曲目数量必须在数据库端投影计算（与 GetHome 的口径一致）。
        // 若先 ToListAsync 物化再 Count 导航属性，未 Include 的 CollectionTracks 会是空集合，计数恒为 0。
        var query = db.EditorialCollections.AsNoTracking()
            .Where(collection => collection.IsPublished && collection.CollectionTracks.Any(link => link.Track.PublishedAt != null))
            .OrderBy(collection => collection.SortOrder).ThenBy(collection => collection.Title);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((pageNumber - 1) * size).Take(size)
            .Select(collection => new CollectionSummaryDto(
                collection.Id,
                collection.Title,
                collection.Description,
                ArtworkUrl(collection.ArtworkId),
                collection.CollectionTracks.Count(link => link.Track.PublishedAt != null)))
            .ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<CollectionSummaryDto>(items, pageNumber, size, total));
    }

    [HttpGet("collections/{collectionId:guid}", Name = "GetCollection")]
    [Tags("Collections")]
    [FeatureGate(nameof(FeatureOptions.EditorialCollections))]
    public async Task<IResult> GetCollection(MusicDbContext db, Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = await db.EditorialCollections.AsNoTracking()
            .Where(collection => collection.Id == collectionId && collection.IsPublished)
            .Include(collection => collection.Artwork)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.TrackArtists).ThenInclude(link => link.Artist)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.TrackGenres).ThenInclude(link => link.Genre)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.Album)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.Artwork)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.MediaAssets.Where(asset => asset.IsPublished))
            .FirstOrDefaultAsync(cancellationToken);
        if (collection is null)
            return ApiResults.NotFound(HttpContext);

        var tracks = collection.CollectionTracks.OrderBy(link => link.SortOrder).Select(link => ToDto(link.Track)).ToArray();
        return Results.Ok(new CollectionDetailDto(collection.Id, collection.Title, collection.Description, ArtworkUrl(collection.ArtworkId), tracks));
    }

    [HttpGet("music/home", Name = "GetMusicHome")]
    [Tags("Music")]
    public async Task<IResult> GetHome(MusicDbContext db, IOptions<FeatureOptions> featureOptions, CancellationToken cancellationToken)
    {
        var features = featureOptions.Value;
        EditorialCollection? featuredCollection = null;
        if (features.EditorialCollections)
            featuredCollection = await db.EditorialCollections.AsNoTracking()
            .Where(collection => collection.IsPublished && collection.CollectionTracks.Any(link => link.Track.PublishedAt != null))
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.TrackArtists).ThenInclude(link => link.Artist)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.TrackGenres).ThenInclude(link => link.Genre)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.Album)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.Artwork)
            .Include(collection => collection.CollectionTracks.Where(link => link.Track.PublishedAt != null))
                .ThenInclude(link => link.Track).ThenInclude(track => track.MediaAssets.Where(asset => asset.IsPublished))
            .OrderBy(collection => collection.SortOrder).ThenBy(collection => collection.Title)
            .FirstOrDefaultAsync(cancellationToken);
        var publishedTracks = PublishedTracks(db);
        var totalTracks = await publishedTracks.CountAsync(cancellationToken);
        var recentTracks = await publishedTracks.OrderByDescending(track => track.PublishedAt)
            .ThenBy(track => track.Title).Take(DefaultPageSize).ToListAsync(cancellationToken);
        IReadOnlyList<CollectionSummaryDto> collectionSummaries = [];
        if (features.EditorialCollections)
        {
            collectionSummaries = await db.EditorialCollections.AsNoTracking()
                .Where(collection => collection.IsPublished && collection.CollectionTracks.Any(link => link.Track.PublishedAt != null))
                .OrderBy(collection => collection.SortOrder).ThenBy(collection => collection.Title).Take(10)
                .Select(collection => new CollectionSummaryDto(
                    collection.Id,
                    collection.Title,
                    collection.Description,
                    ArtworkUrl(collection.ArtworkId),
                    collection.CollectionTracks.Count(link => link.Track.PublishedAt != null)))
                .ToListAsync(cancellationToken);
        }
        var featuredTrack = featuredCollection?.CollectionTracks.OrderBy(link => link.SortOrder)
            .Select(link => ToDto(link.Track)).FirstOrDefault();
        return Results.Ok(new MusicHomeDto(
            featuredTrack,
            collectionSummaries,
            new PageResult<TrackDto>(recentTracks.Select(ToDto).ToArray(), 1, DefaultPageSize, totalTracks)));
    }

    internal static IQueryable<Track> PublishedTracks(MusicDbContext db) => db.Tracks.AsNoTracking()
        .Where(track => track.PublishedAt != null)
        .Include(track => track.TrackArtists).ThenInclude(link => link.Artist)
        .Include(track => track.TrackGenres).ThenInclude(link => link.Genre)
        .Include(track => track.Album)
        .Include(track => track.Artwork)
        .Include(track => track.MediaAssets.Where(asset => asset.IsPublished));

    /// <summary>当前用户可访问（公开 + 本人私有上传）的曲目，附带转为 <see cref="TrackDto"/> 所需的全部导航。</summary>
    internal static IQueryable<Track> AccessibleTracksWithDetails(MusicDbContext db, Guid userId) => db.Tracks.AsNoTracking()
        .Where(track => track.PublishedAt != null || track.OwnerUserId == userId)
        .Include(track => track.TrackArtists).ThenInclude(link => link.Artist)
        .Include(track => track.TrackGenres).ThenInclude(link => link.Genre)
        .Include(track => track.Album)
        .Include(track => track.Artwork)
        .Include(track => track.MediaAssets.Where(asset => asset.IsPublished));

    internal static TrackDto ToDto(Track track)
    {
        var primaryAsset = track.MediaAssets.FirstOrDefault(asset => asset.IsPrimary) ?? track.MediaAssets.FirstOrDefault();
        return new TrackDto(
            track.Id,
            track.Title,
            track.TrackArtists.Select(link => new ArtistSummaryDto(link.Artist.Id, link.Artist.Name)).ToArray(),
            track.Album is null ? null : new AlbumSummaryDto(track.Album.Id, track.Album.Title),
            track.TrackNumber,
            track.DiscNumber,
            track.ReleaseYear,
            track.TrackGenres.Select(link => link.Genre.Name).Order().ToArray(),
            track.DurationMs,
            ArtworkUrl(track.ArtworkId ?? track.Album?.ArtworkId),
            primaryAsset is null ? null : new AudioDto(primaryAsset.Container, primaryAsset.Codec, primaryAsset.BitrateKbps));
    }

    private static CollectionSummaryDto ToDto(EditorialCollection collection) => new(
        collection.Id,
        collection.Title,
        collection.Description,
        ArtworkUrl(collection.ArtworkId),
        collection.CollectionTracks.Count(link => link.Track.PublishedAt != null));

    private static string? ArtworkUrl(Guid? artworkId) => artworkId is null ? null : $"/api/v1/artwork/{artworkId.Value}";

    private static string? GetAudioContentType(string container) => container.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "wav" or "wave" => "audio/wav",
        "aac" or "m4a" or "m4b" or "mp4" => "audio/mp4",
        "ogg" or "oga" => "audio/ogg",
        "wma" => "audio/x-ms-wma",
        "aiff" or "aif" => "audio/aiff",
        "ape" => "audio/ape",
        "wv" => "audio/wavpack",
        "mpc" or "mpp" => "audio/musepack",
        "dsf" => "audio/dsf",
        _ => null
    };

    private static bool TryGetPaging(
        HttpContext ctx,
        int? page,
        int? pageSize,
        out IResult? error,
        out int pageNumber,
        out int size)
    {
        pageNumber = page ?? 1;
        size = pageSize ?? DefaultPageSize;
        error = null;
        if (pageNumber < 1)
            error = ApiResults.ValidationError(ctx, "page", "page must be at least 1.");
        else if (size < 1 || size > MaximumPageSize)
            error = ApiResults.ValidationError(ctx, "pageSize", $"pageSize must be between 1 and {MaximumPageSize}.");
        return error is null;
    }


}