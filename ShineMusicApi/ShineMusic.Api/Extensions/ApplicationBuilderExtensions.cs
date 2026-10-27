// Program.cs 的中间件管道扩展：把异常处理、兜底错误体、响应压缩、路由、CORS、
// 操作日志、认证、授权、限流、端点映射等管道配置收敛为 WebApplication 扩展方法。
// 管道顺序敏感，务必保持：ForwardedHeaders → ExceptionHandler → 兜底错误体 →
// ResponseCompression → Routing → CORS → OperationLogging → Authentication →
// Authorization → RateLimiter → MapControllers。

using Microsoft.AspNetCore.Diagnostics;
using ShineMusic.Model.Contracts;

namespace ShineMusic.Api.Extensions;

/// <summary>WebApplication 中间件管道扩展。</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>配置 ShineMusic 完整中间件管道（顺序敏感）。</summary>
    public static WebApplication UseShineMusicPipeline(this WebApplication app)
    {
        // 反向代理头必须放在管道最前面：先把 X-Forwarded-For/Proto 回填到 HttpContext。
        app.UseForwardedHeaders();

        // 未处理异常统一收敛为 500 JSON。
        app.UseExceptionHandler(exceptionHandler => exceptionHandler.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            // 客户端主动断连：不是服务端故障，返回 499（Nginx 约定）。
            if (exception is OperationCanceledException)
            {
                context.Response.StatusCode = 499;
                return;
            }
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Errors.UnhandledException")
                .LogError(exception, "Unhandled exception on {Method} {Path} (TraceId={TraceId}).",
                    context.Request.Method, context.Request.Path, context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await Results.Json(new ApiErrorResponse(
                new ApiErrorDetail(ErrorCodes.InternalError, "An unexpected error occurred.", context.TraceIdentifier)))
                .ExecuteAsync(context);
        }));

        // 兜底错误体：把「没有响应体」的失败状态（404/405 等）补齐成自定义错误体。
        app.Use(async (context, next) =>
        {
            await next();
            var response = context.Response;
            if (response.StatusCode < StatusCodes.Status400BadRequest) return;
            if (response.HasStarted) return;
            if (!string.IsNullOrEmpty(response.ContentType)) return;
            if (response.ContentLength is > 0) return;

            response.ContentType = "application/json";
            await response.WriteAsJsonAsync(new ApiErrorResponse(
                new ApiErrorDetail(
                    ResolveStatusCodeCode(response.StatusCode),
                    $"接口不存在或请求方式不被支持（HTTP {response.StatusCode}）。",
                    context.TraceIdentifier)));
        });

        app.UseResponseCompression();
        app.UseRouting();
        app.UseCors("Client");
        // 操作日志中间件注册在认证之前，使 401/403 也被记录。
        app.UseMiddleware<ShineMusic.Common.Middleware.OperationLoggingMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapControllers();
        return app;
    }

    /// <summary>兜底错误体的状态码 → 错误码映射（与 Contracts/ErrorCodes 保持一致）。</summary>
    private static string ResolveStatusCodeCode(int statusCode) => statusCode switch
    {
        400 => ErrorCodes.ValidationError,
        401 => ErrorCodes.AuthenticationRequired,
        403 => ErrorCodes.PermissionDenied,
        404 => ErrorCodes.ResourceNotFound,
        405 => ErrorCodes.ResourceNotFound,
        406 => ErrorCodes.UnsupportedMediaFormat,
        408 => ErrorCodes.InternalError,
        413 => ErrorCodes.PayloadTooLarge,
        415 => ErrorCodes.UnsupportedMediaFormat,
        429 => ErrorCodes.RateLimited,
        >= 500 => ErrorCodes.InternalError,
        _ => ErrorCodes.ResourceNotFound
    };
}
