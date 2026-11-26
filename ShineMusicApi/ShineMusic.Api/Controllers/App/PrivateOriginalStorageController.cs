using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Service;

namespace ShineMusic.Api.Controllers;

/// <summary>Read the authenticated owner's retained-original usage without exposing storage paths.</summary>
[ApiController]
[Route("api/v1/me/library/storage")]
[Authorize]
[Tags("My Library")]
public sealed class PrivateOriginalStorageController(UserLibraryWorkflowService workflow) : ControllerBase
{
    [HttpGet(Name = "GetMyOriginalStorage")]
    public async Task<IResult> Get(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "private, no-store";
        if (!HttpContext.User.TryGetUserId(out var owner)) return Results.Unauthorized();
        try { return Results.Ok(await workflow.GetPrivateOriginalStorageAsync(owner, cancellationToken)); }
        catch (MediaProcessingException exception) when (exception.Code == ErrorCodes.PrivateStorageUsageUnavailable)
        { return ApiResults.Error(HttpContext, StatusCodes.Status503ServiceUnavailable, exception.Code, ImportFormatting.SafeErrorMessage(exception.Code)); }
    }
}
