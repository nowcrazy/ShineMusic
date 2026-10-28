// 管理端用户与审计日志契约：用户列表/操作入参 + 登录审计、操作审计的查询返回。

namespace ShineMusic.Model.Contracts;

/// <summary>
/// 管理端用户列表条目（含角色与锁定状态；IsLockedOut 表示当前处于锁定/禁用状态）。
/// Roles 为有效角色：临时管理员未过期时含 admin；TempAdminUntilUtc 仅临时管理员有值，到期自动降级。
/// </summary>
public sealed record AdminUserItem(
    Guid Id,
    string? Email,
    string? UserName,
    string DisplayName,
    IReadOnlyList<string> Roles,
    DateTime? TempAdminUntilUtc,
    DateTime CreatedAtUtc,
    bool LockoutEnabled,
    DateTimeOffset? LockoutEnd,
    int AccessFailedCount,
    bool IsLockedOut);

/// <summary>管理员重置用户密码入参。</summary>
public sealed record AdminResetPasswordRequest(string NewPassword);

/// <summary>管理员修改用户角色入参：user 普通用户 / tempadmin 临时管理员（3 天）/ admin 管理员。</summary>
public sealed record AdminSetRoleRequest(string Role);

/// <summary>登录审计条目（管理端日志页展示）。</summary>
public sealed record LoginAuditItem(
    Guid Id,
    string Login,
    Guid? UserId,
    string Result,
    string Reason,
    string Ip,
    string? ClientType,
    string? DeviceInfo,
    DateTime CreatedAtUtc);

/// <summary>操作审计条目（管理端日志页展示）。</summary>
public sealed record OperationAuditItem(
    Guid Id,
    Guid ActorUserId,
    string? ActorName,
    string Action,
    string ResourceType,
    string ResourceId,
    string? ResourceName,
    string? DetailsJson,
    string? Ip,
    string? ClientType,
    string? DeviceInfo,
    DateTime CreatedAtUtc);
