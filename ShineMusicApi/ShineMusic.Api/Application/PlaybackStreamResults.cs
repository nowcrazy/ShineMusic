using Microsoft.Net.Http.Headers;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Ports;

namespace ShineMusic.Api.Application;

/// <summary>Serves authorized originals or bounded temporary renditions with the same Range and cache behavior.</summary>
public static class PlaybackStreamResults
{
    /// <summary>The caller must validate resource visibility before invoking this method.</summary>
    public static async Task<IResult> CreateAsync(
        HttpContext context,
        string originalPath,
        string contentType,
        string sourceIdentity,
        ITemporaryPlaybackService temporaryPlayback,
        Guid trackId,
        string? format,
        string cacheControl,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            if (response.StatusCode >= StatusCodes.Status400BadRequest)
                response.Headers.CacheControl = "private, no-store";
            return Task.CompletedTask;
        }, context.Response);
        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format, "aac", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new ApiErrorResponse(new ApiErrorDetail(
                ErrorCodes.ValidationError, "format must be aac when specified.", context.TraceIdentifier)), statusCode: 400);

        try
        {
            if (string.Equals(format, "aac", StringComparison.OrdinalIgnoreCase))
            {
                var lease = await temporaryPlayback.GetAacAsync(trackId, originalPath, sourceIdentity, cancellationToken);
                // ASP.NET disposes the file stream after sending it; the response completion also releases
                // the cache reference, including conditional and Range responses with no body.
                context.Response.RegisterForDisposeAsync(lease);
                context.Response.Headers.CacheControl = cacheControl;
                return Results.File(lease.Content, "audio/mp4", lastModified: lease.LastModifiedUtc,
                    entityTag: new EntityTagHeaderValue(lease.EntityTag), enableRangeProcessing: true);
            }

            var file = new FileInfo(originalPath);
            if (!file.Exists) return Results.NotFound();
            context.Response.Headers.CacheControl = cacheControl;
            return Results.File(originalPath, contentType, lastModified: new DateTimeOffset(file.LastWriteTimeUtc),
                entityTag: new EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\""), enableRangeProcessing: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ShineMusic.Service.MediaProcessingException)
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Json(new ApiErrorResponse(new ApiErrorDetail(
                "temporary_playback_unavailable", "Compatibility playback is temporarily unavailable. Try playing the original audio.", context.TraceIdentifier)),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
