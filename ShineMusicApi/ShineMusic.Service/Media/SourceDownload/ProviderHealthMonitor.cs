// 职责：Phase 5 自动熔断恢复——消费已就位的 HealthCheck 配置参数，实现状态机自动转移。
//
// 状态机（三态 + 终止态）：
//   Closed（正常运行）→ 连续失败达 FailureThreshold(5) → Open（暂停请求）
//   Open → 等待 RecoverySeconds(60) → HalfOpen（允许探测）
//   HalfOpen → 探测成功 → Closed（恢复正常运行）
//   HalfOpen → 探测失败 → Open（冷却升级到 MaxRecoverySeconds(300)）
//   ConfigDisabled → 终止态，永远不自动转移
//
// 硬规则：
//   1) 用户手动关掉的音源（配置禁用），永远不自动启用——只有 ConfigDisabled 不能被状态机转出。
//   2) 鉴权失败、无版权、歌曲不存在等明确错误不计入连续失败（不应该重试的错误）。
//   3) 只有因故障暂停的才能自动恢复。
//
// 实现方式：IHostedService + 定时巡检（IntervalMinutes=30），配合 ProviderHealthTracker 的回调。

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;

namespace ShineMusic.Service.SourceDownload;

/// <summary>
/// Phase 5 Provider 健康监控器：自动熔断与恢复状态机。
/// 作为 IHostedService 运行，定时巡检 Provider 健康状态，自动执行 Closed→Open→HalfOpen→Closed 转移。
/// </summary>
public sealed class ProviderHealthMonitor : IHostedService, IDisposable
{
    /// <summary>不计入连续失败的错误码——这些是明确不应该重试的错误。</summary>
    private static readonly HashSet<string> NonRetriableErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth_failed", "authentication_required", "no_license", "no_copyright",
        "track_not_found", "resource_not_found", "source_duplicate",
    };

    private readonly ProviderHealthTracker _healthTracker;
    private readonly ProviderRegistry _registry;
    private readonly SourceDownloadOptions _options;
    private readonly ILogger<ProviderHealthMonitor> _logger;
    private ITimer? _timer;

    /// <summary>构造健康监控器。</summary>
    /// <param name="healthTracker">运行时健康记录器。</param>
    /// <param name="registry">Provider 注册表（获取已注册的 provider 列表）。</param>
    /// <param name="options">SourceDownload 配置。</param>
    /// <param name="logger">日志。</param>
    public ProviderHealthMonitor(
        ProviderHealthTracker healthTracker,
        ProviderRegistry registry,
        IOptions<SourceDownloadOptions> options,
        ILogger<ProviderHealthMonitor> logger)
    {
        _healthTracker = healthTracker ?? throw new ArgumentNullException(nameof(healthTracker));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var healthCheck = _options.ProviderFramework.HealthCheck;
        _healthTracker.ConfigureRecovery(healthCheck.FailureThreshold,
            healthCheck.RecoverySeconds, healthCheck.MaxRecoverySeconds);
    }

    /// <summary>启动监控服务。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.ProviderFramework.HealthCheck.Enabled)
        {
            _logger.LogInformation("ProviderHealthMonitor: 健康检查配置关闭，监控器不启动。");
            return Task.CompletedTask;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.ProviderFramework.HealthCheck.IntervalMinutes));
        _timer = _healthTracker.Clock.CreateTimer(OnTimerTick, null, interval, interval);
        _logger.LogInformation(
            "ProviderHealthMonitor: 已启动，巡检间隔 {Interval} 分钟，失败阈值 {Threshold}，冷却 {Recovery}s/{MaxRecovery}s。",
            interval.TotalMinutes,
            _options.ProviderFramework.HealthCheck.FailureThreshold,
            _options.ProviderFramework.HealthCheck.RecoverySeconds,
            _options.ProviderFramework.HealthCheck.MaxRecoverySeconds);
        return Task.CompletedTask;
    }

    /// <summary>停止监控服务。</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _logger.LogInformation("ProviderHealthMonitor: 已停止。");
        return Task.CompletedTask;
    }

    /// <summary>定时巡检回调：检查所有已注册 provider 的健康状态，执行自动转移。</summary>
    private async void OnTimerTick(object? state)
    {
        try
        {
            await CheckAllProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "ProviderHealthMonitor 巡检异常。");
        }
    }

    /// <summary>
    /// 检查所有已注册 provider 的健康状态，执行自动状态转移。
    /// </summary>
    internal async Task CheckAllProvidersAsync(CancellationToken cancellationToken)
    {
        var descriptors = _registry.List();
        foreach (var descriptor in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CheckProviderAsync(descriptor.ProviderName, descriptor.ConfigEnabled, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 检查单个 provider 的健康状态并执行自动转移。
    /// </summary>
    /// <param name="providerName">平台标识。</param>
    /// <param name="configEnabled">配置层是否启用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal Task CheckProviderAsync(string providerName, bool configEnabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Tracker owns transition timestamps and backoff, using the same clock as this timer.
        _healthTracker.CheckRecovery(providerName, configEnabled);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 判断错误码是否应该计入连续失败（鉴权失败/无版权/歌曲不存在等明确错误不重试）。
    /// 供 ProviderHealthTracker 或 Orchestrator 调用。
    /// </summary>
    /// <param name="errorCode">机器可读错误码。</param>
    /// <returns>true 表示应该计入连续失败；false 表示不计入。</returns>
    public static bool ShouldCountAsFailure(string? errorCode) =>
        !string.IsNullOrWhiteSpace(errorCode)
        && !NonRetriableErrorCodes.Contains(errorCode);

    /// <summary>释放定时器资源。</summary>
    public void Dispose()
    {
        _timer?.Dispose();
    }
}
