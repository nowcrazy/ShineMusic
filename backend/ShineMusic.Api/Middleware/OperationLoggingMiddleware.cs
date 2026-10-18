// 操作日志中间件：对所有进入的 HTTP 请求做“操作级”记录——方法、路径、用户、状态码、
// 耗时，统一写入 "Operations" category，由文件日志提供器路由到
// logs/operations/operations-YYYY-MM-DD.log。放在认证之后、端点路由之前，以便拿到登录用户。
// 即便下游抛异常（被异常处理中间件兜底），这里的 finally 仍会记录一次操作结果。

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace ShineMusic.Api.Middleware;

public sealed class OperationLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger;

    public OperationLoggingMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
    {
        _next = next;
        // category 以 "Operations" 开头 → 文件日志提供器落入 operations 目录。
        _logger = loggerFactory.CreateLogger("Operations");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var method = context.Request.Method;
        var path = context.Request.Path + context.Request.QueryString;

        // 优先取 JWT 的 sub，其次取 NameIdentifier，匿名时为 "anonymous"。
        var userId = context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                     ?? context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? "anonymous";

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            var status = context.Response.StatusCode;
            _logger.LogInformation(
                "Operation | {Method} {Path} | User={User} | Status={Status} | {ElapsedMs}ms",
                method, path, userId, status, stopwatch.ElapsedMilliseconds);
        }
    }
}
