using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Application;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using SqlSugar;

namespace ShineMusic.Api.Controllers.App;

/// <summary>Exports only the current user's active private original audio.</summary>
[ApiController]
[Route("api/v1/me/library")]
[Authorize]
[Tags("My Library")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
public sealed class UserOriginalAudioExportController(ISqlSugarClient db, LibraryStoragePaths libraryPaths) : ControllerBase
{
    [HttpGet("{trackId:guid}/original", Name = "ExportMyOriginalTrackAudio")]
    public async Task<IResult> ExportOriginal(Guid trackId, CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "private, no-store";
        if (!HttpContext.User.TryGetUserId(out _)) return Results.Unauthorized();

        // The owner's private-access rule has no administrator override. Public
        // tracks (even ones retaining an uploader ID) use a separate admin route.
        var track = await db.Queryable<Track>().FirstAsync(candidate => candidate.Id == trackId
            && candidate.PublishedAt == null && !candidate.IsRecycled, cancellationToken);
        if (track is null || !PrivateTrackAccess.CanAccess(track.PublishedAt, track.OwnerUserId, HttpContext.User))
            return ApiResults.NotFound(HttpContext);

        return await OriginalAudioExportResults.CreateAsync(HttpContext, track, db, libraryPaths, cancellationToken);
    }
}
