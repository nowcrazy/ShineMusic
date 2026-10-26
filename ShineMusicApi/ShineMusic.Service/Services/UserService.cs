// 职责：用户与角色数据访问服务（SqlSugar 实现）—— 1b 阶段替代 ASP.NET Core Identity 的
// UserManager / SignInManager / RoleManager 三件套（三者深度绑定 EF Core，随 ORM 切换移除）。
// 行为逐项对齐原 Identity 配置（旧配置见 Git 历史 Program.cs AddIdentityCore）：
//   - 密码哈希：PasswordHasher<ApplicationUser>（PBKDF2，Identity V3 格式头自描述算法/迭代数），
//     与存量哈希完全兼容，老用户无需重置密码；
//   - 密码策略：至少 6 位，不强制数字/大小写/特殊字符（错误文案与 Identity 默认值一致）；
//   - 邮箱唯一：按 NormalizedEmail 判重（等价 RequireUniqueEmail）；
//   - 登录锁定：连续 5 次密码失败锁定 15 分钟（触发锁定时失败计数清零，与 UserManager.AccessFailedAsync 一致）；
//   - 新用户 LockoutEnabled=true（等价 Lockout.AllowedForNewUsers 默认行为）；
//   - 用户名/邮箱查找：先 ToUpperInvariant 归一化再比对 NormalizedUserName/NormalizedEmail 列（Identity 惯例）。

using Microsoft.AspNetCore.Identity;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>
/// 用户与角色的查询/写入服务。无状态；注入的 ISqlSugarClient 是进程级单例（SqlSugarScope），
/// 调用方开启的 db.Ado 事务（AsyncLocal 上下文）会自动覆盖本服务的所有写操作。
/// </summary>
public sealed class UserService(ISqlSugarClient db)
{
    // 以下三项取自原 AddIdentityCore 配置：密码最短 6 位、连续失败 5 次锁定、锁定时长 15 分钟。
    private const int RequiredPasswordLength = 6;
    private const int MaxFailedAccessAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // 哈希器配置保持默认（IdentityV3）；哈希串自描述参数，验证与生成配置解耦。
    private readonly PasswordHasher<ApplicationUser> _passwordHasher = new();

    /// <summary>按邮箱查找用户（大小写不敏感，经 NormalizedEmail 列）。</summary>
    public async Task<ApplicationUser?> FindByEmailAsync(string email)
    {
        var normalized = email.Trim().ToUpperInvariant();
        return await db.Queryable<ApplicationUser>().FirstAsync(user => user.NormalizedEmail == normalized);
    }

    /// <summary>按用户名查找用户（大小写不敏感，经 NormalizedUserName 列）。</summary>
    public async Task<ApplicationUser?> FindByNameAsync(string userName)
    {
        var normalized = userName.Trim().ToUpperInvariant();
        return await db.Queryable<ApplicationUser>().FirstAsync(user => user.NormalizedUserName == normalized);
    }

    /// <summary>按主键查找用户。</summary>
    public async Task<ApplicationUser?> FindByIdAsync(Guid userId) =>
        await db.Queryable<ApplicationUser>().FirstAsync(user => user.Id == userId);

    /// <summary>库中是否已存在任何用户（--create-admin 的守卫条件）。</summary>
    public Task<bool> AnyUsersAsync() => db.Queryable<ApplicationUser>().AnyAsync();

    /// <summary>获取用户的角色名集合（联 AspNetUserRoles/AspNetRoles）。</summary>
    public async Task<List<string>> GetRolesAsync(Guid userId) =>
        await db.Queryable<ApplicationUserRole, ApplicationRole>((userRole, role) => userRole.RoleId == role.Id)
            .Where((userRole, role) => userRole.UserId == userId)
            .Select((userRole, role) => role.Name!)
            .ToListAsync();

    /// <summary>用户是否拥有指定角色（角色名大小写不敏感，按 NormalizedName 匹配）。</summary>
    public Task<bool> IsInRoleAsync(Guid userId, string roleName)
    {
        var normalized = roleName.ToUpperInvariant();
        return db.Queryable<ApplicationUserRole, ApplicationRole>((userRole, role) => userRole.RoleId == role.Id)
            .Where((userRole, role) => userRole.UserId == userId && role.NormalizedName == normalized)
            .AnyAsync();
    }

    /// <summary>授予用户角色；角色不存在或用户已在角色中时返回失败（语义对齐 UserManager.AddToRoleAsync）。</summary>
    public async Task<IdentityResult> AddToRoleAsync(Guid userId, string roleName)
    {
        var normalized = roleName.ToUpperInvariant();
        var role = await db.Queryable<ApplicationRole>().FirstAsync(item => item.NormalizedName == normalized);
        if (role is null)
            return IdentityResult.Failed(Error("RoleNotFound", $"Role '{roleName}' does not exist."));
        var exists = await db.Queryable<ApplicationUserRole>()
            .AnyAsync(item => item.UserId == userId && item.RoleId == role.Id);
        if (exists)
            return IdentityResult.Failed(Error("UserAlreadyInRole", $"User already in role '{roleName}'."));
        await db.Insertable(new ApplicationUserRole { UserId = userId, RoleId = role.Id }).ExecuteCommandAsync();
        return IdentityResult.Success;
    }

    /// <summary>移除用户角色；角色不存在或用户本就不在该角色中时静默成功（幂等）。</summary>
    public async Task RemoveFromRoleAsync(Guid userId, string roleName)
    {
        var normalized = roleName.ToUpperInvariant();
        var role = await db.Queryable<ApplicationRole>().FirstAsync(item => item.NormalizedName == normalized);
        if (role is null) return;
        await db.Deleteable<ApplicationUserRole>()
            .Where(item => item.UserId == userId && item.RoleId == role.Id)
            .ExecuteCommandAsync();
    }

    /// <summary>
    /// 获取用户的有效角色集合（处理临时管理员有效期）：
    /// - 拥有 tempadmin 角色且 TempAdminUntilUtc 未过期：有效角色附加 admin（与管理员同权）；
    /// - 已过期：自动降级为普通用户——移除 tempadmin 角色、清空有效期、确保 user 角色（幂等落库，
    ///   下次读取不再命中降级分支）。
    /// </summary>
    public async Task<List<string>> GetEffectiveRolesAsync(ApplicationUser user)
    {
        var roles = await GetRolesAsync(user.Id);
        var tempIndex = roles.FindIndex(role => string.Equals(role, RoleNames.TempAdmin, StringComparison.OrdinalIgnoreCase));
        if (tempIndex < 0) return roles;

        if (user.TempAdminUntilUtc is not null && user.TempAdminUntilUtc.Value > DateTime.UtcNow)
        {
            // 有效期内：附加管理员权限（无截止时间的临时管理员视为已过期，保证 3 天规则严格生效）
            if (!roles.Contains(RoleNames.Admin)) roles.Add(RoleNames.Admin);
            return roles;
        }

        // 已过期：降级为普通用户并持久化
        roles.RemoveAt(tempIndex);
        await RemoveFromRoleAsync(user.Id, RoleNames.TempAdmin);
        user.TempAdminUntilUtc = null;
        // 显式 UpdateColumns 保证 TempAdminUntilUtc 为 null 时也真实写 NULL
        await db.Updateable(user).UpdateColumns(item => new { item.TempAdminUntilUtc }).ExecuteCommandAsync();
        if (!roles.Contains(RoleNames.User))
        {
            var addResult = await AddToRoleAsync(user.Id, RoleNames.User);
            if (addResult.Succeeded) roles.Add(RoleNames.User);
        }
        return roles;
    }

    /// <summary>角色不存在时创建（幂等），替代启动种子中的 RoleManager。</summary>
    public async Task EnsureRoleAsync(string roleName)
    {
        var normalized = roleName.ToUpperInvariant();
        if (await db.Queryable<ApplicationRole>().AnyAsync(item => item.NormalizedName == normalized))
            return;
        await db.Insertable(new ApplicationRole
        {
            Id = Guid.NewGuid(),
            Name = roleName,
            NormalizedName = normalized,
            ConcurrencyStamp = Guid.NewGuid().ToString()
        }).ExecuteCommandAsync();
    }

    /// <summary>创建用户并写入密码哈希；邮箱唯一冲突或密码不合规时返回失败（不落库）。</summary>
    public async Task<IdentityResult> CreateAsync(ApplicationUser user, string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < RequiredPasswordLength)
            return IdentityResult.Failed(Error("PasswordTooShort", $"Passwords must be at least {RequiredPasswordLength} characters."));

        // 归一化字段随创建写入，供登录/查找做大小写不敏感比对（原由 UserManager 自动维护）。
        user.NormalizedEmail = user.Email?.ToUpperInvariant();
        user.NormalizedUserName = user.UserName?.ToUpperInvariant();
        if (user.NormalizedEmail is not null &&
            await db.Queryable<ApplicationUser>().AnyAsync(item => item.NormalizedEmail == user.NormalizedEmail))
            return IdentityResult.Failed(Error("DuplicateEmail", $"Email '{user.Email}' is already taken."));

        user.PasswordHash = _passwordHasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        // AllowedForNewUsers 默认 true：新建账号纳入登录锁定策略。
        user.LockoutEnabled = true;
        await db.Insertable(user).ExecuteCommandAsync();
        return IdentityResult.Success;
    }

    /// <summary>校验登录密码，并按原 SignInManager.CheckPasswordSignInAsync(lockoutOnFailure: true) 语义维护失败计数与锁定状态。</summary>
    public async Task<PasswordCheckResult> CheckPasswordAsync(ApplicationUser user, string password)
    {
        // 锁定判定等价 SignInManager.IsLockedOutAsync：仅对 LockoutEnabled 用户生效且锁定期未过。
        if (user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value >= DateTimeOffset.UtcNow)
            return PasswordCheckResult.LockedOutResult;

        var verified = user.PasswordHash is not null &&
            _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
        if (!verified)
        {
            if (user.LockoutEnabled)
            {
                user.AccessFailedCount++;
                if (user.AccessFailedCount >= MaxFailedAccessAttempts)
                {
                    // 触发锁定并清零失败计数（与 UserManager.AccessFailedAsync 一致）。
                    user.LockoutEnd = DateTimeOffset.UtcNow.Add(LockoutDuration);
                    user.AccessFailedCount = 0;
                }
                await PersistLockoutStateAsync(user);
            }
            return PasswordCheckResult.FailedResult;
        }

        // 登录成功：清零累计失败计数（SuccessRehashNeeded 不会出现——哈希器配置恒定，若出现按成功放行）。
        if (user.LockoutEnabled && user.AccessFailedCount != 0)
        {
            user.AccessFailedCount = 0;
            await PersistLockoutStateAsync(user);
        }
        return PasswordCheckResult.SuccessResult;
    }

    /// <summary>更新展示昵称（含并发戳轮换，对齐 UserManager.UpdateAsync 行为）。</summary>
    public async Task<IdentityResult> UpdateDisplayNameAsync(ApplicationUser user)
    {
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        await db.Updateable(user)
            .UpdateColumns(item => new { item.DisplayName, item.ConcurrencyStamp })
            .ExecuteCommandAsync();
        return IdentityResult.Success;
    }

    /// <summary>修改密码：先验旧密码再写新哈希；成功时轮换安全戳（对齐 UserManager.ChangePasswordAsync）。</summary>
    public async Task<IdentityResult> ChangePasswordAsync(ApplicationUser user, string currentPassword, string newPassword)
    {
        if (user.PasswordHash is null ||
            _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            return IdentityResult.Failed(Error("PasswordMismatch", "Incorrect password."));
        if (newPassword.Length < RequiredPasswordLength)
            return IdentityResult.Failed(Error("PasswordTooShort", $"Passwords must be at least {RequiredPasswordLength} characters."));
        await WritePasswordAsync(user, newPassword);
        return IdentityResult.Success;
    }

    /// <summary>直接重置密码（启动种子用，无需旧密码），同时轮换安全戳。</summary>
    public Task ResetPasswordAsync(ApplicationUser user, string newPassword) => WritePasswordAsync(user, newPassword);

    /// <summary>解除锁定并清零失败计数（启动种子用）。</summary>
    public async Task ClearLockoutAsync(ApplicationUser user)
    {
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        await PersistLockoutStateAsync(user);
    }

    private async Task WritePasswordAsync(ApplicationUser user, string newPassword)
    {
        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        await db.Updateable(user)
            .UpdateColumns(item => new { item.PasswordHash, item.SecurityStamp, item.ConcurrencyStamp })
            .ExecuteCommandAsync();
    }

    // 显式 UpdateColumns 保证 LockoutEnd 为 null 时也真实写 NULL（SqlSugar 默认跳过未显式声明的列）。
    private Task PersistLockoutStateAsync(ApplicationUser user) =>
        db.Updateable(user)
            .UpdateColumns(item => new { item.LockoutEnd, item.AccessFailedCount })
            .ExecuteCommandAsync();

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}

/// <summary>密码校验结果（对齐 SignInResult 的 Succeeded/IsLockedOut 二元语义）。</summary>
public readonly record struct PasswordCheckResult(bool Succeeded, bool IsLockedOut)
{
    /// <summary>校验通过。</summary>
    public static PasswordCheckResult SuccessResult => new(true, false);
    /// <summary>密码错误。</summary>
    public static PasswordCheckResult FailedResult => new(false, false);
    /// <summary>账号处于锁定期。</summary>
    public static PasswordCheckResult LockedOutResult => new(false, true);
}
