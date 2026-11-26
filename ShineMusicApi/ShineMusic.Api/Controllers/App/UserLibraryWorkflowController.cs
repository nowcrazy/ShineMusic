using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Options;
using ShineMusic.Service;

namespace ShineMusic.Api.Controllers;

/// <summary>Private library upgrade reviews and recycle-bin actions.</summary>
[ApiController]
[Route("api/v1/me/library")]
[Authorize]
[FeatureGate(nameof(FeatureOptions.UserUploads))]
[Tags("My Library")]
public sealed class UserLibraryWorkflowController(UserLibraryWorkflowService workflow) : ControllerBase
{
    [HttpGet("reviews", Name = "GetMyLibraryReviews")]
    public async Task<IResult> GetReviews(string? status, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var result = await workflow.GetReviewsAsync(userId, "private", status,
            Math.Clamp(page ?? 1, 1, 1_000_000), Math.Clamp(pageSize ?? 20, 1, 100), cancellationToken);
        return Results.Ok(result);
    }

    [HttpGet("reviews/{reviewId:guid}", Name = "GetMyLibraryReview")]
    public async Task<IResult> GetReview(Guid reviewId, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var review = await workflow.GetReviewAsync(reviewId, userId, cancellationToken);
        return review is null ? ApiResults.NotFound(HttpContext) : Results.Ok(review);
    }

    [HttpPost("reviews/{reviewId:guid}/resolve", Name = "ResolveMyLibraryReview")]
    public async Task<IResult> ResolveReview(
        Guid reviewId,
        LibraryWorkflowResolveRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        try
        {
            var result = await workflow.ResolveReviewAsync(reviewId, userId, request.Decision, request.Reason, cancellationToken);
            return result is null ? ApiResults.NotFound(HttpContext) : Results.Ok(result);
        }
        catch (LibraryWorkflowConflictException exception)
        {
            return ApiResults.Conflict(HttpContext, exception.Message);
        }
    }

    [HttpGet("recycle-bin", Name = "GetMyLibraryRecycleBin")]
    public async Task<IResult> GetRecycleBin(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        var result = await workflow.GetRecycleBinAsync(userId,
            Math.Clamp(page ?? 1, 1, 1_000_000), Math.Clamp(pageSize ?? 20, 1, 100), cancellationToken);
        return Results.Ok(result);
    }

    [HttpPost("{trackId:guid}/restore", Name = "RestoreMyPrivateTrack")]
    public async Task<IResult> Restore(Guid trackId, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        try
        {
            var restored = await workflow.RestoreAsync(trackId, userId, cancellationToken);
            return restored is not null ? Results.NoContent() : ApiResults.NotFound(HttpContext);
        }
        catch (LibraryWorkflowConflictException exception)
        {
            return ApiResults.Conflict(HttpContext, exception.Message);
        }
    }

    [HttpPost("reviews/{reviewId:guid}/candidate", Name = "SubmitPrivateTrackToPublicReview")]
    public async Task<IResult> SubmitCandidate(Guid reviewId, CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var review = await workflow.GetReviewAsync(reviewId, userId, cancellationToken);
        if (review is null || review.SourceTrackId is null)
            return ApiResults.NotFound(HttpContext);
        try
        {
            var candidate = await workflow.EnqueuePublicReviewAsync(userId, review.SourceTrackId.Value, cancellationToken);
            return candidate is null ? ApiResults.Conflict(HttpContext, "The private track does not meet the current public review quality rules.") : Results.Ok(candidate);
        }
        catch (LibraryWorkflowConflictException exception)
        {
            return ApiResults.Conflict(HttpContext, exception.Message);
        }
    }
}
