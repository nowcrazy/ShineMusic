// 共享的 ClaimsPrincipal 扩展：统一从 JWT 的 sub 声明解析当前用户 ID。
// 项目在多个控制器中重复实现过同一逻辑，这里收敛为一处，避免各写各的。

using System.Security.Claims;

namespace ShineMusic.Api.Extensions;

/// <summary>ClaimsPrincipal 用户身份解析扩展。</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// 尝试从 sub 声明解析用户 ID。
    /// </summary>
    /// <param name="principal">当前请求的用户主体。</param>
    /// <param name="userId">解析出的用户 ID（失败为 Guid.Empty）。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out userId);

    /// <summary>
    /// 从 sub 声明解析用户 ID，无法解析时返回 null。
    /// </summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var userId) ? userId : null;
}
