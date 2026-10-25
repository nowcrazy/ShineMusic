// 职责：外部音源抓歌的「下载编排层」——任务创建与 RabbitMQ 下载作业。
// 该文件包含三个职责内聚的类型：
//   1. SourceDownloadService：任务创建编排（GetTrackAsync 元数据快照 + (Provider,ProviderTrackId) 去重 + 投递消息）；
//   2. SourceDownloadJob：下载处理器（解析下载地址 → 流式下载到隔离区边下边算 SHA-256
//      → 内容哈希查重 → 建 LibraryImportBatch/Item 预填元数据 → 投递 LibraryImportJob）；
//      由 RabbitMQ 消费端按消息触发（每条消息一个 DI 作用域），可直接注入 scoped 服务。
//   3. 下载失败的领域异常（SourceDuplicateException / SourceDownloadTooLargeException）。
//
// 重试模型：业务退避（30s/2min/10min）写入任务表 NextRetryAtUtc，由 SourceDownloadRetrySweepService
// 到点扫描重新投递；任务表 Status/NextRetryAtUtc 是管理端展示与手动重试的唯一事实源。
// 消息重复投递是安全的：消费端先原子认领（queued → downloading），重复消息认领失败直接跳过。
// 下载失败的清理原则：半成品文件立即删除、任务标记 failed（可手动重试），异常收敛为任务级失败。

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service.Messaging;
using SqlSugar;

namespace ShineMusic.Service.SourceDownload;

/// <summary>任务创建结果：新建/重置入队的任务与因重复被跳过的平台曲目 Id。</summary>
public sealed record CreateSourceDownloadTasksResult(
    IReadOnlyList<ExternalDownloadTask> Created,
    IReadOnlyList<string> SkippedDuplicates);

/// <summary>音源下载任务创建编排：元数据快照 + 去重 + 入队。</summary>
public sealed class SourceDownloadService(
    ISqlSugarClient db,
    IEnumerable<IMusicSourceProvider> providers,
    IRabbitMessageBus messageBus)
{
    /// <summary>
    /// 为指定音源的一批平台曲目 Id 创建下载任务。
    /// 已存在且非 failed 的同 (Provider, ProviderTrackId) 任务直接跳过；
    /// 已 failed 的任务重置为 queued 复用（避免唯一索引冲突）；
    /// 新任务先调 GetTrackAsync 取元数据快照（取不到快照也建任务，留给下载阶段报错）。
    /// </summary>
    public async Task<CreateSourceDownloadTasksResult> CreateTasksAsync(
        string providerName,
        IReadOnlyCollection<string> providerTrackIds,
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Provider, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null || !provider.IsEnabled)
            throw new SourceUnavailableException(provider?.DisabledReason ?? $"Unknown source provider '{providerName}'.");

        var created = new List<ExternalDownloadTask>();
        var newTasks = new List<ExternalDownloadTask>();
        var skipped = new List<string>();
        foreach (var providerTrackId in providerTrackIds.Distinct(StringComparer.Ordinal))
        {
            // (Provider, ProviderTrackId) 有唯一索引，先查重再决定新建/重置/跳过。
            var existing = await db.Queryable<ExternalDownloadTask>().FirstAsync(
                task => task.Provider == provider.Provider && task.ProviderTrackId == providerTrackId, cancellationToken);
            if (existing is not null)
            {
                if (existing.Status != ExternalDownloadTaskStatuses.Failed)
                {
                    skipped.Add(providerTrackId);
                    continue;
                }
                // failed 任务重置复用：清空错误与产物关联，重新入队；自动重试计数归零（新一轮完整退避）。
                existing.Status = ExternalDownloadTaskStatuses.Queued;
                existing.ErrorCode = null;
                existing.ErrorMessage = null;
                existing.ImportItemId = null;
                existing.AutoScore = null;
                existing.AutoPublished = false;
                existing.AutoRetryCount = 0;
                existing.NextRetryAtUtc = null;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(existing).ExecuteCommandAsync(cancellationToken);
                created.Add(existing);
                continue;
            }

            // 元数据快照：取不到不阻塞建任务（下载阶段会再解析并暴露错误）。
            SourceSearchResult? snapshot = null;
            try
            {
                snapshot = await provider.GetTrackAsync(providerTrackId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 快照失败仅降级（任务行保留平台 Id，可后续重试）。
            }

            var task = new ExternalDownloadTask
            {
                Provider = provider.Provider,
                ProviderTrackId = providerTrackId,
                Title = snapshot?.Title,
                ArtistsJson = JsonSerializer.Serialize(snapshot?.Artists ?? []),
                AlbumTitle = snapshot?.AlbumTitle,
                GenresJson = JsonSerializer.Serialize(snapshot?.Genres ?? []),
                DurationMs = snapshot?.DurationMs,
                Quality = snapshot?.Quality,
                License = snapshot?.License,
                SourcePageUrl = snapshot?.SourcePageUrl,
                CoverUrl = snapshot?.CoverUrl,
                Status = ExternalDownloadTaskStatuses.Queued,
                CreatedByUserId = adminUserId
            };
            newTasks.Add(task);
            created.Add(task);
        }

        if (newTasks.Count > 0)
            await db.Insertable(newTasks).ExecuteCommandAsync(cancellationToken);
        foreach (var task in created)
            await messageBus.PublishAsync(QueueNames.Downloads, task.Id, cancellationToken);
        return new CreateSourceDownloadTasksResult(created, skipped);
    }
}

/// <summary>
/// 外部音源下载处理器（RabbitMQ 消费端按消息调用，每条消息一个 DI 作用域）。
/// 单个任务流程：原子认领 → ResolveDownloadAsync → 流式下载（80KB 缓冲 + 增量 SHA-256 + 体积上限熔断）
/// → ContentHash 查重 → 下载封面 → 建导入批次/条目（预填音源元数据）→ 投递 LibraryImportJob。
/// 瞬时失败按 30s/2min/10min 指数退避写入 NextRetryAtUtc（重试扫描服务到点重新投递），
/// 超过 3 次转 failed 终态保留手动重试入口。
/// </summary>
public sealed class SourceDownloadJob(
    ISqlSugarClient db,
    IEnumerable<IMusicSourceProvider> providers,
    IRabbitMessageBus messageBus,
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options,
    QuarantineStoragePaths storagePaths,
    ILogger<SourceDownloadJob> logger)
{
    /// <summary>流式下载缓冲区大小（与 ImportStagingService 保持一致）。</summary>
    private const int CopyBufferSize = 81920;

    /// <summary>封面下载体积上限（10MB，防御平台 CDN 异常）。</summary>
    private const long MaxCoverSizeBytes = 10 * 1024 * 1024;

    /// <summary>下载失败自动重试上限（超出后转 failed 终态，仅保留手动重试入口）。</summary>
    private const int MaxAutoRetries = 3;

    /// <summary>自动重试的指数退避节奏：第 1/2/3 次分别等待 30s / 2min / 10min。</summary>
    private static readonly TimeSpan[] AutoRetryBackoffs =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>处理单个下载任务：下载 → 查重 → 移交导入流水线。由 RabbitMQ 消息触发（建任务投递 / 重试扫描 / 启动恢复 / 手动重试）。</summary>
    public async Task ProcessAsync(Guid taskId, CancellationToken cancellationToken)
    {
        // 原子认领：仅当任务处于 queued 且已到重试点才推进为 downloading；
        // 认领失败（不存在/已被其他 worker 拾取/重试未到点）直接跳过。
        var claimTime = DateTime.UtcNow;
        var claimed = await db.Updateable<ExternalDownloadTask>()
            .SetColumns(candidate => candidate.Status == ExternalDownloadTaskStatuses.Downloading)
            .SetColumns(candidate => candidate.UpdatedAtUtc == claimTime)
            .Where(candidate => candidate.Id == taskId
                && candidate.Status == ExternalDownloadTaskStatuses.Queued
                && (candidate.NextRetryAtUtc == null || candidate.NextRetryAtUtc <= claimTime))
            .ExecuteCommandAsync(cancellationToken);
        if (claimed == 0)
            return;
        var task = await db.Queryable<ExternalDownloadTask>().FirstAsync(candidate => candidate.Id == taskId, cancellationToken);

        // 下载产物路径：失败/查重命中时需要清理，全程跟踪。
        string? audioKey = null;
        string? coverKey = null;
        try
        {
            var provider = providers.FirstOrDefault(candidate => candidate.Provider == task.Provider);
            if (provider is null || !provider.IsEnabled)
                throw new SourceUnavailableException(provider?.DisabledReason ?? $"Source provider '{task.Provider}' is not available.");

            // 阶段 1：解析下载地址（优先无损原文件，兜底平台高码率流）。
            // 自带下载能力的 provider（yt-dlp 系）跳过直链解析，下载分支直接调 DownloadToFileAsync。
            var download = provider is ISourceFileDownloader
                ? null
                : await provider.ResolveDownloadAsync(task.ProviderTrackId, cancellationToken);

            // 阶段 1.5：元数据快照兜底回填 —— 建任务时的 GetTrackAsync 可能被平台偶发空响应吞掉
            // （Jamendo 详情接口实测存在该抖动），此处趁下载已成功重取一次，避免评分丢失元数据维度。
            if (string.IsNullOrWhiteSpace(task.Title))
            {
                var snapshot = await provider.GetTrackAsync(task.ProviderTrackId, cancellationToken);
                if (snapshot is not null)
                {
                    task.Title = snapshot.Title;
                    task.ArtistsJson = JsonSerializer.Serialize(snapshot.Artists);
                    task.AlbumTitle = snapshot.AlbumTitle;
                    task.DurationMs ??= snapshot.DurationMs;
                    task.CoverUrl ??= snapshot.CoverUrl;
                    task.License ??= snapshot.License;
                    task.SourcePageUrl ??= snapshot.SourcePageUrl;
                    if (!string.IsNullOrWhiteSpace(snapshot.Quality))
                        task.Quality = snapshot.Quality;
                    task.GenresJson = JsonSerializer.Serialize(snapshot.Genres);
                    task.UpdatedAtUtc = DateTime.UtcNow;
                    await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
                }
            }

            // 阶段 2：下载到隔离区并计算内容哈希。
            // 直链 provider：HttpClient 流式下载，边下边算 SHA-256，超限熔断；
            // 自下载 provider（yt-dlp 系）：子进程落地后再补算哈希并校验体积。
            if (!storagePaths.TryResolve("sourcedl", out var downloadDir))
                throw new MediaProcessingException(ErrorCodes.InternalError, "The download destination path is invalid.");
            Directory.CreateDirectory(downloadDir);
            var maxBytes = options.Value.Download.MaxFileSizeBytes;
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            string contentHash;
            long sizeBytes;
            if (provider is ISourceFileDownloader selfDownloader)
            {
                // 子进程下载含「下载 + wav 提取」两段耗时，超时上限按下载配置双倍放宽（下限 300s）。
                downloadTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(300, options.Value.Download.TimeoutSeconds * 2)));
                var file = await selfDownloader.DownloadToFileAsync(task.ProviderTrackId, downloadDir, task.Id.ToString("N"), downloadTimeout.Token);
                audioKey = $"sourcedl/{Path.GetFileName(file.FilePath)}";
                sizeBytes = new FileInfo(file.FilePath).Length;
                if (sizeBytes > maxBytes)
                    throw new SourceDownloadTooLargeException();
                await using var readStream = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                contentHash = await HashFileAsync(readStream, downloadTimeout.Token);
                // 任务音质快照缺失时用自下载结果补齐（最终定级仍由入库流水线 ffprobe 探测）。
                if (string.IsNullOrWhiteSpace(task.Quality))
                    task.Quality = file.Quality;
            }
            else
            {
                // 单文件下载超时覆盖「连接 + 整段流读取」全程：无损原文件体积大，默认 120s。
                downloadTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, options.Value.Download.TimeoutSeconds)));
                var extension = ResolveExtension(download!);
                audioKey = $"sourcedl/{task.Id:N}.{extension}";
                if (!storagePaths.TryResolve(audioKey, out var audioPath))
                    throw new MediaProcessingException(ErrorCodes.InternalError, "The download destination path is invalid.");
                Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);

                var client = httpClientFactory.CreateClient(task.Provider);
                await using (var httpStream = await OpenDownloadStreamAsync(client, download!.DownloadUrl, maxBytes, downloadTimeout.Token))
                await using (var fileStream = new FileStream(audioPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    (contentHash, sizeBytes) = await CopyWithHashAsync(httpStream, fileStream, maxBytes, downloadTimeout.Token);
                }
            }
            var audioExtension = Path.GetExtension(audioKey).TrimStart('.').ToLowerInvariant();

            // 阶段 3：内容哈希查重 —— 已发布曲库命中，或导入流水线中存在同哈希的非终态条目，都视为重复。
            var duplicateInLibrary = await db.Queryable<MediaAsset>().AnyAsync(asset => asset.ContentHash == contentHash, cancellationToken);
            var duplicateInQueue = await db.Queryable<LibraryImportItem>().AnyAsync(item => item.ContentHash == contentHash &&
                item.Status != LibraryImportItemStatuses.Rejected &&
                item.Status != LibraryImportItemStatuses.ProcessingFailed &&
                item.Status != LibraryImportItemStatuses.Published, cancellationToken);
            if (duplicateInLibrary || duplicateInQueue)
                throw new SourceDuplicateException();

            // 阶段 4：下载封面到隔离区（可选内容，失败仅降级不影响入库）。
            if (!string.IsNullOrWhiteSpace(task.CoverUrl))
            {
                coverKey = await TryDownloadCoverAsync(httpClientFactory.CreateClient(task.Provider), task, cancellationToken);
            }

            // 阶段 5：建导入批次 + 条目，预填音源快照元数据（TagLib 解析为空时流水线回退保留）。
            var batch = new LibraryImportBatch
            {
                CreatedByUserId = task.CreatedByUserId,
                Status = LibraryImportStatuses.Queued
            };

            var artists = DeserializeList(task.ArtistsJson);
            var item = new LibraryImportItem
            {
                BatchId = batch.Id,
                Status = LibraryImportItemStatuses.Uploaded,
                DisplayName = BuildDisplayName(task, artists, audioExtension),
                SourceStorageKey = audioKey,
                ContentHash = contentHash,
                SizeBytes = sizeBytes,
                Title = task.Title,
                ArtistsJson = task.ArtistsJson,
                AlbumTitle = task.AlbumTitle,
                GenresJson = JsonSerializer.Serialize(MapGenresToMoods(DeserializeList(task.GenresJson))),
                DurationMs = task.DurationMs,
                ArtworkStorageKey = coverKey,
                IsUserUpload = false,
                SourceProvider = task.Provider
            };
            // 批次 + 条目同事务提交（SqlSugar 不级联保存）。
            await db.Ado.BeginTranAsync();
            try
            {
                await db.Insertable(batch).ExecuteCommandAsync(cancellationToken);
                await db.Insertable(item).ExecuteCommandAsync(cancellationToken);
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }

            // 阶段 6：移交导入流水线，任务推进为 import_queued 并回填关联（清掉可能存在的自动重试退避时间）。
            task.Status = ExternalDownloadTaskStatuses.ImportQueued;
            task.NextRetryAtUtc = null;
            task.ImportItemId = item.Id;
            task.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
            await messageBus.PublishAsync(QueueNames.Imports, item.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 下载/移交失败：清理半成品文件（含自下载子进程可能残留的 .part/.ytdl 临时文件）。
            TryDeleteQuarantineFile(audioKey);
            TryDeleteQuarantineFile(coverKey);
            TryDeleteQuarantinePartials(task.Id);
            var (code, message) = exception switch
            {
                SourceDuplicateException => (ErrorCodes.SourceDuplicate, "This track already exists in the library or import queue."),
                SourceUnavailableException unavailable => (ErrorCodes.SourceUnavailable, unavailable.Message),
                SourceDownloadTooLargeException => (ErrorCodes.UploadTooLarge, "The downloaded file exceeded the configured size limit."),
                HttpRequestException or TimeoutException or TaskCanceledException => (ErrorCodes.SourceDownloadFailed, "The audio file could not be downloaded from the source platform."),
                MediaProcessingException media => (media.Code, media.Message),
                _ => (ErrorCodes.InternalError, "The download task could not be processed.")
            };
            // 自动重试：重复/超限属于永久失败，重试无意义直接终态；其余失败按 30s/2min/10min 退避定点重排，
            // 达到上限才转 failed（保留手动重试入口）。
            var retryable = exception is not SourceDuplicateException and not SourceDownloadTooLargeException;
            if (retryable && task.AutoRetryCount < MaxAutoRetries)
            {
                var nextRetryAt = DateTime.UtcNow + AutoRetryBackoffs[Math.Min(task.AutoRetryCount, AutoRetryBackoffs.Length - 1)];
                task.AutoRetryCount++;
                task.Status = ExternalDownloadTaskStatuses.Queued;
                task.NextRetryAtUtc = nextRetryAt;
                task.ErrorCode = code;
                task.ErrorMessage = message;
                task.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
                // 定点重试：退避时间已写入 NextRetryAtUtc，由 SourceDownloadRetrySweepService 到点重新投递。
                logger.LogWarning(exception,
                    "Source download task {TaskId} failed with {ErrorCode}; auto retry {Attempt}/{Max} scheduled at {NextRetryAtUtc}.",
                    taskId, code, task.AutoRetryCount, MaxAutoRetries, nextRetryAt);
                return;
            }
            logger.LogWarning(exception, "Source download task {TaskId} failed with {ErrorCode}.", taskId, code);
            task.Status = ExternalDownloadTaskStatuses.Failed;
            task.NextRetryAtUtc = null;
            task.ErrorCode = code;
            task.ErrorMessage = message;
            task.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
        }
    }

    /// <summary>对已落地文件流式计算 SHA-256（自下载 provider 的事后哈希补算）。</summary>
    private static async Task<string> HashFileAsync(Stream source, CancellationToken cancellationToken)
    {
        using var hasher = SHA256.Create();
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            hasher.TransformBlock(buffer, 0, read, null, 0);
        hasher.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
    }

    /// <summary>清理某任务在隔离区 sourcedl/ 下的全部半成品（自下载子进程失败时可能残留 .part/.ytdl 临时文件）。</summary>
    private void TryDeleteQuarantinePartials(Guid taskId)
    {
        if (!storagePaths.TryResolve("sourcedl", out var downloadDir))
            return;
        try
        {
            if (!Directory.Exists(downloadDir))
                return;
            foreach (var path in Directory.EnumerateFiles(downloadDir, $"{taskId:N}.*"))
            {
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }
        catch (IOException) { }
    }

    /// <summary>打开下载流：响应头到达后即开始流式读取；Content-Length 超限在下载前提前熔断。</summary>
    private static async Task<Stream> OpenDownloadStreamAsync(HttpClient client, string url, long maxBytes, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength.Value > maxBytes)
            throw new SourceDownloadTooLargeException();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    /// <summary>流式复制并增量计算 SHA-256；超过体积上限立即熔断（抛 <see cref="SourceDownloadTooLargeException"/>）。</summary>
    private static async Task<(string ContentHash, long SizeBytes)> CopyWithHashAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        using var hasher = SHA256.Create();
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new SourceDownloadTooLargeException();
            hasher.TransformBlock(buffer, 0, read, null, 0);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        hasher.TransformFinalBlock([], 0, 0);
        return (Convert.ToHexString(hasher.Hash!).ToLowerInvariant(), total);
    }

    /// <summary>下载封面到隔离区；任何失败（网络/超限/非法路径）都返回 null 仅降级。</summary>
    private async Task<string?> TryDownloadCoverAsync(HttpClient client, ExternalDownloadTask task, CancellationToken cancellationToken)
    {
        try
        {
            var coverExtension = ResolveCoverExtension(task.CoverUrl!);
            var coverKey = $"sourcedl/{task.Id:N}.cover.{coverExtension}";
            if (!storagePaths.TryResolve(coverKey, out var coverPath))
                return null;
            Directory.CreateDirectory(Path.GetDirectoryName(coverPath)!);
            await using var httpStream = await client.GetStreamAsync(task.CoverUrl, cancellationToken);
            await using var fileStream = new FileStream(coverPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await CopyWithHashAsync(httpStream, fileStream, MaxCoverSizeBytes, cancellationToken);
            return coverKey;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to download cover for source task {TaskId}; continuing without artwork.", task.Id);
            return null;
        }
    }

    /// <summary>解析音频文件扩展名：优先平台声明容器，回退 URL 路径扩展名，最终兜底 mp3。</summary>
    private static string ResolveExtension(SourceDownloadInfo download)
    {
        if (!string.IsNullOrWhiteSpace(download.Container))
        {
            var container = download.Container.Trim().TrimStart('.').ToLowerInvariant();
            if (container is "flac" or "wav" or "mp3" or "ogg" or "oga" or "aac" or "m4a" or "aiff" or "aif")
                return container;
        }
        try
        {
            var pathExtension = Path.GetExtension(new Uri(download.DownloadUrl).AbsolutePath).TrimStart('.').ToLowerInvariant();
            if (pathExtension.Length is >= 2 and <= 5)
                return pathExtension;
        }
        catch (UriFormatException)
        {
            // URL 异常时走兜底扩展名，下载阶段会再暴露真实问题。
        }
        return "mp3";
    }

    /// <summary>从封面 URL 推断图片扩展名（白名单 jpg/png/webp，兜底 jpg）。</summary>
    private static string ResolveCoverExtension(string coverUrl)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(coverUrl).AbsolutePath).TrimStart('.').ToLowerInvariant();
            return extension is "jpg" or "jpeg" or "png" or "webp" ? extension : "jpg";
        }
        catch (UriFormatException)
        {
            return "jpg";
        }
    }

    /// <summary>生成展示文件名（流水线扩展名白名单校验依赖它）：艺人 - 标题.扩展名，剔除非法字符。</summary>
    private static string BuildDisplayName(ExternalDownloadTask task, IReadOnlyList<string> artists, string extension)
    {
        var baseName = artists.Count > 0 && !string.IsNullOrWhiteSpace(task.Title)
            ? $"{artists[0]} - {task.Title}"
            : task.Title ?? $"{task.Provider}-{task.ProviderTrackId}";
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalidChar, '_');
        if (baseName.Length > 180)
            baseName = baseName[..180];
        return $"{baseName}.{extension}";
    }

    /// <summary>音源流派预填策略：英文流派经 GenreMoodMapper 映射为中文 mood，映射不上保留原值。</summary>
    private static List<string> MapGenresToMoods(IReadOnlyList<string> genres)
    {
        var mapped = new List<string>();
        foreach (var genre in genres)
        {
            var value = GenreMoodMapper.Map(genre) ?? genre;
            if (!mapped.Contains(value, StringComparer.Ordinal))
                mapped.Add(value);
        }
        return mapped;
    }

    /// <summary>把 JSON 数组字符串解析为字符串列表；解析失败返回空列表。</summary>
    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>删除隔离区中的单个文件（失败仅忽略）。</summary>
    private void TryDeleteQuarantineFile(string? storageKey)
    {
        if (storageKey is null || !storagePaths.TryResolve(storageKey, out var path))
            return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}

/// <summary>下载内容已被曲库或导入队列持有（内容哈希重复）。</summary>
public sealed class SourceDuplicateException() : Exception("The downloaded content is a duplicate.");

/// <summary>下载文件超过配置体积上限，流式传输已熔断。</summary>
public sealed class SourceDownloadTooLargeException() : Exception("The downloaded file exceeded the configured size limit.");
