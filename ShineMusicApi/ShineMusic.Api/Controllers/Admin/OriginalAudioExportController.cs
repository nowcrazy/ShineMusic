using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Application;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using SqlSugar;

namespace ShineMusic.Api.Controllers.Admin;

/// <summary>Exports retained originals of published tracks without changing their bytes or tags.</summary>
[ApiController]
[Route("api/v1/admin/tracks")]
[Authorize(Roles = "admin")]
[Tags("Admin Library Export")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
public sealed class OriginalAudioExportController(ISqlSugarClient db, LibraryStoragePaths libraryPaths) : ControllerBase
{
    [HttpGet("{trackId:guid}/original", Name = "ExportOriginalTrackAudio")]
    public async Task<IResult> ExportOriginal(Guid trackId, CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "private, no-store";
        // Administrator status does not grant access to a user's private uploads.
        var track = await db.Queryable<Track>().FirstAsync(candidate => candidate.Id == trackId
            && candidate.PublishedAt != null && !candidate.IsRecycled, cancellationToken);
        if (track is null) return ApiResults.NotFound(HttpContext);

        return await OriginalAudioExportResults.CreateAsync(HttpContext, track, db, libraryPaths, cancellationToken);
    }
}
