// 职责：V2.0 ProviderOrchestrator 调度骨架（提案 §2 / §4.3 / §11 原则 6「故障隔离」）。
//
// Phase 1 只提供两个原语，刻意**不**做去重、跨源匹配、版本识别、质量闸门：
//   - InvokeAsync  ：单 provider 的超时隔离 + 异常收敛 + 健康记录；
//   - SearchAsync  ：并发扇出 + 总预算 / 单源预算分离 + 最大并发 + 已完成结果保留。
// SearchAggregator、TrackMatcher、DownloadCoordinator、QualityGate 分属 Phase 2/3，
// 在这里提前塞进去会与已有 TrackIdentityMatcher（唯一身份决策点）形成第二条匹配链。
//
// 硬不变式：任何 provider 的异常、超时、取消都收敛成 ProviderCallResult，绝不冒泡。
// 超时是**非协作式**的：即使适配器完全忽略 CancellationToken，WhenAny 仍会到点返回。

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Engine;

/// <summary>能力感知的 Provider 调度器。</summary>
public sealed class ProviderOrchestrator
{
    /// <summary>单源超时。</summary>
    public const string ErrorProviderTimeout = "provider_timeout";

    /// <summary>单源抛异常。</summary>
    public const string ErrorProviderFailed = "provider_failed";

    /// <summary>外部取消 / 总预算取消。</summary>
    public const string ErrorCancelled = "cancelled";

    /// <summary>整轮搜索预算耗尽。</summary>
    public const string ErrorBudgetExhausted = "search_budget_exhausted";

    /// <summary>provider 未注册、配置禁用或已熔断。</summary>
    public const string ErrorNotAvailable = "provider_not_available";

    /// <summary>provider 返回 null 结果。</summary>
    public const string ErrorEmptyResult = "provider_empty_result";

    private readonly ProviderRegistry _registry;
    private readonly ProviderHealthTracker _health;
    private readonly SourceDownloadOptions _options;
    private readonly ILogger<ProviderOrchestrator> _logger;

    /// <summary>由 DI 构造。</summary>
    /// <param name="registry">Provider 注册表。</param>
    /// <param name="health">运行时健康记录器。</param>
    /// <param name="options">SourceDownload 配置。</param>
    /// <param name="logger">日志。</param>
    public ProviderOrchestrator(
        ProviderRegistry registry,
        ProviderHealthTracker health,
        IOptions<SourceDownloadOptions> options,
        ILogger<ProviderOrchestrator> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 单 provider 调用原语：超时隔离、异常收敛、健康记录。
    /// 找不到 / 配置禁用 / 已熔断的 provider 直接返回结构化失败，不抛异常。
    /// </summary>
    /// <typeparam name="TProvider">要调用的能力接口。</typeparam>
    /// <typeparam name="TResult">结果类型。</typeparam>
    /// <param name="providerName">平台标识。</param>
    /// <param name="operation">实际调用；实现方无需自行吞异常。</param>
    /// <param name="timeout">单源超时；null 时取配置的 Search:ProviderTimeoutSeconds。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    public async Task<ProviderCallResult<TResult>> InvokeAsync<TProvider, TResult>(
        string providerName,
        Func<TProvider, CancellationToken, Task<TResult>> operation,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        where TProvider : class, IMusicProvider
        where TResult : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
            return ProviderCallResult.Fail<TResult>(providerName, ErrorCancelled, "调用被上层取消。", 0);

        var stopwatch = Stopwatch.StartNew();
        var capability = typeof(TProvider) == typeof(IMusicSearchProvider) ? ProviderCapability.Search
            : typeof(TProvider) == typeof(IMusicMetadataProvider) ? ProviderCapability.Metadata
            : typeof(TProvider) == typeof(IMusicDownloadProvider) ? ProviderCapability.Resolve
            : ProviderCapability.None;
        var provider = capability == ProviderCapability.None
            ? _registry.Find(providerName)
            : _registry.Find<TProvider>(providerName, capability);
        if (provider is null)
            return ProviderCallResult.Fail<TResult>(providerName, ErrorNotAvailable,
                "Provider 未注册、配置禁用或已熔断。", stopwatch.ElapsedMilliseconds);
        if (provider is not TProvider typed)
            return ProviderCallResult.Fail<TResult>(providerName, ErrorNotAvailable,
                $"Provider 未实现 {typeof(TProvider).Name}。", stopwatch.ElapsedMilliseconds);

        var effectiveTimeout = timeout
                               ?? TimeSpan.FromSeconds(Math.Max(1, _options.ProviderFramework.Search.ProviderTimeoutSeconds));

        // timeoutCts：传给 provider 的协作式取消信号（外部取消 + 单源到点）。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);
        // timerCts：只驱动有界计时器，finally 里显式取消，避免成功调用遗留挂起任务。
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task<TResult> operationTask;
        try
        {
            operationTask = operation(typed, timeoutCts.Token) ?? Task.FromResult<TResult>(null!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProviderCallResult.Fail<TResult>(providerName, ErrorCancelled,
                "调用被上层取消。", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // 同步抛出的异常（未进入 Task）同样要收敛，不能漏出调度层。
            stopwatch.Stop();
            _health.RecordFailure(providerName, ErrorProviderFailed, stopwatch.Elapsed);
            return ProviderCallResult.Fail<TResult>(providerName, ErrorProviderFailed,
                ex.Message, stopwatch.ElapsedMilliseconds);
        }

        try
        {
            // 不依赖 provider 是否配合 CancellationToken：计时器到点即完成，
            // 即使适配器永久挂死，WhenAny 也会立刻返回，其余 provider 不受影响。
            var timer = Task.Delay(effectiveTimeout, timerCts.Token);
            var completed = await Task.WhenAny(operationTask, timer).ConfigureAwait(false);
            if (completed != operationTask && !operationTask.IsCompleted)
            {
                ObserveFault(operationTask);
                stopwatch.Stop();
                if (cancellationToken.IsCancellationRequested)
                    return ProviderCallResult.Fail<TResult>(providerName, ErrorCancelled,
                        "调用被上层取消。", stopwatch.ElapsedMilliseconds);

                _health.RecordFailure(providerName, ErrorProviderTimeout, stopwatch.Elapsed);
                _logger.LogWarning("V2 provider {Provider} timed out after {Timeout}.", providerName, effectiveTimeout);
                return ProviderCallResult.Fail<TResult>(providerName, ErrorProviderTimeout,
                    $"单源超时（{effectiveTimeout.TotalSeconds:0.#}s）。", stopwatch.ElapsedMilliseconds);
            }

            var value = await operationTask.ConfigureAwait(false);
            stopwatch.Stop();
            if (value is null)
            {
                _health.RecordFailure(providerName, ErrorEmptyResult, stopwatch.Elapsed);
                return ProviderCallResult.Fail<TResult>(providerName, ErrorEmptyResult,
                    "Provider 返回了 null 结果。", stopwatch.ElapsedMilliseconds);
            }

            // A completed invocation is not necessarily a successful download. Record exactly one outcome.
            if (value is DownloadResult { Success: false } failed)
                _health.RecordFailure(providerName, failed.ErrorCode ?? ErrorProviderFailed, stopwatch.Elapsed);
            else
                _health.RecordSuccess(providerName, stopwatch.Elapsed);
            return ProviderCallResult.Ok(providerName, value, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 单源自己到点被 timeoutCts 取消：算 provider 超时。
            stopwatch.Stop();
            _health.RecordFailure(providerName, ErrorProviderTimeout, stopwatch.Elapsed);
            return ProviderCallResult.Fail<TResult>(providerName, ErrorProviderTimeout,
                $"单源超时（{effectiveTimeout.TotalSeconds:0.#}s）。", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // 外部取消 / 总预算耗尽不算 provider 故障，不写失败统计。
            stopwatch.Stop();
            return ProviderCallResult.Fail<TResult>(providerName, ErrorCancelled,
                "调用被上层取消。", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _health.RecordFailure(providerName, ErrorProviderFailed, stopwatch.Elapsed);
            _logger.LogWarning(ex, "V2 provider {Provider} failed; other providers are unaffected.", providerName);
            return ProviderCallResult.Fail<TResult>(providerName, ErrorProviderFailed,
                ex.Message, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            // 只影响计时器，不影响已经完成或仍在跑的 provider 调用。
            timerCts.Cancel();
        }
    }

    /// <summary>
    /// 并发搜索扇出（提案 §4.3）：全部可用搜索 Provider 并发请求，每源独立超时，
    /// 单源失败/超时不影响其他源；总预算到期后立刻取消未完成源，已完成结果照常返回。
    /// 结果顺序与 Registry 的优先级顺序一致（**未**做去重与匹配，属 Phase 2）。
    /// </summary>
    /// <param name="keyword">搜索关键词。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    public Task<IReadOnlyList<ProviderCallResult<IReadOnlyList<MusicTrack>>>> SearchAsync(
        string keyword,
        CancellationToken cancellationToken = default) =>
        SearchAsync(keyword, (IReadOnlySet<string>?)null, cancellationToken);

    /// <summary>
    /// 带源清单过滤的并发搜索扇出。过滤发生在调度之前，被排除的源不会发出请求；
    /// 源清单仍以 <see cref="ProviderRegistry"/> 为准，本参数只能做「取子集」，不能凭空新增源。
    /// Phase 2 的四处生产搜索链路通过 <c>ISearchAggregator</c> 使用这个重载。
    /// </summary>
    /// <param name="keyword">搜索关键词。</param>
    /// <param name="providerNames">限定参与的平台名；null 表示全部可调度源。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    public async Task<IReadOnlyList<ProviderCallResult<IReadOnlyList<MusicTrack>>>> SearchAsync(
        string keyword,
        IReadOnlySet<string>? providerNames,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        var searchOptions = _options.ProviderFramework.Search;
        var providers = _registry.Resolve<IMusicSearchProvider>(ProviderCapability.Search)
            .Where(provider => providerNames is null || providerNames.Contains(provider.ProviderName))
            .ToArray();
        if (providers.Length == 0)
            return [];

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, searchOptions.TotalBudgetSeconds)));
        using var gate = new SemaphoreSlim(Math.Max(1, searchOptions.MaxConcurrentProviders));

        var tasks = providers
            .Select(provider => SearchOneAsync(provider, keyword, gate, budgetCts, cancellationToken))
            .ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 单 provider 下载 / 资源解析入口。调用级成功（异常收敛）与业务级成功（拿到资源）分开表达：
    /// <c>ProviderCallResult.Success=true</c> 但 <c>DownloadResult.Success=false</c> 是合法组合。
    /// </summary>
    /// <param name="providerName">平台标识。</param>
    /// <param name="request">下载请求。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    public async Task<ProviderCallResult<DownloadResult>> DownloadAsync(
        string providerName,
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await InvokeAsync<IMusicDownloadProvider, DownloadResult>(
            providerName,
            (provider, token) => provider.DownloadAsync(request, token),
            TimeSpan.FromSeconds(Math.Max(1, _options.Download.TimeoutSeconds)),
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>超时后挂起的任务仍可能抛异常，观察它以免产生 UnobservedTaskException。</summary>
    private static void ObserveFault<T>(Task<T> task)
    {
        _ = task.ContinueWith(
            completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<ProviderCallResult<IReadOnlyList<MusicTrack>>> SearchOneAsync(
        IMusicSearchProvider provider,
        string keyword,
        SemaphoreSlim gate,
        CancellationTokenSource budgetCts,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await gate.WaitAsync(budgetCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return ProviderCallResult.Fail<IReadOnlyList<MusicTrack>>(provider.ProviderName,
                cancellationToken.IsCancellationRequested ? ErrorCancelled : ErrorBudgetExhausted,
                "等待并发槽时搜索被取消。", stopwatch.ElapsedMilliseconds);
        }

        try
        {
            var result = await InvokeAsync<IMusicSearchProvider, IReadOnlyList<MusicTrack>>(
                provider.ProviderName,
                (instance, token) => instance.SearchAsync(keyword, token),
                cancellationToken: budgetCts.Token).ConfigureAwait(false);

            if (!result.Success && result.ErrorCode == ErrorCancelled
                && budgetCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                return result.WithError(ErrorBudgetExhausted, "总搜索预算耗尽，已完成结果保留。");
            return result;
        }
        finally
        {
            gate.Release();
        }
    }
}
