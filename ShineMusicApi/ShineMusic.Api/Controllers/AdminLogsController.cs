using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>
/// 管理员日志查询端点：登录审计（含失败与客户端画像）与操作审计（管理员动作留痕）。
/// </summary>
[ApiController]
[Route("api/v1/admin/logs")]
[Authorize(Roles = "admin")]
[Tags("Admin Logs")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class AdminLogsController : ControllerBase
{
    /// <summary>
    /// 【GET /api/v1/admin/logs/login】登录审计分页列表。
    /// 查询参数：result（success/failure）、query（账号/IP/设备模糊）、page、pageSize（1-100，默认 20）。
    /// </summary>
    [HttpGet("login", Name = "GetLoginAuditLogs")]
    public async Task<IResult> GetLoginLogs(
        [FromQuery] string? result,
        [FromQuery] string? query,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        ISqlSugarClient db)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize is <= 0 or > 1000 ? 20 : pageSize;

        var queryable = db.Queryable<LoginAuditEntry>();
        if (result is "success" or "failure")
            queryable = queryable.Where(e => e.Result == result);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var kw = query.Trim();
            queryable = queryable.Where(e =>
                e.Login.Contains(kw) || e.Ip.Contains(kw) || (e.DeviceInfo != null && e.DeviceInfo.Contains(kw)));
        }

        var total = await queryable.CountAsync();
        var rows = await queryable
            .OrderByDescending(e => e.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = rows
            .Select(e => new LoginAuditItem(e.Id, e.Login, e.UserId, e.Result, e.Reason, e.Ip, e.ClientType, e.DeviceInfo, e.CreatedAtUtc))
            .ToList();
        return Results.Ok(new PageResult<LoginAuditItem>(items, page, pageSize, total));
    }

    /// <summary>
    /// 【GET /api/v1/admin/logs/operations】操作审计分页列表。
    /// 查询参数：query（动作/资源/操作人/详情模糊）、page、pageSize（1-100，默认 20）。
    /// </summary>
    [HttpGet("operations", Name = "GetOperationAuditLogs")]
    public async Task<IResult> GetOperationLogs(
        [FromQuery] string? query,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        ISqlSugarClient db)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize is <= 0 or > 1000 ? 20 : pageSize;

        var queryable = db.Queryable<AdminAuditEntry>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var kw = query.Trim();
            queryable = queryable.Where(e =>
                e.Action.Contains(kw) || e.ResourceType.Contains(kw) || e.ResourceId.Contains(kw) ||
                (e.DetailsJson != null && e.DetailsJson.Contains(kw)) ||
                (e.Ip != null && e.Ip.Contains(kw)));
        }

        var total = await queryable.CountAsync();
        var rows = await queryable
            .OrderByDescending(e => e.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // 操作人昵称/邮箱一次性批量补齐（避免 N+1）。
        var actorIds = rows.Select(e => e.ActorUserId).Distinct().ToList();
        var actors = await db.Queryable<ApplicationUser>()
            .Where(u => actorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToListAsync();
        var actorMap = actors.ToDictionary(a => a.Id, a => a.DisplayName ?? a.Email);

        var items = rows
            .Select(e => new OperationAuditItem(
                e.Id, e.ActorUserId, actorMap.GetValueOrDefault(e.ActorUserId),
                e.Action, e.ResourceType, e.ResourceId, e.DetailsJson,
                e.Ip, e.ClientType, e.DeviceInfo, e.CreatedAtUtc))
            .ToList();
        return Results.Ok(new PageResult<OperationAuditItem>(items, page, pageSize, total));
    }
}
