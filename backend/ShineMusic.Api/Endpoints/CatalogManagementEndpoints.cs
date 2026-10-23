using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>Provides administrator operations for editing published catalog metadata.</summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "admin")]
[Tags("Admin Catalog")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class CatalogManagementController : ControllerBase
{
    [HttpGet("tracks", Name = "GetAdminTracks")]
    public async Task<IResult> GetTracks(
        MusicDbContext db,
        string? q,
        Guid? artistId,
        Guid? albumId,
        bool? missingMetadata,
        bool? duplicates,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var error, out var pageNumber, out var size))
            return error!;

        var query = CatalogController.PublishedTracks(db);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(track => EF.Functions.Like(track.Title, $"%{term}%") ||
                track.TrackArtists.Any(link => EF.Functions.Like(link.Artist.Name, $"%{term}%")) ||
                (track.Album != null && EF.Functions.Like(track.Album.Title, $"%{term}%")));
        }
        if (artistId.HasValue)
            query = query.Where(track => track.TrackArtists.Any(link => link.ArtistId == artistId.Value));
        if (albumId.HasValue)
            query = query.Where(track => track.AlbumId == albumId.Value);
        if (missingMetadata == true)
            query = query.Where(track => track.TrackArtists.Count == 0 || track.AlbumId == null ||
                track.ReleaseYear == null || track.TrackGenres.Count == 0 ||
                (track.ArtworkId == null && (track.Album == null || track.Album.ArtworkId == null)));
        if (duplicates == true)
            query = query.Where(track => db.Tracks.Any(other => other.Id != track.Id && other.PublishedAt != null &&
                other.Title.ToUpper() == track.Title.ToUpper() &&
                other.TrackArtists.Any(otherLink => track.TrackArtists.Any(link => link.ArtistId == otherLink.ArtistId))));

        var total = await query.CountAsync(cancellationToken);
        var tracks = await query.OrderBy(track => track.Title).ThenBy(track => track.Id)
            .Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<TrackDto>(tracks.Select(CatalogController.ToDto).ToArray(), pageNumber, size, total));
    }

    [HttpPatch("tracks/{trackId:guid}", Name = "UpdateAdminTrack")]
    public async Task<IResult> UpdateTrack(
        Guid trackId,
        UpdateAdminTrackRequest update,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (update.Title is not null && (string.IsNullOrWhiteSpace(update.Title) || update.Title.Trim().Length > 300))
            return ApiResults.ValidationError(HttpContext, "title", "Title must be between 1 and 300 characters.");
        if (update.Artists is { Count: > 10 } || update.Artists?.Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200) == true)
            return ApiResults.ValidationError(HttpContext, "artists", "At most 10 non-empty artist names of 200 characters are allowed.");
        if (update.Genres is { Count: > 30 } || update.Genres?.Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100) == true)
            return ApiResults.ValidationError(HttpContext, "genres", "At most 30 non-empty genre names of 100 characters are allowed.");
        if (update.TrackNumber is <= 0 || update.DiscNumber is <= 0 || update.ReleaseYear is < 1800 or > 2200)
            return ApiResults.ValidationError(HttpContext, "metadata", "Track and disc numbers must be positive and release year must be between 1800 and 2200.");

        var track = await db.Tracks.Include(item => item.TrackArtists).Include(item => item.TrackGenres)
            .FirstOrDefaultAsync(item => item.Id == trackId && item.PublishedAt != null, cancellationToken);
        if (track is null)
            return ApiResults.NotFound(HttpContext);
        if (update.AlbumId.HasValue && !await db.Albums.AnyAsync(item => item.Id == update.AlbumId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);
        if (update.ArtworkId.HasValue && !await db.Artworks.AnyAsync(item => item.Id == update.ArtworkId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        if (update.Title is not null) track.Title = update.Title.Trim();
        if (update.AlbumId.HasValue) track.AlbumId = update.AlbumId;
        if (update.TrackNumber.HasValue) track.TrackNumber = update.TrackNumber;
        if (update.DiscNumber.HasValue) track.DiscNumber = update.DiscNumber;
        if (update.ReleaseYear.HasValue) track.ReleaseYear = update.ReleaseYear;
        if (update.ArtworkId.HasValue) track.ArtworkId = update.ArtworkId;
        if (update.Artists is not null)
        {
            db.Set<TrackArtist>().RemoveRange(track.TrackArtists);
            foreach (var name in update.Artists.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var artist = await FindOrCreateArtistAsync(db, name, cancellationToken);
                db.Set<TrackArtist>().Add(new TrackArtist { TrackId = track.Id, ArtistId = artist.Id });
            }
        }
        if (update.Genres is not null)
        {
            db.Set<TrackGenre>().RemoveRange(track.TrackGenres);
            foreach (var name in update.Genres.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var genre = await db.Genres.FirstOrDefaultAsync(item => item.Name.ToUpper() == name.ToUpper(), cancellationToken)
                    ?? new Genre { Name = name };
                if (db.Entry(genre).State == EntityState.Detached)
                    db.Genres.Add(genre);
                db.Set<TrackGenre>().Add(new TrackGenre { TrackId = track.Id, GenreId = genre.Id });
            }
        }

        AdminAudit.Add(HttpContext, db, "track.updated", "track", track.Id, ChangedFields(update));
        await db.SaveChangesAsync(cancellationToken);
        var refreshed = await CatalogController.PublishedTracks(db).FirstAsync(item => item.Id == track.Id, cancellationToken);
        return Results.Ok(CatalogController.ToDto(refreshed));
    }

    [HttpPatch("albums/{albumId:guid}", Name = "UpdateAdminAlbum")]
    public async Task<IResult> UpdateAlbum(
        Guid albumId,
        UpdateAdminAlbumRequest update,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (update.Title is not null && (string.IsNullOrWhiteSpace(update.Title) || update.Title.Trim().Length > 300))
            return ApiResults.ValidationError(HttpContext, "title", "Title must be between 1 and 300 characters.");
        if (update.Artists is { Count: > 10 } || update.Artists?.Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200) == true)
            return ApiResults.ValidationError(HttpContext, "artists", "At most 10 non-empty artist names of 200 characters are allowed.");
        if (update.ReleaseYear is < 1800 or > 2200)
            return ApiResults.ValidationError(HttpContext, "releaseYear", "Release year must be between 1800 and 2200.");

        var album = await db.Albums.Include(item => item.AlbumArtists)
            .FirstOrDefaultAsync(item => item.Id == albumId, cancellationToken);
        if (album is null)
            return ApiResults.NotFound(HttpContext);
        if (update.ArtworkId.HasValue && !await db.Artworks.AnyAsync(item => item.Id == update.ArtworkId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);
        if (update.Title is not null) album.Title = update.Title.Trim();
        if (update.ReleaseYear.HasValue) album.ReleaseYear = update.ReleaseYear;
        if (update.ArtworkId.HasValue) album.ArtworkId = update.ArtworkId;
        if (update.Artists is not null)
        {
            db.Set<AlbumArtist>().RemoveRange(album.AlbumArtists);
            foreach (var name in update.Artists.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var artist = await FindOrCreateArtistAsync(db, name, cancellationToken);
                db.Set<AlbumArtist>().Add(new AlbumArtist { AlbumId = album.Id, ArtistId = artist.Id });
            }
        }
        AdminAudit.Add(HttpContext, db, "album.updated", "album", album.Id, ChangedFields(update));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new AlbumSummaryDto(album.Id, album.Title));
    }

    [HttpPatch("artists/{artistId:guid}", Name = "UpdateAdminArtist")]
    public async Task<IResult> UpdateArtist(
        Guid artistId,
        UpdateAdminArtistRequest update,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (update.Name is not null && (string.IsNullOrWhiteSpace(update.Name) || update.Name.Trim().Length > 200))
            return ApiResults.ValidationError(HttpContext, "name", "Name must be between 1 and 200 characters.");
        if (update.Biography?.Length > 4000)
            return ApiResults.ValidationError(HttpContext, "biography", "Biography must be at most 4000 characters.");
        var artist = await db.Artists.FirstOrDefaultAsync(item => item.Id == artistId, cancellationToken);
        if (artist is null)
            return ApiResults.NotFound(HttpContext);
        if (update.Name is not null) artist.Name = update.Name.Trim();
        if (update.Biography is not null) artist.Biography = string.IsNullOrWhiteSpace(update.Biography) ? null : update.Biography.Trim();
        AdminAudit.Add(HttpContext, db, "artist.updated", "artist", artist.Id, ChangedFields(update));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new ArtistSummaryDto(artist.Id, artist.Name));
    }

    private static async Task<Artist> FindOrCreateArtistAsync(MusicDbContext db, string name, CancellationToken cancellationToken)
    {
        var artist = await db.Artists.FirstOrDefaultAsync(item => item.Name.ToUpper() == name.ToUpper(), cancellationToken);
        if (artist is not null)
            return artist;
        artist = new Artist { Name = name };
        db.Artists.Add(artist);
        return artist;
    }

    private static object ChangedFields(object request) => request.GetType().GetProperties()
        .Where(property => property.GetValue(request) is not null)
        .Select(property => property.Name)
        .ToArray();

    /// <summary>
    /// 批量回填已发布但缺失元数据（流派/专辑/年份/封面）的曲目。
    /// 逐个调 iTunes Search API，只补空字段，不覆盖已有值。
    /// 注意：iTunes 有频率限制（≈20次/分钟），每首间隔 3 秒。
    /// </summary>
    [HttpPost("tracks/backfill-metadata", Name = "BackfillTrackMetadata")]
    public async Task<IResult> BackfillMetadata(
        MusicDbContext db,
        LibraryStoragePaths libraryPaths,
        ArtworkFetcher artworkFetcher,
        CancellationToken cancellationToken)
    {
        // 找出所有已发布、且至少有一项元数据缺失的曲目
        var candidates = await db.Tracks
            .Where(t => t.PublishedAt != null)
            .Include(t => t.TrackArtists).ThenInclude(ta => ta.Artist)
            .Include(t => t.Album)
            .Include(t => t.TrackGenres).ThenInclude(tg => tg.Genre)
            .Where(t =>
                t.TrackGenres.Count == 0
                || t.ReleaseYear == null
                || t.AlbumId == null
                || t.ArtworkId == null && (t.Album == null || t.Album.ArtworkId == null))
            .ToListAsync(cancellationToken);

        var total = candidates.Count;
        var success = 0;
        var skipped = 0;
        var failed = 0;
        var newArtwork = 0;
        var newAlbum = 0;
        var newGenre = 0;

        var artworkDir = libraryPaths.TryResolve("artwork/.placeholder", out var placeholderPath)
            ? System.IO.Path.GetDirectoryName(placeholderPath)!
            : System.IO.Path.Combine(libraryPaths.Root, "artwork");

        foreach (var track in candidates)
        {
            try
            {
                var artist = track.TrackArtists.FirstOrDefault()?.Artist.Name;
                if (string.IsNullOrWhiteSpace(artist))
                {
                    skipped++;
                    continue;
                }

                // iTunes 搜索
                var itunes = await artworkFetcher.FetchAsync(
                    artist,
                    track.Album?.Title,
                    track.Title,
                    "artwork",
                    artworkDir,
                    cancellationToken);

                if (!itunes.Success)
                {
                    failed++;
                    continue;
                }

                var changed = false;

                // 1. 回填流派：原来没有流派 + iTunes 返回了流派 → 映射成中文 mood 标签
                if (track.TrackGenres.Count == 0 && !string.IsNullOrWhiteSpace(itunes.Genre))
                {
                    var mood = GenreMoodMapper.Map(itunes.Genre);
                    if (mood is not null)
                    {
                        var genre = await db.Genres.FirstOrDefaultAsync(g => g.Name.ToUpper() == mood.ToUpper(), cancellationToken)
                            ?? new Genre { Name = mood };
                        if (db.Entry(genre).State == EntityState.Detached)
                            db.Genres.Add(genre);
                        db.Set<TrackGenre>().Add(new TrackGenre { Track = track, Genre = genre });
                        newGenre++;
                        changed = true;
                    }
                }

                // 2. 回填年份
                if (track.ReleaseYear == null && itunes.ReleaseYear.HasValue)
                {
                    track.ReleaseYear = itunes.ReleaseYear.Value;
                    changed = true;
                }

                // 3. 回填专辑：原来没专辑 + iTunes 返回了专辑名
                if (track.AlbumId == null && !string.IsNullOrWhiteSpace(itunes.AlbumTitle))
                {
                    var albumName = itunes.AlbumTitle.Trim();
                    var album = await db.Albums.FirstOrDefaultAsync(a => a.Title.ToUpper() == albumName.ToUpper(), cancellationToken)
                        ?? new Album { Title = albumName, ReleaseYear = track.ReleaseYear };
                    if (db.Entry(album).State == EntityState.Detached)
                        db.Albums.Add(album);
                    track.Album = album;
                    newAlbum++;
                    changed = true;
                }

                // 4. 回填封面：原来没封面 + iTunes 下载成功
                if (track.ArtworkId == null && itunes.StorageKey != null)
                {
                    if (libraryPaths.TryResolve(itunes.StorageKey, out var artworkPath) && System.IO.File.Exists(artworkPath))
                    {
                        var artwork = new Artwork
                        {
                            Id = Guid.NewGuid(),
                            StorageKey = itunes.StorageKey,
                            ContentType = itunes.ContentType ?? "image/jpeg"
                        };
                        db.Artworks.Add(artwork);
                        track.Artwork = artwork;
                        if (track.Album != null && track.Album.ArtworkId == null)
                            track.Album.Artwork = artwork;
                        newArtwork++;
                        changed = true;
                    }
                }

                if (changed)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    success++;
                }
                else
                {
                    skipped++;
                }
            }
            catch
            {
                failed++;
            }

            await Task.Delay(3000, cancellationToken);
        }

        return Results.Ok(new
        {
            Total = total,
            Success = success,
            Skipped = skipped,
            Failed = failed,
            NewArtwork = newArtwork,
            NewAlbum = newAlbum,
            NewGenre = newGenre
        });
    }

    private static bool TryGetPaging(HttpContext ctx, int? page, int? pageSize, out IResult? error, out int pageNumber, out int size)
    {
        pageNumber = page ?? 1;
        size = pageSize ?? 20;
        error = null;
        if (pageNumber < 1)
            error = ApiResults.ValidationError(ctx, "page", "page must be at least 1.");
        else if (size is < 1 or > 100)
            error = ApiResults.ValidationError(ctx, "pageSize", "pageSize must be between 1 and 100.");
        return error is null;
    }


}

public sealed record UpdateAdminTrackRequest(
    string? Title,
    IReadOnlyList<string>? Artists,
    Guid? AlbumId,
    int? TrackNumber,
    int? DiscNumber,
    int? ReleaseYear,
    IReadOnlyList<string>? Genres,
    Guid? ArtworkId);

public sealed record UpdateAdminAlbumRequest(string? Title, IReadOnlyList<string>? Artists, int? ReleaseYear, Guid? ArtworkId);
public sealed record UpdateAdminArtistRequest(string? Name, string? Biography);