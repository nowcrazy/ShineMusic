// 职责：管理员操作审计的统一写入入口。
// 各管理端接口在执行高权限动作（邀请、审核、发布等）时调用本类追加审计条目；
// 立即执行 INSERT，调用方如需“业务变更 + 审计留痕”同事务，应自行把两者包进 db.Ado 事务。

using System.Text.Json;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>
/// 审计写入静态帮助类：从 HTTP 上下文提取操作人并构造 <see cref="AdminAuditEntry"/> 写入数据库。
/// </summary>
public static class AdminAudit
{
    /// <summary>
    /// 写入一条管理员审计记录（立即执行 INSERT）。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文，用于获取操作人用户 Id 与请求 TraceIdentifier。</param>
    /// <param name="db">SqlSugar 客户端（事务内调用自动并入当前事务）。</param>
    /// <param name="action">动作标识（如 invitation.created、song_request.reviewed）。</param>
    /// <param name="resourceType">被操作资源的类型名。</param>
    /// <param name="resourceId">被操作资源的唯一标识。</param>
    /// <param name="details">可选的详情对象，将被 JSON 序列化保存为变更快照；为 null 则不记录详情。</param>
    /// <exception cref="InvalidOperationException">当前请求没有可解析的已认证用户 Id（sub 声明）时抛出，杜绝“无主”审计记录。</exception>
    public static void Add(HttpContext context, ISqlSugarClient db, string action, string resourceType, Guid resourceId, object? details = null)
    {
        // 操作人取自 JWT 的 sub 声明；解析失败说明未认证或令牌异常，审计不能写入，直接报错（fail-closed）。
        if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var actorUserId))
            throw new InvalidOperationException("An authenticated administrator is required to create an audit entry.");

        db.Insertable(new AdminAuditEntry
        {
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId.ToString(),
            // 详情为可选快照：序列化成 JSON 存储，null 时留空。
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            // 记录请求追踪 Id，便于把审计条目与链路日志关联排查。
            TraceId = context.TraceIdentifier
        }).ExecuteCommand();
    }
}
