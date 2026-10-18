// =============================================================================
// 文件：Contracts/ProfileDtos.cs
// 用途：当前用户资料、头像等「个人中心」对外契约。
//
// 这些类型原先以匿名对象 `Results.Ok(new { ... })` 直接返回。
// 匿名对象的问题是：OpenAPI 只能生成 `object`，前端拿不到任何字段信息，
// 只能照着仓库里某处的手写接口猜 —— 这正是在 2026-10-18 契约审计中被点名的问题之一。
// 改为命名 record 后，类型出现在 swagger.json 里，也能被前端契约生成器读取。
// =============================================================================
namespace ShineMusic.Api.Contracts;

/// <summary>
/// 当前登录用户的资料快照，对应 GET /api/v1/me。
/// 前端据此决定「管理员入口显隐」与头像展示，但真正的权限判定始终以服务端为准。
/// </summary>
/// <param name="Id">用户唯一标识。</param>
/// <param name="Email">登录邮箱（与邀请绑定的邮箱一致）。</param>
/// <param name="DisplayName">展示昵称。</param>
/// <param name="Roles">角色码列表；包含 admin 表示管理员。</param>
/// <param name="AvatarUrl">头像读取地址（带版本参数用于缓存刷新）；未上传时为 null。</param>
public sealed record CurrentUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string? AvatarUrl);

/// <summary>昵称修改请求体，对应 POST /api/v1/me/display-name。</summary>
public sealed record UpdateDisplayNameRequest(string DisplayName);

/// <summary>密码修改请求体，对应 POST /api/v1/me/password。</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>头像上传/更换的响应，对应 POST /api/v1/me/avatar。</summary>
public sealed record AvatarUrlDto(string? AvatarUrl);
