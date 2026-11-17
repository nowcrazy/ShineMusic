using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>The authenticated user's saved public editorial collections.</summary>
[ApiController]
[Authorize]
[Route("api/v1/me/saved-collections")]
[FeatureGate(nameof(FeatureOptions.EditorialCollections))]
[Tags("My Library")]
public sealed class SavedCollectionsController(ISqlSugarClient db) : ControllerBase
{
    [HttpGet(Name = "GetMySavedCollections")]
    [ProducesResponseType(typeof(PageResult<CollectionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IResult> GetSavedCollections(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId)) return Results.Unauthorized();
        var pageNumber = page ?? 1;
        var size = pageSize ?? 20;
        if (pageNumber < 1 || (long)(pageNumber - 1) * size > int.MaxValue)
            return ApiResults.ValidationError(HttpContext, "page", "page must be at least 1 and within the supported offset range.");
        if (size < 1 || size > 1000)
            return ApiResults.ValidationError(HttpContext, "pageSize", "pageSize must be between 1 and 1000.");

        var query = db.Queryable<SavedCollection, EditorialCollection>((saved, collection) => saved.CollectionId == collection.Id)
            .Where((saved, collection) => saved.UserId == userId && collection.IsPublished)
            .Where((saved, collection) => SqlFunc.Subqueryable<CollectionTrack>()
                .InnerJoin<Track>((link, track) => link.TrackId == track.Id)
                .Where((link, track) => link.CollectionId == collection.Id && track.PublishedAt.HasValue).Any());
        var total = await query.CountAsync(cancellationToken);
        var collections = await query.OrderByDescending((saved, collection) => saved.CreatedAtUtc)
            .OrderBy((saved, collection) => saved.CollectionId)
            .Select((saved, collection) => collection).ToOffsetPageAsync(pageNumber, size);

        // Same public-track counts and artwork URL as catalog collections, batched for the entire page.
        var ids = collections.Select(collection => collection.Id).ToList();
        var links = ids.Count == 0 ? [] : await db.Queryable<CollectionTrack>()
            .Where(link => ids.Contains(link.CollectionId))
            .Where(link => SqlFunc.Subqueryable<Track>()
                .Where(track => track.Id == link.TrackId && track.PublishedAt.HasValue).Any())
            .ToListAsync(cancellationToken);
        var counts = links.GroupBy(link => link.CollectionId).ToDictionary(group => group.Key, group => group.Count());
        var items = collections.Select(collection => new CollectionSummaryDto(collection.Id, collection.Title,
            collection.Description, collection.ArtworkId.HasValue ? $"/api/v1/artwork/{collection.ArtworkId.Value}" : null,
            counts.GetValueOrDefault(collection.Id))).ToArray();
        return Results.Ok(new PageResult<CollectionSummaryDto>(items, pageNumber, size, total));
    }

    [HttpPut("{collectionId:guid}", Name = "SaveCollection")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IResult> SaveCollection(Guid collectionId, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId)) return Results.Unauthorized();
        cancellationToken.ThrowIfCancellationRequested();
        // PostgreSQL and SQLite support this atomic public-only insert and composite-key conflict target.
        // A concurrent duplicate PUT neither throws nor changes the original saved timestamp.
        var inserted = await db.Ado.ExecuteCommandAsync("""
            INSERT INTO saved_collections (user_id, collection_id, created_at_utc)
            SELECT @userId, c.id, @createdAtUtc FROM editorial_collections c
            WHERE c.id = @collectionId AND c.is_published = true
              AND EXISTS (SELECT 1 FROM collection_tracks ct JOIN tracks t ON t.id = ct.track_id
                          WHERE ct.collection_id = c.id AND t.published_at IS NOT NULL)
            ON CONFLICT (user_id, collection_id) DO NOTHING
            """, new SugarParameter("@userId", userId), new SugarParameter("@collectionId", collectionId),
            new SugarParameter("@createdAtUtc", DateTime.UtcNow));
        if (inserted == 0 && !await PublicCollections().AnyAsync(collection => collection.Id == collectionId, cancellationToken))
            return ApiResults.NotFound(HttpContext);
        return Results.NoContent();
    }

    [HttpDelete("{collectionId:guid}", Name = "UnsaveCollection")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IResult> UnsaveCollection(Guid collectionId, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId)) return Results.Unauthorized();
        await db.Deleteable<SavedCollection>().Where(saved => saved.UserId == userId && saved.CollectionId == collectionId)
            .ExecuteCommandAsync(cancellationToken);
        return Results.NoContent();
    }

    private ISugarQueryable<EditorialCollection> PublicCollections() => db.Queryable<EditorialCollection>()
        .Where(collection => collection.IsPublished)
        .Where(collection => SqlFunc.Subqueryable<CollectionTrack>()
            .InnerJoin<Track>((link, track) => link.TrackId == track.Id)
            .Where((link, track) => link.CollectionId == collection.Id && track.PublishedAt.HasValue).Any());
}
