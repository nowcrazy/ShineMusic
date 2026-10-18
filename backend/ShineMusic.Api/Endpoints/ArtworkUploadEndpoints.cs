// 职责：管理端上传曲目/专辑封面图（Artwork）。
// 仅管理员可用：接收 multipart 图片，经魔术字节校验后写入曲库 artwork/ 目录，
// 并在 Artworks 表登记一条记录，返回新封面 Id 与读取地址，供后续 PATCH 元数据关联。
// 安全要点：① 仅管理员；② 魔术字节校验（不信任客户端 Content-Type / 扩展名）；
//           ③ LibraryStoragePaths.TryResolve 前缀校验杜绝路径穿越；④ 大小上限。

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>管理端封面图上传接口：接收图片、落盘并登记 Artwork 记录。</summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "admin")]
[Tags("Admin Artwork")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class ArtworkUploadController : ControllerBase
{
    /// <summary>单张封面大小上限：5MB，避免超大图占用存储与带宽。</summary>
    private const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>允许写入的图片 MIME 白名单（与读取端 ArtworkEndpoints 对齐）。</summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    [HttpPost("artwork", Name = "UploadAdminArtwork")]
    public async Task<IResult> UploadArtwork(
        IFormFile file,
        MusicDbContext db,
        LibraryStoragePaths storagePaths,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return ApiResults.ValidationError(HttpContext, "file", "请上传图片文件。");
        if (file.Length > MaxBytes)
            return ApiResults.ValidationError(HttpContext, "file", "图片大小不能超过 5MB。");

        // 读入内存后做魔术字节校验：不信任客户端声明的 Content-Type 与文件名扩展名，
        // 以防伪装扩展名/类型写入非图片二进制。
        using var memory = new MemoryStream();
        await file.CopyToAsync(memory, cancellationToken);
        var bytes = memory.GetBuffer().AsSpan(0, (int)memory.Length);
        var detected = DetectFormat(bytes);
        if (detected is null)
            return ApiResults.ValidationError(HttpContext, "file", "仅支持 JPEG / PNG / WEBP / GIF 图片。");

        var artworkId = Guid.NewGuid();
        var storageKey = $"artwork/{artworkId:N}{detected.Extension}";
        if (!storagePaths.TryResolve(storageKey, out var filePath))
            return ApiResults.Error(HttpContext, 500, ErrorCodes.StorageError, "无法写入封面文件。");

        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        await System.IO.File.WriteAllBytesAsync(filePath, memory.ToArray(), cancellationToken);

        var artwork = new Artwork { Id = artworkId, StorageKey = storageKey, ContentType = detected.ContentType };
        db.Artworks.Add(artwork);
        AdminAudit.Add(HttpContext, db, "artwork.uploaded", "artwork", artworkId, new { detected.ContentType });
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ArtworkUploadResultDto(artworkId, $"/api/v1/artwork/{artworkId}"));
    }

    /// <summary>按文件头（魔术字节）判定图片格式；非法或非图片返回 null。</summary>
    private sealed record DetectedFormat(string Extension, string ContentType);

    private static DetectedFormat? DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return new DetectedFormat(".jpg", "image/jpeg");
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return new DetectedFormat(".png", "image/png");
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return new DetectedFormat(".webp", "image/webp");
        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 &&
            (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
            return new DetectedFormat(".gif", "image/gif");
        return null;
    }


}
