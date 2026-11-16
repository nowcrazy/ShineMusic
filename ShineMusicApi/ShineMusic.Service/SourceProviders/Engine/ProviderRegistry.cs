// 职责：V2.0 ProviderRegistry（提案 §3.4 / §11 原则 1「Provider 插件化」）。
//
// 只通过公共基接口 IMusicProvider 做发现，不认识任何具体平台：
// 新增音源 = 实现能力接口 + 在 DI 里 AddMusicProvider<T>() + 加一段配置，无需改本类。
//
// 三个维度严格分离（提案 §11 最后一条）：
//   配置维度 → ProviderDescriptor.ConfigEnabled / SearchEnabled / DownloadEnabled（来自 appsettings）
//   健康维度 → ProviderHealthTracker 快照（本类只读，不写入配置）
//   单曲质量 → DownloadResult（由适配层返回，Registry 不接触）
//
// IEnumerable<IMusicProvider> 的解析顺序 = DI 注册顺序；优先级相同时以注册顺序为稳定次序。

using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Engine;

/// <summary>按能力建索引的 Provider 注册表。</summary>
public sealed class ProviderRegistry
{
    private sealed record Entry(
        IMusicProvider Provider,
        ProviderDescriptor Descriptor,
        int Priority,
        int RegistrationIndex);

    private readonly IReadOnlyList<Entry> _entries;
    private readonly SourceDownloadOptions _options;
    private readonly ProviderHealthTracker _health;

    /// <summary>由 DI 构造：providers 的顺序即注册顺序。</summary>
    /// <param name="providers">所有已注册的 V2 Provider。</param>
    /// <param name="options">SourceDownload 配置（读取 ProviderFramework 节）。</param>
    /// <param name="health">运行时健康记录器。</param>
    public ProviderRegistry(
        IEnumerable<IMusicProvider> providers,
        IOptions<SourceDownloadOptions> options,
        ProviderHealthTracker health)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _options = options.Value;
        var recovery = _options.ProviderFramework.HealthCheck;
        _health.ConfigureRecovery(recovery.FailureThreshold, recovery.RecoverySeconds, recovery.MaxRecoverySeconds);

        var index = 0;
        _entries = providers
            .Select(provider =>
            {
                ArgumentNullException.ThrowIfNull(provider);
                var descriptor = BuildDescriptor(provider);
                return new Entry(provider, descriptor, descriptor.Priority, index++);
            })
            .OrderByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.RegistrationIndex)
            .ToArray();
    }

    /// <summary>全部 Provider 的配置 + 能力快照（含被禁用的，供管理页展示）。</summary>
    public IReadOnlyList<ProviderDescriptor> List() =>
        _entries.Select(entry => entry.Descriptor).ToArray();

    /// <summary>运行时健康快照。</summary>
    /// <param name="providerName">平台标识。</param>
    public ProviderHealth? Health(string providerName)
    {
        var entry = _entries.FirstOrDefault(item =>
            string.Equals(item.Provider.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : _health.Snapshot(entry.Provider.ProviderName, entry.Descriptor.ConfigEnabled);
    }

    /// <summary>按名称找可调度实例；配置禁用或已熔断返回 null。</summary>
    /// <param name="providerName">平台标识。</param>
    public IMusicProvider? Find(string providerName)
    {
        var entry = _entries.FirstOrDefault(item =>
            string.Equals(item.Provider.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.Descriptor.ConfigEnabled || !IsHealthy(entry))
            return null;
        return entry.Provider;
    }

    /// <summary>按名称与单项能力获取可调度实现；三项开关分别检查。</summary>
    public T? Find<T>(string providerName, ProviderCapability capability) where T : class, IMusicProvider =>
        Resolve<T>(capability).FirstOrDefault(provider =>
            string.Equals(provider.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>能力是否在配置和实现中可用；不把暂时熔断当成永久禁用。</summary>
    public bool IsConfigured(string providerName, ProviderCapability capability) =>
        _entries.Any(entry => string.Equals(entry.Provider.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)
            && entry.Descriptor.ConfigEnabled
            && CapabilitySwitch(entry.Descriptor, capability)
            && entry.Descriptor.Declares(capability));

    /// <summary>
    /// 按能力解析可调度 Provider，顺序 = 优先级降序（相同则按注册顺序）。
    /// 过滤条件：实现了目标接口 + 配置启用 + 能力开关放行 + 能力声明可调度 + 健康未熔断。
    /// <see cref="ProviderStatus.Unknown"/>（尚未检测）一律不放行，质量优先。
    /// </summary>
    /// <typeparam name="T">目标能力接口。</typeparam>
    /// <param name="capability">要调度的能力（决定使用 SearchEnabled 还是 DownloadEnabled 开关）。</param>
    public IReadOnlyList<T> Resolve<T>(ProviderCapability capability) where T : class, IMusicProvider =>
        _entries
            .Where(entry => entry.Provider is T
                            && entry.Descriptor.ConfigEnabled
                            && CapabilitySwitch(entry.Descriptor, capability)
                            && entry.Descriptor.Declares(capability)
                            && IsHealthy(entry))
            .Select(entry => (T)entry.Provider)
            .ToArray();

    private ProviderDescriptor BuildDescriptor(IMusicProvider provider)
    {
        var policy = PolicyFor(provider.ProviderName);
        var configEnabled = _options.ProviderFramework.Enabled
                            && provider.IsConfigEnabled
                            && policy.Enabled;
        var reason = configEnabled
            ? null
            : provider.ConfigDisabledReason
              ?? (!policy.Enabled
                  ? $"V2 配置禁用（SourceDownload:ProviderFramework:Providers:{provider.ProviderName}:Enabled=false）。"
                  : "V2 Provider 框架总开关已关闭（SourceDownload:ProviderFramework:Enabled=false）。");

        return new ProviderDescriptor(
            provider.ProviderName,
            policy.Priority,
            configEnabled,
            policy.SearchEnabled,
            policy.DownloadEnabled,
            provider.UsesVerifiedCapabilities
                ? VerifiedProviderCapabilities.Apply(provider.ProviderName, provider.DeclaredCapabilities)
                : provider.DeclaredCapabilities,
            reason,
            policy.MetadataEnabled);
    }

    private SourceProviderPolicyOptions PolicyFor(string providerName) =>
        _options.ProviderFramework.Providers.TryGetValue(providerName, out var policy) && policy is not null
            ? policy
            : new SourceProviderPolicyOptions();

    private static bool CapabilitySwitch(ProviderDescriptor descriptor, ProviderCapability capability) =>
        capability switch
        {
            ProviderCapability.Search => descriptor.SearchEnabled,
            ProviderCapability.Metadata => descriptor.MetadataEnabled,
            ProviderCapability.Resolve or ProviderCapability.Preview
                or ProviderCapability.Download320 or ProviderCapability.DownloadLossless => descriptor.DownloadEnabled,
            _ => true,
        };

    private bool IsHealthy(Entry entry)
    {
        _health.CheckRecovery(entry.Provider.ProviderName, entry.Descriptor.ConfigEnabled);
        var snapshot = _health.Snapshot(entry.Provider.ProviderName, entry.Descriptor.ConfigEnabled);
        if (snapshot.CircuitState is ProviderCircuitState.Open or ProviderCircuitState.ConfigDisabled)
            return false;
        return snapshot.Status != ProviderStatus.Unavailable;
    }
}
