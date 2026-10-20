// =============================================================================
// 文件：InvitationEndpoints.cs
// 端点组：管理端邀请（Admin Invitations）—— 管理员创建/查询/重发/撤销注册邀请。
// 路由前缀：/api/v1/admin/invitations（类级统一前缀）。
// 鉴权边界：类级 [Authorize(Roles = "admin")]，所有端点仅管理员可访问；非管理员由
//           认证/授权中间件直接拦截（401 未认证 / 403 角色不足），不会进入 Action。
// 业务规则：邀请明文令牌仅在创建/重发当次生成并通过邮件下发，数据库只存哈希；
//           同一邮箱存在未过期有效邀请或已注册账号时禁止重复创建；所有写操作记 AdminAudit。
// =============================================================================
using System.Net.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>Manages administrator-issued invitations and email delivery state. 管理端邀请端点：维护邀请记录的生命周期（创建、分页查询、重发、撤销）与邮件投递状态。</summary>
[ApiController]
[Route("api/v1/admin/invitations")]
[Authorize(Roles = "admin")]
[Tags("Admin Invitations")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class InvitationController : ControllerBase
{
    // 【POST /api/v1/admin/invitations】创建邀请并发送邀请邮件（管理员）。
    // 请求体：CreateInvitationRequest.Email。
    // 返回：201 Created（邮件投递成功，载荷 InvitationDto）；400 邮箱格式非法；
    //       409 resource_conflict（该邮箱已注册，或已存在未过期有效邀请）；
    //       503 email_delivery_failed（邀请已落库但邮件发送失败，状态置为 DeliveryFailed，可重发）。
    [HttpPost("", Name = "CreateInvitation")]
    public async Task<IResult> CreateInvitation(
        CreateInvitationRequest request,
        MusicDbContext db,
        UserManager<ApplicationUser> userManager,
        IInvitationEmailSender emailSender,
        IOptions<AuthOptions> authOptions,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeEmail(request.Email, out var email, out var normalizedEmail))
            return ApiResults.ValidationError(HttpContext, "email", "A valid email address is required."); // 400：邮箱缺失/格式非法
        if (await userManager.FindByEmailAsync(email) is not null)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "An account already exists for this email.", HttpContext.TraceIdentifier))); // 409：邮箱已注册

        var now = DateTime.UtcNow;
        var existingPending = await db.Invitations.AnyAsync(invitation =>
            invitation.NormalizedEmail == normalizedEmail &&
            invitation.Status == InvitationStatuses.Sent && invitation.ExpiresAtUtc > now,
            cancellationToken);
        if (existingPending)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "An active invitation already exists for this email.", HttpContext.TraceIdentifier))); // 409：已有未过期的 Sent 邀请

        // 生成一次性明文令牌（仅随邮件下发），数据库只存哈希；到期时长取自 AuthOptions。
        var secret = TokenService.CreateRandomToken();
        var expiresAt = now.AddHours(authOptions.Value.InvitationExpiryHours);
        var invitation = new Invitation
        {
            Email = email,
            NormalizedEmail = normalizedEmail,
            TokenHash = TokenService.HashToken(secret),
            CreatedByUserId = Guid.Parse(HttpContext.User.FindFirst("sub")!.Value),
            Status = InvitationStatuses.Created,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAt
        };
        db.Invitations.Add(invitation);
        AdminAudit.Add(HttpContext, db, "invitation.created", "invitation", invitation.Id);
        await db.SaveChangesAsync(cancellationToken);
        // 投递结果决定最终返回 201 还是 503；明文 secret 不会出现在任何响应中。
        return await DeliverInvitation(invitation, secret, db, emailSender, HttpContext, cancellationToken);
    }

    // 【POST /api/v1/admin/invitations/code】生成注册码/CDK（管理员）。
    // 注册码不绑定邮箱：生成时 Email 为空、状态为 issued；注册时由用户自填合法邮箱。
    // 明文注册码仅在本响应中返回一次，数据库只存哈希；重复生成换发新码。
    // 返回：201 InvitationCodeResponse（明文 code + InvitationDto）。
    [HttpPost("code", Name = "CreateInvitationCode")]
    public async Task<IResult> CreateInvitationCode(
        MusicDbContext db,
        IOptions<AuthOptions> authOptions,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var code = TokenService.CreateRegistrationCode();
        var invitation = new Invitation
        {
            Email = null,
            NormalizedEmail = null,
            TokenHash = TokenService.HashToken(code),
            CreatedByUserId = Guid.Parse(HttpContext.User.FindFirst("sub")!.Value),
            Status = InvitationStatuses.Issued,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(authOptions.Value.InvitationExpiryHours)
        };
        db.Invitations.Add(invitation);
        AdminAudit.Add(HttpContext, db, "invitation.code_created", "invitation", invitation.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/admin/invitations/{invitation.Id}",
            new InvitationCodeResponse(code, ToDto(invitation)));
    }

    // 【GET /api/v1/admin/invitations】分页查询邀请列表（管理员）。
    // 查询参数：status（可选；pending 为聚合虚拟状态=Created/Sent/DeliveryFailed 且未过期，
    //           其余值必须是 InvitationStatuses.All 中的精确状态）、page（默认 1）、pageSize（默认 20，1~100）。
    // 返回：200 PageResult<InvitationDto>（按创建时间倒序）；400 分页参数或 status 非法。
    [HttpGet("", Name = "ListInvitations")]
    public async Task<IResult> ListInvitations(
        string? status,
        MusicDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var pagingError, out var pageNumber, out var size))
            return pagingError!; // 400：分页参数越界

        IQueryable<Invitation> query = db.Invitations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToLowerInvariant();
            if (normalizedStatus == "pending")
            {
                // 虚拟状态 pending：所有“尚未核销/撤销且未过期”的投递中状态合集（含注册码 issued）。
                var now = DateTime.UtcNow;
                query = query.Where(invitation => invitation.ExpiresAtUtc > now &&
                    (invitation.Status == InvitationStatuses.Created ||
                     invitation.Status == InvitationStatuses.Sent ||
                     invitation.Status == InvitationStatuses.Issued ||
                     invitation.Status == InvitationStatuses.DeliveryFailed));
            }
            else
            {
                if (!InvitationStatuses.All.Contains(normalizedStatus))
                    return ApiResults.ValidationError(HttpContext, "status", "The invitation status is not supported."); // 400：状态枚举非法
                query = query.Where(invitation => invitation.Status == normalizedStatus);
            }
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(invitation => invitation.CreatedAtUtc)
            .Skip((pageNumber - 1) * size).Take(size)
            .Select(invitation => new InvitationDto(
                invitation.Id,
                invitation.Email,
                invitation.Status,
                AsUtc(invitation.CreatedAtUtc),
                AsUtc(invitation.ExpiresAtUtc),
                invitation.SentAtUtc.HasValue ? AsUtc(invitation.SentAtUtc.Value) : null,
                invitation.RedeemedAtUtc.HasValue ? AsUtc(invitation.RedeemedAtUtc.Value) : null))
            .ToListAsync(cancellationToken);
        return Results.Ok(new PageResult<InvitationDto>(items, pageNumber, size, total));
    }

    // 【POST /api/v1/admin/invitations/{invitationId:guid}/resend】重发邀请（管理员）。
    // 路由参数：invitationId；成功时等价于“重置”该邀请：更换令牌、重置到期时间、清空 SentAt。
    // 返回：201 InvitationDto（重新投递成功）；404 邀请不存在；409 已核销/已撤销不可重发；503 邮件投递失败。
    [HttpPost("{invitationId:guid}/resend", Name = "ResendInvitation")]
    public async Task<IResult> ResendInvitation(
        Guid invitationId,
        MusicDbContext db,
        IInvitationEmailSender emailSender,
        IOptions<AuthOptions> authOptions,
        CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        if (invitation is null)
            return ApiResults.NotFound(HttpContext); // 404
        if (invitation.Status is InvitationStatuses.Redeemed or InvitationStatuses.Revoked)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "This invitation can no longer be resent.", HttpContext.TraceIdentifier))); // 409：终态邀请不可重发

        // 旧令牌立即作废：换发新随机令牌、状态回到 Created、到期时间重新起算、SentAt 清空。
        var now = DateTime.UtcNow;
        var secret = TokenService.CreateRandomToken();
        invitation.TokenHash = TokenService.HashToken(secret);
        invitation.Status = InvitationStatuses.Created;
        invitation.CreatedAtUtc = now;
        invitation.ExpiresAtUtc = now.AddHours(authOptions.Value.InvitationExpiryHours);
        invitation.SentAtUtc = null;
        AdminAudit.Add(HttpContext, db, "invitation.resent", "invitation", invitation.Id);
        await db.SaveChangesAsync(cancellationToken);
        return await DeliverInvitation(invitation, secret, db, emailSender, HttpContext, cancellationToken);
    }

    // 【DELETE /api/v1/admin/invitations/{invitationId:guid}】撤销邀请（管理员）。
    // 返回：204 撤销成功；404 不存在；409 已核销/已撤销（终态不可再次撤销）。
    [HttpDelete("{invitationId:guid}", Name = "RevokeInvitation")]
    public async Task<IResult> RevokeInvitation(
        Guid invitationId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        if (invitation is null)
            return ApiResults.NotFound(HttpContext); // 404
        if (invitation.Status is InvitationStatuses.Redeemed or InvitationStatuses.Revoked)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "This invitation can no longer be revoked.", HttpContext.TraceIdentifier))); // 409

        invitation.Status = InvitationStatuses.Revoked;
        AdminAudit.Add(HttpContext, db, "invitation.revoked", "invitation", invitation.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>发送邀请邮件并落库投递结果；明文一次性邀请令牌不会出现在任何响应中。</summary>
    /// <param name="invitation">邀请实体（更新其 Status/SentAt 并保存）。</param>
    /// <param name="secret">本次生成的明文邀请令牌，仅传给邮件发送器。</param>
    /// <param name="db">DI：持久化投递状态。</param>
    /// <param name="emailSender">DI：邮件发送抽象。</param>
    /// <param name="ctx">DI：失败时记录日志、取 TraceIdentifier。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>201 + InvitationDto（Sent）；503 email_delivery_failed（已落库 DeliveryFailed，允许后续重发）。</returns>
    // 投递失败时把失败状态持久化，但绝不把一次性邀请明文令牌泄露到响应中。
    private static async Task<IResult> DeliverInvitation(
        Invitation invitation,
        string secret,
        MusicDbContext db,
        IInvitationEmailSender emailSender,
        HttpContext ctx,
        CancellationToken cancellationToken)
    {
        try
        {
            await emailSender.SendInvitationAsync(invitation.Email!, secret, invitation.ExpiresAtUtc, cancellationToken);
            invitation.Status = InvitationStatuses.Sent;
            invitation.SentAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/v1/admin/invitations/{invitation.Id}", ToDto(invitation));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 邀请记录已先于邮件落库：失败时标记 DeliveryFailed（用 CancellationToken.None 确保状态仍被写入），
            // 管理员之后可通过 resend 重新触发投递。
            invitation.Status = InvitationStatuses.DeliveryFailed;
            await db.SaveChangesAsync(CancellationToken.None);
            ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("InvitationEmail")
                .LogWarning(exception, "Invitation email delivery failed for invitation {InvitationId}.", invitation.Id);
            return Results.Json(
                new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.EmailDeliveryFailed, "The invitation was created but the email could not be delivered.", ctx.TraceIdentifier)),
                statusCode: StatusCodes.Status503ServiceUnavailable); // 503：资源已创建但外部依赖（邮件）不可用
        }
    }

    /// <summary>校验并规范化邮箱：必须能被 MailAddress 解析且字符串本身就是完整地址（拒收显示名等冗余内容）。</summary>
    /// <param name="value">原始输入。</param>
    /// <param name="email">输出：规范化邮箱原文。</param>
    /// <param name="normalizedEmail">输出：大写形式，用于唯一比较。</param>
    /// <returns>合法返回 true；空值或格式错误返回 false。</returns>
    private static bool TryNormalizeEmail(string? value, out string email, out string normalizedEmail)
    {
        email = string.Empty;
        normalizedEmail = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            var parsed = new MailAddress(value.Trim());
            if (!string.Equals(parsed.Address, value.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
            email = parsed.Address;
            normalizedEmail = parsed.Address.ToUpperInvariant();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>把邀请实体投影为 InvitationDto，并把时间标记为 UTC Kind。</summary>
    private static InvitationDto ToDto(Invitation invitation) => new(
        invitation.Id,
        invitation.Email,
        invitation.Status,
        AsUtc(invitation.CreatedAtUtc),
        AsUtc(invitation.ExpiresAtUtc),
        invitation.SentAtUtc.HasValue ? AsUtc(invitation.SentAtUtc.Value) : null,
        invitation.RedeemedAtUtc.HasValue ? AsUtc(invitation.RedeemedAtUtc.Value) : null);

    /// <summary>把 DateTime 的 Kind 显式指定为 Utc，保证序列化为带 Z 的 ISO 时间。</summary>
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>解析并校验分页参数：page 默认 1 且 ≥1；pageSize 默认 20，范围 1~100。</summary>
    /// <param name="ctx">DI 上下文，用于构造错误响应。</param>
    /// <param name="page">查询字符串 page。</param>
    /// <param name="pageSize">查询字符串 pageSize。</param>
    /// <param name="error">输出：校验失败时的 400 IResult。</param>
    /// <param name="pageNumber">输出：规范化后的页码。</param>
    /// <param name="size">输出：规范化后的页大小。</param>
    /// <returns>参数合法返回 true；否则 false（此时 error 非空）。</returns>
    private static bool TryGetPaging(HttpContext ctx, int? page, int? pageSize, out IResult? error, out int pageNumber, out int size)
    {
        pageNumber = page ?? 1;
        size = pageSize ?? 20;
        error = null;
        if (pageNumber < 1)
            error = ApiResults.ValidationError(ctx, "page", "page must be at least 1.");
        else if (size is < 1 or > 100)
            error = ApiResults.ValidationError(ctx, "pageSize", "pageSize must be between 1 and 100.");
        return error is null;
    }


}

public sealed record CreateInvitationRequest(string? Email); // 创建邀请请求体：受邀邮箱
public sealed record InvitationDto(Guid Id, string? Email, string Status, DateTime CreatedAt, DateTime ExpiresAt, DateTime? SentAt, DateTime? RedeemedAt); // 邀请响应 DTO
public sealed record InvitationCodeResponse(string Code, InvitationDto Invitation); // 注册码生成响应：明文注册码（仅一次）+ 邀请记录
