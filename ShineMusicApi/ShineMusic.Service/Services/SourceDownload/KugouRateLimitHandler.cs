// 职责：酷狗出站 HTTP 请求的全局频控网关（令牌桶限流）。
// 背景：酷狗对同一 IP 的密集请求（尤其 getSongInfo.php）会返回 errcode:1002「您操作太频繁了」，
//   封禁窗口长达 45-60 分钟。作为 "kugou" HttpClient 的 DelegatingHandler 插入，
//   对 search / getSongInfo / plist / special/song 等所有出站请求统一限流，从源头降低触发概率。
// 算法：令牌桶——每次请求消耗 1 个令牌，不足时阻塞等待补充；SemaphoreSlim 保证桶状态线程安全。
// 与 KugouSourceProvider 内部的 30s 滑动缓存形成纵深防御：
//   缓存：同 hash 的 ResolveDownloadAsync + GetTrackAsync 复用一次调用（减少调用次数）；
//   频控网关：全局控制所有酷狗出站请求的时间间隔（打散密集调用）。

using Microsoft.Extensions.Logging;

namespace ShineMusic.Service.SourceDownload;

/// <summary>
/// 酷狗出站请求的令牌桶限流网关。注册为 "kugou" HttpClient 的 DelegatingHandler，
/// 对所有出站请求（search / getSongInfo / plist / special/song 等）统一限流，
/// 防止触发酷狗 errcode:1002 频控（封禁长达 45-60 分钟）。
/// </summary>
public sealed class KugouRateLimitHandler(ILogger<KugouRateLimitHandler> logger) : DelegatingHandler
{
    /// <summary>桶的最大令牌数（即突发上限）。</summary>
    private const int MaxTokens = 2;

    /// <summary>每秒补充的令牌数（即平均 QPS 上限）。保守值：2 QPS，实测该速率可稳定运行不触发 errcode:1002。</summary>
    private const double RefillTokensPerSecond = 2.0;

    /// <summary>互斥门闩：同一时刻只允许一个请求执行令牌获取逻辑（含等待），保证桶状态一致性。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private double _tokens = MaxTokens;
    private DateTime _lastRefillUtc = DateTime.UtcNow;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await AcquireTokenAsync(cancellationToken);
        logger.LogDebug("KugouRateLimit: issued token for {Method} {Uri}", request.Method, request.RequestUri);
        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// 阻塞直到桶里有 1 个可用令牌。令牌桶以固定速率补充，平均不超过 <see cref="RefillTokensPerSecond"/> QPS。
    /// </summary>
    private async Task AcquireTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            Refill(now);
            while (_tokens < 1.0)
            {
                var waitSeconds = (1.0 - _tokens) / RefillTokensPerSecond;
                var wait = TimeSpan.FromSeconds(Math.Max(waitSeconds, 0.001));
                logger.LogDebug("KugouRateLimit: throttle wait {Wait}ms (tokens={Tokens:F2})",
                    wait.TotalMilliseconds, _tokens);
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                now = DateTime.UtcNow;
                Refill(now);
            }
            _tokens -= 1.0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按经过时间补充令牌（不超过上限）。调用方需持有 <see cref="_gate"/>。</summary>
    private void Refill(DateTime now)
    {
        var elapsed = (now - _lastRefillUtc).TotalSeconds;
        if (elapsed <= 0)
            return;
        _tokens = Math.Min(MaxTokens, _tokens + elapsed * RefillTokensPerSecond);
        _lastRefillUtc = now;
    }
}
