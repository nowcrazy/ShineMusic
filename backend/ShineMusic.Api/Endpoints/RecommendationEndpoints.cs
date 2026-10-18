// =============================================================================
// 文件：RecommendationEndpoints.cs
// 端点组：个性化推荐。
// 路由前缀：/api/v1/recommendations
// 鉴权边界：匿名可访问；携带有效 JWT 时返回该用户的个性化结果，否则返回热度/最新兜底。
// 能力开关：PersonalizedRecommendations（关闭时整组返回 404）。
// 覆盖：
//   GET /recommendations/daily?limit=30   每日推荐（默认 30 首、互不重复、当日稳定）
// =============================================================================
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Extensions;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>Serves the daily recommendation list for the current (optional) user.</summary>
[ApiController]
[Route("api/v1/recommendations")]
[FeatureGate(nameof(FeatureOptions.PersonalizedRecommendations))]
[Tags("Recommendations")]
public sealed class RecommendationController(RecommendationService recommendations) : ControllerBase
{
    [HttpGet("daily", Name = "GetDailyRecommendations")]
    public async Task<IResult> GetDaily(int? limit, CancellationToken cancellationToken)
    {
        // limit 夹紧到 [1, 50]，默认 30。
        var size = Math.Clamp(limit ?? 30, 1, 50);
        // 登录用户（sub 可解析）走个性化；匿名则 userId 为 null，服务内部走兜底。
        var userId = HttpContext.User.GetUserId();

        var result = await recommendations.GetDailyAsync(userId, size, cancellationToken);
        var dto = new DailyRecommendationDto(
            result.Date.ToString("yyyy-MM-dd"),
            result.Personalized,
            result.Tracks.Select(CatalogController.ToDto).ToArray());
        return Results.Ok(dto);
    }
}
