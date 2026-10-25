// Swagger 安全要求补齐：Swashbuckle 不会自动把 [Authorize] 转成 OpenAPI 的 security 声明，
// 缺了它 Swagger UI 的 Authorize 按钮贴上的 Bearer 令牌不会被带进请求，受保护接口只能得到 401。
// 这里只为带 [Authorize]（含控制器级）的操作追加 Bearer 安全要求，匿名端点保持公开。
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ShineMusic.Api.OpenApi;

public sealed class BearerSecurityOperationFilter : IOperationFilter
{
    private const string SchemeName = "Bearer";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var requiresAuth = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>()
            .Any(data => data.Policy != "AllowAnonymous");
        if (!requiresAuth) return;

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = []
        });
    }
}
