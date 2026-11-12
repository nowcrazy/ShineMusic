// =============================================================================
// 文件：SystemController.cs
// 端点组：系统（System）—— 健康检查与客户端运行时能力（功能开关、上传限制）查询。
// 路由前缀：无统一前缀；健康检查为 GET /health，能力查询为 GET /api/v1/system/capabilities。
// 鉴权边界：两个端点均允许匿名访问（无 [Authorize]），供客户端启动探活与能力发现。
// =============================================================================
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Options;

namespace ShineMusic.Api.Controllers;

/// <summary>Provides health and runtime capability information for clients. 系统端点：为客户端提供健康检查与运行时功能开关/上传约束等能力信息。</summary>
[ApiController]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class SystemController : ControllerBase
{
    // 【GET /health】存活/就绪探针（匿名）。无参数、无依赖；始终返回 200 与固定载荷 { status = "ok" }。
    [HttpGet("/health", Name = "HealthCheck")]
    public IActionResult GetHealth()
    {
        return Ok(new { status = "ok" });
    }

    // 【GET /api/v1/system/capabilities】查询服务端功能矩阵（匿名）。
    // 依赖注入：featureOptions（功能开关）、uploadOptions（允许格式与单文件大小上限），均来自配置。
    // 返回：200 SystemCapabilitiesDto；前端据此决定是否展示收藏、点歌、导入等入口。
    [HttpGet("/api/v1/system/capabilities", Name = "GetSystemCapabilities")]
    [Produces("application/json")]
    public IActionResult GetCapabilities(
        IOptions<FeatureOptions> featureOptions,
        IOptions<UploadOptions> uploadOptions,
        IOptions<PasswordPolicyOptions> passwordPolicy)
    {
        var features = featureOptions.Value;
        var uploads = uploadOptions.Value;
        // 仅当“管理端导入 + 人工审核 + 服务端转码”三个开关同时开启时，才向客户端宣告批量导入能力可用。
        return Ok(new SystemCapabilitiesDto(
            features.AnonymousPlayback,
            features.UserCollections,
            features.SongRequests,
            features.AdminLibraryImport && features.ManualReview && features.ServerTranscoding,
            features.ManualReview,
            features.EditorialCollections,
            features.ListeningEvents,
            features.PersonalizedRecommendations,
            features.ServerTranscoding,
            features.UserUploads,
            uploads.AllowedFormats,
            uploads.MaxFileSizeBytes,
            passwordPolicy.Value.MinimumStrength));
    }
}
