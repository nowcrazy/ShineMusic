// =============================================================================
// 文件：CollectionManagementEndpoints.cs
// 端点组：运营歌单（Editorial Collections）管理 —— 歌单的创建、信息修改、删除与曲目编排。
// 路由前缀：/api/v1/admin/collections（仅 admin 角色可访问，服务端独立鉴权）。
// 数据模型：EditorialCollection（标题/描述/封面/排序/发布态）+ CollectionTrack（曲目与集合内顺序）。
// 业务规则：
// - 歌单曲目仅允许编排「已发布」曲目（PublishedAt != null），私有曲目不会出现在公共歌单中；
// - 封面可显式指定（ArtworkId），未指定时自动取首曲的封面（曲目封面优先，其次专辑封面）；
// - 新歌单排序号自动排在现有歌单之后（max(SortOrder)+1），发布后即出现在前台「发现」页；
// - 曲目替换为整体替换语义（按传入数组顺序重排 SortOrder），非增量增删；
// - 所有写操作随业务变更同事务写入管理员审计日志。
// =============================================================================
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>运营歌单管理端点：歌单 CRUD 与曲目编排（仅管理员）。</summary>
[ApiController]
[Route("api/v1/admin/collections")]
[Authorize(Roles = "admin")]
[Tags("Admin Collections")]
public sealed class CollectionManagementController : ControllerBase
{
    // 单个歌单曲目数量上限（与用户歌单上限保持一致）。
    private const int MaxTracksPerCollection = 200;

    // 【POST /api/v1/admin/collections】创建歌单（标题 + 曲目数组 + 可选封面/描述）。
    // 返回：201 新建歌单摘要；400 标题为空/超长、曲目含未发布或不存在曲目、封面不存在；
    //       401 未登录；403 非管理员。
    [HttpPost(Name = "CreateCollection")]
    public async Task<IResult> Create(
        CreateCollectionRequest request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 100)
            return ValidationError("title", "Title is required and must be at most 100 characters.");
        if (request.Description is { Length: > 500 })
            return ValidationError("description", "Description must be at most 500 characters.");

        var parseResult = ParseTrackIds(request.TrackIds);
        if (parseResult.Error is not null)
            return parseResult.Error;
        var trackIds = parseResult.Value!;
        if (trackIds.Length > MaxTracksPerCollection)
            return ValidationError("trackIds", $"At most {MaxTracksPerCollection} tracks per collection.");
        if (trackIds.Length > 0 && !await AllTracksPublishedAsync(db, trackIds, cancellationToken))
            return ValidationError("trackIds", "All tracks must exist and be published.");

        Guid? artworkId = null;
        if (!string.IsNullOrWhiteSpace(request.ArtworkId))
        {
            var artworkResult = ParseArtworkId(request.ArtworkId);
            if (artworkResult.Error is not null)
                return artworkResult.Error;
            if (!await db.Artworks.AnyAsync(item => item.Id == artworkResult.Value, cancellationToken))
                return ValidationError("artworkId", "The specified artwork does not exist.");
            artworkId = artworkResult.Value;
        }
        else if (trackIds.Length > 0)
        {
            // 未指定封面：自动取首曲封面（曲目自身封面优先，其次所属专辑封面）。
            artworkId = await db.Tracks.AsNoTracking()
                .Where(track => track.Id == trackIds[0])
                .Select(track => track.ArtworkId ?? track.Album!.ArtworkId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // 排序号排在现有歌单之后，保证新歌单出现在列表末尾（SortOrder 越小越靠前）。
        var nextSortOrder = (await db.EditorialCollections.MaxAsync(item => (int?)item.SortOrder, cancellationToken) ?? 0) + 1;
        var collection = new EditorialCollection
        {
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            ArtworkId = artworkId,
            SortOrder = nextSortOrder,
            IsPublished = true,
            CollectionTracks = trackIds.Select((trackId, index) => new CollectionTrack
            {
                TrackId = trackId,
                SortOrder = index
            }).ToList()
        };
        db.EditorialCollections.Add(collection);
        AdminAudit.Add(HttpContext, db, "collection.created", "collection", collection.Id, new
        {
            collection.Title,
            TrackCount = collection.CollectionTracks.Count
        });
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/admin/collections/{collection.Id}",
            ToSummary(collection.Id, collection.Title, collection.Description, collection.ArtworkId, collection.CollectionTracks.Count));
    }

    // 【PUT /api/v1/admin/collections/{collectionId}】修改歌单信息（标题/描述/发布态/封面，均为可选的部分更新）。
    // 返回：200 更新后的歌单摘要；400 字段校验失败；404 歌单不存在。
    [HttpPut("{collectionId:guid}", Name = "UpdateCollection")]
    public async Task<IResult> Update(
        Guid collectionId,
        UpdateCollectionRequest request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        var collection = await db.EditorialCollections
            .FirstOrDefaultAsync(item => item.Id == collectionId, cancellationToken);
        if (collection is null)
            return NotFound(HttpContext);

        if (request.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 100)
                return ValidationError("title", "Title is required and must be at most 100 characters.");
            collection.Title = request.Title.Trim();
        }
        if (request.Description is not null)
        {
            if (request.Description.Length > 500)
                return ValidationError("description", "Description must be at most 500 characters.");
            collection.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        }
        if (request.IsPublished is not null)
            collection.IsPublished = request.IsPublished.Value;
        if (!string.IsNullOrWhiteSpace(request.ArtworkId))
        {
            var artworkResult = ParseArtworkId(request.ArtworkId);
            if (artworkResult.Error is not null)
                return artworkResult.Error;
            if (!await db.Artworks.AnyAsync(item => item.Id == artworkResult.Value, cancellationToken))
                return ValidationError("artworkId", "The specified artwork does not exist.");
            collection.ArtworkId = artworkResult.Value;
        }

        AdminAudit.Add(HttpContext, db, "collection.updated", "collection", collectionId, new
        {
            request.Title,
            request.Description,
            request.IsPublished
        });
        await db.SaveChangesAsync(cancellationToken);

        var trackCount = await db.Set<CollectionTrack>()
            .CountAsync(link => link.CollectionId == collectionId && link.Track.PublishedAt != null, cancellationToken);
        return Results.Ok(ToSummary(collection.Id, collection.Title, collection.Description, collection.ArtworkId, trackCount));
    }

    // 【DELETE /api/v1/admin/collections/{collectionId}】删除歌单（含集合内曲目关联，曲目本体不受影响）。
    // 返回：204 成功；404 歌单不存在。
    [HttpDelete("{collectionId:guid}", Name = "DeleteCollection")]
    public async Task<IResult> Delete(
        Guid collectionId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        var collection = await db.EditorialCollections
            .FirstOrDefaultAsync(item => item.Id == collectionId, cancellationToken);
        if (collection is null)
            return NotFound(HttpContext);

        // CollectionTrack 无级联外键删除行为，先显式删除关联行再删歌单本体。
        await db.Set<CollectionTrack>().Where(link => link.CollectionId == collectionId)
            .ExecuteDeleteAsync(cancellationToken);
        db.EditorialCollections.Remove(collection);
        AdminAudit.Add(HttpContext, db, "collection.deleted", "collection", collectionId, new { collection.Title });
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // 【PUT /api/v1/admin/collections/{collectionId}/tracks】整体替换歌单曲目（按数组顺序重排）。
    // 语义：传入的 trackIds 成为歌单的全部曲目，顺序即 SortOrder；原有关联行全部删除后重建。
    // 返回：200 更新后的歌单摘要；400 曲目含未发布或不存在曲目；404 歌单不存在。
    [HttpPut("{collectionId:guid}/tracks", Name = "ReplaceCollectionTracks")]
    public async Task<IResult> ReplaceTracks(
        Guid collectionId,
        ReplaceCollectionTracksRequest request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        var collection = await db.EditorialCollections
            .FirstOrDefaultAsync(item => item.Id == collectionId, cancellationToken);
        if (collection is null)
            return NotFound(HttpContext);

        var parseResult = ParseTrackIds(request.TrackIds);
        if (parseResult.Error is not null)
            return parseResult.Error;
        var trackIds = parseResult.Value!;
        if (trackIds.Length > MaxTracksPerCollection)
            return ValidationError("trackIds", $"At most {MaxTracksPerCollection} tracks per collection.");
        if (trackIds.Length > 0 && !await AllTracksPublishedAsync(db, trackIds, cancellationToken))
            return ValidationError("trackIds", "All tracks must exist and be published.");

        await db.Set<CollectionTrack>().Where(link => link.CollectionId == collectionId)
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var (trackId, index) in trackIds.Select((trackId, index) => (trackId, index)))
            db.Set<CollectionTrack>().Add(new CollectionTrack { CollectionId = collectionId, TrackId = trackId, SortOrder = index });

        AdminAudit.Add(HttpContext, db, "collection.tracks_replaced", "collection", collectionId, new { TrackCount = trackIds.Length });
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToSummary(collection.Id, collection.Title, collection.Description, collection.ArtworkId, trackIds.Length));
    }

    /// <summary>
    /// 解析曲目 ID 数组：去重 + 校验 Guid 格式；解析失败时返回字段级 400 错误。
    /// </summary>
    private (Guid[]? Value, IResult? Error) ParseTrackIds(string[] rawIds)
    {
        if (rawIds.Length > MaxTracksPerCollection)
            return (null, ValidationError("trackIds", $"At most {MaxTracksPerCollection} tracks per collection."));
        var parsed = new List<Guid>(rawIds.Length);
        foreach (var raw in rawIds)
        {
            if (!Guid.TryParse(raw, out var id))
                return (null, ValidationError("trackIds", $"'{raw}' is not a valid track id."));
            if (!parsed.Contains(id))
                parsed.Add(id);
        }
        return (parsed.ToArray(), null);
    }

    /// <summary>解析封面 ID 字符串；格式非法时返回字段级 400 错误。</summary>
    private (Guid Value, IResult? Error) ParseArtworkId(string rawId) =>
        Guid.TryParse(rawId, out var id)
            ? (id, null)
            : (Guid.Empty, ValidationError("artworkId", $"'{rawId}' is not a valid artwork id."));

    /// <summary>校验给定曲目 ID 是否全部存在且已发布（已发布曲目才可进入公共歌单）。</summary>
    private static async Task<bool> AllTracksPublishedAsync(MusicDbContext db, Guid[] trackIds, CancellationToken cancellationToken) =>
        await db.Tracks.CountAsync(
            track => trackIds.Contains(track.Id) && track.PublishedAt != null, cancellationToken) == trackIds.Length;

    /// <summary>构造歌单摘要 DTO（封面 URL 与前台目录端点保持同一格式）。</summary>
    private static CollectionSummaryDto ToSummary(Guid id, string title, string? description, Guid? artworkId, int trackCount) =>
        new(id, title, description, artworkId is null ? null : $"/api/v1/artwork/{artworkId.Value}", trackCount);

    /// <summary>构造字段级 400 validation_error 响应。</summary>
    private IResult ValidationError(string field, string message) => Results.BadRequest(
        new ApiErrorResponse(new ApiErrorDetail("validation_error", $"{field}: {message}", HttpContext.TraceIdentifier)));

    /// <summary>构造统一的 404 错误响应。</summary>
    private IResult NotFound(HttpContext ctx) => Results.NotFound(
        new ApiErrorResponse(new ApiErrorDetail("resource_not_found", "The requested resource was not found.", ctx.TraceIdentifier)));
}

// 以下为本端点组使用的请求 DTO（由请求体反序列化）。
public sealed record CreateCollectionRequest(string Title, string? Description, string[] TrackIds, string? ArtworkId);  // 创建歌单请求体
public sealed record UpdateCollectionRequest(string? Title, string? Description, bool? IsPublished, string? ArtworkId); // 修改歌单信息请求体（部分更新）
public sealed record ReplaceCollectionTracksRequest(string[] TrackIds);                                                 // 整体替换曲目请求体
