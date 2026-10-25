// =============================================================================
// 文件：Contracts/ProfileDtos.cs
// 用途：当前用户资料、头像等「个人中心」对外契约。
//
// 这些类型原先以匿名对象 `Results.Ok(new { ... })` 直接返回。
// 匿名对象的问题是：OpenAPI 只能生成 `object`，前端拿不到任何字段信息，
// 只能照着仓库里某处的手写接口猜 —— 这正是在 2026-10-18 契约审计中被点名的问题之一。
// 改为命名 record 后，类型出现在 swagger.json 里，也能被前端契约生成器读取。
// =============================================================================
namespace ShineMusic.Model.Contracts;

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

/// <summary>
/// 用户自定义图片主题配置，对应 GET/PUT /api/v1/me/theme 与 POST /api/v1/me/theme/wallpaper 的响应。
/// 颜色为前端取色后的 #RRGGBB；从未配置过时各颜色字段为 null。
/// </summary>
/// <param name="Accent">主强调色（#RRGGBB）；未配置为 null。</param>
/// <param name="Accent2">次强调色（#RRGGBB）；可空。</param>
/// <param name="Accent3">第三强调色（#RRGGBB）；可空。</param>
/// <param name="MaskOpacity">壁纸暗色遮罩不透明度（0-0.85）。</param>
/// <param name="Enabled">是否启用自定义主题（多端同步的开关事实源）。</param>
/// <param name="WallpaperUrl">壁纸读取地址（带版本参数）；未上传为 null。地址需携带登录态请求取回。</param>
public sealed record UserThemeDto(
    string? Accent,
    string? Accent2,
    string? Accent3,
    float MaskOpacity,
    bool Enabled,
    string? WallpaperUrl);

/// <summary>自定义主题配置更新请求体，对应 PUT /api/v1/me/theme；颜色传 null 表示清除该槽位。</summary>
public sealed record UpdateUserThemeRequest(string? Accent, string? Accent2, string? Accent3, float MaskOpacity, bool Enabled);
