// 职责：点歌需求状态事件的统一写入入口。
// 每次状态推进（提交/纳入计划/处理中/入库/拒绝/撤回/重新开启）追加一条事件，
// 与业务变更在同一工作单元内提交，构成对用户可见的处理时间线。

using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>需求状态事件写入帮助类。</summary>
public static class SongRequestEvents
{
    /// <summary>
    /// 追加一条需求状态事件（立即执行 INSERT，调用方如需与业务变更同事务，应自行把两者包进 db.Ado 事务）。
    /// </summary>
    /// <param name="db">SqlSugar 客户端（事务内调用自动并入当前事务）。</param>
    /// <param name="request">被操作的需求记录（读取其 Id 与提交人）。</param>
    /// <param name="kind">事件类型，取值见 <see cref="SongRequestEventKinds"/>。</param>
    /// <param name="fromStatus">变更前状态；提交事件传 null。</param>
    /// <param name="toStatus">变更后状态。</param>
    /// <param name="actorUserId">操作人用户 Id；系统事件传 null。</param>
    /// <param name="actorKind">操作人身份，取值见 <see cref="SongRequestActorKinds"/>。</param>
    /// <param name="note">可选备注（拒绝原因/重新开启原因等）。</param>
    public static void Add(
        ISqlSugarClient db,
        SongRequestRecord request,
        string kind,
        string? fromStatus,
        string? toStatus,
        Guid? actorUserId,
        string actorKind,
        string? note = null)
    {
        db.Insertable(new SongRequestEventRecord
        {
            RequestId = request.Id,
            Kind = kind,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ActorUserId = actorUserId,
            ActorKind = actorKind,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        }).ExecuteCommand();
    }

    /// <summary>
    /// 把目标状态映射为事件类型；未知状态回退为 submitted（保证时间线不会因新增状态而中断）。
    /// </summary>
    public static string KindForStatus(string status) => status switch
    {
        SongRequestStatuses.Planned => SongRequestEventKinds.Planned,
        SongRequestStatuses.InProgress => SongRequestEventKinds.InProgress,
        SongRequestStatuses.Fulfilled => SongRequestEventKinds.Fulfilled,
        SongRequestStatuses.Declined => SongRequestEventKinds.Declined,
        SongRequestStatuses.Withdrawn => SongRequestEventKinds.Withdrawn,
        _ => SongRequestEventKinds.Submitted
    };
}
