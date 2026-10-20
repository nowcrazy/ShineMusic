// =============================================================================
// 文件：AuthEndpoints.cs
// 端点组：认证（Authentication）—— 邀请令牌校验、邀请注册、账号密码登录、
//         JWT 访问令牌 + 刷新令牌的换发与登出、当前登录用户资料。
// 路由前缀：类级 [Route("api/v1/auth")]（register/me 使用绝对路由覆盖前缀）。
// 鉴权边界：validate/register/login/refresh/logout 全部匿名可访问；仅 GET /api/v1/me
//           带 [Authorize]，必须携带有效 Bearer JWT。
// 核心流程：邀请令牌哈希校验 → 注册时事务内抢占式核销令牌（防并发复用）→ 建号授角色
//           → 登录成功签发 access token 并持久化 refresh token（仅存哈希）；
//           refresh 采用轮换（rotation）：旧令牌立即失效并记录 ReplacedByTokenHash。
// =============================================================================
using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>Handles invitation registration, token sessions, and the current-user profile. 认证端点：邀请校验/注册、登录与刷新令牌轮换、登出及当前用户资料。</summary>
[ApiController]
[Route("api/v1/auth")]
[Tags("Authentication")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class AuthController : ControllerBase
{
    private readonly ILogger<AuthController> _logger;

    public AuthController(ILogger<AuthController> logger) => _logger = logger;

    // 【POST /api/v1/auth/invitations/validate】注册前校验邀请令牌（匿名）。
    // 请求体：ValidateInvitationRequest.InvitationToken（明文一次性令牌，可为邮件链接令牌或注册码/CDK）。
    // 返回：200 InvitationValidationResponse（脱敏邮箱（CDK 为空）+ 渠道 channel=email|code + 到期时间 UTC）；
    //       400 validation_error（令牌为空）/ invitation_invalid / invitation_redeemed / invitation_expired。
    // 副作用：令牌已过期且状态仍为 Sent/Issued 时，惰性将其置为 Expired。
    [HttpPost("invitations/validate", Name = "ValidateInvitation")]
    public async Task<IResult> ValidateInvitation(
        ValidateInvitationRequest request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InvitationToken))
            return ApiResults.ValidationError(HttpContext, "invitationToken", "Invitation token is required."); // 400：令牌缺失

        // 数据库只存令牌哈希，按哈希查找邀请记录。
        var tokenHash = TokenService.HashToken(request.InvitationToken);
        var invitation = await db.Invitations
            .FirstOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        if (invitation is null)
            return InvitationError(HttpContext, ErrorCodes.InvitationInvalid, "The invitation is invalid."); // 400：哈希无匹配
        if (invitation.Status == InvitationStatuses.Redeemed)
            return InvitationError(HttpContext, ErrorCodes.InvitationRedeemed, "The invitation has already been used."); // 400：已被核销
        if (invitation.ExpiresAtUtc <= DateTime.UtcNow)
        {
            // 惰性过期：首次在过期后被使用时把 Sent/Issued 状态落库为 Expired，便于管理端列表展示。
            if (invitation.Status is InvitationStatuses.Sent or InvitationStatuses.Issued)
            {
                invitation.Status = InvitationStatuses.Expired;
                await db.SaveChangesAsync(cancellationToken);
            }
            return InvitationError(HttpContext, ErrorCodes.InvitationExpired, "The invitation has expired."); // 400：已过期
        }
        if (invitation.Status is not (InvitationStatuses.Sent or InvitationStatuses.Issued))
            return InvitationError(HttpContext, ErrorCodes.InvitationInvalid, "The invitation is not available."); // 400：Created/失败/撤销等非可用状态

        // CDK(issued) 无绑定邮箱：返回空邮箱 + code 渠道，由前端提示用户自填邮箱。
        var isCode = invitation.Status == InvitationStatuses.Issued;
        return Results.Ok(new InvitationValidationResponse(
            isCode ? string.Empty : MaskEmail(invitation.Email!),
            isCode ? "code" : "email",
            DateTime.SpecifyKind(invitation.ExpiresAtUtc, DateTimeKind.Utc)));
    }

    // 【POST /api/v1/auth/register】凭邀请令牌注册新用户（匿名）。
    // 请求体：RegisterRequest（invitationToken、password、displayName、email[仅 CDK 渠道必填]）。
    // 返回：201 Created（Location=/api/v1/me，载荷含 id/email/displayName/role=user）；
    //       400 令牌类错误（同 validate）或 Identity 密码/身份校验错误。
    // 业务规则：核销邀请与创建用户在同一数据库事务内完成；先以条件 UPDATE 抢占令牌，
    //           影响行数不为 1（已被并发请求抢走/已过期）即回滚，杜绝一邀多用。
    // 邮箱来源：邮件邀请沿用绑定邮箱；CDK(issued) 要求请求体自填合法邮箱。
    [HttpPost("/api/v1/auth/register", Name = "Register")]
    public async Task<IResult> Register(
        RegisterRequest request,
        MusicDbContext db,
        UserManager<ApplicationUser> userManager,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InvitationToken))
            return ApiResults.ValidationError(HttpContext, "invitationToken", "Invitation token is required."); // 400
        if (string.IsNullOrWhiteSpace(request.Password))
            return ApiResults.ValidationError(HttpContext, "password", "Password is required."); // 400
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 100)
            return ApiResults.ValidationError(HttpContext, "displayName", "Display name is required and must be at most 100 characters."); // 400

        var tokenHash = TokenService.HashToken(request.InvitationToken);
        var invitation = await db.Invitations
            .FirstOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        if (invitation is null)
            return InvitationError(HttpContext, ErrorCodes.InvitationInvalid, "The invitation is invalid."); // 400

        var now = DateTime.UtcNow;
        if (invitation.Status == InvitationStatuses.Redeemed)
            return InvitationError(HttpContext, ErrorCodes.InvitationRedeemed, "The invitation has already been used."); // 400
        if (invitation.ExpiresAtUtc <= now)
        {
            if (invitation.Status is InvitationStatuses.Sent or InvitationStatuses.Issued)
            {
                invitation.Status = InvitationStatuses.Expired;
                await db.SaveChangesAsync(cancellationToken);
            }
            return InvitationError(HttpContext, ErrorCodes.InvitationExpired, "The invitation has expired."); // 400
        }
        if (invitation.Status is not (InvitationStatuses.Sent or InvitationStatuses.Issued))
            return InvitationError(HttpContext, ErrorCodes.InvitationInvalid, "The invitation is not available."); // 400

        // 邮箱来源：邮件邀请沿用绑定邮箱；CDK(issued) 要求自填并校验合法、且不得重复建号。
        var isCode = invitation.Status == InvitationStatuses.Issued;
        string email;
        if (isCode)
        {
            if (!TryNormalizeEmail(request.Email, out email!))
                return ApiResults.ValidationError(HttpContext, "email", "A valid email address is required for this registration code."); // 400：CDK 自填邮箱非法
            if (await userManager.FindByEmailAsync(email) is not null)
                return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "An account already exists for this email.", HttpContext.TraceIdentifier))); // 409：邮箱已被占用
        }
        else
        {
            email = invitation.Email!;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // 抢占式核销：仅当状态仍为 Sent/Issued 且未过期时更新成功（CAS 语义），防止两个并发注册共用同一邀请。
        var redeemed = await db.Invitations
            .Where(item => item.Id == invitation.Id &&
                (item.Status == InvitationStatuses.Sent || item.Status == InvitationStatuses.Issued) &&
                item.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, InvitationStatuses.Redeemed)
                .SetProperty(item => item.RedeemedAtUtc, now), cancellationToken);
        if (redeemed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InvitationError(HttpContext, ErrorCodes.InvitationInvalid, "The invitation is invalid or expired."); // 400：并发竞争失败
        }

        var user = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = request.DisplayName.Trim(),
            EmailConfirmed = true
        };
        var createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken); // 400：密码强度等 Identity 错误，回滚令牌核销
            return IdentityValidationError(HttpContext, createResult.Errors);
        }

        var roleResult = await userManager.AddToRoleAsync(user, "user");
        if (!roleResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken); // 400：授角色失败一并回滚，邀请恢复为可用
            return IdentityValidationError(HttpContext, roleResult.Errors);
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Created("/api/v1/me", new { id = user.Id, email = user.Email, displayName = user.DisplayName, role = "user" });
    }

    // 【POST /api/v1/auth/login】邮箱密码登录（匿名）。
    // 请求体：LoginRequest（email、password）。
    // 返回：200 AuthTokenResponse（accessToken/refreshToken/tokenType=Bearer/expiresIn）；
    //       400 凭证字段为空；401 authentication_required（用户不存在或密码错误，统一文案防止枚举邮箱；
    //       启用 lockoutOnFailure，连续失败会触发账号锁定，锁定时同样返回 401）。
    [HttpPost("login", Name = "Login")]
    public async Task<IResult> Login(
        LoginRequest request,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        TokenService tokenService,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        var loginIdentifier = string.IsNullOrWhiteSpace(request.Email)
            ? request.Username?.Trim()
            : request.Email.Trim();
        if (string.IsNullOrWhiteSpace(loginIdentifier) || string.IsNullOrWhiteSpace(request.Password))
            return ApiResults.ValidationError(HttpContext, "credentials", "Email or username and password are required."); // 400

        var user = await userManager.FindByEmailAsync(loginIdentifier);
        user ??= await userManager.FindByNameAsync(loginIdentifier);
        if (user is null)
        {
            // 账号不存在：仅服务端记录（响应与密码错误保持一致，避免邮箱枚举）；带 TraceId 便于定位。
            _logger.LogWarning("Login failed: account not found for {Login}. TraceId={TraceId}", loginIdentifier, HttpContext.TraceIdentifier);
            return Unauthorized(HttpContext);
        }

        var signInResult = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!signInResult.Succeeded)
        {
            // 密码错误或账号被锁定：记录原因 + 账号 + TraceId，出问题可快速排查某一账号的登录异常。
            var reason = signInResult.IsLockedOut ? "account_locked" : "bad_password";
            _logger.LogWarning("Login failed: {Reason} for {Login}. TraceId={TraceId}", reason, loginIdentifier, HttpContext.TraceIdentifier);
            return Unauthorized(HttpContext);
        }

        // 校验通过：签发 JWT 访问令牌并落库新的刷新令牌（仅存哈希）。
        var tokens = await IssueTokensAsync(user, userManager, tokenService, db, cancellationToken);
        return Results.Ok(tokens);
    }

    // 【POST /api/v1/auth/refresh】刷新令牌轮换（匿名，凭 refresh token 换发全新令牌对）。
    // 请求体：RefreshTokenRequest.RefreshToken。
    // 返回：200 新的 AuthTokenResponse；401 令牌不存在/已撤销/已过期/并发轮换竞争失败。
    // 业务规则：刷新令牌一次性使用——事务内条件 UPDATE 撤销旧令牌并写入 ReplacedByTokenHash，
    //           再写入新刷新令牌；旧令牌一旦换发即永久失效（rotation）。
    [HttpPost("refresh", Name = "RefreshTokens")]
    public async Task<IResult> Refresh(
        RefreshTokenRequest request,
        MusicDbContext db,
        UserManager<ApplicationUser> userManager,
        TokenService tokenService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return ApiResults.ValidationError(HttpContext, "refreshToken", "Refresh token is required."); // 400

        var tokenHash = TokenService.HashToken(request.RefreshToken);
        var storedToken = await db.RefreshTokens.AsNoTracking()
            .Include(token => token.User)
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);
        var now = DateTime.UtcNow;
        // 401：哈希无匹配（伪造）、已被撤销（含已轮换/已登出）、或已过期。
        if (storedToken is null || storedToken.RevokedAtUtc is not null || storedToken.ExpiresAtUtc <= now)
            return Unauthorized(HttpContext);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var replacement = tokenService.CreateRefreshToken();
        // CAS 条件撤销：仅当数据库中该令牌仍未撤销且未过期才生效，拦截同一旧令牌的并发重放。
        var revoked = await db.RefreshTokens
            .Where(token => token.Id == storedToken.Id && token.RevokedAtUtc == null && token.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RevokedAtUtc, now)
                .SetProperty(token => token.ReplacedByTokenHash, replacement.Hash), cancellationToken);
        if (revoked != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Unauthorized(HttpContext); // 401：已被另一个请求抢先轮换
        }

        var roles = await userManager.GetRolesAsync(storedToken.User);
        var accessToken = tokenService.CreateAccessToken(storedToken.User, roles);
        // 落库的是新刷新令牌的哈希；明文 replacement.Token 仅在本次响应中返回给客户端。
        db.RefreshTokens.Add(new Data.RefreshToken
        {
            UserId = storedToken.UserId,
            TokenHash = replacement.Hash,
            CreatedAtUtc = now,
            ExpiresAtUtc = tokenService.GetRefreshTokenExpiry(now)
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new AuthTokenResponse(accessToken.Token, replacement.Token, "Bearer", GetExpiresInSeconds(accessToken.ExpiresAtUtc)));
    }

    // 【POST /api/v1/auth/logout】登出（匿名可调用）。
    // 请求体：RefreshTokenRequest.RefreshToken（可空）。
    // 返回：始终 204 No Content；提供有效刷新令牌时将其撤销（幂等，已撤销/不存在也不报错）。
    [HttpPost("logout", Name = "Logout")]
    public async Task<IResult> Logout(
        RefreshTokenRequest request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            var tokenHash = TokenService.HashToken(request.RefreshToken);
            var now = DateTime.UtcNow;
            // 仅撤销尚未撤销的令牌；匹配 0 行（不存在/已失效）同样视为登出成功。
            await db.RefreshTokens.Where(token => token.TokenHash == tokenHash && token.RevokedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAtUtc, now), cancellationToken);
        }
        return Results.NoContent();
    }

    // 【GET /api/v1/me】获取当前登录用户资料（[Authorize]，必须携带有效 JWT）。
    // 参数：ClaimsPrincipal 由认证中间件注入（sub 声明为用户 Id），UserManager 由 DI 注入。
    // 返回：200 匿名对象（id/email/displayName/roles/avatarUrl）；401 未认证、sub 声明缺失/非法或用户已不存在。
    // avatarUrl：用户已上传头像时为带版本参数的读取地址，未上传时为 null（前端回退为首字母头像）。
    [HttpGet("/api/v1/me", Name = "GetCurrentUser")]
    [Authorize]
    public async Task<IResult> GetMe(
        UserManager<ApplicationUser> userManager,
        LibraryStoragePaths storagePaths)
    {
        var subject = HttpContext.User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return Results.Unauthorized(); // 401：令牌缺少合法用户标识
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return Results.Unauthorized(); // 401：令牌有效但用户已被删除
        var roles = await userManager.GetRolesAsync(user);
        return Results.Ok(new
        {
            id = user.Id,
            email = user.Email,
            displayName = user.DisplayName,
            roles,
            avatarUrl = UserAvatarFiles.BuildUrl(storagePaths, userId)
        });
    }

    /// <summary>登录成功后统一签发令牌对：生成 JWT 访问令牌与刷新令牌，并把刷新令牌哈希持久化。</summary>
    /// <param name="user">已通过密码校验的用户（依赖注入之外的业务入参）。</param>
    /// <param name="userManager">DI：读取用户角色，写入 JWT 角色声明。</param>
    /// <param name="tokenService">DI：创建访问/刷新令牌及计算到期时间。</param>
    /// <param name="db">DI：写入 RefreshTokens 表。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>AuthTokenResponse（明文访问令牌 + 明文刷新令牌 + Bearer + 剩余秒数）。</returns>
    private static async Task<AuthTokenResponse> IssueTokensAsync(
        ApplicationUser user,
        UserManager<ApplicationUser> userManager,
        TokenService tokenService,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var refresh = tokenService.CreateRefreshToken();
        var access = tokenService.CreateAccessToken(user, await userManager.GetRolesAsync(user));
        db.RefreshTokens.Add(new Data.RefreshToken
        {
            UserId = user.Id,
            TokenHash = refresh.Hash,
            CreatedAtUtc = now,
            ExpiresAtUtc = tokenService.GetRefreshTokenExpiry(now)
        });
        await db.SaveChangesAsync(cancellationToken);
        return new AuthTokenResponse(access.Token, refresh.Token, "Bearer", GetExpiresInSeconds(access.ExpiresAtUtc));
    }

    /// <summary>把访问令牌的到期时刻换算为剩余有效秒数；已过期时钳制为 0，避免返回负数。</summary>
    private static int GetExpiresInSeconds(DateTime expiresAtUtc) => Math.Max(0, (int)(expiresAtUtc - DateTime.UtcNow).TotalSeconds);

    /// <summary>邮箱脱敏：保留首字符与 @ 前最后一个字符，中间以 *** 代替，用于邀请校验响应，避免泄露完整受邀邮箱。</summary>
    private static string MaskEmail(string email)
    {
        var separator = email.IndexOf('@');
        if (separator <= 1)
            return $"*{email[separator..]}";
        return $"{email[0]}***{email[(separator - 1)..]}";
    }

    /// <summary>规范化注册码（CDK）注册时用户自填的邮箱：trim 并校验为合法邮箱，合法则赋给 <paramref name="email"/> 返回 true。</summary>
    private static bool TryNormalizeEmail(string? input, out string email)
    {
        email = input?.Trim() ?? string.Empty;
        if (email.Length is 0 or > 256)
            return false;
        try
        {
            var addr = new MailAddress(email);
            if (!string.Equals(addr.Address, email, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (FormatException)
        {
            return false;
        }
        return true;
    }

    /// <summary>把 Identity 错误集合合并为单个 400 validation_error 响应。</summary>
    private static IResult IdentityValidationError(HttpContext ctx, IEnumerable<IdentityError> errors) =>
        Results.BadRequest(new ApiErrorResponse(new ApiErrorDetail(
            ErrorCodes.ValidationError,
            string.Join(" ", errors.Select(error => error.Description)),
            ctx.TraceIdentifier)));

    /// <summary>构造字段级 400 validation_error 响应。</summary>

    /// <summary>构造邀请相关 400 响应（invitation_invalid/redeemed/expired 等业务错误码）。</summary>
    private static IResult InvitationError(HttpContext ctx, string code, string message) =>
        Results.BadRequest(new ApiErrorResponse(new ApiErrorDetail(code, message, ctx.TraceIdentifier)));

    /// <summary>构造统一的 401 authentication_required 响应；文案不区分“用户不存在/密码错误”，防止邮箱枚举。</summary>
    private static IResult Unauthorized(HttpContext ctx) => Results.Json(
        new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.AuthenticationRequired, "Email or password is invalid.", ctx.TraceIdentifier)),
        statusCode: StatusCodes.Status401Unauthorized);
}

// 以下为本端点组使用的请求/响应 DTO（由请求体反序列化或作为响应载荷返回）。
public sealed record ValidateInvitationRequest(string? InvitationToken);   // 邀请校验请求体：明文邀请令牌（邮件链接令牌或注册码/CDK）
public sealed record RegisterRequest(string? InvitationToken, string Password, string DisplayName, string? Email = null); // 注册请求体：邀请令牌 + 密码 + 昵称 + 邮箱（仅 CDK 渠道必填）
public sealed record LoginRequest(string? Email, string? Username, string Password); // 登录请求体：邮箱或用户名 + 密码
public sealed record RefreshTokenRequest(string? RefreshToken);           // 刷新/登出请求体：刷新令牌（登出时可空）
public sealed record InvitationValidationResponse(string Email, string Channel, DateTime ExpiresAt); // 邀请校验成功响应：脱敏邮箱（CDK 为空）+ 渠道(email|code) + 到期时间（UTC）
