using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

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
        ISqlSugarClient db,
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

        var query = CatalogQueries.PublishedTracks(db);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(track => track.Title.Contains(term) ||
                SqlFunc.Subqueryable<TrackArtist>().LeftJoin<Artist>((link, artist) => link.ArtistId == artist.Id)
                    .Where((link, artist) => link.TrackId == track.Id && artist.Name.Contains(term)).Any() ||
                SqlFunc.Subqueryable<Album>().Where(album => album.Id == track.AlbumId && album.Title.Contains(term)).Any());
        }
        if (artistId.HasValue)
            query = query.Where(track => SqlFunc.Subqueryable<TrackArtist>()
                .Where(link => link.TrackId == track.Id && link.ArtistId == artistId.Value).Any());
        if (albumId.HasValue)
            query = query.Where(track => track.AlbumId == albumId.Value);
        if (missingMetadata == true)
            // ReleaseYear 是 int?（无转换器，== null 可正常翻译 IS NULL）；
            // AlbumId/ArtworkId 是 Guid?（带大写转换器），必须用 !HasValue 才能译为 IS NULL。
            // 注意：SqlSugar 会丢失以 NOT 开头的 && 操作数的连接符（NOT(...) NOT(...) 拼在一起产生语法错误），
            // 必须德摩根改写消灭 &&：!A && !B ≡ !(A || B)，!A && E ≡ !(A || !E)。
            query = query.Where(track =>
                !SqlFunc.Subqueryable<TrackArtist>().Where(link => link.TrackId == track.Id).Any() ||
                !track.AlbumId.HasValue ||
                track.ReleaseYear == null ||
                !SqlFunc.Subqueryable<TrackGenre>().Where(link => link.TrackId == track.Id).Any() ||
                !(track.ArtworkId.HasValue || track.AlbumId.HasValue) ||
                !(track.ArtworkId.HasValue ||
                    !SqlFunc.Subqueryable<Album>().Where(album => album.Id == track.AlbumId && !album.ArtworkId.HasValue).Any()));
        if (duplicates == true)
            // 三层嵌套子查询：与当前曲目同名（大小写不敏感）且至少共享一位艺人的其他已发布曲目存在即视为重复。
            query = query.Where(track => SqlFunc.Subqueryable<Track>()
                .Where(other => other.Id != track.Id && other.PublishedAt.HasValue &&
                    other.Title.ToUpper() == track.Title.ToUpper() &&
                    SqlFunc.Subqueryable<TrackArtist>().Where(otherLink => otherLink.TrackId == other.Id &&
                        SqlFunc.Subqueryable<TrackArtist>().Where(link =>
                            link.TrackId == track.Id && link.ArtistId == otherLink.ArtistId).Any()).Any())
                .Any());

        var total = await query.CountAsync(cancellationToken);
        var tracks = await query.OrderBy(track => track.Title).OrderBy(track => track.Id)
            .Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        return Results.Ok(new PageResult<TrackDto>(tracks.Select(CatalogController.ToDto).ToArray(), pageNumber, size, total));
    }

    [HttpPatch("tracks/{trackId:guid}", Name = "UpdateAdminTrack")]
    public async Task<IResult> UpdateTrack(
        Guid trackId,
        UpdateAdminTrackRequest update,
        ISqlSugarClient db,
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

        var track = await db.Queryable<Track>()
            .FirstAsync(item => item.Id == trackId && item.PublishedAt.HasValue, cancellationToken);
        if (track is null)
            return ApiResults.NotFound(HttpContext);
        if (update.AlbumId.HasValue && !await db.Queryable<Album>().AnyAsync(item => item.Id == update.AlbumId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);
        if (update.ArtworkId.HasValue && !await db.Queryable<Artwork>().AnyAsync(item => item.Id == update.ArtworkId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        if (update.Title is not null) track.Title = update.Title.Trim();
        if (update.AlbumId.HasValue) track.AlbumId = update.AlbumId;
        if (update.TrackNumber.HasValue) track.TrackNumber = update.TrackNumber;
        if (update.DiscNumber.HasValue) track.DiscNumber = update.DiscNumber;
        if (update.ReleaseYear.HasValue) track.ReleaseYear = update.ReleaseYear;
        if (update.ArtworkId.HasValue) track.ArtworkId = update.ArtworkId;

        // 标量更新 + 关联整体重建 + 审计同事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            // 上面所有标量赋值均为非空值（HasValue 守卫），整实体回写不会误清任何列。
            await db.Updateable(track).ExecuteCommandAsync(cancellationToken);

            if (update.Artists is not null)
            {
                await db.Deleteable<TrackArtist>().Where(link => link.TrackId == track.Id).ExecuteCommandAsync(cancellationToken);
                foreach (var name in update.Artists.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var artist = await FindOrCreateArtistAsync(db, name, cancellationToken);
                    await db.Insertable(new TrackArtist { TrackId = track.Id, ArtistId = artist.Id }).ExecuteCommandAsync(cancellationToken);
                }
            }
            if (update.Genres is not null)
            {
                await db.Deleteable<TrackGenre>().Where(link => link.TrackId == track.Id).ExecuteCommandAsync(cancellationToken);
                foreach (var name in update.Genres.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var genre = await db.Queryable<Genre>().FirstAsync(item => item.Name.ToUpper() == name.ToUpper(), cancellationToken);
                    if (genre is null)
                    {
                        genre = new Genre { Name = name };
                        await db.Insertable(genre).ExecuteCommandAsync(cancellationToken);
                    }
                    await db.Insertable(new TrackGenre { TrackId = track.Id, GenreId = genre.Id }).ExecuteCommandAsync(cancellationToken);
                }
            }

            AdminAudit.Add(HttpContext, db, "track.updated", "track", track.Id, ChangedFields(update));
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        var refreshed = await CatalogQueries.PublishedTracks(db).FirstAsync(item => item.Id == track.Id, cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, [refreshed], cancellationToken);
        return Results.Ok(CatalogController.ToDto(refreshed));
    }

    [HttpPatch("albums/{albumId:guid}", Name = "UpdateAdminAlbum")]
    public async Task<IResult> UpdateAlbum(
        Guid albumId,
        UpdateAdminAlbumRequest update,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (update.Title is not null && (string.IsNullOrWhiteSpace(update.Title) || update.Title.Trim().Length > 300))
            return ApiResults.ValidationError(HttpContext, "title", "Title must be between 1 and 300 characters.");
        if (update.Artists is { Count: > 10 } || update.Artists?.Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200) == true)
            return ApiResults.ValidationError(HttpContext, "artists", "At most 10 non-empty artist names of 200 characters are allowed.");
        if (update.ReleaseYear is < 1800 or > 2200)
            return ApiResults.ValidationError(HttpContext, "releaseYear", "Release year must be between 1800 and 2200.");

        var album = await db.Queryable<Album>().FirstAsync(item => item.Id == albumId, cancellationToken);
        if (album is null)
            return ApiResults.NotFound(HttpContext);
        if (update.ArtworkId.HasValue && !await db.Queryable<Artwork>().AnyAsync(item => item.Id == update.ArtworkId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);
        if (update.Title is not null) album.Title = update.Title.Trim();
        if (update.ReleaseYear.HasValue) album.ReleaseYear = update.ReleaseYear;
        if (update.ArtworkId.HasValue) album.ArtworkId = update.ArtworkId;

        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(album).ExecuteCommandAsync(cancellationToken);
            if (update.Artists is not null)
            {
                await db.Deleteable<AlbumArtist>().Where(link => link.AlbumId == album.Id).ExecuteCommandAsync(cancellationToken);
                foreach (var name in update.Artists.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var artist = await FindOrCreateArtistAsync(db, name, cancellationToken);
                    await db.Insertable(new AlbumArtist { AlbumId = album.Id, ArtistId = artist.Id }).ExecuteCommandAsync(cancellationToken);
                }
            }
            AdminAudit.Add(HttpContext, db, "album.updated", "album", album.Id, ChangedFields(update));
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.Ok(new AlbumSummaryDto(album.Id, album.Title));
    }

    [HttpPatch("artists/{artistId:guid}", Name = "UpdateAdminArtist")]
    public async Task<IResult> UpdateArtist(
        Guid artistId,
        UpdateAdminArtistRequest update,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (update.Name is not null && (string.IsNullOrWhiteSpace(update.Name) || update.Name.Trim().Length > 200))
            return ApiResults.ValidationError(HttpContext, "name", "Name must be between 1 and 200 characters.");
        if (update.Biography?.Length > 4000)
            return ApiResults.ValidationError(HttpContext, "biography", "Biography must be at most 4000 characters.");
        var artist = await db.Queryable<Artist>().FirstAsync(item => item.Id == artistId, cancellationToken);
        if (artist is null)
            return ApiResults.NotFound(HttpContext);
        if (update.Name is not null) artist.Name = update.Name.Trim();
        if (update.Biography is not null) artist.Biography = string.IsNullOrWhiteSpace(update.Biography) ? null : update.Biography.Trim();

        await db.Ado.BeginTranAsync();
        try
        {
            // Biography 允许被清空为 NULL，必须显式 UpdateColumns 才会把 null 写入数据库。
            await db.Updateable(artist).UpdateColumns(item => new { item.Name, item.Biography }).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "artist.updated", "artist", artist.Id, ChangedFields(update));
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.Ok(new ArtistSummaryDto(artist.Id, artist.Name));
    }

    private static async Task<Artist> FindOrCreateArtistAsync(ISqlSugarClient db, string name, CancellationToken cancellationToken)
    {
        var artist = await db.Queryable<Artist>().FirstAsync(item => item.Name.ToUpper() == name.ToUpper(), cancellationToken);
        if (artist is not null)
            return artist;
        artist = new Artist { Name = name };
        await db.Insertable(artist).ExecuteCommandAsync(cancellationToken);
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
        ISqlSugarClient db,
        LibraryStoragePaths libraryPaths,
        ArtworkFetcher artworkFetcher,
        CancellationToken cancellationToken)
    {
        // 找出所有已发布、且至少有一项元数据缺失的曲目
        // 注意：同 GetTracks 的 missingMetadata——NOT 开头的 && 操作数会被 SqlSugar 丢连接符，德摩根改写消灭 &&。
        var candidates = await db.Queryable<Track>()
            .Where(t => t.PublishedAt.HasValue)
            .Where(t =>
                !SqlFunc.Subqueryable<TrackGenre>().Where(link => link.TrackId == t.Id).Any()
                || t.ReleaseYear == null
                || !t.AlbumId.HasValue
                || !(t.ArtworkId.HasValue || t.AlbumId.HasValue)
                || !(t.ArtworkId.HasValue ||
                    !SqlFunc.Subqueryable<Album>().Where(album => album.Id == t.AlbumId && !album.ArtworkId.HasValue).Any()))
            .ToListAsync(cancellationToken);
        // 水合艺人/流派/专辑导航（回填逻辑只需这三项，媒体资产用不到）。
        await CatalogQueries.HydrateTracksAsync(db, candidates, cancellationToken);

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

                // 每首曲目的多处写入包成一个事务（保证单曲目元数据变更的原子粒度）。
                await db.Ado.BeginTranAsync();
                try
                {
                    // 1. 回填流派：原来没有流派 + iTunes 返回了流派 → 映射成中文 mood 标签
                    if (track.TrackGenres.Count == 0 && !string.IsNullOrWhiteSpace(itunes.Genre))
                    {
                        var mood = GenreMoodMapper.Map(itunes.Genre);
                        if (mood is not null)
                        {
                            var genre = await db.Queryable<Genre>().FirstAsync(g => g.Name.ToUpper() == mood.ToUpper(), cancellationToken);
                            if (genre is null)
                            {
                                genre = new Genre { Name = mood };
                                await db.Insertable(genre).ExecuteCommandAsync(cancellationToken);
                            }
                            await db.Insertable(new TrackGenre { TrackId = track.Id, GenreId = genre.Id }).ExecuteCommandAsync(cancellationToken);
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
                        var album = await db.Queryable<Album>().FirstAsync(a => a.Title.ToUpper() == albumName.ToUpper(), cancellationToken);
                        if (album is null)
                        {
                            album = new Album { Title = albumName, ReleaseYear = track.ReleaseYear };
                            await db.Insertable(album).ExecuteCommandAsync(cancellationToken);
                        }
                        track.Album = album;
                        track.AlbumId = album.Id;
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
                            await db.Insertable(artwork).ExecuteCommandAsync(cancellationToken);
                            track.Artwork = artwork;
                            track.ArtworkId = artwork.Id;
                            if (track.Album != null && track.Album.ArtworkId == null)
                            {
                                track.Album.ArtworkId = artwork.Id;
                                await db.Updateable<Album>()
                                    .SetColumns(item => item.ArtworkId == artwork.Id)
                                    .Where(item => item.Id == track.Album.Id)
                                    .ExecuteCommandAsync(cancellationToken);
                            }
                            newArtwork++;
                            changed = true;
                        }
                    }

                    if (changed)
                        // ReleaseYear/AlbumId/ArtworkId 允许写 NULL 语义不存在（本流程只赋值非空），显式列回写即可。
                        await db.Updateable(track)
                            .UpdateColumns(item => new { item.ReleaseYear, item.AlbumId, item.ArtworkId })
                            .ExecuteCommandAsync(cancellationToken);
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                if (changed)
                    success++;
                else
                    skipped++;
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
