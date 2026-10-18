// =============================================================================
// 文件：ArtworkEndpoints.cs
// 端点组：封面图（Artwork）—— 按封面 ID 读取图片文件并以文件流返回。
// 路由前缀：GET /api/v1/artwork（匿名可读，但仅当封面被公开发布的内容引用时才可见）。
// 安全要点：① 可见性校验（已发布曲目/含已发布曲目的专辑/已发布编辑精选集）；
//           ② Content-Type 白名单；③ LibraryStoragePaths.TryResolve 全路径前缀校验，
//           阻止 storageKey 中夹带 ../ 的目录穿越攻击。
// =============================================================================
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;

namespace ShineMusic.Api.Endpoints;

/// <summary>Returns artwork only when it is referenced by publicly visible content. 封面端点：仅当封面被公开展示的内容引用时才返回图片，防止未发布/孤立封面被直接枚举访问。</summary>
[ApiController]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class ArtworkController : ControllerBase
{
    // 允许输出的图片 MIME 白名单；数据库中的 ContentType 不在此列时按 404 处理，避免伪造类型。
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    // 【GET /api/v1/artwork/{artworkId:guid}】读取封面图片（匿名）。
    // 路由参数：artworkId（Guid）；依赖注入：db、HttpContext、LibraryStoragePaths。
    // 返回：200 图片文件流（Results.File 自动支持协商缓存/范围等静态文件语义）；
    //       404 resource_not_found：封面不存在、未被公开内容引用、类型非法、路径越界或磁盘文件缺失。
    [HttpGet("/api/v1/artwork/{artworkId:guid}", Name = "GetArtwork")]
    [Tags("Artwork")]
    public async Task<IResult> GetArtwork(
        MusicDbContext db,
        LibraryStoragePaths storagePaths,
        Guid artworkId,
        CancellationToken cancellationToken)
    {
        // 仅当封面被“已发布曲目 / 含已发布曲目的专辑 / 已发布编辑精选集”引用时才查出，未发布内容引用的封面视同不存在。
        var artwork = await db.Artworks.AsNoTracking()
            .Where(item => item.Id == artworkId && (
                db.Tracks.Any(track => track.ArtworkId == item.Id && track.PublishedAt != null) ||
                db.Albums.Any(album => album.ArtworkId == item.Id && album.Tracks.Any(track => track.PublishedAt != null)) ||
                db.EditorialCollections.Any(collection => collection.ArtworkId == item.Id && collection.IsPublished)))
            .Select(item => new { item.StorageKey, item.ContentType })
            .FirstOrDefaultAsync(cancellationToken);
        // 404：记录不存在，或内容类型不在图片白名单内（统一不报具体原因，避免探测）。
        if (artwork is null || !AllowedContentTypes.Contains(artwork.ContentType))
            return ApiResults.NotFound(HttpContext);

        // 404：storageKey 解析后跳出媒体根目录（目录穿越），或物理文件实际不存在。
        if (!storagePaths.TryResolve(artwork.StorageKey, out var filePath) || !System.IO.File.Exists(filePath))
            return ApiResults.NotFound(HttpContext);

        // 以物理路径流式返回文件，Content-Type 使用数据库中已通过白名单校验的类型。
        return Results.File(filePath, artwork.ContentType);
    }

}
