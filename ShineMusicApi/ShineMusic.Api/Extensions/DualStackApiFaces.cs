// =============================================================================
// 文件：Extensions/DualStackApiFaces.cs
// 职责：Admin / App 双 API 面（P4）。
//
// 设计取舍：P2 已把控制器物理分为 Controllers/Admin、Controllers/App、Controllers/Shared，
// 但历史路由仍是单一 /api/v1 命名空间（管理端靠 /api/v1/admin 子路径区分）。
// 为了不打断 54 个前端文件与 67 步链路脚本，这里采用「兼容入口 + 路径重写」：
//   /admin/v1/<rest>  →  /api/v1/admin/<rest>
//   /app/v1/<rest>    →  /api/v1/<rest>
// 旧 /api/v1 完全不变；新增两个前缀只是同一组端点的新入口，鉴权/限流/错误体全部复用。
// 重写发生在 UseRouting 之前，因此端点匹配、[Authorize]、[EnableRateLimiting] 都照常生效。
// 未来若要真正拆成两个独立进程，只需把这一段替换成网关/BFF，控制器不必再改。
// =============================================================================

using Microsoft.AspNetCore.Http;

namespace ShineMusic.Api.Extensions;

/// <summary>双栈 API 面的常量与纯路径映射逻辑（可单测）。</summary>
public static class DualStackApiFaces
{
    /// <summary>HttpContext.Items 中记录本次请求来自哪个 API 面的键。</summary>
    public const string StackItemKey = "ShineMusic.ApiStack";

    /// <summary>回传给客户端的 API 面标识响应头。</summary>
    public const string StackHeaderName = "X-ShineMusic-Api-Stack";

    /// <summary>管理端入口前缀。</summary>
    public const string AdminPrefix = "/admin/v1";

    /// <summary>App 端入口前缀。</summary>
    public const string AppPrefix = "/app/v1";

    /// <summary>管理端。</summary>
    public const string AdminStack = "admin";

    /// <summary>App 端。</summary>
    public const string AppStack = "app";

    /// <summary>
    /// 把双栈入口路径映射回历史 /api/v1 路径。只有精确前缀或前缀后跟 '/' 才匹配，
    /// 避免 /admin/v1x 这类前缀误吞。未命中时返回 false 且 rewritten 等于原值。
    /// </summary>
    public static bool TryRewrite(string path, out string rewritten, out string stack)
    {
        rewritten = path;
        stack = string.Empty;
        if (TryMap(path, AdminPrefix, "/api/v1/admin", out rewritten))
        {
            stack = AdminStack;
            return true;
        }
        if (TryMap(path, AppPrefix, "/api/v1", out rewritten))
        {
            stack = AppStack;
            return true;
        }
        rewritten = path;
        return false;
    }

    private static bool TryMap(string path, string prefix, string target, out string mapped)
    {
        mapped = path;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        // 前缀必须正好结束，或后面紧跟路径分隔符。
        if (path.Length > prefix.Length && path[prefix.Length] != '/') return false;
        mapped = target + path[prefix.Length..];
        return true;
    }
}

/// <summary>把双栈入口注册到管道最前端（必须在 UseRouting 之前）。</summary>
public static class DualStackApiFacesExtensions
{
    /// <summary>注册 Admin/App 双栈兼容入口的路径重写中间件。</summary>
    public static IApplicationBuilder UseDualStackApiFaces(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var originalPath = context.Request.Path.Value ?? string.Empty;
            if (DualStackApiFaces.TryRewrite(originalPath, out var rewritten, out var stack))
            {
                context.Request.Path = new PathString(rewritten);
                context.Items[DualStackApiFaces.StackItemKey] = stack;
                context.Response.Headers[DualStackApiFaces.StackHeaderName] = stack;
            }
            await next();
        });
}
