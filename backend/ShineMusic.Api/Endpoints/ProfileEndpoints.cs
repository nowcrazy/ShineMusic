// =============================================================================
// 文件：ProfileEndpoints.cs
// 端点组：个人资料（Profile）—— 昵称修改、头像上传/读取/删除、登录密码修改。
// 路由前缀：/api/v1/me（与 UserUploadController 共用前缀，按子路由区分，互不冲突）。
// 鉴权边界：全部端点 [Authorize]，且仅作用于当前登录用户（sub 声明），
//           不存在越权修改他人资料的路径。
// 头像落盘：avatars/{userId}.{ext}（jpg/png/webp，≤2MB，魔术字节校验），
//           存储审计已排除该目录，详见 Services/UserAvatarFiles.cs。
// =============================================================================
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>个人资料管理：昵称、头像与密码。所有操作限定当前登录用户自身。</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[Tags("Profile")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class ProfileController : ControllerBase
{
    // 头像请求体的 multipart 上限：在文件上限基础上留出 multipart 封装开销。
    private const long MultipartBodyLimit = UserAvatarFiles.MaxBytes + 64 * 1024;

    // 【PATCH /api/v1/me】修改展示昵称（1-100 字符）。
    // 返回：200 更新后的用户资料（含头像 URL）；400 昵称为空/超长或 Identity 校验失败；401 未登录。
    [HttpPatch(Name = "UpdateMyProfile")]
    public async Task<IResult> UpdateProfile(
        UpdateDisplayNameRequest request,
        UserManager<ApplicationUser> userManager,
        LibraryStoragePaths storagePaths,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 100)
            return ApiResults.ValidationError(HttpContext, "displayName", "Display name is required and must be at most 100 characters.");

        user.DisplayName = request.DisplayName.Trim();
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return ApiResults.ValidationError(HttpContext, "displayName", string.Join(" ", result.Errors.Select(error => error.Description)));

        var roles = await userManager.GetRolesAsync(user);
        return Results.Ok(new CurrentUserDto(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            roles.ToList(),
            UserAvatarFiles.BuildUrl(storagePaths, userId)));
    }

    // 【POST /api/v1/me/avatar】上传/更换头像（multipart/form-data，字段名 avatar）。
    // 校验：文件非空、≤2MB、魔术字节为 JPEG/PNG/WebP 之一（不信任客户端声明的 MIME）。
    // 行为：先删除该用户旧头像文件，再写入新文件（上传即整体替换）。
    // 返回：200 { avatarUrl }（带版本参数，前端可直接用作图片地址）；401 未登录。
    [HttpPost("avatar", Name = "UploadMyAvatar")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = MultipartBodyLimit)]
    public async Task<IResult> UploadAvatar(
        IFormFile? avatar,
        UserManager<ApplicationUser> userManager,
        LibraryStoragePaths storagePaths,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return Results.Unauthorized();

        if (avatar is null || avatar.Length == 0)
            return ApiResults.ValidationError(HttpContext, "avatar", "An image file is required.");
        if (avatar.Length > UserAvatarFiles.MaxBytes)
            return ApiResults.Error(HttpContext, StatusCodes.Status413PayloadTooLarge, ErrorCodes.AvatarTooLarge, "Avatar image must be at most 2MB.");

        // 整体读入内存（≤2MB）后校验文件头，再落盘；避免流位置重置的兼容问题。
        using var buffer = new MemoryStream();
        await avatar.CopyToAsync(buffer, cancellationToken);
        var content = buffer.ToArray();
        var format = UserAvatarFiles.DetectFormat(content.AsSpan(0, Math.Min(content.Length, 16)));
        if (format is null)
            return ApiResults.Error(HttpContext, StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaFormat, "Only JPEG, PNG and WebP images are accepted.");

        // storageKey 由服务端拼装（avatars/{userId}{ext}），userId 为 Guid 不含路径分隔符，无法穿越。
        if (!storagePaths.TryResolve($"{UserAvatarFiles.DirectoryKey}{userId}{format.Value.Extension}", out var targetPath))
            return ApiResults.Error(HttpContext, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, "Unable to resolve avatar storage path.");
        var directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);

        // 上传即替换：先清掉旧头像（任何扩展名），再写新文件。
        UserAvatarFiles.DeleteAll(storagePaths, userId);
        await System.IO.File.WriteAllBytesAsync(targetPath, content, cancellationToken);

        return Results.Ok(new AvatarUrlDto(UserAvatarFiles.BuildUrl(storagePaths, userId)));
    }

    // 【GET /api/v1/me/avatar】读取当前用户头像（匿名不可见，必须登录且头像存在）。
    // 返回：200 图片二进制；404 无头像或文件缺失。
    [HttpGet("avatar", Name = "GetMyAvatar")]
    public IResult GetAvatar(LibraryStoragePaths storagePaths)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var path = UserAvatarFiles.FindFilePath(storagePaths, userId);
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

    // 【DELETE /api/v1/me/avatar】删除当前用户头像（幂等：无头像也返回 204）。
    [HttpDelete("avatar", Name = "DeleteMyAvatar")]
    public IResult DeleteAvatar(LibraryStoragePaths storagePaths)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        UserAvatarFiles.DeleteAll(storagePaths, userId);
        return Results.NoContent();
    }

    // 【POST /api/v1/me/password】修改登录密码（需提供当前密码）。
    // 新密码先校验密码强度门槛（PasswordPolicy:MinimumStrength，0-4 级），随后由 Identity 校验基础策略；
    // 成功后当前会话保持有效（不强制重新登录）。
    // 返回：204 成功；400 字段缺失/强度不足/旧密码错误/新密码不合规；401 未登录。
    [HttpPost("password", Name = "ChangeMyPassword")]
    public async Task<IResult> ChangePassword(
        ChangePasswordRequest request,
        UserManager<ApplicationUser> userManager,
        IOptions<PasswordPolicyOptions> passwordPolicy,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return ApiResults.ValidationError(HttpContext, "password", "Current and new password are required.");

        if (PasswordStrength.Estimate(request.NewPassword) < passwordPolicy.Value.MinimumStrength)
            return ApiResults.Error(HttpContext, StatusCodes.Status400BadRequest, ErrorCodes.PasswordTooWeak, "The new password is too weak.");

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return ApiResults.ValidationError(HttpContext, "password", string.Join(" ", result.Errors.Select(error => error.Description)));
        return Results.NoContent();
    }

}

// 以下为本端点组使用的请求 DTO（由请求体反序列化）。
