// 职责：点歌需求审核状态机（纯函数，无 IO、无依赖）。
//
// 从 Controllers/SongRequestController.cs 中抽出的领域规则：
// 控制器只负责 HTTP 边界（取参数、返回契约错误体），状态流转合法性由这里判定。
// 抽出后可直接单元测试，且后端新增/调整状态时只需改这一处、由测试锁定。

using ShineMusic.Model.Entities;

namespace ShineMusic.Model.Domain;

/// <summary>
/// 点歌需求的状态流转规则。
/// </summary>
/// <remarks>
/// 取值与数据库 <c>SongRequestRecord.Status</c>（varchar）一一对应，
/// 字面量集中在 <see cref="SongRequestStatuses"/>。
/// </remarks>
public static class SongRequestStateMachine
{
    /// <summary>
    /// 合法状态流转白名单：允许向前推进/跳级，禁止回退，终态无出口。
    /// 例外是 declined/withdrawn → open：管理员可在补到音源后重新开启（走专用 reopen 端点）。
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> AllowedTransitions = new()
    {
        [SongRequestStatuses.Open] = [SongRequestStatuses.Planned, SongRequestStatuses.InProgress, SongRequestStatuses.Fulfilled, SongRequestStatuses.Declined, SongRequestStatuses.Withdrawn],
        [SongRequestStatuses.Planned] = [SongRequestStatuses.InProgress, SongRequestStatuses.Fulfilled, SongRequestStatuses.Declined, SongRequestStatuses.Withdrawn],
        [SongRequestStatuses.InProgress] = [SongRequestStatuses.Fulfilled, SongRequestStatuses.Declined],
        [SongRequestStatuses.Fulfilled] = [],
        [SongRequestStatuses.Declined] = [SongRequestStatuses.Open],
        [SongRequestStatuses.Withdrawn] = [SongRequestStatuses.Open]
    };

    /// <summary>
    /// 判断一次状态变更是否被允许。
    /// </summary>
    /// <param name="from">当前状态（数据库中已归一化的小写取值）。</param>
    /// <param name="to">目标状态。</param>
    /// <returns>合法返回 true；未知来源状态一律视为不合法（fail-closed）。</returns>
    public static bool IsTransitionAllowed(string from, string to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>
    /// 是否为终态：终态没有正常流转出口。declined/withdrawn 均可经专用 reopen 端点
    /// 重新开启，故只有 fulfilled（已入库）才算不可逆终态。
    /// </summary>
    public static bool IsTerminal(string status) =>
        status is SongRequestStatuses.Fulfilled;

    /// <summary>
    /// 状态字面量是否为已知取值。
    /// </summary>
    public static bool IsKnownStatus(string status) => SongRequestStatuses.All.Contains(status);
}
