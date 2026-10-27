using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>
/// 管理员用户管理端点：用户列表检索、重置密码、禁用/启用账号。
/// 仅 admin 角色可用；所有写操作均落管理员操作审计。
/// </summary>
[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Roles = "admin")]
[Tags("Admin Users")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
public sealed class AdminUsersController : ControllerBase
{
    /// <summary>
    /// 【GET /api/v1/admin/users】用户分页列表。
    /// 查询参数：query（邮箱/用户名/昵称模糊）、page、pageSize（1-1000，默认 20）。
    /// </summary>
    [HttpGet(Name = "GetAdminUsers")]
    public async Task<IResult> GetUsers(
        [FromQuery] string? query,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        ISqlSugarClient db,
        UserService userService)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize is <= 0 or > 1000 ? 20 : pageSize;

        var queryable = db.Queryable<ApplicationUser>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var kw = query.Trim();
            queryable = queryable.Where(u =>
                u.Email!.Contains(kw) || u.UserName!.Contains(kw) || u.DisplayName.Contains(kw));
        }

        var total = await queryable.CountAsync();
        var users = await queryable
            .OrderByDescending(u => u.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = new List<AdminUserItem>(users.Count);
        foreach (var user in users)
        {
            // 有效角色：过期临时管理员在此处被自动降级并持久化（“3 日后自动改为普通用户”）
            var roles = await userService.GetEffectiveRolesAsync(user);
            var locked = user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value >= DateTimeOffset.UtcNow;
            items.Add(new AdminUserItem(
                user.Id, user.Email, user.UserName, user.DisplayName, roles,
                user.TempAdminUntilUtc,
                user.CreatedAtUtc, user.LockoutEnabled, user.LockoutEnd, user.AccessFailedCount, locked));
        }
        return Results.Ok(new PageResult<AdminUserItem>(items, page, pageSize, total));
    }

    /// <summary>
    /// 【POST /api/v1/admin/users/{id}/role】修改用户角色（user / tempadmin / admin，互斥替换）。
    /// tempadmin 自修改起 3 天内视同管理员，到期自动降级为普通用户；admin 长期有效。
    /// 修改后吊销该用户全部刷新令牌（强制重新登录获取新角色声明）。不能修改自己的角色。
    /// </summary>
    [HttpPost("{id:guid}/role", Name = "AdminSetUserRole")]
    public async Task<IResult> SetRole(
        Guid id,
        AdminSetRoleRequest request,
        ISqlSugarClient db,
        UserService userService)
    {
        var role = request.Role?.Trim().ToLowerInvariant();
        if (role is not (RoleNames.User or RoleNames.TempAdmin or RoleNames.Admin))
            return ApiResults.ValidationError(HttpContext, "role", "Role must be one of: user, tempadmin, admin.");

        if (Guid.TryParse(User.FindFirst("sub")?.Value, out var actor) && actor == id)
            return ApiResults.ValidationError(HttpContext, "id", "You cannot change your own role.");

        var user = await userService.FindByIdAsync(id);
        if (user is null)
            return ApiResults.NotFound(HttpContext, "User not found.");

        var now = DateTime.UtcNow;
        // 临时管理员有效期：自修改时起 3 天；其余角色清空该字段
        var tempAdminUntil = role == RoleNames.TempAdmin ? now.AddDays(3) : (DateTime?)null;
        await db.Ado.UseTranAsync(async () =>
        {
            // 角色互斥：清除旧角色关联后授予新角色
            await db.Deleteable<ApplicationUserRole>().Where(item => item.UserId == id).ExecuteCommandAsync();
            var addResult = await userService.AddToRoleAsync(id, role);
            if (!addResult.Succeeded)
                throw new InvalidOperationException($"Failed to assign role '{role}' to user {id}.");
            user.TempAdminUntilUtc = tempAdminUntil;
            // 显式 UpdateColumns 保证 tempAdminUntil 为 null 时也真实写 NULL
            await db.Updateable(user).UpdateColumns(item => new { item.TempAdminUntilUtc }).ExecuteCommandAsync();
            // 角色已变化：吊销全部刷新令牌，强制各端重新登录以获取新角色声明
            await db.Updateable<RefreshToken>()
                .SetColumns(t => t.RevokedAtUtc == now)
                .Where(t => t.UserId == id && t.RevokedAtUtc == null)
                .ExecuteCommandAsync();
        });
        AdminAudit.Add(HttpContext, db, "user.role_changed", "user", id, new { role, tempAdminUntilUtc = tempAdminUntil });
        return Results.Ok(new { id, role, tempAdminUntilUtc = tempAdminUntil });
    }

    /// <summary>
    /// 【POST /api/v1/admin/users/{id}/reset-password】管理员重置用户密码并解除锁定、吊销全部刷新令牌。
    /// 请求体：AdminResetPasswordRequest.NewPassword（≥8 位）。不能重置自己的密码。
    /// </summary>
    [HttpPost("{id:guid}/reset-password", Name = "AdminResetUserPassword")]
    public async Task<IResult> ResetPassword(
        Guid id,
        AdminResetPasswordRequest request,
        ISqlSugarClient db,
        UserService userService)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return ApiResults.ValidationError(HttpContext, "newPassword", "Password must be at least 8 characters.");

        var user = await userService.FindByIdAsync(id);
        if (user is null)
            return ApiResults.NotFound(HttpContext, "User not found.");

        await userService.ResetPasswordAsync(user, request.NewPassword);
        await userService.ClearLockoutAsync(user);
        // 重置密码后吊销该用户全部刷新令牌，强制所有端重新登录。
        var now = DateTime.UtcNow;
        await db.Updateable<RefreshToken>()
            .SetColumns(t => t.RevokedAtUtc == now)
            .Where(t => t.UserId == id && t.RevokedAtUtc == null)
            .ExecuteCommandAsync();

        AdminAudit.Add(HttpContext, db, "user.reset_password", "user", id);
        return Results.Ok(new { id, reset = true });
    }

    /// <summary>
    /// 【POST /api/v1/admin/users/{id}/lock】禁用账号（长期锁定 + 吊销刷新令牌）。不能禁用自己。
    /// </summary>
    [HttpPost("{id:guid}/lock", Name = "AdminLockUser")]
    public async Task<IResult> LockUser(Guid id, ISqlSugarClient db, UserService userService)
    {
        if (Guid.TryParse(User.FindFirst("sub")?.Value, out var actor) && actor == id)
            return ApiResults.ValidationError(HttpContext, "id", "You cannot lock your own account.");

        var user = await userService.FindByIdAsync(id);
        if (user is null)
            return ApiResults.NotFound(HttpContext, "User not found.");

        var now = DateTime.UtcNow;
        await db.Ado.UseTranAsync(async () =>
        {
            await db.Updateable<ApplicationUser>()
                .SetColumns(u => new ApplicationUser { LockoutEnabled = true, LockoutEnd = DateTimeOffset.MaxValue })
                .Where(u => u.Id == id)
                .ExecuteCommandAsync();
            await db.Updateable<RefreshToken>()
                .SetColumns(t => t.RevokedAtUtc == now)
                .Where(t => t.UserId == id && t.RevokedAtUtc == null)
                .ExecuteCommandAsync();
        });
        AdminAudit.Add(HttpContext, db, "user.locked", "user", id);
        return Results.Ok(new { id, locked = true });
    }

    /// <summary>【POST /api/v1/admin/users/{id}/unlock】解除禁用/锁定并清零失败计数。</summary>
    [HttpPost("{id:guid}/unlock", Name = "AdminUnlockUser")]
    public async Task<IResult> UnlockUser(Guid id, ISqlSugarClient db, UserService userService)
    {
        var user = await userService.FindByIdAsync(id);
        if (user is null)
            return ApiResults.NotFound(HttpContext, "User not found.");

        await userService.ClearLockoutAsync(user);
        AdminAudit.Add(HttpContext, db, "user.unlocked", "user", id);
        return Results.Ok(new { id, locked = false });
    }
}
