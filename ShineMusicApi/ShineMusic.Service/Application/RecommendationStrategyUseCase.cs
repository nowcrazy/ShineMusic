// 职责：推荐策略变更用例（Application 层）。
// 把「业务变更 + 审计留痕」的事务边界从 Controller 移到用例：接口层不再碰 ISqlSugarClient，
// 也不再需要知道 BeginTran / CommitTran / RollbackTran 的顺序。
// 说明：当前事务仍直接依赖 ISqlSugarClient，属于渐进式改造的已知耦合；
//       P6 的架构约束只要求「接口层不得引用基础设施」，应用层后续可再引入 IUnitOfWork 端口。

using ShineMusic.Model.Contracts;
using SqlSugar;

namespace ShineMusic.Service.Application;

/// <summary>管理员推荐策略用例。</summary>
public sealed class RecommendationStrategyUseCase(
    RecommendationStrategyService strategyService,
    ISqlSugarClient db)
{
    /// <summary>安排次日生效的策略，并在同一事务内写入审计记录；返回更新后的策略视图。</summary>
    public async Task<RecommendationStrategyAdminDto> ScheduleAsync(
        UpdateRecommendationStrategyRequest request,
        Guid adminUserId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await db.Ado.BeginTranAsync();
        try
        {
            var effectiveDate = await strategyService.ScheduleForNextDayAsync(request, adminUserId, cancellationToken);
            var settings = await strategyService.GetAdminAsync(cancellationToken);
            AdminAudit.Add(context, db, "recommendation.strategy_scheduled", "recommendation_strategy",
                Guid.Empty, new { effectiveDate, request });
            await db.Ado.CommitTranAsync();
            return settings;
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }
}
