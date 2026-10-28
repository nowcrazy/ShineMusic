// =============================================================================
// 文件：ThemeController.cs
// 端点组：自定义图片主题（User Theme）—— 壁纸上传/读取、主题配置读写与删除。
// 路由前缀：/api/v1/me/theme（与 ProfileController 共用 /me 前缀体系，按子路由区分）。
// 鉴权边界：全部端点 [Authorize]，仅作用于当前登录用户自身（sub 声明），
//           壁纸是用户私人图片，读取同样要求登录（无匿名访问路径）。
// 壁纸落盘：themes/{userId}.{ext}（jpg/png/webp，≤8MB，魔术字节校验），
//           存储审计已排除该目录，详见 Services/UserThemeFiles.cs。
// 数据落库：UserThemeProfile（每用户一条，UserId 即主键），颜色为前端取色的 #RRGGBB。
// 数据访问：1b-④ 起由 SqlSugar 承担（原 EF Core 已移除）。
// =============================================================================
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>用户自定义图片主题：壁纸上传/读取与主题配置管理。所有操作限定当前登录用户自身。</summary>
[ApiController]
[Route("api/v1/me/theme")]
[Authorize]
[Tags("Profile")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
public sealed class ThemeController : ControllerBase
{
    // 壁纸请求体的 multipart 上限：在文件上限基础上留出 multipart 封装开销。
    private const long MultipartBodyLimit = UserThemeFiles.MaxBytes + 64 * 1024;
    // 遮罩不透明度上限：再高壁纸就完全看不见了，失去“壁纸主题”的意义。
    private const float MaxMaskOpacity = 0.85f;
    // #RRGGBB 十六进制颜色校验（大小写均可）。
    private static readonly Regex HexColorRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    // 【GET /api/v1/me/theme】读取当前用户自定义主题配置（未配置时返回全空配置）。
    // 注意：单行查询不传 RequestAborted。客户端（页面刷新/切换）断连时传入取消令牌会让
    // SqlSugar 抛 OperationCanceledException，调试器「抛出即中断」下每次都会弹异常框；
    // 而这条查询本身极快，取消没有收益。
    [HttpGet(Name = "GetMyTheme")]
    public async Task<IResult> GetTheme(
        ISqlSugarClient db,
        LibraryStoragePaths storagePaths)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var profile = await db.Queryable<UserThemeProfile>()
            .FirstAsync(candidate => candidate.UserId == userId);
        return Results.Ok(ToDto(storagePaths, userId, profile));
    }

    // 【POST /api/v1/me/theme/wallpaper】上传/更换壁纸（multipart/form-data，字段名 wallpaper）。
    // 校验：文件非空、≤8MB、魔术字节为 JPEG/PNG/WebP 之一（不信任客户端声明的 MIME）。
    // 行为：先删除旧壁纸文件再写新文件（整体替换）；配置行不存在时创建默认行并启用。
    // 返回：200 更新后的主题配置；401 未登录；413/415 体积或格式不合规。
    [HttpPost("wallpaper", Name = "UploadMyThemeWallpaper")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = MultipartBodyLimit)]
    public async Task<IResult> UploadWallpaper(
        IFormFile? wallpaper,
        ISqlSugarClient db,
        LibraryStoragePaths storagePaths,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        if (wallpaper is null || wallpaper.Length == 0)
            return ApiResults.ValidationError(HttpContext, "wallpaper", "An image file is required.");
        if (wallpaper.Length > UserThemeFiles.MaxBytes)
            return ApiResults.Error(HttpContext, StatusCodes.Status413PayloadTooLarge, ErrorCodes.UploadTooLarge, "Wallpaper image must be at most 8MB.");

        // 整体读入内存（≤8MB）后校验文件头，再落盘；避免流位置重置的兼容问题。
        using var buffer = new MemoryStream();
        await wallpaper.CopyToAsync(buffer, cancellationToken);
        var content = buffer.ToArray();
        var format = DetectImageFormat(content.AsSpan(0, Math.Min(content.Length, 16)));
        if (format is null)
            return ApiResults.Error(HttpContext, StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaFormat, "Only JPEG, PNG and WebP images are accepted.");

        // storageKey 由服务端拼装（themes/{userId}{ext}），userId 为 Guid 不含路径分隔符，无法穿越。
        if (!storagePaths.TryResolve($"{UserThemeFiles.DirectoryKey}{userId}{format.Value.Extension}", out var targetPath))
            return ApiResults.Error(HttpContext, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, "Unable to resolve wallpaper storage path.");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        // 上传即替换：先清掉旧壁纸（任何扩展名），再写新文件。
        UserThemeFiles.DeleteAll(storagePaths, userId);
        await System.IO.File.WriteAllBytesAsync(targetPath, content, cancellationToken);

        // 配置行不存在时创建默认行（颜色待前端取色后经 PUT 回填），并默认启用。
        // 落库不走 RequestAborted：上传中途断连时仍希望保留已写入的主题状态，避免半成品数据。
        var profile = await db.Queryable<UserThemeProfile>()
            .FirstAsync(candidate => candidate.UserId == userId, CancellationToken.None);
        if (profile is null)
        {
            profile = new UserThemeProfile
            {
                UserId = userId,
                WallpaperExtension = format.Value.Extension,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await db.Insertable(profile).ExecuteCommandAsync(CancellationToken.None);
        }
        else
        {
            profile.WallpaperExtension = format.Value.Extension;
            profile.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(profile)
                .UpdateColumns(item => new { item.WallpaperExtension, item.UpdatedAtUtc })
                .ExecuteCommandAsync(CancellationToken.None);
        }

        return Results.Ok(ToDto(storagePaths, userId, profile));
    }

    // 【GET /api/v1/me/theme/wallpaper】读取当前用户壁纸（登录用户本人的私人图片）。
    // 返回：200 图片二进制；404 未上传壁纸或文件缺失。
    // 前端以携带 Authorization 的请求取回二进制后转为 objectURL 注入样式（CSS 背景无法携带请求头）。
    [HttpGet("wallpaper", Name = "GetMyThemeWallpaper")]
    public IResult GetWallpaper(LibraryStoragePaths storagePaths)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var path = UserThemeFiles.FindFilePath(storagePaths, userId);
        if (path is null || !System.IO.File.Exists(path))
            return ApiResults.NotFound(HttpContext);
        // 扩展名决定 Content-Type（文件由本服务的上传端点写入，扩展名与真实格式一致）。
        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
        return Results.File(path, contentType);
    }

    // 【PUT /api/v1/me/theme】更新主题配置（色板、遮罩深浅、启用开关）。
    // 校验：颜色为 #RRGGBB 或 null；遮罩 0-0.85；启用开关要求壁纸已存在。
    // 返回：200 更新后的主题配置；400 字段不合法；401 未登录。
    [HttpPut(Name = "UpdateMyTheme")]
    public async Task<IResult> UpdateTheme(
        UpdateUserThemeRequest request,
        ISqlSugarClient db,
        LibraryStoragePaths storagePaths)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();

        if (!ValidateColor(request.Accent, "accent", out var accentError)) return accentError!;
        if (!ValidateColor(request.Accent2, "accent2", out var accent2Error)) return accent2Error!;
        if (!ValidateColor(request.Accent3, "accent3", out var accent3Error)) return accent3Error!;
        if (!float.IsFinite(request.MaskOpacity) || request.MaskOpacity is < 0f or > MaxMaskOpacity)
            return ApiResults.ValidationError(HttpContext, "maskOpacity", $"Mask opacity must be between 0 and {MaxMaskOpacity}.");

        var profile = await db.Queryable<UserThemeProfile>()
            .FirstAsync(candidate => candidate.UserId == userId);
        if (profile is null)
        {
            // 尚未上传壁纸时也允许先存色板；启用开关留待上传壁纸后再打开。
            if (request.Enabled)
                return ApiResults.ValidationError(HttpContext, "enabled", "A wallpaper must be uploaded before enabling the custom theme.");
            profile = new UserThemeProfile
            {
                UserId = userId,
                Accent = request.Accent,
                Accent2 = request.Accent2,
                Accent3 = request.Accent3,
                MaskOpacity = request.MaskOpacity,
                Enabled = request.Enabled,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await db.Insertable(profile).ExecuteCommandAsync();
        }
        else
        {
            if (request.Enabled && string.IsNullOrEmpty(profile.WallpaperExtension))
                return ApiResults.ValidationError(HttpContext, "enabled", "A wallpaper must be uploaded before enabling the custom theme.");

            profile.Accent = request.Accent;
            profile.Accent2 = request.Accent2;
            profile.Accent3 = request.Accent3;
            profile.MaskOpacity = request.MaskOpacity;
            profile.Enabled = request.Enabled;
            profile.UpdatedAtUtc = DateTime.UtcNow;
            // 显式全列更新：颜色字段允许清为 NULL，必须走 UpdateColumns（默认会跳过 null 值列）。
            await db.Updateable(profile)
                .UpdateColumns(item => new { item.Accent, item.Accent2, item.Accent3, item.MaskOpacity, item.Enabled, item.UpdatedAtUtc })
                .ExecuteCommandAsync();
        }

        return Results.Ok(ToDto(storagePaths, userId, profile));
    }

    // 【DELETE /api/v1/me/theme】删除自定义主题（壁纸文件 + 配置行，幂等：无配置也返回 204）。
    [HttpDelete(Name = "DeleteMyTheme")]
    public async Task<IResult> DeleteTheme(
        ISqlSugarClient db,
        LibraryStoragePaths storagePaths)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        UserThemeFiles.DeleteAll(storagePaths, userId);
        await db.Deleteable<UserThemeProfile>()
            .Where(candidate => candidate.UserId == userId)
            .ExecuteCommandAsync();
        return Results.NoContent();
    }

    // 组装对外 DTO；壁纸 URL 带版本参数（文件最后写入时间）以便换图后刷新缓存。
    private static UserThemeDto ToDto(LibraryStoragePaths storagePaths, Guid userId, UserThemeProfile? profile) =>
        new(
            profile?.Accent,
            profile?.Accent2,
            profile?.Accent3,
            profile?.MaskOpacity ?? 0.45f,
            profile?.Enabled ?? false,
            UserThemeFiles.BuildUrl(storagePaths, userId));

    // 校验可选颜色字段：null 合法（清除槽位）；非 null 时必须为 #RRGGBB。
    private bool ValidateColor(string? value, string field, out IResult? error)
    {
        error = null;
        if (value is null)
            return true;
        if (!HexColorRegex.IsMatch(value))
        {
            error = ApiResults.ValidationError(HttpContext, field, "Colors must be in #RRGGBB format.");
            return false;
        }
        return true;
    }

    // 通过图片文件头魔术字节识别受支持的图片格式（复用头像助手的判定表）。
    private static (string ContentType, string Extension)? DetectImageFormat(ReadOnlySpan<byte> header) =>
        UserAvatarFiles.DetectFormat(header);
}
