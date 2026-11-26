using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShineMusic.Service.Media;

namespace ShineMusic.Tasks;

/// <summary>媒体组独有的启动恢复及周期租约扫描；Web-only 主机不会注册它。</summary>
public sealed class MediaQueueRecoveryService(
    IServiceScopeFactory scopeFactory,
    ILogger<MediaQueueRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = true;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MediaQueueRecoveryJob>().RecoverAsync(startup, stoppingToken);
                startup = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Media lease recovery failed; next scan will retry.");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
