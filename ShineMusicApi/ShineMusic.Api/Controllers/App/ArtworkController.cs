// =============================================================================
// 文件：ArtworkController.cs
// 端点组：封面图（Artwork）—— 按封面 ID 读取图片文件并以文件流返回。
// 路由前缀：GET /api/v1/artwork（匿名可读，但仅当封面被公开发布的内容引用时才可见）。
// 架构：可见性、MIME 白名单、路径穿越校验已全部下沉到 Application 层的
//       ArtworkReadUseCase（端口 IArtworkVisibilityStore）。本控制器只做 HTTP 映射。
// =============================================================================
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Service.Application;

namespace ShineMusic.Api.Controllers;

/// <summary>Returns artwork only when it is referenced by publicly visible content. 封面端点：仅当封面被公开展示的内容引用时才返回图片，防止未发布/孤立封面被直接枚举访问。</summary>
[ApiController]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class ArtworkController : ControllerBase
{
    // 【GET /api/v1/artwork/{artworkId:guid}】读取封面图片（匿名）。
    // 返回：200 图片文件流（Results.File 自动支持协商缓存/范围等静态文件语义）；
    //       404 resource_not_found：封面不存在、未被公开内容引用、类型非法、路径越界或磁盘文件缺失。
    [HttpGet("/api/v1/artwork/{artworkId:guid}", Name = "GetArtwork")]
    [Tags("Artwork")]
    public async Task<IResult> GetArtwork(
        ArtworkReadUseCase useCase,
        Guid artworkId,
        CancellationToken cancellationToken)
    {
        var artwork = await useCase.ResolveAsync(artworkId, cancellationToken);
        return artwork is null
            ? ApiResults.NotFound(HttpContext)
            : Results.File(artwork.FilePath, artwork.ContentType);
    }
}
