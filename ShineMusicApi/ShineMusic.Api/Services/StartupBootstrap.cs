// 职责：把 Program.cs 在 app.Run() 之前必须完成的两件启动工作收拢成一个可替换的接缝。
//
// 背景（docs/音源质量稳定性策略.md §12.4 / §13）：原先 Program.cs 顶层直接 await
// StartupDataInitializer 与静态 QueueBootstrap.InitializeAsync，二者会真实连 PostgreSQL
// 并投递 RabbitMQ。WebApplicationFactory 无法跳过顶层 await，导致 L3a 流断言集成测试
// 一启动宿主就会连真实库（存量库未跑命名重构迁移时还有误判空库重建表的风险）。
//
// 抽成接口后，生产行为完全不变；集成测试用 ConfigureTestServices 换成无外部依赖的实现。
// 顺序敏感：队列恢复依赖已建好的业务表，务必先数据准备、后队列恢复。

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Engine;
using ShineMusic.Tasks;

namespace ShineMusic.Api.Services;

/// <summary>Web 主机启动前的引导工作（数据准备 → 队列恢复）。</summary>
public interface IStartupBootstrap
{
    /// <summary>执行启动引导；<paramref name="services"/> 为根服务提供器。</summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default);
}

/// <summary>生产实现：与重构前 Program.cs 的两段顶层 await 逐步等价。</summary>
public sealed class StartupBootstrap : IStartupBootstrap
{
    /// <inheritdoc />
    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();

        // 0) V2 Phase 2 源清单一致性：生产搜索已收口到 ProviderRegistry，
        //    legacy 启用但 V2 未注册会造成「下载能用、所有搜索都看不到」的静默漏源，启动即拒绝。
        //    纯内存比较，发生在连库/投递之前；集成测试用 Noop 引导天然绕过。
        var options = services.GetRequiredService<IOptions<SourceDownloadOptions>>().Value;
        SearchProviderParity.ValidateOrThrow(
            services.GetRequiredService<IEnumerable<IMusicSourceProvider>>(),
            services.GetRequiredService<ProviderRegistry>().List(),
            options.ProviderFramework.Enabled);

        // 1) CodeFirst 建表 + 角色播种 + 合集播种。
        await using (var scope = services.CreateAsyncScope())
        {
            var initializer = scope.ServiceProvider.GetRequiredService<StartupDataInitializer>();
            await initializer.InitializeAsync();
        }

        // 2) 恢复中断任务并投递到 RabbitMQ（必须在建表之后）。
        await QueueBootstrap.InitializeAsync(services, cancellationToken);
    }
}
