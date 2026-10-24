// LibraryImportQueue.cs
// 曲库导入队列与后台消费服务。
// 该文件包含两个职责内聚的类型：
//   1. LibraryImportQueue：基于 System.Threading.Channels 的无界队列，充当导入任务的内存缓冲，
//      支持多生产者（上传接口入队）与多消费者（后台 worker 并发出队）；
//   2. LibraryImportBackgroundService：BackgroundService 派生类，负责启动时恢复未完成任务、
//      按配置并发上限启动多个 worker 循环消费队列，并推动每条导入项的状态机直至完成或失败。
//
// 与 MusicDbContext 的交互方式：
//   - 该后台服务本身以单例运行，无法直接注入 scoped 的 MusicDbContext；
//   - 因此通过 IServiceScopeFactory 在每次处理单条导入项时创建独立作用域，
//     从作用域中解析 MusicDbContext，处理完毕后作用域自动释放，确保 DbContext 生命周期安全。

using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Data;
using ShineMusic.Api.Options;
using ShineMusic.Api.Contracts;

namespace ShineMusic.Api.Services;

/// <summary>
/// 曲库导入任务内存队列。
/// 基于 System.Threading.Channels 的无界 Channel，支持多生产者多消费者并发读写。
/// 上传接口将导入项 ID 写入队列，后台服务从中读取并处理。
/// </summary>
public sealed class LibraryImportQueue
{
    // 无界通道：生产速度可能快于消费速度，不设上限以避免阻塞上传接口；
    // SingleReader=false / SingleWriter=false 表示允许多个读写端并发操作。
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    /// <summary>
    /// 将导入项 ID 入队。
    /// </summary>
    /// <param name="itemId">导入项 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask QueueAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(itemId, cancellationToken);

    /// <summary>
    /// 异步读取队列中的所有导入项 ID，直至通道关闭或取消。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// 曲库导入后台消费服务。
/// 启动时先恢复数据库中处于中间状态（Validating/ParsingMetadata/Transcoding）的任务，
/// 随后按配置的并发上限启动多个 worker 循环，从队列中消费导入项并依次完成：
/// 校验 → 解析元数据 → 转码 AAC → 入库待审核。每条任务的状态推进通过 MusicDbContext 持久化。
/// </summary>
public sealed class LibraryImportBackgroundService(
    LibraryImportQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<UploadOptions> uploadOptions,
    IOptions<TranscodingOptions> transcodingOptions,
    IOptions<SourceDownloadOptions> sourceDownloadOptions,
    FfmpegMediaService mediaService,
    AudioMetadataParser metadataParser,
    ILyricsResolver lyricsResolver,
    QuarantineStoragePaths storagePaths,
    LibraryStoragePaths libraryPaths,
    ILogger<LibraryImportBackgroundService> logger) : BackgroundService
{
    /// <summary>
    /// 后台服务入口。
    /// 先恢复未完成任务，再按并发上限启动多个 worker 并等待全部结束。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 恢复启动前处于中间状态的任务（进程重启时这些任务可能卡在处理中）。
        await RecoverQueuedItemsAsync(stoppingToken);
        // 并发上限来自配置，夹紧在 1~4 之间，避免转码占用过多资源。
        var workerCount = Math.Clamp(transcodingOptions.Value.MaxConcurrentJobs, 1, 4);
        // 启动 N 个并行的消费者循环，共享同一队列通道；外加一个转码失败自动重试扫描器。
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => ProcessQueueAsync(stoppingToken))
            .Append(ProcessingRetrySweepAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    /// <summary>转码/处理失败的自动重试上限（超出后停留在 processing_failed，需人工处理）。</summary>
    private const int MaxProcessingRetries = 2;

    /// <summary>转码失败自动重试的扫描间隔（5 分钟）。</summary>
    private static readonly TimeSpan ProcessingRetrySweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>永久性失败错误码：重试不会改变结果，直接排除在自动重试之外。</summary>
    private static readonly HashSet<string> NonRetryableErrorCodes = new(StringComparer.Ordinal)
    {
        ErrorCodes.UnsupportedMediaFormat,
        ErrorCodes.UploadTooLarge,
        ErrorCodes.ResourceNotFound,
        "duplicate_private_upload"
    };

    /// <summary>
    /// 转码失败自动重试扫描：每 5 分钟把「processing_failed 且未达重试上限且错误码可重试」的条目
    /// 重置回 uploaded 重新入队（计数 +1），覆盖 ffmpeg 抖动/资源竞争等瞬时失败场景。
    /// </summary>
    private async Task ProcessingRetrySweepAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ProcessingRetrySweepInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
                var candidates = await db.LibraryImportItems
                    .Where(item => item.Status == LibraryImportItemStatuses.ProcessingFailed
                        && item.ProcessingRetryCount < MaxProcessingRetries
                        && (item.ErrorCode == null || !NonRetryableErrorCodes.Contains(item.ErrorCode)))
                    .OrderBy(item => item.UpdatedAtUtc)
                    .Take(20)
                    .ToListAsync(cancellationToken);
                foreach (var item in candidates)
                {
                    item.Status = LibraryImportItemStatuses.Uploaded;
                    item.TranscodeStatus = "queued";
                    item.ErrorCode = null;
                    item.ErrorMessage = null;
                    item.ProcessingRetryCount++;
                    item.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    await queue.QueueAsync(item.Id, cancellationToken);
                    logger.LogInformation("Import item {ImportItemId} re-queued for processing retry {Attempt}/{Max}.",
                        item.Id, item.ProcessingRetryCount, MaxProcessingRetries);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Processing retry sweep failed.");
            }
        }
    }

    /// <summary>
    /// 恢复未完成的导入任务。
    /// 将处于 Validating/ParsingMetadata/Transcoding 状态的条目重置为 Uploaded 并重新入队，
    /// 确保进程重启后中断的任务能继续处理。
    /// </summary>
    private async Task RecoverQueuedItemsAsync(CancellationToken cancellationToken)
    {
        // 后台服务为单例，需手动创建作用域以获取 scoped 的 MusicDbContext。
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
        var retryableStatuses = new[]
        {
            LibraryImportItemStatuses.Validating,
            LibraryImportItemStatuses.ParsingMetadata,
            LibraryImportItemStatuses.Transcoding
        };
        // 批量将中间状态重置为 Uploaded（待重新处理），并重置转码子状态为 queued。
        await db.LibraryImportItems.Where(item => retryableStatuses.Contains(item.Status))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, LibraryImportItemStatuses.Uploaded)
                .SetProperty(item => item.TranscodeStatus, "queued"), cancellationToken);
        // 查询所有 Uploaded 状态的条目 ID 并重新入队。
        var queuedItems = await db.LibraryImportItems.AsNoTracking()
            .Where(item => item.Status == LibraryImportItemStatuses.Uploaded)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var itemId in queuedItems)
            await queue.QueueAsync(itemId, cancellationToken);
    }

    /// <summary>
    /// 单个 worker 的消费循环。
    /// 持续从队列读取导入项 ID 并处理，直到服务停止或通道关闭。
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var itemId in queue.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessItemAsync(itemId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 服务正常停止时退出循环。
                    return;
                }
                catch (Exception exception)
                {
                    // 单个任务处理失败不应影响整个 worker，记录日志并标记失败后继续消费下一条。
                    logger.LogError(exception, "Failed to process import item {ImportItemId}.", itemId);
                    await MarkFailedAsync(itemId, ErrorCodes.InternalError, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 主机关闭时队列读取被取消（无界通道在停止前不会主动 Complete），
            // 属于正常的优雅关闭流程，无需作为错误记录。
            logger.LogDebug("Library import worker is stopping because the host is shutting down.");
        }
    }

    /// <summary>
    /// 处理单条导入项：推动其状态机从 Uploaded 到 PendingReview。
    /// 完整流程：校验文件 → ffprobe 探测 → TagLib# 解析元数据 → ffmpeg 转码 AAC → 持久化。
    /// 任一阶段抛出 MediaProcessingException 时，将条目标记为 ProcessingFailed 并记录错误码。
    /// </summary>
    private async Task ProcessItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // 为每条任务创建独立作用域，确保 DbContext 不被多线程共享。
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
        var item = await db.LibraryImportItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        // 条目不存在或已被其他 worker 处理（状态非 Uploaded）则跳过。
        if (item is null || item.Status != LibraryImportItemStatuses.Uploaded)
            return;

        // 将所属批次状态推进为 Processing，并清空完成时间。
        await db.LibraryImportBatches.Where(batch => batch.Id == item.BatchId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(batch => batch.Status, LibraryImportStatuses.Processing)
                .SetProperty(batch => batch.CompletedAtUtc, (DateTime?)null), cancellationToken);

        try
        {
            // 阶段 1：校验 —— 状态置为 Validating，检查文件存在性、扩展名与大小。
            item.Status = LibraryImportItemStatuses.Validating;
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            // 通过隔离存储路径解析器定位上传文件的物理路径。
            if (!storagePaths.TryResolve(item.SourceStorageKey, out var sourcePath) || !File.Exists(sourcePath))
                throw new MediaProcessingException(ErrorCodes.ResourceNotFound, "The uploaded file could not be found in quarantine.");

            var extension = Path.GetExtension(item.DisplayName).TrimStart('.').ToLowerInvariant();
            if (!uploadOptions.Value.AllowedFormats.Contains(extension, StringComparer.OrdinalIgnoreCase))
                throw new MediaProcessingException(ErrorCodes.UnsupportedMediaFormat, "The file extension is not allowed.");
            if (new FileInfo(sourcePath).Length > uploadOptions.Value.MaxFileSizeBytes)
                throw new MediaProcessingException(ErrorCodes.UploadTooLarge, "The file exceeded the configured upload limit.");

            // 使用 ffprobe 探测真实容器，防止扩展名伪造。
            var probe = await mediaService.ProbeAsync(sourcePath, cancellationToken);
            if (!MatchesAllowedContainer(probe.FormatNames, uploadOptions.Value.AllowedFormats))
                throw new MediaProcessingException(ErrorCodes.UnsupportedMediaFormat, "The detected audio container is not allowed.");

            // 阶段 2：解析元数据 —— 状态置为 ParsingMetadata，保存探测结果。
            item.Status = LibraryImportItemStatuses.ParsingMetadata;
            item.Container = probe.Container;
            item.Codec = probe.Codec;
            item.BitrateKbps = probe.BitrateKbps;
            item.DurationMs = probe.DurationMs;
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            // 通过 TagLib# 解析标签并提取封面。
            // 空值回退：外部音源下载的文件常无 ID3 标签，解析结果为空时保留音源 API 预填的元数据
            // （手动上传原本就无预填值，?? 回退无副作用）。
            var metadata = metadataParser.Parse(sourcePath, item.Id);
            item.Title = metadata.Title ?? item.Title;
            if (metadata.Artists.Count > 0)
                item.ArtistsJson = JsonSerializer.Serialize(metadata.Artists);
            item.AlbumTitle = metadata.AlbumTitle ?? item.AlbumTitle;
            item.Lyrics = metadata.Lyrics;
            item.TrackNumber = metadata.TrackNumber ?? item.TrackNumber;
            item.DiscNumber = metadata.DiscNumber ?? item.DiscNumber;
            item.ReleaseYear = metadata.ReleaseYear ?? item.ReleaseYear;
            if (metadata.Genres.Count > 0)
                item.GenresJson = JsonSerializer.Serialize(metadata.Genres);
            // 时长优先采用元数据解析结果，回退到 ffprobe 探测值。
            item.DurationMs = metadata.DurationMs ?? item.DurationMs;
            item.ArtworkStorageKey = metadata.ArtworkStorageKey ?? item.ArtworkStorageKey;

            // 歌词自动补全：内嵌标签 → 同名旁挂 .lrc → 外部歌词源（LRCLIB）。
            // 歌词属于可选内容，解析失败只记告警，不影响转码与后续发布。
            var warnings = metadata.Warnings.ToList();
            var lyricsMatch = await lyricsResolver.ResolveAsync(
                new LyricsQuery(
                    item.Title,
                    metadata.Artists.Count > 0 ? metadata.Artists : DeserializeList(item.ArtistsJson),
                    item.AlbumTitle,
                    item.DurationMs,
                    metadata.Lyrics,
                    sourcePath),
                cancellationToken);
            if (lyricsMatch is not null)
            {
                item.Lyrics = lyricsMatch.Content;
                item.LyricsSource = lyricsMatch.Source;
                item.LyricsConfidence = lyricsMatch.Confidence;
                item.LyricsProviderId = lyricsMatch.ProviderId;
            }
            else
            {
                warnings.Add("lyrics_missing");
            }
            item.WarningsJson = JsonSerializer.Serialize(warnings);
            // 阶段 3：转码 —— 状态置为 Transcoding，转码子状态置为 processing。
            item.Status = LibraryImportItemStatuses.Transcoding;
            item.TranscodeStatus = "processing";
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            // 输出存储键格式：transcoded/{itemId}.m4a
            var outputKey = $"transcoded/{item.Id:N}.m4a";
            if (!storagePaths.TryResolve(outputKey, out var outputPath))
                throw new MediaProcessingException(ErrorCodes.InternalError, "The transcode output path is invalid.");
            // 调用 ffmpeg 将源文件转码为 AAC/m4a。
            await mediaService.TranscodeToAacAsync(sourcePath, outputPath, transcodingOptions.Value.BitrateKbps, cancellationToken);
            // 转码后再次探测，校验输出确实为 AAC 编码，防止静默失败。
            var outputProbe = await mediaService.ProbeAsync(outputPath, cancellationToken);
            if (!string.Equals(outputProbe.Codec, "aac", StringComparison.OrdinalIgnoreCase))
                throw new MediaProcessingException(ErrorCodes.TranscodeFailed, "The output did not contain AAC audio.");

            // 阶段 4：完成 —— 转码产物落库，按来源分流：管理员导入进入人工审核发布队列；
            // 用户上传自动私有入库（立即可听），并进入“待审核转公开”队列。
            item.TranscodedStorageKey = outputKey;
            item.TranscodeStatus = "succeeded";
            item.TranscodeFormat = "m4a";
            item.TranscodeBitrateKbps = transcodingOptions.Value.BitrateKbps;

            if (item.IsUserUpload)
            {
                var uploaderId = await db.LibraryImportBatches.AsNoTracking()
                    .Where(batch => batch.Id == item.BatchId)
                    .Select(batch => batch.CreatedByUserId)
                    .FirstAsync(cancellationToken);
                var (privateTrackId, alreadyPublic) = await AutoPublishPrivateAsync(db, item, uploaderId, outputPath, cancellationToken);
                if (privateTrackId is null)
                {
                    // 同一音频已被其他用户作为私有曲目上传，无法安全引用。
                    item.Status = LibraryImportItemStatuses.ProcessingFailed;
                    item.TranscodeStatus = "failed";
                    item.ErrorCode = "duplicate_private_upload";
                    item.ErrorMessage = "The same audio was uploaded as a private track by another user.";
                }
                else
                {
                    item.PublishedTrackId = privateTrackId;
                    // 引用到已公开曲目（重复上传同一首公共歌曲）直接终态；新建私有曲目待管理员审核转公开。
                    item.Status = alreadyPublic
                        ? LibraryImportItemStatuses.Published
                        : LibraryImportItemStatuses.PendingPublication;
                }
            }
            else
            {
                // 音源抓歌条目：转码完成后先评分落库；达标且总开关开启时走 ImportPublishService 直通发布，
                // 未达标/开关关闭/直通失败一律转人工审核（不自动丢弃）。
                if (item.SourceProvider is not null)
                {
                    var score = SourceDownload.SourceScoreCalculator.Calculate(item);
                    item.AutoScore = score.Total;
                    item.AutoScoreBreakdownJson = score.ToBreakdownJson();

                    if (sourceDownloadOptions.Value.AutoPublishEnabled &&
                        score.Total >= sourceDownloadOptions.Value.AutoPublishThreshold)
                    {
                        // PublishAsync 的前置守卫要求「已审核 + ReadyToPublish」，直通即视为评分审核通过。
                        item.MetadataReviewed = true;
                        item.Status = LibraryImportItemStatuses.ReadyToPublish;
                        item.UpdatedAtUtc = DateTime.UtcNow;
                        await db.SaveChangesAsync(cancellationToken);

                        var publishService = scope.ServiceProvider.GetRequiredService<ImportPublishService>();
                        var publishResult = await publishService.PublishAsync(
                            item,
                            new ImportPublishContext(Guid.Empty, $"auto-publish:{item.Id:N}", IsAutomatic: true),
                            forceFulfill: false,
                            overrideReason: null,
                            cancellationToken);
                        if (publishResult.Succeeded)
                        {
                            // PublishAsync 内部已完成事务提交、批次收口与隔离区清理，仅回写外部任务终态。
                            await UpdateExternalTaskAsync(db, item, autoPublished: true, cancellationToken);
                            return;
                        }
                        // 直通失败不阻断：降级转人工审核，由管理员在审核页处理。
                        logger.LogWarning("Auto-publish failed for import item {ImportItemId} (score {Score}): {Message}; falling back to manual review.",
                            item.Id, score.Total, publishResult.Error?.Message);
                        item.MetadataReviewed = false;
                        item.Status = LibraryImportItemStatuses.PendingReview;
                        await UpdateExternalTaskAsync(db, item, autoPublished: false, cancellationToken);
                    }
                    else
                    {
                        item.Status = LibraryImportItemStatuses.PendingReview;
                        await UpdateExternalTaskAsync(db, item, autoPublished: false, cancellationToken);
                    }
                }
                else
                {
                    item.Status = LibraryImportItemStatuses.PendingReview;
                }
            }
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            // 入库成功后清理隔离区（源文件/转码产物/封面均已落库，不再保留）。
            if (item.IsUserUpload && item.Status is LibraryImportItemStatuses.PendingPublication or LibraryImportItemStatuses.Published)
            {
                TryDeleteQuarantineFile(item.SourceStorageKey);
                TryDeleteQuarantineFile(item.TranscodedStorageKey);
                if (item.ArtworkStorageKey is not null)
                    TryDeleteQuarantineFile(item.ArtworkStorageKey);
            }
            // 尝试将所属批次标记为完成。
            await UpdateBatchStatusAsync(db, item.BatchId, cancellationToken);
        }
        catch (MediaProcessingException exception)
        {
            // 业务校验或处理失败：标记为 ProcessingFailed 并保存错误码。
            item.Status = LibraryImportItemStatuses.ProcessingFailed;
            item.TranscodeStatus = "failed";
            item.ErrorCode = exception.Code;
            item.ErrorMessage = SafeErrorMessage(exception.Code);
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await UpdateBatchStatusAsync(db, item.BatchId, cancellationToken);
        }
    }

    /// <summary>
    /// 用户上传条目的自动私有入库：创建私有曲目（仅上传者可听）+ 播放媒体资产 + 歌词 + 曲库引用。
    /// 发布时按内容哈希二次查重：命中公开曲目（或上传者自己的私有曲目）直接建引用、不重复存储；
    /// 命中其他用户的私有曲目时无法安全引用，返回 null（调用方标记处理失败）。
    /// </summary>
    /// <returns>(曲目 Id, 是否为已公开曲目)；无法入库时为 (null, false)。</returns>
    private async Task<(Guid? TrackId, bool AlreadyPublic)> AutoPublishPrivateAsync(
        MusicDbContext db,
        LibraryImportItem item,
        Guid uploaderId,
        string transcodedPath,
        CancellationToken cancellationToken)
    {
        // 二次查重：内容哈希命中已有媒体资产。
        var existing = await db.MediaAssets.AsNoTracking()
            .Where(asset => asset.ContentHash == item.ContentHash)
            .Select(asset => new { asset.TrackId, asset.Track!.PublishedAt, asset.Track.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            var isPublic = existing.PublishedAt != null;
            if (!isPublic && existing.OwnerUserId != uploaderId)
                return (null, false);
            if (!await db.UserLibraryItems.AnyAsync(entry => entry.UserId == uploaderId && entry.TrackId == existing.TrackId, cancellationToken))
                db.UserLibraryItems.Add(new UserLibraryItem { UserId = uploaderId, TrackId = existing.TrackId });
            return (existing.TrackId, isPublic);
        }

        // 标题回退：标签缺失时用文件名主干，保证曲目可识别。
        var title = string.IsNullOrWhiteSpace(item.Title)
            ? Path.GetFileNameWithoutExtension(item.DisplayName).Trim()
            : item.Title.Trim();
        if (title.Length == 0)
            title = "未命名曲目";

        var track = new Track
        {
            Title = title.Length <= 300 ? title : title[..300],
            TrackNumber = item.TrackNumber,
            DiscNumber = item.DiscNumber,
            ReleaseYear = item.ReleaseYear,
            DurationMs = item.DurationMs,
            AddedAt = DateTimeOffset.UtcNow,
            // 私有：PublishedAt 为空，前台查询天然不可见，仅上传者本人可播放。
            PublishedAt = null,
            OwnerUserId = uploaderId
        };

        // 媒体资产：仅存转码产物（playback m4a），不存原文件，避免原文件数倍冗余。
        var assetId = Guid.NewGuid();
        var playbackKey = $"playback/{track.Id:N}/{assetId:N}.m4a";
        if (!libraryPaths.TryResolve(playbackKey, out var finalPlaybackPath))
            throw new MediaProcessingException(ErrorCodes.InternalError, "The playback destination is invalid.");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPlaybackPath)!);
        System.IO.File.Copy(transcodedPath, finalPlaybackPath, overwrite: false);
        db.MediaAssets.Add(new MediaAsset
        {
            Id = assetId,
            Track = track,
            StorageKey = playbackKey,
            Container = "m4a",
            Codec = "aac",
            ContentHash = item.ContentHash,
            BitrateKbps = item.TranscodeBitrateKbps,
            IsPublished = true,
            IsPrimary = true
        });

        // 封面：内嵌封面直接搬运；无内嵌封面时私有阶段不抓取外部图（转公开审核时可再补）。
        if (item.ArtworkStorageKey is not null && storagePaths.TryResolve(item.ArtworkStorageKey, out var artworkSource) && System.IO.File.Exists(artworkSource))
        {
            var artworkId = Guid.NewGuid();
            var artworkExtension = Path.GetExtension(artworkSource);
            var artworkKey = $"artwork/{artworkId:N}{artworkExtension}";
            if (libraryPaths.TryResolve(artworkKey, out var artworkDestination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(artworkDestination)!);
                System.IO.File.Copy(artworkSource, artworkDestination, overwrite: false);
                track.Artwork = new Artwork { Id = artworkId, StorageKey = artworkKey, ContentType = GetImageContentType(artworkExtension) };
            }
        }

        // 艺人/专辑/流派：与发布流程一致的查重建档（名称大小写不敏感）。
        var artistNames = DeserializeList(item.ArtistsJson);
        var resolvedArtists = new List<Artist>();
        foreach (var artistName in artistNames)
        {
            var normalized = artistName.ToUpperInvariant();
            var artist = await db.Artists.FirstOrDefaultAsync(candidate => candidate.Name.ToUpper() == normalized, cancellationToken)
                ?? new Artist { Name = artistName };
            if (db.Entry(artist).State == EntityState.Detached)
                db.Artists.Add(artist);
            resolvedArtists.Add(artist);
            db.Set<TrackArtist>().Add(new TrackArtist { Track = track, Artist = artist });
        }
        if (!string.IsNullOrWhiteSpace(item.AlbumTitle))
        {
            var albumName = item.AlbumTitle.Trim();
            var album = await db.Albums.FirstOrDefaultAsync(candidate => candidate.Title.ToUpper() == albumName.ToUpperInvariant(), cancellationToken)
                ?? new Album { Title = albumName, ReleaseYear = item.ReleaseYear };
            if (track.Artwork is not null && album.ArtworkId is null)
                album.Artwork = track.Artwork;
            track.Album = album;
            foreach (var artist in resolvedArtists)
                db.Set<AlbumArtist>().Add(new AlbumArtist { Album = album, Artist = artist });
        }
        foreach (var genreName in DeserializeList(item.GenresJson))
        {
            var normalized = genreName.ToUpperInvariant();
            var genre = await db.Genres.FirstOrDefaultAsync(candidate => candidate.Name.ToUpper() == normalized, cancellationToken)
                ?? new Genre { Name = genreName };
            if (db.Entry(genre).State == EntityState.Detached)
                db.Genres.Add(genre);
            db.Set<TrackGenre>().Add(new TrackGenre { Track = track, Genre = genre });
        }

        // 歌词：内嵌/旁挂/外部源自动匹配的结果直接落库（来源与置信度一并保留）。
        if (!string.IsNullOrWhiteSpace(item.Lyrics))
        {
            var isLrc = System.Text.RegularExpressions.Regex.IsMatch(item.Lyrics, @"\[\d{1,2}:\d{1,2}");
            db.Lyrics.Add(new Lyric
            {
                Track = track,
                Language = null,
                Format = isLrc ? LyricFormats.Lrc : LyricFormats.Plain,
                Content = item.Lyrics,
                IsDefault = true,
                Source = item.LyricsSource ?? LyricsSources.Manual,
                Confidence = item.LyricsConfidence ?? 1.0,
                ProviderId = item.LyricsProviderId
            });
        }

        db.Tracks.Add(track);
        db.UserLibraryItems.Add(new UserLibraryItem { UserId = uploaderId, Track = track });
        return (track.Id, false);
    }

    /// <summary>
    /// 回写外部音源下载任务的终态：导入流水线处理完成后，按 ImportItemId 反查任务，
    /// 写入评分与 completed 状态（AutoPublished 区分直通入库还是转人工审核）。任务不存在时静默跳过。
    /// </summary>
    private static async Task UpdateExternalTaskAsync(MusicDbContext db, LibraryImportItem item, bool autoPublished, CancellationToken cancellationToken)
    {
        await db.ExternalDownloadTasks
            .Where(task => task.ImportItemId == item.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(task => task.Status, ExternalDownloadTaskStatuses.Completed)
                .SetProperty(task => task.AutoScore, item.AutoScore)
                .SetProperty(task => task.AutoPublished, autoPublished)
                .SetProperty(task => task.UpdatedAtUtc, DateTime.UtcNow), cancellationToken);
    }

    /// <summary>删除隔离区中的单个文件（入库后清理，失败仅忽略）。</summary>
    private void TryDeleteQuarantineFile(string? storageKey)
    {
        if (storageKey is null || !storagePaths.TryResolve(storageKey, out var path))
            return;
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException) { }
    }

    /// <summary>把 JSON 数组字符串解析为字符串列表；解析失败返回空列表。</summary>
    private static List<string> DeserializeList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>按扩展名推断封面 MIME 类型。</summary>
    private static string GetImageContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// 将导入项标记为处理失败。
    /// 用于 ProcessQueueAsync 捕获到非 MediaProcessingException 的未知异常时的兜底标记。
    /// 内部再次创建独立作用域，避免与已释放的作用域冲突。
    /// </summary>
    private async Task MarkFailedAsync(Guid itemId, string errorCode, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
            var item = await db.LibraryImportItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
            if (item is null)
                return;
            item.Status = LibraryImportItemStatuses.ProcessingFailed;
            item.TranscodeStatus = "failed";
            item.ErrorCode = errorCode;
            item.ErrorMessage = SafeErrorMessage(errorCode);
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await UpdateBatchStatusAsync(db, item.BatchId, cancellationToken);
        }
        catch (Exception exception)
        {
            // 标记失败本身出错时仅记录日志，不再向上抛出，避免无限循环。
            logger.LogError(exception, "Failed to persist processing error for import item {ImportItemId}.", itemId);
        }
    }

    /// <summary>
    /// 根据批次内所有导入项的状态，推进批次的整体状态。
    /// 当批次内所有条目都处于终态（待审核/已发布/已拒绝/处理失败）时，
    /// 将批次标记为 Completed 或 CompletedWithErrors，并记录完成时间。
    /// </summary>
    private static async Task UpdateBatchStatusAsync(MusicDbContext db, Guid batchId, CancellationToken cancellationToken)
    {
        var statuses = await db.LibraryImportItems.AsNoTracking()
            .Where(item => item.BatchId == batchId)
            .Select(item => item.Status)
            .ToListAsync(cancellationToken);
        // 若存在非终态条目，则批次尚未完成，直接返回。
        if (statuses.Count == 0 || statuses.Any(status => status is not (
            LibraryImportItemStatuses.PendingReview or
            LibraryImportItemStatuses.ReadyToPublish or
            LibraryImportItemStatuses.PendingPublication or
            LibraryImportItemStatuses.Published or
            LibraryImportItemStatuses.Rejected or
            LibraryImportItemStatuses.ProcessingFailed)))
            return;

        // 只要有处理失败或被拒绝的条目，批次即标记为带错完成。
        var hasErrors = statuses.Any(status => status is LibraryImportItemStatuses.ProcessingFailed or LibraryImportItemStatuses.Rejected);
        await db.LibraryImportBatches.Where(batch => batch.Id == batchId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(batch => batch.Status, hasErrors ? LibraryImportStatuses.CompletedWithErrors : LibraryImportStatuses.Completed)
                .SetProperty(batch => batch.CompletedAtUtc, DateTime.UtcNow), cancellationToken);
    }

    /// <summary>
    /// 判断 ffprobe 探测到的容器名是否匹配允许的格式列表。
    /// 处理 ffprobe 容器名与扩展名的别名映射（如 matroska↔mka、asf↔wma、mov/mp4↔m4a/m4b）。
    /// </summary>
    private static bool MatchesAllowedContainer(string formatNames, IReadOnlyCollection<string> allowedFormats)
    {
        var aliases = formatNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var allowed = new HashSet<string>(allowedFormats, StringComparer.OrdinalIgnoreCase);
        return aliases.Any(alias => allowed.Contains(alias)) ||
            (aliases.Contains("matroska", StringComparer.OrdinalIgnoreCase) && allowed.Contains("mka")) ||
            (aliases.Contains("asf", StringComparer.OrdinalIgnoreCase) && allowed.Contains("wma")) ||
            (aliases.Any(alias => alias is "mov" or "mp4") && (allowed.Contains("m4a") || allowed.Contains("m4b")));
    }

    /// <summary>
    /// 将内部错误码映射为面向用户的友好错误消息，避免泄露内部实现细节。
    /// </summary>
    private static string SafeErrorMessage(string code) => code switch
    {
        ErrorCodes.UnsupportedMediaFormat => "The file format is not supported.",
        ErrorCodes.UploadTooLarge => "The file exceeded the configured upload limit.",
        "media_processor_unavailable" => "The server media processor is not available.",
        "media_processing_timeout" => "Media processing exceeded the configured time limit.",
        ErrorCodes.MediaParseFailed => "The media file could not be parsed.",
        ErrorCodes.TranscodeFailed => "The media file could not be transcoded.",
        ErrorCodes.ResourceNotFound => "The quarantined file could not be found.",
        _ => "The import item could not be processed."
    };
}