// 职责：歌词（Lyric）的对外查询与管理端维护接口。
// 公开接口供前端播放时按需拉取曲目歌词；管理端接口供管理员为曲目增删改歌词，
// 支持多语言、多格式（纯文本 / LRC 逐行时间轴），并通过 IsDefault 标记默认展示版本。

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>公开歌词查询接口：已发布曲目的歌词列表与默认歌词。</summary>
[ApiController]
[Route("api/v1")]
[Tags("Lyrics")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class LyricController : ControllerBase
{
    [HttpGet("tracks/{trackId:guid}/lyrics", Name = "GetTrackLyrics")]
    public async Task<IResult> GetTrackLyrics(ISqlSugarClient db, Guid trackId, CancellationToken cancellationToken)
    {
        // 已公开曲目任何人可查；私有曲目（用户上传、未公开）仅上传者本人与管理员可查。
        var track = await db.Queryable<Track>()
            .Where(candidate => candidate.Id == trackId)
            .Select(candidate => new { candidate.PublishedAt, candidate.OwnerUserId })
            .FirstAsync(cancellationToken);
        if (track is null || !PrivateTrackAccess.CanAccess(track.PublishedAt, track.OwnerUserId, HttpContext.User))
            return ApiResults.NotFound(HttpContext);

        var lyrics = await db.Queryable<Lyric>()
            .Where(lyric => lyric.TrackId == trackId)
            // 为保持既有的「默认歌词优先 + 创建时间升序」行为一致，
            // 单首歌词数量极少，取到内存后再排序。
            .ToListAsync(cancellationToken);
        var ordered = lyrics
            .OrderByDescending(lyric => lyric.IsDefault)
            .ThenBy(lyric => lyric.CreatedAtUtc)
            .Select(lyric => ToDto(lyric))
            .ToList();
        return Results.Ok(ordered);
    }

    [HttpGet("tracks/{trackId:guid}/lyrics/default", Name = "GetTrackDefaultLyric")]
    public async Task<IResult> GetTrackDefaultLyric(ISqlSugarClient db, Guid trackId, CancellationToken cancellationToken)
    {
        // 已公开曲目任何人可查；私有曲目（用户上传、未公开）仅上传者本人与管理员可查。
        var track = await db.Queryable<Track>()
            .Where(candidate => candidate.Id == trackId)
            .Select(candidate => new { candidate.PublishedAt, candidate.OwnerUserId })
            .FirstAsync(cancellationToken);
        if (track is null || !PrivateTrackAccess.CanAccess(track.PublishedAt, track.OwnerUserId, HttpContext.User))
            return ApiResults.NotFound(HttpContext);

        // 优先取标记为默认的歌词；若没有默认，则取该曲目最早创建的一份作为兜底。
        var allLyrics = await db.Queryable<Lyric>()
            .Where(lyric => lyric.TrackId == trackId)
            .ToListAsync(cancellationToken);
        var lyric = allLyrics
            .OrderByDescending(item => item.IsDefault)
            .ThenBy(item => item.CreatedAtUtc)
            .FirstOrDefault();
        return lyric is null ? ApiResults.NotFound(HttpContext) : Results.Ok(ToDto(lyric));
    }

    internal static LyricDto ToDto(Lyric lyric) => new(
        lyric.Id,
        lyric.TrackId,
        lyric.Language,
        lyric.Format,
        lyric.Content,
        lyric.IsDefault,
        lyric.UpdatedAtUtc);

}

/// <summary>管理端歌词维护接口：为已发布曲目增删改歌词。</summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "admin")]
[Tags("Admin Lyrics")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class LyricManagementController : ControllerBase
{
    [HttpGet("tracks/{trackId:guid}/lyrics", Name = "GetAdminTrackLyrics")]
    public async Task<IResult> GetTrackLyrics(ISqlSugarClient db, Guid trackId, CancellationToken cancellationToken)
    {
        var track = await db.Queryable<Track>().FirstAsync(item => item.Id == trackId, cancellationToken);
        if (track is null)
            return ApiResults.NotFound(HttpContext);

        var lyrics = await db.Queryable<Lyric>()
            .Where(lyric => lyric.TrackId == trackId)
            .OrderByDescending(lyric => lyric.IsDefault)
            .OrderBy(lyric => lyric.Language)
            .ToListAsync(cancellationToken);
        return Results.Ok(lyrics.Select(lyric => LyricController.ToDto(lyric)).ToList());
    }

    [HttpPost("tracks/{trackId:guid}/lyrics", Name = "CreateAdminLyric")]
    public async Task<IResult> CreateLyric(
        Guid trackId,
        CreateLyricRequest request,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        var track = await db.Queryable<Track>().FirstAsync(item => item.Id == trackId, cancellationToken);
        if (track is null)
            return ApiResults.NotFound(HttpContext);

        if (string.IsNullOrWhiteSpace(request.Content))
            return ApiResults.ValidationError(HttpContext, "content", "Content must not be empty.");

        var format = NormalizeFormat(request.Format);
        if (format is null)
            return ApiResults.ValidationError(HttpContext, "format", "Format must be 'plain' or 'lrc'.");

        var lyric = new Lyric
        {
            TrackId = trackId,
            Language = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim(),
            Format = format,
            Content = request.Content,
            IsDefault = request.IsDefault
        };

        await db.Ado.BeginTranAsync();
        try
        {
            // 若新歌词被设为默认，先取消同曲目其他歌词的默认标记，保证同一曲目最多一份默认。
            if (request.IsDefault)
                await db.Updateable<Lyric>()
                    .SetColumns(item => item.IsDefault == false)
                    .Where(item => item.TrackId == trackId && item.IsDefault)
                    .ExecuteCommandAsync(cancellationToken);
            await db.Insertable(lyric).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "lyric.created", "lyric", lyric.Id, new { trackId, lyric.Language, lyric.Format });
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.Ok(LyricController.ToDto(lyric));
    }

    [HttpPut("lyrics/{lyricId:guid}", Name = "UpdateAdminLyric")]
    public async Task<IResult> UpdateLyric(
        Guid lyricId,
        UpdateLyricRequest request,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        var lyric = await db.Queryable<Lyric>().FirstAsync(item => item.Id == lyricId, cancellationToken);
        if (lyric is null)
            return ApiResults.NotFound(HttpContext);

        if (request.Content is not null && string.IsNullOrWhiteSpace(request.Content))
            return ApiResults.ValidationError(HttpContext, "content", "Content must not be empty.");

        if (request.Format is not null)
        {
            var format = NormalizeFormat(request.Format);
            if (format is null)
                return ApiResults.ValidationError(HttpContext, "format", "Format must be 'plain' or 'lrc'.");
            lyric.Format = format;
        }

        if (request.IsDefault == true)
            lyric.IsDefault = true;
        else if (request.IsDefault == false)
            lyric.IsDefault = false;

        if (request.Language is not null)
            lyric.Language = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim();
        if (request.Content is not null)
            lyric.Content = request.Content;

        lyric.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.Ado.BeginTranAsync();
        try
        {
            // 若本次更新把该歌词设为默认，先取消同曲目其他歌词的默认标记。
            if (request.IsDefault == true)
                await db.Updateable<Lyric>()
                    .SetColumns(item => item.IsDefault == false)
                    .Where(item => item.TrackId == lyric.TrackId && item.Id != lyricId && item.IsDefault)
                    .ExecuteCommandAsync(cancellationToken);
            // Language 允许被清空为 NULL，必须显式 UpdateColumns 才会把 null 写入数据库。
            await db.Updateable(lyric)
                .UpdateColumns(item => new { item.Format, item.IsDefault, item.Language, item.Content, item.UpdatedAtUtc })
                .ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "lyric.updated", "lyric", lyric.Id, new { lyric.TrackId, lyric.Language, lyric.Format, lyric.IsDefault });
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.Ok(LyricController.ToDto(lyric));
    }

    [HttpDelete("lyrics/{lyricId:guid}", Name = "DeleteAdminLyric")]
    public async Task<IResult> DeleteLyric(
        Guid lyricId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        var lyric = await db.Queryable<Lyric>().FirstAsync(item => item.Id == lyricId, cancellationToken);
        if (lyric is null)
            return ApiResults.NotFound(HttpContext);

        await db.Ado.BeginTranAsync();
        try
        {
            // 注意：不能用 Deleteable(lyric) 实体形式——其主键条件会内联成小写 GUID 字面量，
            // 绕过 UpperGuidConverter 转换器，与 EF 大写存量不匹配导致静默删 0 行。
            await db.Deleteable<Lyric>().Where(item => item.Id == lyricId).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "lyric.deleted", "lyric", lyricId, new { lyric.TrackId });
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.NoContent();
    }

    /// <summary>规范化歌词格式：接受 plain/lrc（大小写不敏感），非法值返回 null。</summary>
    private static string? NormalizeFormat(string? format) =>
        format?.Trim().ToLowerInvariant() switch
        {
            null or "" => LyricFormats.Plain,
            LyricFormats.Plain => LyricFormats.Plain,
            LyricFormats.Lrc => LyricFormats.Lrc,
            _ => null
        };


}
