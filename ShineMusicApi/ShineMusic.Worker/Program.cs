// =============================================================================
// 文件：ShineMusic.Worker/Program.cs
// 用途：P5 —— 可独立部署的 Media Worker 宿主。
//
// 它只承载 4 个媒体后台任务（音源下载消费、导入转码消费、两者重试扫描），
// 与 Web 进程通过 RabbitMQ 协作；Web 进程用 Worker__Mode=web 关掉媒体组，
// 两边不会重复消费同一条队列。若两边都不设置（默认 all），行为与改造前完全一致。
//
// 设计取舍：Worker 复用 ShineMusic.Api 的组合根扩展方法（选项校验、基础设施、
// 按模块 DI），避免复制 600 行注册逻辑；Web 与 Worker 共用同一份 appsettings 与
// 同一个 ContentRoot（保证 Storage:LibraryRoot 这类相对路径解析到同一目录）。
// 后续若要彻底解耦，可把 Extensions/ 抽到独立的 ShineMusic.Composition 项目。
//
// 启动：
//   dotnet run --project ShineMusicApi/ShineMusic.Worker
//   # Web 侧：Worker__Mode=web dotnet run --project ShineMusicApi/ShineMusic.Api
// =============================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShineMusic.Api.Extensions;

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
var apiContentRoot = Path.Combine(repoRoot, "ShineMusicApi", "ShineMusic.Api");

var host = Host.CreateDefaultBuilder(args)
    // 与 Web 主机保持同一个 ContentRoot：Storage:LibraryRoot / QuarantineRoot 都是相对路径。
    .UseContentRoot(apiContentRoot)
    .ConfigureAppConfiguration((_, configuration) =>
    {
        // config/hosts.json 是 server.origin / 端口的唯一来源，Web 主机也是这样加载的。
        configuration.AddJsonFile(
            Path.Combine(repoRoot, "config", "hosts.json"),
            optional: true,
            reloadOnChange: false);
    })
    .ConfigureServices((context, services) =>
    {
        services.AddShineMusicOptions(context.Configuration, context.HostingEnvironment);
        // Media Worker 固定只跑媒体组；Web 进程用 Worker:Mode=web 跑其余 3 个任务。
        services.AddShineMusicInfrastructure(context.Configuration, HostedServiceMode.MediaOnly);
        services.AddShineMusicApplicationServices();
    })
    .Build();

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Worker.Startup")
    .LogInformation("ShineMusic Media Worker starting | ContentRoot={ContentRoot} | Mode=MediaOnly", apiContentRoot);

await host.RunAsync();

// 从当前目录或程序集目录向上找仓库根（以 ShineMusicApi/ShineMusic.slnx 为标志），
// 这样无论从仓库根、项目目录还是 IDE 里启动都能定位到同一份配置。
static string FindRepoRoot(string start)
{
    foreach (var candidate in new[] { start, AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(candidate);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ShineMusicApi", "ShineMusic.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
    }
    throw new InvalidOperationException("Cannot locate repository root (ShineMusicApi/ShineMusic.slnx).");
}
