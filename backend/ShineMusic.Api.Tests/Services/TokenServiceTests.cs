using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Data;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Tests.Services;

/// <summary>
/// 认证令牌服务测试。
///
/// 锁定的安全属性：
///   - access token 只带必要声明（sub/email/jti/display_name/role），过期时间等于配置的分钟数；
///   - refresh token 明文永不落库，落库/比对的是大写十六进制 SHA256 哈希；
///   - 哈希确定且抗碰撞（不同明文得到不同摘要），支持 O(1) 复用检测；
///   - 刷新窗口按配置天数滑动，可通过注入「当前时间」测试边界。
/// </summary>
public sealed class TokenServiceTests
{
    // HMAC-SHA256 要求密钥不少于 32 字节，测试用同样长度的确定性密钥。
    private const string SigningKey = "unit-test-signing-key-0123456789abcdef";

    private static TokenService CreateService(AuthOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? new AuthOptions { SigningKey = SigningKey }));

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Email = "listener@example.com",
            UserName = "listener@example.com",
            DisplayName = "听众",
        };

    [Fact]
    public void CreateAccessToken_写出核心声明与角色声明()
    {
        var service = CreateService();
        var before = DateTime.UtcNow;

        var (token, expiresAtUtc) = service.CreateAccessToken(CreateUser(), ["user", "admin"]);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("ShineMusic", jwt.Issuer);
        Assert.Equal("ShineMusic.Client", jwt.Audiences.Single());
        Assert.Equal("11111111-2222-3333-4444-555555555555", jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("listener@example.com", jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal("听众", jwt.Claims.First(c => c.Type == "display_name").Value);
        Assert.Contains(jwt.Claims, c => c.Type == JwtRegisteredClaimNames.Jti);
        Assert.Equal(
            new[] { "admin", "user" },
            jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).Order());
    }

    [Fact]
    public void CreateAccessToken_过期时间等于配置的分钟数()
    {
        var service = CreateService(new AuthOptions { SigningKey = SigningKey, AccessTokenMinutes = 30 });
        var before = DateTime.UtcNow;

        var (_, expiresAtUtc) = service.CreateAccessToken(CreateUser(), []);

        var after = DateTime.UtcNow;
        Assert.InRange(expiresAtUtc, before.AddMinutes(30).AddSeconds(-5), after.AddMinutes(30).AddSeconds(5));
    }

    [Fact]
    public void CreateAccessToken_邮箱为空时写空字符串占位以保证声明存在()
    {
        var service = CreateService();
        var user = CreateUser();
        user.Email = null;

        var (token, _) = service.CreateAccessToken(user, []);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(string.Empty, jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value);
    }

    [Fact]
    public void CreateAccessToken_同样输入签发两枚_因jti不同而互不相同()
    {
        var service = CreateService();
        var user = CreateUser();

        var (first, _) = service.CreateAccessToken(user, []);
        var (second, _) = service.CreateAccessToken(user, []);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateRefreshToken_返回明文与64位大写十六进制哈希()
    {
        var service = CreateService();

        var (token, hash) = service.CreateRefreshToken();

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToUpperInvariant());
        Assert.All(hash, ch => Assert.Contains(ch, "0123456789ABCDEF"));
        // 落库值必须是摘要而非明文：明文长度 48 随机字节的 Base64 远长于 64 个十六进制字符的等价值，
        // 这里直接验证「哈希不是明文本身」。
        Assert.NotEqual(token, hash);
    }

    [Fact]
    public void HashToken_确定性且不同明文得到不同摘要()
    {
        var service = CreateService();

        Assert.Equal(TokenService.HashToken("alpha"), TokenService.HashToken("alpha"));
        Assert.NotEqual(TokenService.HashToken("alpha"), TokenService.HashToken("beta"));
    }

    [Fact]
    public void CreateRefreshToken_每次调用都生成新的明文与哈希()
    {
        var service = CreateService();

        var (firstToken, firstHash) = service.CreateRefreshToken();
        var (secondToken, secondHash) = service.CreateRefreshToken();

        Assert.NotEqual(firstToken, secondToken);
        Assert.NotEqual(firstHash, secondHash);
        // 哈希与明文必须自洽：CreateRefreshToken 返回的 hash 就是 token 的摘要。
        Assert.Equal(TokenService.HashToken(firstToken), firstHash);
    }

    [Fact]
    public void GetRefreshTokenExpiry_按配置天数滑动()
    {
        var service = CreateService(new AuthOptions { SigningKey = SigningKey, RefreshTokenDays = 7 });

        var expiry = service.GetRefreshTokenExpiry(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 1, 8, 12, 0, 0, DateTimeKind.Utc), expiry);
    }

    [Fact]
    public void GetRefreshTokenExpiry_默认取当前UTC时间()
    {
        var service = CreateService(new AuthOptions { SigningKey = SigningKey, RefreshTokenDays = 30 });
        var before = DateTime.UtcNow;

        var expiry = service.GetRefreshTokenExpiry();

        var after = DateTime.UtcNow;
        Assert.InRange(expiry, before.AddDays(30).AddSeconds(-5), after.AddDays(30).AddSeconds(5));
    }

    [Fact]
    public void CreateRandomToken_长度固定且两次调用不重复()
    {
        var first = TokenService.CreateRandomToken();
        var second = TokenService.CreateRandomToken();

        Assert.Equal(64, first.Length); // 48 字节 → Base64 64 字符
        Assert.NotEqual(first, second);
    }
}
