using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

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
        MusicDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = from favorite in db.Favorites.AsNoTracking()
                    join track in CatalogController.PublishedTracks(db) on favorite.TrackId equals track.Id
                    where favorite.UserId == userId
                    orderby favorite.CreatedAtUtc descending
                    select track;
        var total = await query.CountAsync(cancellationToken);
        var tracks = await query.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<TrackDto>(tracks.Select(CatalogController.ToDto).ToArray(), pageNumber, size, total));
    }

    [HttpPut("favorites/{trackId:guid}", Name = "AddFavorite")]
    public async Task<IResult> AddFavorite(
        Guid trackId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!await CatalogController.PublishedTracks(db).AnyAsync(track => track.Id == trackId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        // 幂等写入：已收藏则直接返回，未收藏则落库（并发重复插入由唯一键兜底忽略）。
        if (!await db.Favorites.AnyAsync(favorite => favorite.UserId == userId && favorite.TrackId == trackId, cancellationToken))
        {
            db.Favorites.Add(new Favorite { UserId = userId, TrackId = trackId, CreatedAtUtc = DateTime.UtcNow });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
        return Results.NoContent();
    }

    [HttpDelete("favorites/{trackId:guid}", Name = "RemoveFavorite")]
    public async Task<IResult> RemoveFavorite(
        Guid trackId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        await db.Favorites.Where(favorite => favorite.UserId == userId && favorite.TrackId == trackId)
            .ExecuteDeleteAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpGet("playlists", Name = "GetMyPlaylists")]
    public async Task<IResult> GetPlaylists(
        MusicDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!;

        var query = db.UserPlaylists.AsNoTracking()
            .Where(playlist => playlist.UserId == userId)
            .OrderByDescending(playlist => playlist.UpdatedAtUtc);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((pageNumber - 1) * size).Take(size)
            .Select(playlist => new PlaylistSummaryDto(
                playlist.Id,
                playlist.Name,
                playlist.Description,
                playlist.Tracks.Count(link => db.Tracks.Any(track => track.Id == link.TrackId && track.PublishedAt != null)),
                AsUtc(playlist.UpdatedAtUtc),
                playlist.ArtworkId == null ? null : $"/api/v1/artwork/{playlist.ArtworkId.Value}"))
            .ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<PlaylistSummaryDto>(items, pageNumber, size, total));
    }

    [HttpPost("playlists", Name = "CreatePlaylist")]
    public async Task<IResult> CreatePlaylist(
        CreatePlaylistRequest request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            return ApiResults.ValidationError(HttpContext, "name", "Playlist name is required and must be at most 100 characters.");
        if (request.Description?.Length > 500)
            return ApiResults.ValidationError(HttpContext, "description", "Description must be at most 500 characters.");
        if (request.ArtworkId.HasValue && !await PublicArtworks(db).AnyAsync(item => item.Id == request.ArtworkId.Value, cancellationToken))
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
        db.UserPlaylists.Add(playlist);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/me/playlists/{playlist.Id}",
            new PlaylistSummaryDto(playlist.Id, playlist.Name, playlist.Description, 0, AsUtc(now),
                playlist.ArtworkId == null ? null : $"/api/v1/artwork/{playlist.ArtworkId.Value}"));
    }

    [HttpGet("playlists/{playlistId:guid}", Name = "GetPlaylist")]
    public async Task<IResult> GetPlaylist(
        Guid playlistId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlist = await db.UserPlaylists.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (playlist is null)
            return ApiResults.NotFound(HttpContext);

        var tracks = await (from link in db.UserPlaylistTracks.AsNoTracking()
                            join track in CatalogController.PublishedTracks(db) on link.TrackId equals track.Id
                            where link.PlaylistId == playlistId
                            orderby link.SortOrder, link.AddedAtUtc
                            select track)
            .ToListAsync(cancellationToken);
        return Results.Ok(new PlaylistDetailDto(
            playlist.Id,
            playlist.Name,
            playlist.Description,
            tracks.Select(CatalogController.ToDto).ToArray(),
            AsUtc(playlist.UpdatedAtUtc),
            playlist.ArtworkId == null ? null : $"/api/v1/artwork/{playlist.ArtworkId.Value}"));
    }

    [HttpPatch("playlists/{playlistId:guid}", Name = "UpdatePlaylist")]
    public async Task<IResult> UpdatePlaylist(
        UpdatePlaylistRequest request,
        Guid playlistId,
        MusicDbContext db,
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
        if (request.ArtworkId.HasValue && !await PublicArtworks(db).AnyAsync(item => item.Id == request.ArtworkId.Value, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        var playlist = await db.UserPlaylists.FirstOrDefaultAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpDelete("playlists/{playlistId:guid}", Name = "DeletePlaylist")]
    public async Task<IResult> DeletePlaylist(
        Guid playlistId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlist = await db.UserPlaylists.FirstOrDefaultAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (playlist is null)
            return ApiResults.NotFound(HttpContext);
        db.UserPlaylists.Remove(playlist);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    [HttpPut("playlists/{playlistId:guid}/tracks/{trackId:guid}", Name = "AddPlaylistTrack")]
    public async Task<IResult> AddTrack(
        Guid playlistId,
        Guid trackId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlistExists = await db.UserPlaylists.AnyAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (!playlistExists)
            return ApiResults.NotFound(HttpContext);
        if (!await CatalogController.PublishedTracks(db).AnyAsync(track => track.Id == trackId, cancellationToken))
            return ApiResults.NotFound(HttpContext);

        if (!await db.UserPlaylistTracks.AnyAsync(link => link.PlaylistId == playlistId && link.TrackId == trackId, cancellationToken))
        {
            var sortOrder = await db.UserPlaylistTracks.Where(link => link.PlaylistId == playlistId)
                .Select(link => (int?)link.SortOrder).MaxAsync(cancellationToken) ?? -1;
            db.UserPlaylistTracks.Add(new UserPlaylistTrack
            {
                PlaylistId = playlistId,
                TrackId = trackId,
                SortOrder = sortOrder + 1
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
        await db.UserPlaylists.Where(item => item.Id == playlistId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, DateTime.UtcNow), cancellationToken);
        return Results.NoContent();
    }

    [HttpDelete("playlists/{playlistId:guid}/tracks/{trackId:guid}", Name = "RemovePlaylistTrack")]
    public async Task<IResult> RemoveTrack(
        Guid playlistId,
        Guid trackId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var playlistExists = await db.UserPlaylists.AnyAsync(item => item.Id == playlistId && item.UserId == userId, cancellationToken);
        if (!playlistExists)
            return ApiResults.NotFound(HttpContext);
        await db.UserPlaylistTracks.Where(link => link.PlaylistId == playlistId && link.TrackId == trackId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.UserPlaylists.Where(item => item.Id == playlistId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAtUtc, DateTime.UtcNow), cancellationToken);
        return Results.NoContent();
    }

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
    private static IQueryable<Artwork> PublicArtworks(MusicDbContext db) => db.Artworks.AsNoTracking()
        .Where(artwork => db.Tracks.Any(track => track.ArtworkId == artwork.Id && track.PublishedAt != null) ||
            db.Albums.Any(album => album.ArtworkId == artwork.Id && album.Tracks.Any(track => track.PublishedAt != null)) ||
            db.EditorialCollections.Any(collection => collection.ArtworkId == artwork.Id && collection.IsPublished));
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}