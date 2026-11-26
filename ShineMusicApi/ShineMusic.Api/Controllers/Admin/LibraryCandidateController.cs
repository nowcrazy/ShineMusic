using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Application;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Model.Ports;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers.Admin;

/// <summary>Dedicated public-library review queue for opted-in high-quality user uploads.</summary>
[ApiController]
[Route("api/v1/admin/library-candidates")]
[Authorize(Roles = "admin")]
[Tags("Public Library Review")]
public sealed class LibraryCandidateController(UserLibraryWorkflowService workflow) : ControllerBase
{
    [HttpGet("settings", Name = "GetLibraryCandidateSettings")]
    public IResult GetSettings(IOptions<LibraryWorkflowOptions> options)
    {
        var value = options.Value;
        return Results.Ok(new LibraryWorkflowSettingsDto(value.PrivateReviewDays, value.PublicReviewDays,
            value.RecycleDays, value.ReminderHours, value.Mp3MinimumKbps, value.AacLcMinimumKbps,
            value.OpusMinimumKbps, value.VorbisMinimumKbps));
    }

    [HttpGet(Name = "GetLibraryCandidates")]
    public async Task<IResult> GetCandidates(string? status, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var result = await workflow.GetReviewsAsync(null, "public", status,
            Math.Clamp(page ?? 1, 1, 1_000_000), Math.Clamp(pageSize ?? 20, 1, 100), cancellationToken);
        return Results.Ok(result);
    }

    [HttpGet("{reviewId:guid}", Name = "GetLibraryCandidate")]
    public async Task<IResult> GetCandidate(Guid reviewId, CancellationToken cancellationToken)
    {
        var review = await workflow.GetReviewAsync(reviewId, null, cancellationToken);
        return review is null || review.Kind != "public" ? ApiResults.NotFound(HttpContext) : Results.Ok(review);
    }

    [HttpPost("{reviewId:guid}/resolve", Name = "ResolveLibraryCandidate")]
    public async Task<IResult> ResolveCandidate(
        Guid reviewId,
        LibraryWorkflowResolveRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await workflow.ResolveReviewAsync(reviewId, null, request.Decision, request.Reason, cancellationToken);
            return result is null || result.Kind != "public" ? ApiResults.NotFound(HttpContext) : Results.Ok(result);
        }
        catch (LibraryWorkflowConflictException exception)
        {
            return ApiResults.Conflict(HttpContext, exception.Message);
        }
    }

    [HttpGet("{reviewId:guid}/stream", Name = "AuditionLibraryCandidate")]
    public async Task<IResult> Stream(
        Guid reviewId,
        string? format,
        ISqlSugarClient db,
        LibraryStoragePaths libraryPaths,
        ITemporaryPlaybackService temporaryPlayback,
        CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "private, no-store";
        // Use the nominated version's immutable snapshot, not a possibly upgraded private track.
        var review = await db.Queryable<LibraryWorkflowReview>()
            .FirstAsync(candidate => candidate.Id == reviewId && candidate.Kind == "public"
                && candidate.Status == "pending" && candidate.ExpiresAtUtc > DateTime.UtcNow, cancellationToken);
        if (review is null || !libraryPaths.TryResolve(review.OriginalStorageKey, out var path) || !System.IO.File.Exists(path))
            return ApiResults.NotFound(HttpContext);

        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == review.ImportItemId,
            cancellationToken);
        if (item is null || !item.ParticipatesInPublicReview)
            return ApiResults.NotFound(HttpContext);
        var contentType = PlaybackMediaPolicy.TryGetContentType(item.Container) ?? "application/octet-stream";
        return await PlaybackStreamResults.CreateAsync(HttpContext, path, contentType,
            $"review:{review.Id:N}:{review.ContentHash}", temporaryPlayback, review.SourceTrackId ?? review.Id,
            format, "private, no-store", cancellationToken);
    }
}
