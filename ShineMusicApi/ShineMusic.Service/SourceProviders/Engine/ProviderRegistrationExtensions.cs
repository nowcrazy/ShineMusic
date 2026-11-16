// 职责：新增音源的**唯一注册入口**（提案 §3.4 / §11 原则 1）。
//
// 以后接入 QQ 音乐 / 酷狗 / 酷我，只需实现能力接口、写一段配置、在本方法同级的
// DI 模块里多调一次 AddMusicProvider<NewProvider>()，不需要修改 Registry / Orchestrator。
// 注意：IEnumerable<IMusicProvider> 的解析顺序即注册顺序，同一接口的多个实现必须在
// 同一个模块方法内连续注册，不得跨模块拆分（与既有 11 个 provider 的约束一致）。

using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Engine;

/// <summary>Provider 框架的 DI 扩展。</summary>
public static class ProviderRegistrationExtensions
{
    /// <summary>
    /// 注册一个 V2 Provider，并同时把它暴露为 <see cref="IMusicProvider"/> 供 Registry 发现。
    /// 同一实例在两个服务类型下共享（GetRequiredService&lt;T&gt; 转发），避免能力接口拿到不同副本。
    /// </summary>
    /// <typeparam name="T">Provider 实现类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="factory">可选构造工厂；适配层需要从既有 provider 集合里挑具体实例时使用。</param>
    public static IServiceCollection AddMusicProvider<T>(
        this IServiceCollection services,
        Func<IServiceProvider, T>? factory = null)
        where T : class, IMusicProvider
    {
        ArgumentNullException.ThrowIfNull(services);
        if (factory is null)
            services.AddSingleton<T>();
        else
            services.AddSingleton(factory);

        services.AddSingleton<IMusicProvider>(serviceProvider => serviceProvider.GetRequiredService<T>());
        return services;
    }

    /// <summary>注册框架服务（不含具体平台，具体平台由 Api 层模块注册）。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddShineMusicProviderFrameworkCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ProviderHealthTracker>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<ProviderOrchestrator>();
        // Phase 2：四处生产搜索链路统一注入这个聚合器，不再各自扇出 / 超时 / 吞异常。
        services.AddSingleton<ISearchAggregator, SearchAggregator>();
        services.AddSingleton<ProviderAccessService>();
        return services;
    }
}
