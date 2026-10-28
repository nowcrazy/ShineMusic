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
    /// 查询参数：query（动作/资源/操作人/详情模糊）、action（动作前缀，如 track / invitation）、
    /// actor（system=系统自动任务，admin=管理员）、page、pageSize（1-1000，默认 20）。
    /// 返回时补齐操作人昵称与被操作资源的可读名称（曲名/邮箱/收藏名等），避免前端只看到 GUID。
    /// </summary>
    [HttpGet("operations", Name = "GetOperationAuditLogs")]
    public async Task<IResult> GetOperationLogs(
        [FromQuery] string? query,
        [FromQuery] string? action,
        [FromQuery] string? actor,
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
        // 动作前缀过滤：track → track.published / track.auto_published / track.updated…
        if (!string.IsNullOrWhiteSpace(action))
        {
            var prefix = action.Trim();
            queryable = queryable.Where(e => e.Action == prefix || e.Action.StartsWith(prefix + "."));
        }
        // 操作人过滤：系统自动任务（后台作业，ActorUserId 为空 Guid）与真人管理员分开查看。
        if (actor == "system")
            queryable = queryable.Where(e => e.ActorUserId == Guid.Empty);
        else if (actor == "admin")
            queryable = queryable.Where(e => e.ActorUserId != Guid.Empty);

        var total = await queryable.CountAsync();
        var rows = await queryable
            .OrderByDescending(e => e.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // 操作人昵称/邮箱一次性批量补齐（避免 N+1）；系统任务（空 Guid）直接显示为系统。
        var actorIds = rows.Select(e => e.ActorUserId).Where(id => id != Guid.Empty).Distinct().ToList();
        var actorMap = new Dictionary<Guid, string?>();
        if (actorIds.Count > 0)
        {
            var actors = await db.Queryable<ApplicationUser>()
                .Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName, u.Email })
                .ToListAsync();
            actorMap = actors.ToDictionary(a => a.Id, a => (string?)a.DisplayName ?? a.Email);
        }

        // 被操作资源的可读名称：把详情里的裸 GUID 换成「曲名 / 邮箱 / 收藏名」。
        var resourceNames = await ResolveResourceNamesAsync(db, rows);

        var items = rows
            .Select(e => new OperationAuditItem(
                e.Id, e.ActorUserId, actorMap.GetValueOrDefault(e.ActorUserId),
                e.Action, e.ResourceType, e.ResourceId, resourceNames.GetValueOrDefault(ResourceKey(e)),
                e.DetailsJson, e.Ip, e.ClientType, e.DeviceInfo, e.CreatedAtUtc))
            .ToList();
        return Results.Ok(new PageResult<OperationAuditItem>(items, page, pageSize, total));
    }

    /// <summary>资源键：资源类型 + Id，用于把审计行与批量子查询结果对应起来。</summary>
    private static string ResourceKey(AdminAuditEntry entry) =>
        entry.ResourceType.ToLowerInvariant() + "|" + entry.ResourceId;

    /// <summary>
    /// 按资源类型批量取可读名称（每类一次查询，避免逐行 N+1）。
    /// 未登记的类型（artwork、storage、import_batch 等无自然名称的）保持为空。
    /// </summary>
    private static async Task<Dictionary<string, string>> ResolveResourceNamesAsync(
        ISqlSugarClient db, List<AdminAuditEntry> rows)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = rows
            .Select(e => (Type: e.ResourceType.ToLowerInvariant(), Id: Guid.TryParse(e.ResourceId, out var id) ? id : Guid.Empty))
            .Where(x => x.Id != Guid.Empty)
            .GroupBy(x => x.Type);

        foreach (var group in groups)
        {
            var ids = group.Select(x => x.Id).Distinct().ToList();
            var map = await LoadNamesAsync(db, group.Key, ids);
            foreach (var (id, name) in map)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    names[$"{group.Key}|{id}"] = name;
            }
        }
        return names;
    }

    /// <summary>单一资源类型的名称查询；返回 Id → 名称。</summary>
    private static async Task<Dictionary<Guid, string?>> LoadNamesAsync(ISqlSugarClient db, string type, List<Guid> ids)
    {
        switch (type)
        {
            case "track":
                return await ToMap(db.Queryable<Track>().Where(t => ids.Contains(t.Id))
                    .Select(t => new NameRow { Id = t.Id, Name = t.Title }));
            case "import_item":
                return await ToMap(db.Queryable<LibraryImportItem>().Where(t => ids.Contains(t.Id))
                    .Select(t => new NameRow { Id = t.Id, Name = t.Title ?? t.DisplayName }));
            case "album":
                return await ToMap(db.Queryable<Album>().Where(t => ids.Contains(t.Id))
                    .Select(t => new NameRow { Id = t.Id, Name = t.Title }));
            case "artist":
                return await ToMap(db.Queryable<Artist>().Where(t => ids.Contains(t.Id))
                    .Select(t => new NameRow { Id = t.Id, Name = t.Name }));
            case "collection":
                return await ToMap(db.Queryable<EditorialCollection>().Where(t => ids.Contains(t.Id))
                    .Select(t => new NameRow { Id = t.Id, Name = t.Title }));
            case "song_request":
                return await ToMap(db.Queryable<SongRequestRecord>().Where(t => ids.Contains(t.Id))
                    .Select(t => new NameRow { Id = t.Id, Name = t.Title }));
            case "user":
                return await ToMap(db.Queryable<ApplicationUser>().Where(u => ids.Contains(u.Id))
                    .Select(u => new NameRow { Id = u.Id, Name = u.DisplayName ?? u.Email }));
            case "invitation":
                return await ToMap(db.Queryable<Invitation>().Where(i => ids.Contains(i.Id))
                    .Select(i => new NameRow { Id = i.Id, Name = i.Email }));
            case "source_download_task":
                return await ToMap(db.Queryable<ExternalDownloadTask>().Where(t => ids.Contains(t.Id))
                    .Select(t => new NameRow { Id = t.Id, Name = t.Title }));
            default:
                return [];
        }
    }

    /// <summary>执行名称投影并转成 Id → 名称字典。</summary>
    private static async Task<Dictionary<Guid, string?>> ToMap(ISugarQueryable<NameRow> query)
    {
        var rows = await query.ToListAsync();
        return rows.ToDictionary(row => row.Id, row => row.Name);
    }

    /// <summary>名称投影载体：Id + 可读名称。</summary>
    private sealed class NameRow
    {
        /// <summary>资源 Id。</summary>
        public Guid Id { get; set; }
        /// <summary>可读名称（可能为空）。</summary>
        public string? Name { get; set; }
    }
}
