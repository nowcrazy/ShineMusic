// 职责：点歌需求自动归档后台任务。
// 周期性扫描“已满足（fulfilled）且超过保留期”的需求，写入 ArchivedAtUtc，
// 使其从公共需求池隐藏；记录本身保留，提交人仍可在「我的」查看历史。

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Data;
using ShineMusic.Api.Options;

namespace ShineMusic.Api.Services;

/// <summary>点歌需求自动归档服务：定时把已满足且超期的需求标记为已归档。</summary>
public sealed class SongRequestArchiveService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SongRequestOptions> _options;
    private readonly ILogger<SongRequestArchiveService> _logger;

    public SongRequestArchiveService(
        IServiceScopeFactory scopeFactory,
        IOptions<SongRequestOptions> options,
        ILogger<SongRequestArchiveService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.ArchiveEnabled)
        {
            _logger.LogInformation("Song request auto-archive is disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.ArchiveScanIntervalMinutes));

        // 启动后短暂等待，避免与应用启动阶段争抢数据库连接。
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ArchiveOnceAsync(options.ArchiveAfterDays, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to archive fulfilled song requests.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>执行一次归档扫描：把超过保留期的已满足需求写入 ArchivedAtUtc。</summary>
    private async Task ArchiveOnceAsync(int archiveAfterDays, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, archiveAfterDays));
        var now = DateTime.UtcNow;

        // 单条 UPDATE 批量归档，避免把大量实体加载进内存。
        var archived = await db.SongRequests
            .Where(request =>
                request.Status == SongRequestStatuses.Fulfilled &&
                request.ArchivedAtUtc == null &&
                request.UpdatedAtUtc < cutoff)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(request => request.ArchivedAtUtc, now),
                cancellationToken);

        if (archived > 0)
            _logger.LogInformation(
                "Archived {Count} fulfilled song request(s) older than {Days} day(s).",
                archived,
                archiveAfterDays);
    }
}
