using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>Manages the authenticated user's favorites and playlists.</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[FeatureGate(nameof(FeatureOptions.UserCollections))]
[Tags("My Library")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class UserCollectionController : ControllerBase
{
    [HttpGet("favorites", Name = "GetMyFavorites")]
    public async Task<IResult> GetFavorites(
        ISqlSugarClient db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = db.Queryable<Favorite, Track>((favorite, track) => favorite.TrackId == track.Id)
            .Where((favorite, track) => favorite.UserId == userId && track.PublishedAt.HasValue);
        var total = await query.CountAsync(cancellationToken);
        var tracks = await query
            .OrderByDescending((favorite, track) => favorite.CreatedAtUtc)
            .Select((favorite, track) => track)
            .ToOffsetPageAsync(pageNumber, size);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        return Results.Ok(new PageResult<TrackDto>(tracks.Select(CatalogController.ToDto).ToArray(), pageNumber, size, total));
    }

    [HttpPut("favorites/{trackId:guid}", Name = "AddFavorite")]
    public async Task<IResult> AddFavorite(
        Guid trackId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!await CatalogQueries.PublishedTracks(db).AnyAsync(track => track.Id == trackId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        // 幂等写入：已收藏则直接返回，未收藏则落库（并发重复插入由唯一键兜底忽略）。
        if (!await db.Queryable<Favorite>().AnyAsync(favorite => favorite.UserId == userId && favorite.TrackId == trackId, cancellationToken))
        {
            try
            {
                await db.Insertable(new Favorite { UserId = userId, TrackId = trackId, CreatedAtUtc = DateTime.UtcNow })
                    .ExecuteCommandAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // UNIQUE 约束冲突（并发重复收藏）：幂等语义下视为成功。
            }
        }
        return Results.NoContent();
    }

    [HttpDelete("favorites/{trackId:guid}", Name = "RemoveFavorite")]
    public async Task<IResult> RemoveFavorite(
        Guid trackId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        await db.Deleteable<Favorite>().Where(favorite => favorite.UserId == userId && favorite.TrackId == trackId)
            .ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpGet("playlists", Name = "GetMyPlaylists")]
    public async Task<IResult> GetPlaylists(
        ISqlSugarClient db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = db.Queryable<UserPlaylist>()
            .Where(playlist => playlist.UserId == userId);
        var total = await query.CountAsync(cancellationToken);
        var playlists = await query.OrderByDescending(playlist => playlist.UpdatedAtUtc)
            .ToOffsetPageAsync(pageNumber, size);

        // 曲目计数：按本页歌单 Id 批量查「已发布曲目」关联后内存分组（SqlSugar 无导航 Count 子查询等价物）。
        var playlistIds = playlists.Select(playlist => playlist.Id).ToList();
        var countMap = playlistIds.Count == 0
            ? new Dictionary<Guid, int>()
            : (await db.Queryable<UserPlaylistTrack>()
                    .Where(link => playlistIds.Contains(link.PlaylistId))
                    .Where(link => SqlFunc.Subqueryable<Track>()
                        .Where(track => track.Id == link.TrackId && track.PublishedAt.HasValue).Any())
                    .ToListAsync(cancellationToken))
                .GroupBy(link => link.PlaylistId)
                .ToDictionary(group => group.Key, group => group.Count());

        var items = playlists.Select(playlist => new PlaylistSummaryDto(
                playlist.Id,
                playlist.Name,
                playlist.Description,
                countMap.GetValueOrDefault(playlist.Id),
                AsUtc(playlist.UpdatedAtUtc),
                playlist.ArtworkId.HasValue ? $"/api/v1/artwork/{playlist.ArtworkId.Value}" : null))
            .ToList();
        return Results.Ok(new PageResult<PlaylistSummaryDto>(items, pageNumber, size, total));
    }

    [HttpPost("playlists", Name = "CreatePlaylist")]
    public async Task<IResult> CreatePlaylist(
        CreatePlaylistRequest request,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            return ApiResults.ValidationError(HttpContext, "name", "Playlist name is required and must be at most 100 characters.");
        if (request.Description?.Length > 500)
            return ApiResults.ValidationError(HttpContext, "description", "Description must be at most 500 characters.");
        if (request.ArtworkId.HasValue && !await IsPublicArtworkAsync(db, request.ArtworkId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var now = DateTime.UtcNow;
        var playlist = new UserPlaylist
        {
            UserId = userId,
            Name = request.Name.Trim(),
            Description = NormalizeOptional(request.Description),
            ArtworkId = request.ArtworkId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await db.Insertable(playlist).ExecuteCommandAsync(cancellationToken);
        return Results.Created($"/api/v1/me/playlists/{playlist.Id}",
            new PlaylistSummaryDto(playlist.Id, playlist.Name, playlist.Description, 0, AsUtc(now),
                playlist.ArtworkId.HasValue ? $"/api/v1/artwork/{playlist.ArtworkId.Value}" : null));
    }

    [HttpGet("playlists/{playlistId:guid}", Name = "GetPlaylist")]
    public async Task<IResult> GetPlaylist(
        Guid playlistId,
        ISqlSugarClient db,
        ShineMusic.Service.PlaylistImport.PlaylistImportService importService,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlist = await db.Queryable<UserPlaylist>()
            .FirstAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (playlist is null)
            return ApiResults.NotFound(HttpContext);

        // 懒结算：先把下载已发布完成的 pending 行转正为正式曲目，再组装响应。
        await importService.ResolvePendingAsync(playlistId, cancellationToken);

        // 先查关联（含 SortOrder），再按 Id 集查已发布曲目并水合，最后按关联顺序内存排列。
        var links = await db.Queryable<UserPlaylistTrack>()
            .Where(link => link.PlaylistId == playlistId)
            .OrderBy(link => link.SortOrder)
            .OrderBy(link => link.AddedAtUtc)
            .ToListAsync(cancellationToken);
        var trackIds = links.Select(link => link.TrackId).ToList();
        var tracks = trackIds.Count == 0
            ? []
            : await CatalogQueries.PublishedTracks(db)
                .Where(track => trackIds.Contains(track.Id))
                .ToListAsync(cancellationToken);
        await CatalogQueries.HydrateTracksAsync(db, tracks, cancellationToken);
        var trackMap = tracks.ToDictionary(track => track.Id);
        var orderedTracks = links
            .Select(link => trackMap.GetValueOrDefault(link.TrackId))
            .Where(track => track is not null)
            .Select(track => track!)
            .ToList();
        var pendings = await db.Queryable<UserPlaylistPendingTrack>()
            .Where(item => item.PlaylistId == playlistId)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        return Results.Ok(new PlaylistDetailDto(
            playlist.Id,
            playlist.Name,
            playlist.Description,
            orderedTracks.Select(CatalogController.ToDto).ToArray(),
            AsUtc(playlist.UpdatedAtUtc),
            playlist.ArtworkId.HasValue ? $"/api/v1/artwork/{playlist.ArtworkId.Value}" : null,
            pendings.Count == 0
                ? null
                : pendings.Select(item => new PlaylistPendingTrackDto(
                    item.Id,
                    item.Title,
                    DeserializeArtists(item.ArtistsJson),
                    item.AlbumTitle,
                    item.DurationMs,
                    item.CoverUrl,
                    item.SortOrder,
                    item.Status)).ToArray()));
    }

    /// <summary>删除歌单中的一条待补齐（pending）曲目。</summary>
    [HttpDelete("playlists/{playlistId:guid}/pending/{pendingId:guid}", Name = "RemovePlaylistPendingTrack")]
    public async Task<IResult> RemovePendingTrack(
        Guid playlistId,
        Guid pendingId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlistExists = await db.Queryable<UserPlaylist>().AnyAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (!playlistExists)
            return ApiResults.NotFound(HttpContext);
        await db.Deleteable<UserPlaylistPendingTrack>()
            .Where(item => item.Id == pendingId && item.PlaylistId == playlistId)
            .ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>把 JSON 数组字符串解析为字符串列表；解析失败返回空列表。</summary>
    private static IReadOnlyList<string> DeserializeArtists(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    [HttpPatch("playlists/{playlistId:guid}", Name = "UpdatePlaylist")]
    public async Task<IResult> UpdatePlaylist(
        UpdatePlaylistRequest request,
        Guid playlistId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (request.Name is not null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100))
            return ApiResults.ValidationError(HttpContext, "name", "Playlist name must be between 1 and 100 characters.");
        if (request.Description?.Length > 500)
            return ApiResults.ValidationError(HttpContext, "description", "Description must be at most 500 characters.");
        if (request.ArtworkId.HasValue && request.ClearArtwork)
            return ApiResults.ValidationError(HttpContext, "artworkId", "Specify either artworkId or clearArtwork, not both.");
        if (request.ArtworkId.HasValue && !await IsPublicArtworkAsync(db, request.ArtworkId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var playlist = await db.Queryable<UserPlaylist>()
            .FirstAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (playlist is null)
            return ApiResults.NotFound(HttpContext);
        if (request.Name is not null)
            playlist.Name = request.Name.Trim();
        if (request.Description is not null)
            playlist.Description = NormalizeOptional(request.Description);
        if (request.ArtworkId.HasValue)
            playlist.ArtworkId = request.ArtworkId;
        else if (request.ClearArtwork)
            playlist.ArtworkId = null;
        playlist.UpdatedAtUtc = DateTime.UtcNow;
        // 显式列更新：Description/ArtworkId 允许清为 NULL，必须走 UpdateColumns 全量显式列。
        await db.Updateable(playlist)
            .UpdateColumns(item => new { item.Name, item.Description, item.ArtworkId, item.UpdatedAtUtc })
            .ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpDelete("playlists/{playlistId:guid}", Name = "DeletePlaylist")]
    public async Task<IResult> DeletePlaylist(
        Guid playlistId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlist = await db.Queryable<UserPlaylist>()
            .FirstAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (playlist is null)
            return ApiResults.NotFound(HttpContext);
        // SqlSugar 无 EF 级联删除，显式删关联行再删歌单本体（同事务）。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Deleteable<UserPlaylistTrack>().Where(link => link.PlaylistId == playlistId).ExecuteCommandAsync(cancellationToken);
            // Deleteable(实体) 主键内联小写 GUID 会绕过转换器（模式 11），必须用 Where 谓词形式。
            await db.Deleteable<UserPlaylist>().Where(item => item.Id == playlistId).ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.NoContent();
    }

    [HttpPut("playlists/{playlistId:guid}/tracks/{trackId:guid}", Name = "AddPlaylistTrack")]
    public async Task<IResult> AddTrack(
        Guid playlistId,
        Guid trackId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlistExists = await db.Queryable<UserPlaylist>().AnyAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (!playlistExists)
            return ApiResults.NotFound(HttpContext);
        if (!await CatalogQueries.PublishedTracks(db).AnyAsync(track => track.Id == trackId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        if (!await db.Queryable<UserPlaylistTrack>().AnyAsync(link => link.PlaylistId == playlistId && link.TrackId == trackId, cancellationToken))
        {
            var last = await db.Queryable<UserPlaylistTrack>()
                .Where(link => link.PlaylistId == playlistId)
                .OrderByDescending(link => link.SortOrder)
                .FirstAsync(cancellationToken);
            try
            {
                await db.Insertable(new UserPlaylistTrack
                {
                    PlaylistId = playlistId,
                    TrackId = trackId,
                    SortOrder = (last?.SortOrder ?? -1) + 1
                }).ExecuteCommandAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // UNIQUE 约束冲突（并发重复加曲）：幂等语义下视为成功。
            }
        }
        await TouchPlaylistUpdatedAt(db, playlistId, cancellationToken);
        return Results.NoContent();
    }

    [HttpDelete("playlists/{playlistId:guid}/tracks/{trackId:guid}", Name = "RemovePlaylistTrack")]
    public async Task<IResult> RemoveTrack(
        Guid playlistId,
        Guid trackId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlistExists = await db.Queryable<UserPlaylist>().AnyAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (!playlistExists)
            return ApiResults.NotFound(HttpContext);
        await db.Deleteable<UserPlaylistTrack>().Where(link => link.PlaylistId == playlistId && link.TrackId == trackId)
            .ExecuteCommandAsync(cancellationToken);
        await TouchPlaylistUpdatedAt(db, playlistId, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>歌单内容变更后触碰 UpdatedAtUtc（我的歌单列表按此倒序）。</summary>
    private static async Task TouchPlaylistUpdatedAt(ISqlSugarClient db, Guid playlistId, CancellationToken cancellationToken) =>
        await db.Updateable<UserPlaylist>()
            .SetColumns(item => item.UpdatedAtUtc == DateTime.UtcNow)
            .Where(item => item.Id == playlistId)
            .ExecuteCommandAsync(cancellationToken);

    private static bool TryGetPaging(HttpContext ctx, int? page, int? pageSize, out IResult? error, out int pageNumber, out int size)
    {
        pageNumber = page ?? 1;
        size = pageSize ?? 20;
        error = null;
        if (pageNumber < 1)
            error = ApiResults.ValidationError(ctx, "page", "page must be at least 1.");
        else if (size < 1 || size > 100)
            error = ApiResults.ValidationError(ctx, "pageSize", "pageSize must be between 1 and 100.");
        return error is null;
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// 封面可见性校验：仅当被「已发布曲目 / 含已发布曲目的专辑 / 已发布编辑精选集」引用时可见。
    /// 主键与 OR 组拆成链式 Where（模式 12：标量条件与括号 OR 组的合取会被 SqlSugar 丢连接符）。
    /// </summary>
    private static async Task<bool> IsPublicArtworkAsync(ISqlSugarClient db, Guid artworkId, CancellationToken cancellationToken) =>
        await db.Queryable<Artwork>()
            .Where(item => item.Id == artworkId)
            .Where(item =>
                SqlFunc.Subqueryable<Track>().Where(track => track.ArtworkId == item.Id && track.PublishedAt.HasValue).Any() ||
                SqlFunc.Subqueryable<Album>().Where(album => album.ArtworkId == item.Id &&
                    SqlFunc.Subqueryable<Track>().Where(track => track.AlbumId == album.Id && track.PublishedAt.HasValue).Any()).Any() ||
                SqlFunc.Subqueryable<EditorialCollection>().Where(collection => collection.ArtworkId == item.Id && collection.IsPublished).Any())
            .AnyAsync(cancellationToken);

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
