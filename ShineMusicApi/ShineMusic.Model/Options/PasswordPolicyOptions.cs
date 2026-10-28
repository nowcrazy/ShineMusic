// 密码策略配置选项：绑定 "PasswordPolicy" 配置节。
// 用于控制登录密码的强度门槛（0-4 级），独立于 Identity 的基础密码策略
// （RequiredLength=6 等），后者对所有 CreateAsync/ChangePasswordAsync 生效。
namespace ShineMusic.Model.Options;

/// <summary>
/// 密码强度策略，对应配置节 "PasswordPolicy"。
/// </summary>
public sealed class PasswordPolicyOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "PasswordPolicy";

    /// <summary>
    /// 修改密码要求达到的最低强度等级（0-4，越大越强）。
    /// 由 PasswordStrength（Service 层）计算；
    /// 仅用于用户主动修改密码的环节，不作用于 --create-admin 的首位管理员创建流程。
    /// </summary>
    public int MinimumStrength { get; set; } = 2;
}
