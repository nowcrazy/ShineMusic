// 职责：锁定 P4 双栈兼容入口的路径映射，避免重写规则被后来者改坏导致整个 API 面失效。

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ShineMusic.Api.Extensions;
using Xunit;

namespace ShineMusic.Tests.Architecture;

public class DualStackApiFacesTests
{
    [Theory]
    [InlineData("/admin/v1/users", "/api/v1/admin/users", DualStackApiFaces.AdminStack)]
    [InlineData("/admin/v1", "/api/v1/admin", DualStackApiFaces.AdminStack)]
    [InlineData("/admin/v1/library/import-items", "/api/v1/admin/library/import-items", DualStackApiFaces.AdminStack)]
    [InlineData("/app/v1/auth/login", "/api/v1/auth/login", DualStackApiFaces.AppStack)]
    [InlineData("/app/v1/me/playlist-imports/parse", "/api/v1/me/playlist-imports/parse", DualStackApiFaces.AppStack)]
    [InlineData("/app/v1/artwork/2b7f0000-0000-0000-0000-000000000000", "/api/v1/artwork/2b7f0000-0000-0000-0000-000000000000", DualStackApiFaces.AppStack)]
    public void Maps_dual_stack_prefixes_to_legacy_routes(string path, string expected, string expectedStack)
    {
        Assert.True(DualStackApiFaces.TryRewrite(path, out var rewritten, out var stack));
        Assert.Equal(expected, rewritten);
        Assert.Equal(expectedStack, stack);
    }

    [Theory]
    [InlineData("/api/v1/admin/users")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/admin/v1x/users")]
    [InlineData("/app/v1x/auth/login")]
    [InlineData("/health")]
    [InlineData("/")]
    public void Leaves_legacy_and_unrelated_paths_untouched(string path)
    {
        Assert.False(DualStackApiFaces.TryRewrite(path, out var rewritten, out var stack));
        Assert.Equal(path, rewritten);
        Assert.Equal(string.Empty, stack);
    }

    [Fact]
    public void Prefix_match_is_case_insensitive_but_preserves_remainder()
    {
        Assert.True(DualStackApiFaces.TryRewrite("/ADMIN/v1/Users", out var rewritten, out var stack));
        Assert.Equal("/api/v1/admin/Users", rewritten);
        Assert.Equal(DualStackApiFaces.AdminStack, stack);
    }

    // 只测中间件本身，不起真实主机、不连数据库：验证重写确实发生在下游端点之前，
    // 且 Items / 响应头 / QueryString 的语义符合 P4 契约。
    [Theory]
    [InlineData("/admin/v1/users", "/api/v1/admin/users", DualStackApiFaces.AdminStack)]
    [InlineData("/app/v1/auth/login", "/api/v1/auth/login", DualStackApiFaces.AppStack)]
    [InlineData("/api/v1/admin/users", "/api/v1/admin/users", "")]
    [InlineData("/health", "/health", "")]
    public async Task Middleware_rewrites_before_downstream_and_exposes_stack(
        string requestPath,
        string expectedPath,
        string expectedStack)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseDualStackApiFaces();
        string? observedPath = null;
        app.Run(context =>
        {
            // 模拟 UseRouting 之后才执行的端点：它必须已经看到重写后的路径。
            observedPath = context.Request.Path.Value;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = requestPath;
        context.Request.QueryString = new QueryString("?page=2&size=10");
        await app.Build()(context);

        Assert.Equal(expectedPath, observedPath);
        Assert.Equal(expectedPath, context.Request.Path.Value);
        // 重写只动 Path，查询串必须原样交给下游端点。
        Assert.Equal("?page=2&size=10", context.Request.QueryString.Value);

        if (expectedStack.Length == 0)
        {
            Assert.False(context.Items.ContainsKey(DualStackApiFaces.StackItemKey));
            Assert.False(context.Response.Headers.ContainsKey(DualStackApiFaces.StackHeaderName));
        }
        else
        {
            Assert.Equal(expectedStack, context.Items[DualStackApiFaces.StackItemKey]);
            Assert.Equal(expectedStack, context.Response.Headers[DualStackApiFaces.StackHeaderName].ToString());
        }
    }

}
