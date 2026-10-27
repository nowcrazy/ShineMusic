// 职责：认证令牌服务。
// 负责两类令牌的生成：
//   1) JWT access token（短期，携带用户 Id/邮箱/角色等声明，HMAC-SHA256 对称签名）；
//   2) refresh token（长期，使用加密安全随机数生成明文令牌，服务端仅保存其 SHA256 哈希）。
// 另负责令牌有效期计算与令牌哈希计算。

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;

namespace ShineMusic.Service;

/// <summary>
/// JWT 访问令牌与刷新令牌的生成服务（主构造函数注入认证配置 AuthOptions）。
/// </summary>
public sealed class TokenService(IOptions<AuthOptions> authOptions)
{
    private readonly AuthOptions _options = authOptions.Value;

    /// <summary>
    /// 为指定用户生成 JWT 访问令牌。
    /// </summary>
    /// <param name="user">登录用户实体。</param>
    /// <param name="roles">该用户拥有的角色名集合，逐个写入角色声明。</param>
    /// <returns>已签名的 JWT 字符串及其 UTC 过期时间。</returns>
    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(ApplicationUser user, IEnumerable<string> roles)
    {
        // 短期令牌：过期时间 = 当前 UTC + 配置的 AccessTokenMinutes。
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            // sub：用户唯一标识（Guid），鉴权/审计取用户 Id 的来源。
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            // email：邮箱可能为空，写空字符串占位以保证声明存在。
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            // jti：每枚令牌唯一 Id，支持单点吊销/追踪。
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            // 自定义声明：前端展示用昵称。
            new("display_name", user.DisplayName)
        };
        // 角色逐个映射为标准角色声明，供 [Authorize(Roles=...)] 判定。
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        // 使用配置中的对称密钥，以 HMAC-SHA256 签名。
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            expires: expiresAt,
            signingCredentials: credentials);
        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <summary>
    /// 生成一对刷新令牌：返回（明文令牌， 服务端应保存的哈希）。
    /// 明文仅在本次响应中下发给客户端，数据库只存哈希，泄露库也无法直接冒用。
    /// </summary>
    public static (string Token, string Hash) CreateRefreshToken()
    {
        var token = CreateRandomToken();
        return (token, HashToken(token));
    }

    /// <summary>
    /// 使用加密安全随机数生成器（RNG）产生 48 字节随机串，
    /// 并做 Base64URL 风格处理（去 '=' 填充，'+'→'-'，'/'→'_'）以便安全放入 HTTP 头/URL。
    /// </summary>
    public static string CreateRandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    /// <summary>
    /// 生成注册码（CDK）：加密安全随机数产生一段便于人工输入/复制的大写短码，
    /// 形如 ABCD-EFGH-JKLM（数字组去易混淆字符）。明文仅在生成响应中返回一次，
    /// 库内复用 TokenHash/HashToken 存哈希。
    /// </summary>
    public static string CreateRegistrationCode(int groups = 3, int groupSize = 4)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 去掉易混淆的 0/O/1/I
        var parts = new string[groups];
        for (var g = 0; g < groups; g++)
        {
            var bytes = RandomNumberGenerator.GetBytes(groupSize);
            var chars = new char[groupSize];
            for (var i = 0; i < groupSize; i++) chars[i] = alphabet[bytes[i] % alphabet.Length];
            parts[g] = new string(chars);
        }
        return string.Join('-', parts);
    }

    /// <summary>
    /// 计算刷新令牌的过期时刻（UTC）；可注入 nowUtc 以便测试。
    /// </summary>
    public DateTime GetRefreshTokenExpiry(DateTime? nowUtc = null) =>
        (nowUtc ?? DateTime.UtcNow).AddDays(_options.RefreshTokenDays);

    /// <summary>
    /// 对明文令牌做 SHA256 并转十六进制大写字符串；数据库存储与比对均使用该哈希值。
    /// </summary>
    public static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// 登录/刷新成功后返回给客户端的认证响应载荷：访问令牌 + 刷新令牌 + 令牌类型 + 有效期秒数。
/// </summary>
/// <param name="AccessToken">JWT 访问令牌。</param>
/// <param name="RefreshToken">刷新令牌明文（仅此一次随响应下发）。</param>
/// <param name="TokenType">令牌类型（Bearer）。</param>
/// <param name="ExpiresIn">访问令牌有效期（秒）。</param>
public sealed record AuthTokenResponse(string AccessToken, string RefreshToken, string TokenType, int ExpiresIn);
