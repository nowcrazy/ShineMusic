using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Service;
using ShineMusic.Service.Application;

namespace ShineMusic.Api.Controllers;

/// <summary>管理员个性化推荐策略配置；策略更新从次日开始生效。</summary>
[ApiController]
[Route("api/v1/admin/recommendations/strategy")]
[Authorize(Roles = "admin")]
[Tags("Admin Recommendations")]
public sealed class RecommendationStrategyAdminController(
    RecommendationStrategyService strategyService,
    RecommendationStrategyUseCase strategyUseCase) : ControllerBase
{
    /// <summary>查询当前推荐策略及已排期的次日策略。</summary>
    [HttpGet]
    [ProducesResponseType(typeof(RecommendationStrategyAdminDto), StatusCodes.Status200OK)]
    public async Task<IResult> Get(CancellationToken cancellationToken)
    {
        var settings = await strategyService.GetAdminAsync(cancellationToken);
        return Results.Ok(settings);
    }

    /// <summary>安排新的推荐策略，于次日推荐生成时生效；业务变更与审计写入同一事务（由用例收口）。</summary>
    [HttpPut]
    [ProducesResponseType(typeof(RecommendationStrategyAdminDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IResult> Update(
        [FromBody] UpdateRecommendationStrategyRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst("sub")?.Value, out var adminUserId))
            return Results.Unauthorized();

        var settings = await strategyUseCase.ScheduleAsync(request, adminUserId, HttpContext, cancellationToken);
        return Results.Ok(settings);
    }
}
