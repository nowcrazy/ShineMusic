// 认证配置选项：绑定 "Auth" 配置节，集中管理 JWT 签名密钥、签发方、受众及各类令牌有效期。
namespace ShineMusic.Model.Options;

/// <summary>
/// JWT 认证与邀请令牌相关配置，对应配置节 "Auth"。
/// </summary>
public sealed class AuthOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// JWT 对称签名密钥原文（HMAC-SHA256），按 UTF-8 计的字节长度必须不少于 32；
    /// 属于敏感机密，只能通过用户机密（User Secrets）或环境变量 Auth__SigningKey 注入，
    /// 不得提交到 appsettings 或源码仓库。
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>JWT 签发者标识，写入 iss 声明并在验签时校验，默认 "ShineMusic"。</summary>
    public string Issuer { get; set; } = "ShineMusic";

    /// <summary>JWT 受众标识，写入 aud 声明并在验签时校验，默认 "ShineMusic.Client"。</summary>
    public string Audience { get; set; } = "ShineMusic.Client";

    /// <summary>访问令牌（AccessToken）有效期，单位分钟，取值范围 1～60，默认 15；短有效期用于降低令牌泄露风险。</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>刷新令牌（RefreshToken）有效期，单位天，取值范围 1～90，默认 30。</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>管理员邀请链接的有效期，单位小时，取值范围 1～720（最长 30 天），默认 48。</summary>
    public int InvitationExpiryHours { get; set; } = 48;
}
