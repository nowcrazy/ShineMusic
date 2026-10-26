// 职责：外部歌单解析导入 API——POST parse 预览（含曲库匹配标记）、POST import 生成歌单并静默下载缺失曲目。

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Options;
using ShineMusic.Api.Extensions;
using ShineMusic.Service;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Api.Controllers;

/// <summary>外部歌单解析导入（网易云/QQ 音乐/酷我/YouTube Music）。</summary>
[ApiController]
[Route("api/v1/me/playlist-imports")]
[Authorize]
[FeatureGate(nameof(FeatureOptions.UserCollections))]
[Tags("My Library")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class PlaylistImportController : ControllerBase
{
    /// <summary>解析歌单并预览（逐曲标记是否已匹配曲库）。</summary>
    [HttpPost("parse", Name = "ParsePlaylistImport")]
    public async Task<IResult> Parse(
        PlaylistImportParseRequest request,
        PlaylistImportService importService,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out _))
            return Results.Unauthorized();
        try
        {
            var preview = await importService.ParsePreviewAsync(request.Url ?? string.Empty, cancellationToken);
            return Results.Ok(new PlaylistImportParseResponse(
                preview.Platform,
                preview.Name,
                preview.CoverUrl,
                preview.Total,
                preview.Tracks.Select(track => new PlaylistImportPreviewTrackDto(
                    track.Title, track.Artists, track.AlbumTitle, track.DurationMs,
                    track.Matched, track.MatchedTrackId)).ToArray()));
        }
        catch (PlaylistParseException exception)
        {
            return ApiResults.ValidationError(HttpContext, "url", exception.Message);
        }
        catch (SourceUnavailableException exception)
        {
            return ApiResults.ValidationError(HttpContext, "url", exception.Message);
        }
    }

    /// <summary>导入歌单：立即生成新歌单，未匹配曲目后台静默下载补齐。</summary>
    [HttpPost("import", Name = "ImportPlaylist")]
    public async Task<IResult> Import(
        PlaylistImportRequest request,
        PlaylistImportService importService,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        try
        {
            var result = await importService.ImportAsync(request.Url ?? string.Empty, request.Name, userId, cancellationToken);
            return Results.Ok(new PlaylistImportResponse(result.PlaylistId, result.Total, result.Matched, result.Pending));
        }
        catch (PlaylistParseException exception)
        {
            return ApiResults.ValidationError(HttpContext, "url", exception.Message);
        }
        catch (SourceUnavailableException exception)
        {
            return ApiResults.ValidationError(HttpContext, "url", exception.Message);
        }
    }
}

/// <summary>解析预览请求体。</summary>
/// <param name="Url">外部平台歌单链接。</param>
public sealed record PlaylistImportParseRequest(string? Url);

/// <summary>导入请求体。</summary>
/// <param name="Url">外部平台歌单链接。</param>
/// <param name="Name">新歌单名称（留空则用原歌单名）。</param>
public sealed record PlaylistImportRequest(string? Url, string? Name);

/// <summary>解析预览响应中的单条曲目。</summary>
public sealed record PlaylistImportPreviewTrackDto(
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    int? DurationMs,
    bool Matched,
    Guid? MatchedTrackId);

/// <summary>解析预览响应。</summary>
public sealed record PlaylistImportParseResponse(
    string Platform,
    string Name,
    string? CoverUrl,
    int Total,
    IReadOnlyList<PlaylistImportPreviewTrackDto> Tracks);

/// <summary>导入响应。</summary>
/// <param name="PlaylistId">新歌单 Id。</param>
/// <param name="Total">原歌单曲目总数。</param>
/// <param name="Matched">已匹配曲库直接入歌单的数量。</param>
/// <param name="Pending">进入待补齐（静默下载）的数量。</param>
public sealed record PlaylistImportResponse(Guid PlaylistId, int Total, int Matched, int Pending);
