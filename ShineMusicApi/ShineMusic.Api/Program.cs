// 应用程序入口：组装依赖注入容器、配置 HTTP 中间件管道与端点映射；
// 启动时由 StartupDataInitializer 完成 CodeFirst 建表 + 角色播种 + 合集播种，
// 另支持两个命令行参数（均在启动数据准备之前/之后直接退出，不启动 Web 服务）：
//   --create-database 确保业务库存在（不存在则创建，已存在则跳过）；
//   --create-admin    创建管理员账号（可重复执行，不受“库中无用户”限制）。
// 大块服务注册与中间件管道已提炼到 Extensions/ 与 Services/ 目录，本文件只保留调用入口。

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ShineMusic.Api.Extensions;
using ShineMusic.Api.Services;
using ShineMusic.Common.Logging;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;

var builder = WebApplication.CreateBuilder(args);

// ===== WebHost 级别配置（不属于服务注册，保留在此）=====
// Kestrel 请求体上限放宽到 4 GiB：整批无损音频上传需要的理论上限。
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4294967296);

// 全局地址配置：仓库根目录 config/hosts.json 为唯一来源。
var repoRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
var hostsRelativePath = Path.Combine("config", "hosts.json");
if (File.Exists(Path.Combine(repoRoot, hostsRelativePath)))
{
    builder.Configuration.AddJsonFile(
        new Microsoft.Extensions.FileProviders.PhysicalFileProvider(repoRoot),
        hostsRelativePath,
        optional: true,
        reloadOnChange: false);
}

// 文件日志：按分类与日期分离落盘。
builder.Logging.AddFileLogger(options =>
{
    options.LogsRoot = Path.Combine(builder.Environment.ContentRootPath, "logs");
});

// ===== 服务注册：全部提炼为扩展方法 =====
builder.Services.AddShineMusicSwagger();
builder.Services.AddShineMusicCrossCutting();
builder.Services.AddShineMusicControllers();
builder.Services.AddShineMusicOptions(builder.Configuration, builder.Environment);
builder.Services.AddShineMusicCors(builder.Configuration, builder.Environment);
builder.Services.AddShineMusicInfrastructure(builder.Configuration);
builder.Services.AddShineMusicApplicationServices();
builder.Services.AddShineMusicAuthentication(builder.Configuration);

// 启动数据初始化服务与管理员创建命令（Scoped，依赖 ISqlSugarClient / UserService）。
builder.Services.AddScoped<StartupDataInitializer>();
builder.Services.AddScoped<CreateAdminCommand>();

var app = builder.Build();

// 启动快照：把环境、监听地址、日志目录记到 logs/app。
app.Logger.LogInformation(
    "ShineMusic API starting | Environment={Environment} | ContentRoot={ContentRoot} | Urls={Urls} | LogsRoot={LogsRoot}",
    app.Environment.EnvironmentName,
    app.Environment.ContentRootPath,
    builder.Configuration["Urls"] ?? "(default)",
    Path.Combine(app.Environment.ContentRootPath, "logs"));

// Swagger UI 仅在开发环境暴露。
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "ShineMusic API v1");
        c.RoutePrefix = "swagger";
    });
}

// ===== 中间件管道（顺序敏感，全部提炼到 UseShineMusicPipeline）=====
// 静态文件托管：直接服务 Web 前端构建产物（ShineMusicWeb/dist），
// 与 API 同域同源，无需跨域配置。仅当 dist 目录存在时启用。
var webDistPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "ShineMusicWeb", "dist"));
if (Directory.Exists(webDistPath))
{
    var distFileProvider = new PhysicalFileProvider(webDistPath);
    // UseDefaultFiles 让 "/" 自动映射到 index.html（无需走 fallback 管道）。
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = distFileProvider,
        DefaultFileNames = ["index.html"]
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = distFileProvider,
        RequestPath = ""
    });
}

app.UseShineMusicPipeline();

// SPA 路由回退：非 /api、/swagger、/health 的未知路径返回 index.html，由前端路由处理。
// /api/* 不匹配时由管道内的兜底错误体返回 JSON 404，不会被 fallback 吞掉。
if (Directory.Exists(webDistPath))
{
    app.MapWhen(
        ctx => !ctx.Request.Path.StartsWithSegments("/api")
            && !ctx.Request.Path.StartsWithSegments("/swagger")
            && !ctx.Request.Path.StartsWithSegments("/health"),
        spa =>
        {
            spa.UseRouting();
            spa.UseEndpoints(endpoints =>
            {
                endpoints.MapFallbackToFile("index.html", new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(webDistPath)
                });
            });
        });
}

// ===== 命令行模式：--create-database 确保业务库存在（不存在则创建，已存在则跳过）=====
// 必须在启动数据准备之前执行：初始化会立即连接业务库，库不存在会直接抛异常退出。
// 单独使用本参数时建库后即退出；与 --create-admin 组合时继续走建表与管理员创建。
if (args.Contains("--create-database", StringComparer.OrdinalIgnoreCase))
{
    SqlSugarSchemaBootstrap.EnsureDatabase(
        builder.Configuration[$"{DatabaseOptions.SectionName}:ConnectionString"]!,
        app.Logger);
    if (!args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
        return;
}

// ===== 启动数据准备：CodeFirst 建表 + 角色播种 + 合集播种 =====
await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<StartupDataInitializer>();
    await initializer.InitializeAsync();
}

// ===== 队列引导：恢复中断任务（必须在 CodeFirst 初始化之后执行）=====
await ShineMusic.Tasks.QueueBootstrap.InitializeAsync(app.Services);

// ===== 命令行模式：--create-admin 创建管理员账号 =====
// 可重复执行（不做“库中无用户”限制），重复的用户名/邮箱由 UserService 唯一性校验拦下。
// 流程结束后直接退出，不启动 Web 主机。
if (args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var command = scope.ServiceProvider.GetRequiredService<CreateAdminCommand>();
    var success = await command.ExecuteAsync();
    Environment.ExitCode = success ? 0 : 1;
    return;
}

app.Run();
