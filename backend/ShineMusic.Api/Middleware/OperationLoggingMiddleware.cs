// 操作日志中间件：对所有进入的 HTTP 请求做“操作级”记录——方法、路径、用户、状态码、
// 耗时，统一写入 "Operations" category，由文件日志提供器路由到
// logs/operations/operations-YYYY-MM-DD.log。放在认证之后、端点路由之前，以便拿到登录用户。
// 即便下游抛异常（被异常处理中间件兜底），这里的 finally 仍会记录一次操作结果。

using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ShineMusic.Api.Logging;
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
                     ?? context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? "anonymous";

        // 把本次请求的 TraceId 注入关联上下文：其后的 SQL、异常、外部调用日志都会带上它，
        // 出问题顺着 TraceId 即可贯穿整条链路。请求结束还原，避免串号。
        var previousTraceId = CorrelationContext.CurrentTraceId;
        CorrelationContext.CurrentTraceId = context.TraceIdentifier;
        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            var status = context.Response.StatusCode;
            // 日志级别按结果分流：2xx/3xx 记信息，4xx（客户端/业务错误）记警告，5xx（服务端错误）记错误，
            // 这样在 operations 日志里一眼就能定位异常请求。
            var level = status >= 500 ? LogLevel.Error
                      : status >= 400 ? LogLevel.Warning
                      : LogLevel.Information;
            // 写操作日志前把关联标识设回本次请求，使 operations 日志也带上统一的 [trace:] 前缀，
            // 与 data/app 日志保持一致，便于顺着 TraceId 把整条链路串起来；写完再还原为先前值。
            CorrelationContext.CurrentTraceId = context.TraceIdentifier;
            _logger.Log(level,
                "Operation | {Method} {Path} | User={User} | Status={Status} | {ElapsedMs}ms",
                method, path, userId, status, stopwatch.ElapsedMilliseconds);
            CorrelationContext.CurrentTraceId = previousTraceId;
        }
    }
}
