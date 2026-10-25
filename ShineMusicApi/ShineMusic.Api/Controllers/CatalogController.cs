using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

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
        ISqlSugarClient db,
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

        var query = CatalogQueries.PublishedTracks(db);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(track =>
                track.Title.Contains(term) ||
                SqlFunc.Subqueryable<Album>().Where(album => album.Id == track.AlbumId && album.Title.Contains(term)).Any() ||
                SqlFunc.Subqueryable<TrackArtist>().LeftJoin<Artist>((link, artist) => link.ArtistId == artist.Id)
                    .Where((link, artist) => link.TrackId == track.Id && artist.Name.Contains(term)).Any());
        }
        if (artistId.HasValue)
            query = query.Where(track => SqlFunc.Subqueryable<TrackArtist>()
                .Where(link => link.TrackId == track.Id && link.ArtistId == artistId.Value).Any());
        if (albumId.HasValue)
            query = query.Where(track => track.AlbumId == albumId.Value);
        if (!string.IsNullOrWhiteSpace(genre))
        {
            var genreName = genre.Trim();
            query = query.Where(track => SqlFunc.Subqueryable<TrackGenre>()
                .LeftJoin<Genre>((link, g) => link.GenreId == g.Id)
                .Where((link, g) => link.TrackId == track.Id && g.Name == genreName)
                .Any());
        }

        var total = await query.CountAsync(cancellationToken);
        query = sort?.ToLowerInvariant() switch
        {
            // PublishedAt 列为 UTC 固定宽度 TEXT，DB 端字典序与时序一致，可安全排序。
            null or "recent" => query.OrderByDescending(track => track.PublishedAt).OrderBy(track => track.Title),
            "title" => query.OrderBy(track => track.Title).OrderBy(track => track.Id),
            "duration" => query.OrderBy(track => track.DurationMs).OrderBy(track => track.Title),
            _ => null!
        };
        if (query is null)
            return ApiResults.ValidationError(HttpContext, "sort", "sort must be recent, title, or duration.");

        var tracks = await query.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        return Results.Ok(new PageResult<TrackDto>(tracks.Select(ToDto).ToArray(), pageNumber, size, total));
    }

    [HttpGet("tracks/{trackId:guid}", Name = "GetTrack")]
    [Tags("Tracks")]
    public async Task<IResult> GetTrack(ISqlSugarClient db, Guid trackId, CancellationToken cancellationToken)
    {
        Track? track = await CatalogQueries.PublishedTracks(db).FirstAsync(track => track.Id == trackId, cancellationToken);
        if (track is null)
        {
            // 未公开曲目（用户上传的私有曲目）：仅上传者本人与管理员可查看详情。
            var privateTrack = await db.Queryable<Track>()
                .FirstAsync(candidate => candidate.Id == trackId && !candidate.PublishedAt.HasValue, cancellationToken);
            if (privateTrack is null || !PrivateTrackAccess.CanAccess(privateTrack.PublishedAt, privateTrack.OwnerUserId, HttpContext.User))
                return ApiResults.NotFound(HttpContext);
            track = privateTrack;
        }
        await CatalogQueries.HydrateTracksAsync(db, [track], cancellationToken);
        return Results.Ok(ToDto(track));
    }

    [HttpGet("tracks/{trackId:guid}/stream", Name = "StreamTrack")]
    [Tags("Tracks")]
    public async Task<IResult> StreamTrack(
        ISqlSugarClient db,
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
        var track = await db.Queryable<Track>()
            .Where(candidate => candidate.Id == trackId)
            .Select(candidate => new { candidate.PublishedAt, candidate.OwnerUserId })
            .FirstAsync(cancellationToken);
        if (track is null || !PrivateTrackAccess.CanAccess(track.PublishedAt, track.OwnerUserId, HttpContext.User))
            return ApiResults.NotFound(HttpContext);

        var asset = await db.Queryable<MediaAsset>()
            .Where(asset => asset.TrackId == trackId && asset.IsPublished)
            .OrderByDescending(asset => asset.IsPrimary)
            .Select(asset => new { asset.StorageKey, asset.Container })
            .FirstAsync(cancellationToken);
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
        ISqlSugarClient db,
        string? q,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        // 列表只投影 Id+Title，无需水合导航属性。
        var query = db.Queryable<Album>()
            .Where(album => SqlFunc.Subqueryable<Track>().Where(track => track.AlbumId == album.Id && track.PublishedAt.HasValue).Any());
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(album => album.Title.Contains(term) ||
                SqlFunc.Subqueryable<AlbumArtist>().LeftJoin<Artist>((link, artist) => link.ArtistId == artist.Id)
                    .Where((link, artist) => link.AlbumId == album.Id && artist.Name.Contains(term)).Any());
        }

        var total = await query.CountAsync(cancellationToken);
        var albums = await query.OrderBy(album => album.Title).OrderBy(album => album.Id)
            .Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = albums.Select(album => new AlbumSummaryDto(album.Id, album.Title)).ToArray();
        return Results.Ok(new PageResult<AlbumSummaryDto>(items, pageNumber, size, total));
    }

    [HttpGet("albums/{albumId:guid}", Name = "GetAlbum")]
    [Tags("Albums")]
    public async Task<IResult> GetAlbum(ISqlSugarClient db, Guid albumId, CancellationToken cancellationToken)
    {
        var album = await db.Queryable<Album>()
            .Where(album => album.Id == albumId &&
                SqlFunc.Subqueryable<Track>().Where(track => track.AlbumId == album.Id && track.PublishedAt.HasValue).Any())
            .FirstAsync(cancellationToken);
        if (album is null)
            return ApiResults.NotFound(HttpContext);

        // 水合：专辑-艺人关联 + 艺人维度。
        var albumArtists = await db.Queryable<AlbumArtist>().Where(link => link.AlbumId == album.Id).ToListAsync(cancellationToken);
        var artistIds = albumArtists.Select(link => link.ArtistId).Distinct().ToList();
        var artists = artistIds.Count == 0
            ? []
            : await db.Queryable<Artist>().Where(artist => artistIds.Contains(artist.Id)).ToListAsync(cancellationToken);
        var artistMap = artists.ToDictionary(artist => artist.Id);
        foreach (var link in albumArtists)
            if (artistMap.TryGetValue(link.ArtistId, out var linkArtist))
                link.Artist = linkArtist;
        album.AlbumArtists = albumArtists;

        // 水合：专辑内已发布曲目（含完整导航，供 ToDto 映射）。
        var tracks = await db.Queryable<Track>()
            .Where(track => track.AlbumId == album.Id && track.PublishedAt.HasValue)
            .ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        album.Tracks = tracks;

        var trackDtos = album.Tracks.OrderBy(track => track.DiscNumber).ThenBy(track => track.TrackNumber).ThenBy(track => track.Title)
            .Select(ToDto).ToArray();
        return Results.Ok(new AlbumDetailDto(
            album.Id,
            album.Title,
            album.ReleaseYear,
            album.AlbumArtists.Select(link => new ArtistSummaryDto(link.Artist.Id, link.Artist.Name)).ToArray(),
            ArtworkUrl(album.ArtworkId),
            trackDtos));
    }

    [HttpGet("artists", Name = "GetArtists")]
    [Tags("Artists")]
    public async Task<IResult> GetArtists(
        ISqlSugarClient db,
        string? q,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = db.Queryable<Artist>()
            .Where(artist => SqlFunc.Subqueryable<TrackArtist>()
                .LeftJoin<Track>((link, track) => link.TrackId == track.Id)
                .Where((link, track) => link.ArtistId == artist.Id && track.PublishedAt.HasValue)
                .Any());
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(artist => artist.Name.Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var artists = await query.OrderBy(artist => artist.Name).OrderBy(artist => artist.Id)
            .Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = artists.Select(artist => new ArtistSummaryDto(artist.Id, artist.Name)).ToArray();
        return Results.Ok(new PageResult<ArtistSummaryDto>(items, pageNumber, size, total));
    }

    [HttpGet("artists/{artistId:guid}", Name = "GetArtist")]
    [Tags("Artists")]
    public async Task<IResult> GetArtist(ISqlSugarClient db, Guid artistId, CancellationToken cancellationToken)
    {
        var artist = await db.Queryable<Artist>()
            .Where(artist => artist.Id == artistId && SqlFunc.Subqueryable<TrackArtist>()
                .LeftJoin<Track>((link, track) => link.TrackId == track.Id)
                .Where((link, track) => link.ArtistId == artist.Id && track.PublishedAt.HasValue)
                .Any())
            .FirstAsync(cancellationToken);
        if (artist is null)
            return ApiResults.NotFound(HttpContext);

        // 水合：艺人-专辑关联 + 专辑维度；仅保留含已发布曲目的专辑。
        var albumLinks = await db.Queryable<AlbumArtist>().Where(link => link.ArtistId == artist.Id).ToListAsync(cancellationToken);
        var albumIds = albumLinks.Select(link => link.AlbumId).Distinct().ToList();
        var albums = albumIds.Count == 0
            ? []
            : await db.Queryable<Album>().Where(album => albumIds.Contains(album.Id)).ToListAsync(cancellationToken);
        var publishedAlbumIds = albumIds.Count == 0
            ? []
            : (await db.Queryable<Track>()
                .Where(track => track.AlbumId.HasValue && albumIds.Contains(track.AlbumId.Value) && track.PublishedAt.HasValue)
                .Select(track => track.AlbumId)
                .ToListAsync(cancellationToken)).Select(id => id!.Value).ToHashSet();
        var albumMap = albums.ToDictionary(album => album.Id);
        artist.AlbumArtists = albumLinks.Where(link => albumMap.ContainsKey(link.AlbumId)).ToList();
        foreach (var link in artist.AlbumArtists)
            link.Album = albumMap[link.AlbumId];

        // 水合：艺人的已发布曲目（含完整导航，供 ToDto 映射）。
        var trackLinks = await db.Queryable<TrackArtist>().Where(link => link.ArtistId == artist.Id).ToListAsync(cancellationToken);
        var trackIds = trackLinks.Select(link => link.TrackId).Distinct().ToList();
        var tracks = trackIds.Count == 0
            ? []
            : await db.Queryable<Track>().Where(track => trackIds.Contains(track.Id) && track.PublishedAt.HasValue).ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        var trackMap = tracks.ToDictionary(track => track.Id);
        artist.TrackArtists = trackLinks.Where(link => trackMap.ContainsKey(link.TrackId)).ToList();
        foreach (var link in artist.TrackArtists)
            link.Track = trackMap[link.TrackId];

        var albumSummaries = artist.AlbumArtists.Select(link => link.Album)
            .Where(album => publishedAlbumIds.Contains(album.Id))
            .DistinctBy(album => album.Id)
            .OrderBy(album => album.Title)
            .Select(album => new AlbumSummaryDto(album.Id, album.Title)).ToArray();
        var trackDtos = artist.TrackArtists.Select(link => link.Track).OrderBy(track => track.Title).Select(ToDto).ToArray();
        return Results.Ok(new ArtistDetailDto(artist.Id, artist.Name, artist.Biography, albumSummaries, trackDtos));
    }

    [HttpGet("genres", Name = "GetGenres")]
    [Tags("Genres")]
    public async Task<IResult> GetGenres(ISqlSugarClient db, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = db.Queryable<Genre>()
            .Where(genre => SqlFunc.Subqueryable<TrackGenre>()
                .LeftJoin<Track>((link, track) => link.TrackId == track.Id)
                .Where((link, track) => link.GenreId == genre.Id && track.PublishedAt.HasValue)
                .Any())
            .OrderBy(genre => genre.Name)
            .Select(genre => genre.Name);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<string>(items, pageNumber, size, total));
    }

    [HttpGet("collections", Name = "GetCollections")]
    [Tags("Collections")]
    [FeatureGate(nameof(FeatureOptions.EditorialCollections))]
    public async Task<IResult> GetCollections(ISqlSugarClient db, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        // 注意：曲目数量必须按数据库行统计（与 GetHome 的口径一致），不能依赖导航属性 Count（未水合恒为 0）。
        var query = db.Queryable<EditorialCollection>()
            .Where(collection => collection.IsPublished && SqlFunc.Subqueryable<CollectionTrack>()
                .LeftJoin<Track>((link, track) => link.TrackId == track.Id)
                .Where((link, track) => link.CollectionId == collection.Id && track.PublishedAt.HasValue)
                .Any())
            .OrderBy(collection => collection.SortOrder).OrderBy(collection => collection.Title);
        var total = await query.CountAsync(cancellationToken);
        var pageItems = await query.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = await ToCollectionSummariesAsync(db, pageItems, cancellationToken);
        return Results.Ok(new PageResult<CollectionSummaryDto>(items, pageNumber, size, total));
    }

    [HttpGet("collections/{collectionId:guid}", Name = "GetCollection")]
    [Tags("Collections")]
    [FeatureGate(nameof(FeatureOptions.EditorialCollections))]
    public async Task<IResult> GetCollection(ISqlSugarClient db, Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = await db.Queryable<EditorialCollection>()
            .Where(collection => collection.Id == collectionId && collection.IsPublished)
            .FirstAsync(cancellationToken);
        if (collection is null)
            return ApiResults.NotFound(HttpContext);

        // 水合：集合内已发布曲目（保留 SortOrder 顺序，含完整导航）。
        var links = await db.Queryable<CollectionTrack>().Where(link => link.CollectionId == collection.Id).ToListAsync(cancellationToken);
        var trackIds = links.Select(link => link.TrackId).ToList();
        var tracks = trackIds.Count == 0
            ? []
            : await db.Queryable<Track>().Where(track => trackIds.Contains(track.Id) && track.PublishedAt.HasValue).ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        var trackMap = tracks.ToDictionary(track => track.Id);
        collection.CollectionTracks = links.Where(link => trackMap.ContainsKey(link.TrackId)).ToList();
        foreach (var link in collection.CollectionTracks)
            link.Track = trackMap[link.TrackId];

        var trackDtos = collection.CollectionTracks.OrderBy(link => link.SortOrder).Select(link => ToDto(link.Track)).ToArray();
        return Results.Ok(new CollectionDetailDto(collection.Id, collection.Title, collection.Description, ArtworkUrl(collection.ArtworkId), trackDtos));
    }

    /// <summary>首页聚合缓存键（Redis）；首页是匿名高热端点，60s 短 TTL 即可削掉绝大部分重复查询。</summary>
    private const string HomeCacheKey = "catalog:home:v1";

    private static readonly DistributedCacheEntryOptions HomeCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    };

    [HttpGet("music/home", Name = "GetMusicHome")]
    [Tags("Music")]
    public async Task<IResult> GetHome(
        ISqlSugarClient db,
        IOptions<FeatureOptions> featureOptions,
        IDistributedCache cache,
        CancellationToken cancellationToken)
    {
        // 命中缓存直接返回序列化好的 DTO（首页聚合涉及 4~5 次查询，冷启动集中调用收益最大）；
        // 缓存失败（Redis 抖动）不阻断，直接落库计算。
        var cachedPayload = await cache.GetStringAsync(HomeCacheKey, cancellationToken);
        if (cachedPayload is not null)
        {
            var cached = JsonSerializer.Deserialize<MusicHomeDto>(cachedPayload);
            if (cached is not null)
                return Results.Ok(cached);
        }

        var features = featureOptions.Value;
        EditorialCollection? featuredCollection = null;
        if (features.EditorialCollections)
            featuredCollection = await db.Queryable<EditorialCollection>()
                .Where(collection => collection.IsPublished && SqlFunc.Subqueryable<CollectionTrack>()
                    .LeftJoin<Track>((link, track) => link.TrackId == track.Id)
                    .Where((link, track) => link.CollectionId == collection.Id && track.PublishedAt.HasValue)
                    .Any())
                .OrderBy(collection => collection.SortOrder).OrderBy(collection => collection.Title)
                .FirstAsync(cancellationToken);
        var publishedTracks = CatalogQueries.PublishedTracks(db);
        var totalTracks = await publishedTracks.CountAsync(cancellationToken);
        var recentTracks = await publishedTracks.OrderByDescending(track => track.PublishedAt)
            .OrderBy(track => track.Title).Take(DefaultPageSize).ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, recentTracks, cancellationToken);
        IReadOnlyList<CollectionSummaryDto> collectionSummaries = [];
        if (features.EditorialCollections)
        {
            var summaries = await db.Queryable<EditorialCollection>()
                .Where(collection => collection.IsPublished && SqlFunc.Subqueryable<CollectionTrack>()
                    .LeftJoin<Track>((link, track) => link.TrackId == track.Id)
                    .Where((link, track) => link.CollectionId == collection.Id && track.PublishedAt.HasValue)
                    .Any())
                .OrderBy(collection => collection.SortOrder).OrderBy(collection => collection.Title).Take(10)
                .ToListAsync(cancellationToken);
            collectionSummaries = await ToCollectionSummariesAsync(db, summaries, cancellationToken);
        }
        TrackDto? featuredTrack = null;
        if (featuredCollection is not null)
        {
            // 精选集合取排序第一首已发布曲目作为封面曲。
            var links = await db.Queryable<CollectionTrack>().Where(link => link.CollectionId == featuredCollection.Id).ToListAsync(cancellationToken);
            var trackIds = links.Select(link => link.TrackId).ToList();
            var featuredTracks = trackIds.Count == 0
                ? []
                : await db.Queryable<Track>().Where(track => trackIds.Contains(track.Id) && track.PublishedAt.HasValue).ToListAsync(cancellationToken);
            await CatalogQueries.HydrateTracksAsync(db, featuredTracks, cancellationToken);
            var featuredTrackMap = featuredTracks.ToDictionary(track => track.Id);
            featuredTrack = links.OrderBy(link => link.SortOrder)
                .Where(link => featuredTrackMap.ContainsKey(link.TrackId))
                .Select(link => ToDto(featuredTrackMap[link.TrackId]))
                .FirstOrDefault();
        }
        var homeDto = new MusicHomeDto(
            featuredTrack,
            collectionSummaries,
            new PageResult<TrackDto>(recentTracks.Select(ToDto).ToArray(), 1, DefaultPageSize, totalTracks));
        await cache.SetStringAsync(HomeCacheKey, JsonSerializer.Serialize(homeDto), HomeCacheOptions, cancellationToken);
        return Results.Ok(homeDto);
    }

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

    /// <summary>批量构造歌单摘要：按集合 Id 统计各自「已发布曲目」数量（数据库行数口径），再映射 DTO。</summary>
    private static async Task<CollectionSummaryDto[]> ToCollectionSummariesAsync(
        ISqlSugarClient db, List<EditorialCollection> collections, CancellationToken cancellationToken)
    {
        var collectionIds = collections.Select(collection => collection.Id).ToList();
        var links = collectionIds.Count == 0
            ? []
            : await db.Queryable<CollectionTrack>()
                .Where(link => collectionIds.Contains(link.CollectionId) &&
                    SqlFunc.Subqueryable<Track>().Where(track => track.Id == link.TrackId && track.PublishedAt.HasValue).Any())
                .ToListAsync(cancellationToken);
        var countMap = links.GroupBy(link => link.CollectionId).ToDictionary(group => group.Key, group => group.Count());
        return collections
            .Select(collection => new CollectionSummaryDto(
                collection.Id,
                collection.Title,
                collection.Description,
                ArtworkUrl(collection.ArtworkId),
                countMap.GetValueOrDefault(collection.Id)))
            .ToArray();
    }

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
