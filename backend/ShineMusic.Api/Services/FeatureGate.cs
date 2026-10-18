// 职责：功能能力开关（Feature Gate）。
// 通过 [FeatureGate("xxx")] 特性标注在 Controller/Action 上，运行时按配置（FeatureOptions）
// 判定对应功能是否开放；关闭时返回 404（而非 403），对外表现为“资源不存在”，
// 既控制能力可用性，也避免暴露未开放功能的存在性。

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Options;

namespace ShineMusic.Api.Services;

/// <summary>当配置中对应功能不可用时，将 API 动作隐藏（返回 404）的 MVC 过滤器特性。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class FeatureGateAttribute(string featureName) : Attribute, IFilterFactory
{
    // 不可复用：每次请求都通过工厂创建过滤器实例，确保按请求从 DI 解析最新配置。
    public bool IsReusable => false;

    /// <summary>由 MVC 管道调用：从 DI 容器获取 FeatureOptions 并创建实际执行判定的动作过滤器。</summary>
    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new FeatureGateActionFilter(featureName, serviceProvider.GetRequiredService<IOptions<FeatureOptions>>());
}

/// <summary>
/// 实际执行能力开关判定的异步动作过滤器（内部实现，由 <see cref="FeatureGateAttribute"/> 工厂创建）。
/// </summary>
internal sealed class FeatureGateActionFilter(string featureName, IOptions<FeatureOptions> featureOptions) : IAsyncActionFilter
{
    /// <summary>动作执行前判定功能开关；未开放则短路管道返回 404，开放则继续执行后续过滤器与动作。</summary>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var features = featureOptions.Value;
        // 能力判定映射：特性名 → 配置开关组合。
        var enabled = featureName switch
        {
            // 编辑精选集合：单一开关直接控制。
            nameof(FeatureOptions.EditorialCollections) => features.EditorialCollections,
            // 用户自建歌单/收藏：单一开关。
            nameof(FeatureOptions.UserCollections) => features.UserCollections,
            // 点歌/求歌：单一开关。
            nameof(FeatureOptions.SongRequests) => features.SongRequests,
            // 用户上传本地音频（扫码导入）：单一开关。
            nameof(FeatureOptions.UserUploads) => features.UserUploads,
            // 管理端曲库导入是组合能力：必须同时开启“导入功能 + 人工审核 + 服务端转码”三个前置开关，缺一不可。
            nameof(FeatureOptions.AdminLibraryImport) => features.AdminLibraryImport && features.ManualReview && features.ServerTranscoding,
            // 收听事件采集：单一开关。
            nameof(FeatureOptions.ListeningEvents) => features.ListeningEvents,
            // 个性化推荐：单一开关。
            nameof(FeatureOptions.PersonalizedRecommendations) => features.PersonalizedRecommendations,
            // 未知特性名一律按“关闭”处理（fail-closed），防止误配导致功能意外暴露。
            _ => false
        };

        if (!enabled)
        {
            // 返回 404 而非 403：对无权限/未开放功能统一伪装成资源不存在，减少接口面暴露。
            context.Result = new NotFoundObjectResult(new ApiErrorResponse(new ApiErrorDetail(
                ErrorCodes.ResourceNotFound,
                "The requested resource was not found.",
                context.HttpContext.TraceIdentifier)));
            return;
        }

        await next();
    }
}
