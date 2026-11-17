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
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;
using SqlSugar;

namespace ShineMusic.Service.SourceDownload;

/// <summary>任务创建结果：新建/重置入队的任务与因重复被跳过的平台曲目 Id。</summary>
public sealed record CreateSourceDownloadTasksResult(
    IReadOnlyList<ExternalDownloadTask> Created,
    IReadOnlyList<string> SkippedDuplicates);

/// <summary>
/// 多候选换源任务创建：一首歌跨多个 provider 的候选（已按音质从好到坏排序）。
/// </summary>
/// <param name="PrimaryTitle">主标题（用于元数据快照 + 管理端展示）。</param>
/// <param name="PrimaryArtists">主艺人列表。</param>
/// <param name="Candidates">跨 provider 的搜索结果候选，按音质从好到坏排序。</param>
internal sealed record SongCandidateGroup(
    string PrimaryTitle,
    IReadOnlyList<string> PrimaryArtists,
    IReadOnlyList<SourceSearchItemDto> Candidates);

/// <summary>音源下载任务创建编排：元数据快照 + 去重 + 入队。</summary>
public sealed class SourceDownloadService(
    ISqlSugarClient db,
    IEnumerable<IMusicSourceProvider> providers,
    IRabbitMessageBus messageBus,
    ProviderAccessService providerAccess,
    ISearchAggregator? searchAggregator = null)
{
    /// <summary>
    /// 从请求 DTO 解析出要创建的任务列表，兼容两种模式：
    ///   1) 多候选换源模式（Candidates 非空）：每首歌的多个 provider 候选组；
    ///   2) 单 provider 模式（Provider + ProviderTrackIds）：旧前端兼容。
    /// </summary>
    private static List<SongCandidateGroup> ResolveCandidateGroups(CreateSourceDownloadTasksRequest request)
    {
        if (request.Candidates is { Count: > 0 })
        {
            var primary = request.Candidates[0];
            return [new SongCandidateGroup(
                primary.Title,
                primary.Artists,
                request.Candidates)];
        }

        // 旧单 provider 模式：把每首 trackId 转成单候选组。
        if (string.IsNullOrWhiteSpace(request.Provider) || request.ProviderTrackIds is null)
            throw new ArgumentException("Either Candidates or Provider+ProviderTrackIds must be specified.");

        var result = new List<SongCandidateGroup>();
        foreach (var trackId in request.ProviderTrackIds.Distinct(StringComparer.Ordinal))
        {
            SourceSearchItemDto? prefilled = null;
            if (request.Metadata is not null && request.Metadata.TryGetValue(trackId, out var meta))
                prefilled = meta;
            result.Add(new SongCandidateGroup(
                prefilled?.Title ?? string.Empty,
                prefilled?.Artists ?? [],
                [new SourceSearchItemDto(
                    request.Provider, trackId,
                    prefilled?.Title ?? string.Empty,
                    prefilled?.Artists ?? [],
                    prefilled?.AlbumTitle,
                    prefilled?.DurationMs,
                    prefilled?.Genres ?? [],
                    prefilled?.CoverUrl,
                    prefilled?.SourcePageUrl,
                    prefilled?.License,
                    prefilled?.Quality ?? SourceQualities.Standard,
                    prefilled?.IsLosslessAvailable ?? false,
                    prefilled?.ReleaseYear)]));
        }
        return result;
    }

    /// <summary>
    /// 为一批歌曲创建下载任务（每首歌可能跨多个 provider 带候选）。
    /// 已存在的同 (Provider, ProviderTrackId) 任务处理策略：
    ///   - failed/completed → 重置为 queued 复用；
    ///   - 中间态（queued/downloading/import_queued）→ 跳过避免并发冲突。
    /// 多候选换源模式下，任务的 Provider/ProviderTrackId 取音质最好的候选，
    /// 所有候选序列化进 CandidateProvidersJson，下载阶段自动按顺序换源。
    /// </summary>
    public async Task<CreateSourceDownloadTasksResult> CreateTasksAsync(
        CreateSourceDownloadTasksRequest request,
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        var groups = ResolveCandidateGroups(request);
        if (groups.Count == 0)
            throw new ArgumentException("At least one candidate group is required.");

        // 验证所有候选用到的 provider 都存在且已启用（多候选场景下 Provider 字段各自不同）。
        var providerLookup = providers.ToDictionary(p => p.Provider, StringComparer.OrdinalIgnoreCase);
        var created = new List<ExternalDownloadTask>();
        var skipped = new List<string>();
        var newTasks = new List<ExternalDownloadTask>();

        foreach (var originalGroup in groups)
        {
            var selected = originalGroup.Candidates[0];
            // ID-only clients have no selection snapshot: obtain identity from that exact ID.
            if (request.ExpectedTrack is null && string.IsNullOrWhiteSpace(selected.Title)
                && providerAccess.IsDownloadConfigured(selected.Provider))
            {
                try
                {
                    var detail = await providerAccess.GetMetadataAsync(selected.Provider, selected.ProviderTrackId, cancellationToken);
                    if (detail is not null)
                        selected = selected with { Title = detail.Title, Artists = detail.Artists,
                            DurationMs = detail.DurationMs, AlbumTitle = detail.AlbumTitle };
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { }
            }
            var expectedIdentity = request.ExpectedTrack is { } requested
                ? new TrackIdentity(requested.Title, requested.Artists, requested.DurationMs, requested.AlbumTitle)
                : new TrackIdentity(selected.Title, selected.Artists, selected.DurationMs, selected.AlbumTitle);
            bool Available(SourceSearchItemDto item) =>
                providerLookup.TryGetValue(item.Provider, out var provider) && provider.IsEnabled
                && providerAccess.IsDownloadConfigured(item.Provider);
            // Never replace the selected identity with a higher-quality or newly discovered result.
            if (!Available(selected))
                throw new SourceUnavailableException("The selected provider does not have download enabled.");
            var candidates = new[] { selected }.Concat(originalGroup.Candidates.Skip(1)).Take(20)
                .Where((item, index) => Available(item) && (index == 0 || MatchesFallback(expectedIdentity, item)))
                .DistinctBy(item => (item.Provider.ToLowerInvariant(), item.ProviderTrackId)).ToList();
            if (originalGroup.Candidates.Count == 1 && searchAggregator is not null
                && !string.IsNullOrWhiteSpace(expectedIdentity.Title) && expectedIdentity.Artists.Count > 0
                && expectedIdentity.DurationMs is > 0)
            {
                var names = searchAggregator.Providers
                    .Where(p => p.ConfigEnabled && p.SearchEnabled && p.DownloadEnabled
                        && p.Declares(ProviderCapability.Search) && providerAccess.IsDownloadConfigured(p.ProviderName)
                        && providerLookup.TryGetValue(p.ProviderName, out var legacy) && legacy.IsEnabled
                        && !string.Equals(p.ProviderName, selected.Provider, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Priority).Take(5).Select(p => p.ProviderName).ToArray();
                if (names.Length > 0)
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    budget.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        var found = await searchAggregator.SearchAsync(
                            $"{expectedIdentity.Title} {expectedIdentity.Artists[0]}", names, 10, budget.Token);
                        candidates.AddRange(found.Outcomes.Where(o => o.ErrorCode is null).SelectMany(o => o.Results)
                            .Where(item => names.Contains(item.Provider, StringComparer.OrdinalIgnoreCase))
                            .Select(item => new SourceSearchItemDto(item.Provider, item.ProviderTrackId, item.Title,
                                item.Artists, item.AlbumTitle, item.DurationMs, item.Genres, item.CoverUrl,
                                item.SourcePageUrl, item.License, item.Quality, item.IsLosslessAvailable, item.ReleaseYear))
                            .Where(item => Available(item) && item.DurationMs is > 0 && MatchesFallback(expectedIdentity, item))
                            .OrderBy(item => SourceQualities.Rank(item.Quality)).Take(19));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Discovery is optional; never expose provider exceptions, URLs or credentials.
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            var group = originalGroup with { Candidates = candidates
                .DistinctBy(item => (item.Provider.ToLowerInvariant(), item.ProviderTrackId)).Take(20).ToList() };
            var primary = selected;

            // (Provider, ProviderTrackId) 有唯一索引，先查重再决定新建/重置/跳过。
            var existing = await db.Queryable<ExternalDownloadTask>().FirstAsync(
                task => task.SourceProvider == primary.Provider && task.ProviderTrackId == primary.ProviderTrackId,
                cancellationToken);
            if (existing is not null)
            {
                if (existing.Status == ExternalDownloadTaskStatuses.Queued
                    || existing.Status == ExternalDownloadTaskStatuses.Downloading
                    || existing.Status == ExternalDownloadTaskStatuses.ImportQueued)
                {
                    skipped.Add(primary.ProviderTrackId);
                    continue;
                }
                // failed / completed → 重置复用。多候选模式下一并清空换源相关状态。
                existing.Status = ExternalDownloadTaskStatuses.Queued;
                existing.ErrorCode = null;
                existing.ErrorMessage = null;
                existing.AttemptErrorsJson = null;
                existing.ImportItemId = null;
                existing.AutoScore = null;
                existing.IsAutoPublished = false;
                existing.AutoRetryCount = 0;
                existing.NextRetryAtUtc = null;
                existing.CandidateProvidersJson = group.Candidates.Count > 1
                    ? JsonSerializer.Serialize(group.Candidates)
                    : null;
                existing.AttemptIndex = group.Candidates.Count > 1 ? group.Candidates.ToList().IndexOf(primary) : null;
                existing.SourceProvider = primary.Provider;
                existing.ProviderTrackId = primary.ProviderTrackId;
                existing.ReleaseYear = primary.ReleaseYear;
                existing.ExpectedTitle = expectedIdentity.Title;
                existing.ExpectedArtistsJson = JsonSerializer.Serialize(expectedIdentity.Artists);
                existing.ExpectedDurationMs = expectedIdentity.DurationMs;
                existing.ExpectedAlbumTitle = expectedIdentity.AlbumTitle;
                existing.Title = primary.Title;
                existing.ArtistsJson = JsonSerializer.Serialize(primary.Artists);
                existing.AlbumTitle = primary.AlbumTitle;
                existing.DurationMs = primary.DurationMs;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(existing).ExecuteCommandAsync(cancellationToken);
                created.Add(existing);
                continue;
            }

            // 元数据快照：优先用前端带入的，缺失时回退 GetTrackAsync（降级）。
            SourceSearchResult? snapshot = null;
            try
            {
                snapshot = await providerAccess.GetMetadataAsync(primary.Provider, primary.ProviderTrackId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 快照旧降级（任务行保留 providerTrackId，下载阶段再暴露错误）。
            }

            var candidateProvidersJson = group.Candidates.Count > 1
                ? JsonSerializer.Serialize(group.Candidates)
                : null;

            var task = new ExternalDownloadTask
            {
                SourceProvider = primary.Provider,
                ProviderTrackId = primary.ProviderTrackId,
                Title = snapshot?.Title ?? primary.Title,
                ArtistsJson = JsonSerializer.Serialize(snapshot?.Artists ?? primary.Artists),
                AlbumTitle = snapshot?.AlbumTitle ?? primary.AlbumTitle,
                GenresJson = JsonSerializer.Serialize(snapshot?.Genres ?? primary.Genres),
                DurationMs = snapshot?.DurationMs ?? primary.DurationMs,
                AudioQualityTier = snapshot?.Quality ?? primary.Quality,
                License = snapshot?.License ?? primary.License,
                SourcePageUrl = snapshot?.SourcePageUrl ?? primary.SourcePageUrl,
                CoverUrl = snapshot?.CoverUrl ?? primary.CoverUrl,
                ReleaseYear = snapshot?.ReleaseYear ?? primary.ReleaseYear,
                CandidateProvidersJson = candidateProvidersJson,
                AttemptIndex = candidateProvidersJson is not null ? group.Candidates.ToList().IndexOf(primary) : null,
                Status = ExternalDownloadTaskStatuses.Queued,
                CreatedByUserId = adminUserId,
                ExpectedTitle = expectedIdentity.Title,
                ExpectedArtistsJson = JsonSerializer.Serialize(expectedIdentity.Artists),
                ExpectedDurationMs = expectedIdentity.DurationMs,
                ExpectedAlbumTitle = expectedIdentity.AlbumTitle
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

    internal static bool MatchesFallback(TrackIdentity expected, SourceSearchItemDto candidate)
    {
        var actual = new TrackIdentity(candidate.Title, candidate.Artists, candidate.DurationMs, candidate.AlbumTitle);
        return TrackIdentityMatcher.Evaluate(expected, actual).Verdict == IdentityVerdict.Matched
            && TrackIdentityMatcher.Evaluate(actual, expected).Verdict == IdentityVerdict.Matched
            && (string.IsNullOrWhiteSpace(expected.AlbumTitle)
                || string.Equals(expected.AlbumTitle.Trim(), candidate.AlbumTitle?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 单 provider 便捷重载（旧前端兼容入口）。
    /// </summary>
    public async Task<CreateSourceDownloadTasksResult> CreateTasksAsync(
        string providerName,
        IReadOnlyCollection<string> providerTrackIds,
        IReadOnlyDictionary<string, SourceSearchItemDto>? metadata,
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        var request = new CreateSourceDownloadTasksRequest(
            providerName,
            providerTrackIds as IReadOnlyList<string>,
            metadata);
        return await CreateTasksAsync(request, adminUserId, cancellationToken);
    }
}

/// <summary>
/// 外部音源下载处理器（RabbitMQ 消费端按消息调用，每条消息一个 DI 作用域）。
/// 单个任务流程：原子认领 → 详情基准 → ResolveDownloadAsync → 流式下载（80KB 缓冲 + 增量 SHA-256 + 体积上限熔断）
/// → 有效性校验（时长三层防线：绝对下限 / 基准偏差 / 跨源佐证）→ ContentHash 查重
/// → 下载封面 → 建导入批次/条目（预填音源元数据）→ 投递 LibraryImportJob。
/// 瞬时失败按 30s/2min/10min 指数退避写入 NextRetryAtUtc（重试扫描服务到点重新投递），
/// 超过 3 次转 failed 终态保留手动重试入口。
/// </summary>
public sealed class SourceDownloadJob(
    ISqlSugarClient db,
    ISearchAggregator searchAggregator,
    IRabbitMessageBus messageBus,
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options,
    QuarantineStoragePaths storagePaths,
    ShineMusic.Model.Ports.IMediaTranscoder mediaService,
    ILogger<SourceDownloadJob> logger,
    ProviderAccessService providerAccess)
{
    /// <summary>流式下载缓冲区大小（与 ImportStagingService 保持一致）。</summary>
    private const int CopyBufferSize = 81920;

    /// <summary>Phase 3 音质验证器：复用已注入的 FfmpegMediaService，在下载后做编码级检测。</summary>
    private readonly AudioValidator _audioValidator = new(mediaService, logger);

    /// <summary>Phase 3 质量闸门：消费 QualityPolicy 配置，统一降级策略。</summary>
    private readonly QualityGate _qualityGate = new(options);

    /// <summary>封面下载体积上限（10MB，防御平台 CDN 异常）。</summary>
    private const long MaxCoverSizeBytes = 10 * 1024 * 1024;

    /// <summary>下载失败自动重试上限（超出后转 failed 终态，仅保留手动重试入口）。</summary>
    private const int MaxAutoRetries = 3;

    /// <summary>自动重试的指数退避节奏：第 1/2/3 次分别等待 30s / 2min / 10min。</summary>
    private static readonly TimeSpan[] AutoRetryBackoffs =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>
    /// 频控专用快速退避节奏：第 1/2/3 次分别等待 15s / 30s / 60s。
    /// 适用于酷狗等平台的 errcode:1002「操作太频繁」——封禁是瞬时条件，
    /// 应更快重试而非等待 2min/10min 把任务拖死。
    /// </summary>
    private static readonly TimeSpan[] RateLimitBackoffs =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    /// <summary>判定异常是否由平台频控引起（酷狗等平台返回 errcode:1002 / "操作太频繁"）。</summary>
    private static bool IsRateLimited(Exception exception) =>
        exception is SourceUnavailableException unavailable
        && (unavailable.Message.Contains("频控", StringComparison.Ordinal)
            || unavailable.Message.Contains("操作太频繁", StringComparison.Ordinal));

    /// <summary>跨源时长佐证的按任务记忆化缓存（job 实例随消息 DI 作用域创建，天然任务级隔离）。</summary>
    private long? _crossSourceDurationMs;
    private bool _crossSourceDurationLoaded;

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
        var downloadStarted = false;
        var downloadVerified = false;
        var handoffCommitted = false;
        var downloadWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var provider = providerAccess.RequireDownloadProvider(task.SourceProvider);
            downloadStarted = true;

            // 阶段 1：解析下载地址（优先无损原文件，兜底平台高码率流）。
            // 自带下载能力的 provider（yt-dlp 系）跳过直链解析，下载分支直接调 DownloadToFileAsync。
            var download = provider is ISourceFileDownloader or IQualityLadderProvider
                ? null
                : await provider.ResolveDownloadAsync(task.ProviderTrackId, cancellationToken);

            // 阶段 1.5：详情接口取权威基准 —— 搜索快照可能脏（平台错标/缓存），详情接口是该曲目当前的权威记录；
            // 时长用于下载校验基准（见 EvaluateDownloadedAudioAsync），标题缺失时顺手回填元数据快照。
            // 取不到详情不阻塞下载（保持既有降级哲学），仅退化为快照基准。
            SourceSearchResult? detail = null;
            try
            {
                detail = await providerAccess.GetMetadataAsync(task.SourceProvider, task.ProviderTrackId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Source download task {TaskId}: detail lookup failed; duration validation falls back to snapshot.", task.Id);
            }

            if (detail is not null && ApplyConfirmedMetadata(task, detail))
            {
                task.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
            }

            // 时长校验基准：详情时长优先（权威、实时），缺失或非法时退回任务快照。
            long? expectedDurationMs = task.ExpectedDurationMs is > 0 ? task.ExpectedDurationMs
                : detail?.DurationMs is > 0 ? detail.DurationMs : task.DurationMs;

            // 阶段 2：下载到隔离区并计算内容哈希。
            // 直链 provider：HttpClient 流式下载，边下边算 SHA-256，超限熔断；
            // 自下载 provider（yt-dlp 系）：子进程落地后再补算哈希并校验体积。
            if (!storagePaths.TryResolve("sourcedl", out var downloadDir))
                throw new MediaProcessingException(ErrorCodes.InternalError, "The download destination path is invalid.");
            Directory.CreateDirectory(downloadDir);
            var maxBytes = options.Value.Download.MaxFileSizeBytes;
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            string contentHash = string.Empty;
            long sizeBytes = 0;
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
                // 自下载 provider 的声明等级就是当前实际请求的档位，覆盖搜索阶段的能力快照。
                task.AudioQualityTier = file.Quality;
            }
            else
            {
                var client = httpClientFactory.CreateClient(task.SourceProvider);
                // 在既有候选预算内按目标档优先下载，原文件实测合格即停止。
                var candidates = provider is IQualityLadderProvider ladder
                    ? await ladder.ResolveDownloadCandidatesAsync(task.ProviderTrackId, cancellationToken)
                    : [download!];

                SourceDownloadInfo? accepted = null;
                Exception? lastCandidateDownloadException = null;
                string? lastRejectReason = null;
                var qualityChain = _qualityGate.GetAllowedQualityChain().ToArray();
                foreach (var candidate in candidates.OrderBy(candidate =>
                    candidate.BitrateKbps is null or <= 0 && !candidate.IsLossless ? int.MaxValue :
                    Array.IndexOf(qualityChain, candidate.IsLossless ? AudioQualityLevel.Lossless
                        : candidate.BitrateKbps is >= 320 ? AudioQualityLevel.High320K : AudioQualityLevel.Below320K)))
                {
                    if (!_qualityGate.IsCandidateAllowed(candidate.IsLossless, candidate.BitrateKbps))
                        continue;
                    try
                    {
                        // 每档独立超时：单文件下载覆盖「连接 + 整段流读取」全程，无损原文件体积大，默认 120s。
                        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        attemptTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, options.Value.Download.TimeoutSeconds)));

                        var extension = ResolveExtension(candidate);
                        audioKey = $"sourcedl/{task.Id:N}.{extension}";
                        if (!storagePaths.TryResolve(audioKey, out var audioPath))
                            throw new MediaProcessingException(ErrorCodes.InternalError, "The download destination path is invalid.");
                        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);

                        await using (var httpStream = await OpenDownloadStreamAsync(client, candidate.DownloadUrl, maxBytes, attemptTimeout.Token))
                        await using (var fileStream = new FileStream(audioPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            (contentHash, sizeBytes) = await CopyWithHashAsync(httpStream, fileStream, maxBytes, attemptTimeout.Token);
                        }

                        var (valid, rejectReason) = await EvaluateDownloadedAudioAsync(task, expectedDurationMs, audioKey, cancellationToken, contentHash);
                        if (valid)
                        {
                            accepted = candidate;
                            break;
                        }

                        lastRejectReason = rejectReason;
                        logger.LogWarning(
                            "Source download task {TaskId}: quality {Quality} rejected ({Reason}), falling back to next candidate.",
                            task.Id, candidate.Quality, rejectReason);
                        TryDeleteQuarantineFile(audioKey);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is HttpRequestException
                        or IOException
                        or OperationCanceledException
                        or SourceUnavailableException
                        or SourceDownloadTooLargeException)
                    {
                        lastCandidateDownloadException = exception;
                        logger.LogWarning(exception,
                            "Source download task {TaskId}: quality {Quality} download failed; falling back to next candidate.",
                            task.Id, candidate.Quality);
                        TryDeleteQuarantineFile(audioKey);
                    }
                }

                if (accepted is null)
                {
                    if (lastCandidateDownloadException is not null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo
                            .Capture(lastCandidateDownloadException)
                            .Throw();
                    throw new SourceUnavailableException(lastRejectReason ?? "所有候选音质均未通过有效性校验（音源返回了占位/试听资源）。");
                }

                // 以实际采用的档位回写音质快照（最终定级仍由入库流水线 ffprobe 探测）。
                task.AudioQualityTier = accepted.Quality;
            }
            var audioExtension = Path.GetExtension(audioKey!).TrimStart('.').ToLowerInvariant();

            // 阶段 2.5：有效性校验 —— 直链 provider 已在候选循环内逐档校验；
            // 自下载 provider（yt-dlp 系）只有一个产物，这里单独校验。
            if (provider is ISourceFileDownloader)
                await EnsureDownloadedAudioMatchesTaskAsync(task, expectedDurationMs, audioKey!, cancellationToken, contentHash);

            downloadVerified = true;
            providerAccess.RecordDownloadSuccess(task.SourceProvider, downloadWatch.Elapsed);

            // 阶段 3：内容哈希查重 —— 已发布曲库命中，或导入流水线中存在「有效」同哈希条目，都视为重复。
            // 关键：ImportItem 必须处于终态（published / pending_review / ready_to_publish）才视为真·重复；
            //       processing_failed / parsing_metadata / validating 等中间态或失败态的 ImportItem 被排除，
            //       因为它们对应的隔离文件可能已丢失、或入库流程没走完，本次下载就是为了重新跑一遍。
            var existingAsset = await db.Queryable<MediaAsset>()
                .FirstAsync(asset => asset.ContentHash == contentHash, cancellationToken);
            var effectiveImportItem = await db.Queryable<LibraryImportItem>()
                .Where(item => item.ContentHash == contentHash
                    && (item.Status == LibraryImportItemStatuses.PendingReview
                        || item.Status == LibraryImportItemStatuses.ReadyToPublish
                        || item.Status == LibraryImportItemStatuses.Published))
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstAsync(cancellationToken);

            if (existingAsset is not null || effectiveImportItem is not null)
            {
                // 优先关联到有效导入条目（有跳转审核页的能力）；曲库直命中时尝试反查其导入来源。
                var linkedItemId = effectiveImportItem?.Id;
                if (linkedItemId is null && existingAsset is not null)
                {
                    var trackImportItem = await db.Queryable<LibraryImportItem>()
                        .Where(item => item.PublishedTrackId == existingAsset.TrackId
                            && (item.Status == LibraryImportItemStatuses.PendingReview
                                || item.Status == LibraryImportItemStatuses.ReadyToPublish
                                || item.Status == LibraryImportItemStatuses.Published))
                        .OrderByDescending(item => item.CreatedAtUtc)
                        .FirstAsync(cancellationToken);
                    linkedItemId = trackImportItem?.Id;
                }

                task.Status = ExternalDownloadTaskStatuses.Completed;
                task.NextRetryAtUtc = null;
                task.ImportItemId = linkedItemId;
                task.ErrorCode = null;
                task.ErrorMessage = null;
                task.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(task).ExecuteCommandAsync(cancellationToken);

                // 去重命中的「有效导入条目」可能仍在等人工审核/发布。它引用的源文件就是
                // sourcedl/<taskId>.<ext>（任务重跑时文件名不变，SourceStorageKey 与本次 audioKey 相同）。
                // 若这里照常清理，该条目发布时会因暂存源文件缺失而恒返回 404，永远无法入库。
                // 因此只清理没有被待发布条目引用的文件；本轮新下载的冗余文件仍正常删除。
                var retainedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (effectiveImportItem is { Status: not LibraryImportItemStatuses.Published })
                {
                    if (!string.IsNullOrWhiteSpace(effectiveImportItem.SourceStorageKey))
                        retainedKeys.Add(effectiveImportItem.SourceStorageKey);
                    if (!string.IsNullOrWhiteSpace(effectiveImportItem.ArtworkStorageKey))
                        retainedKeys.Add(effectiveImportItem.ArtworkStorageKey);
                }
                if (audioKey is null || !retainedKeys.Contains(audioKey))
                    TryDeleteQuarantineFile(audioKey);
                if (coverKey is null || !retainedKeys.Contains(coverKey))
                    TryDeleteQuarantineFile(coverKey);
                // TryDeleteQuarantinePartials 按 <taskId>.* 前缀批量删除，可能连带删掉上面要保留的源文件。
                var retainedPartialPrefix = $"sourcedl/{task.Id:N}.";
                if (!retainedKeys.Any(key => key.StartsWith(retainedPartialPrefix, StringComparison.OrdinalIgnoreCase)))
                    TryDeleteQuarantinePartials(task.Id);

                logger.LogInformation(
                    "Source download task {TaskId} deduplicated: content already exists (asset={HasAsset}, importItem={HasImportItem}). Marked as completed.",
                    taskId, existingAsset is not null, linkedItemId is not null);
                return;
            }

            logger.LogInformation(
                "Source download task {TaskId} content hash {Hash} not found in published library or terminal import items; proceeding to stage 4 (cover + create import item).",
                taskId, contentHash);

            // 阶段 4：下载封面到隔离区（可选内容，失败仅降级不影响入库）。
            if (!string.IsNullOrWhiteSpace(task.CoverUrl))
            {
                coverKey = await TryDownloadCoverAsync(httpClientFactory.CreateClient(task.SourceProvider), task, cancellationToken);
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
                SourceStorageKey = audioKey!,
                ContentHash = contentHash,
                SizeBytes = sizeBytes,
                Title = task.Title,
                ArtistsJson = task.ArtistsJson,
                AlbumTitle = task.AlbumTitle,
                GenresJson = JsonSerializer.Serialize(MapGenresToMoods(DeserializeList(task.GenresJson))),
                OriginalGenresJson = task.GenresJson,
                DurationMs = task.DurationMs,
                ReleaseYear = task.ReleaseYear,
                ArtworkStorageKey = coverKey,
                IsUserUpload = false,
                SourceProvider = task.SourceProvider
            };
            MetadataMerge.RecordExisting(item, "title", item.Title, task.SourceProvider, task.ProviderTrackId);
            MetadataMerge.RecordExisting(item, "artists", artists, task.SourceProvider, task.ProviderTrackId);
            MetadataMerge.RecordExisting(item, "album", item.AlbumTitle, task.SourceProvider, task.ProviderTrackId);
            MetadataMerge.RecordExisting(item, "releaseYear", item.ReleaseYear, task.SourceProvider, task.ProviderTrackId);
            MetadataMerge.RecordExisting(item, "originalGenres", DeserializeList(task.GenresJson), task.SourceProvider, task.ProviderTrackId);
            MetadataMerge.RecordExisting(item, "cover", item.ArtworkStorageKey, task.SourceProvider, task.ProviderTrackId);
            // 批次 + 条目同事务提交（SqlSugar 不级联保存）。
            await db.Ado.BeginTranAsync();
            try
            {
                await db.Insertable(batch).ExecuteCommandAsync(cancellationToken);
                await db.Insertable(item).ExecuteCommandAsync(cancellationToken);
                task.Status = ExternalDownloadTaskStatuses.ImportQueued;
                task.NextRetryAtUtc = null;
                task.ImportItemId = item.Id;
                task.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
                await db.Ado.CommitTranAsync();
                handoffCommitted = true;
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }

            // 阶段 6：移交导入流水线，任务推进为 import_queued 并回填关联（清掉可能存在的自动重试退避时间）。
            await messageBus.PublishAsync(QueueNames.Imports, item.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 下载/移交失败：清理半成品文件（含自下载子进程可能残留的 .part/.ytdl 临时文件）。
            if (!handoffCommitted)
            {
                TryDeleteQuarantineFile(audioKey);
                TryDeleteQuarantineFile(coverKey);
                TryDeleteQuarantinePartials(task.Id);
            }
            else
            {
                // 已提交的导入条目拥有这些文件；消息失败交由导入恢复扫描重投。
                logger.LogError(exception, "Import handoff failed after commit for source task {TaskId}; retaining media for recovery.", task.Id);
                return;
            }
            var (code, message) = exception switch
            {
                SourceDuplicateException => (ErrorCodes.SourceDuplicate, "This track already exists in the library or import queue."),
                SourceUnavailableException unavailable => (ErrorCodes.SourceUnavailable, unavailable.Message),
                SourceDownloadTooLargeException => (ErrorCodes.UploadTooLarge, "The downloaded file exceeded the configured size limit."),
                HttpRequestException or TimeoutException or TaskCanceledException => (ErrorCodes.SourceDownloadFailed, "The audio file could not be downloaded from the source platform."),
                MediaProcessingException media => (media.Code, media.Message),
                _ => (ErrorCodes.InternalError, "The download task could not be processed.")
            };
            if (downloadStarted && !downloadVerified)
                providerAccess.RecordDownloadFailure(task.SourceProvider, code, downloadWatch.Elapsed);

            // 多候选换源模型：尝试记录本次 provider 的错误，并决定是继续重试同 provider 还是换源。
            var candidates = LoadCandidateList(task);
            var currentCandidateIndex = task.AttemptIndex ?? (candidates.Count > 1 ? 0 : -1);
            RecordAttemptError(task, currentCandidateIndex, task.SourceProvider!, code, message);

            // 自动重试策略：
            //   - 瞬时故障（网络抖动、频控）→ 同 candidate 内指数退避重试（30s/2min/10min，频控用 15s/30s/60s）；
            //   - 永久/不可恢复错误（资源不可用、占位文件、已下架）→ 直接换源，不浪费重试配额；
            //   - 每个 candidate 最多 3 次 AutoRetry；所有 candidate 都耗尽才转 failed。
            var retryable = exception is not SourceDuplicateException and not SourceDownloadTooLargeException;
            var isTransient = retryable && exception is HttpRequestException or TimeoutException or TaskCanceledException;

            if (isTransient && task.AutoRetryCount < MaxAutoRetries)
            {
                // 同 candidate 的瞬时故障自动重试（指数退避定点重排）。
                var backoffs = IsRateLimited(exception) ? RateLimitBackoffs : AutoRetryBackoffs;
                var nextRetryAt = DateTime.UtcNow + backoffs[Math.Min(task.AutoRetryCount, backoffs.Length - 1)];
                task.AutoRetryCount++;
                task.Status = ExternalDownloadTaskStatuses.Queued;
                task.NextRetryAtUtc = nextRetryAt;
                task.ErrorCode = code;
                task.ErrorMessage = message;
                task.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
                logger.LogWarning(exception,
                    "Source download task {TaskId} (candidate {AttemptIndex}, provider {Provider}) failed with {ErrorCode}; {Mode} auto retry {Retry}/{Max} scheduled at {NextRetryAtUtc}.",
                    taskId, currentCandidateIndex, task.SourceProvider, code, IsRateLimited(exception) ? "rate-limit" : "standard", task.AutoRetryCount, MaxAutoRetries, nextRetryAt);
                return;
            }

            // 当前 candidate 彻底失败（瞬时重试耗尽 或 永久/不可恢复错误）→ 检查是否有下一个候选换源。
            var nextCandidateIndex = currentCandidateIndex + 1;
            if (candidates.Count > 1 && nextCandidateIndex < candidates.Count)
            {
                var next = candidates[nextCandidateIndex];
                logger.LogWarning(exception,
                    "Source download task {TaskId}: candidate {CurrIndex} ({Provider}) exhausted — switching to candidate {NextIndex} ({NextProvider}).",
                    taskId, currentCandidateIndex, task.SourceProvider, nextCandidateIndex, next.Provider);

                // 换源：更新当前尝试的 provider 信息，重置换源内的重试计数，清除单次错误。
                task.SourceProvider = next.Provider;
                task.ProviderTrackId = next.ProviderTrackId;
                task.AttemptIndex = nextCandidateIndex;
                task.AutoRetryCount = 0;
                task.ErrorCode = null;
                task.ErrorMessage = null;
                task.NextRetryAtUtc = null;
                task.Status = ExternalDownloadTaskStatuses.Queued;
                task.ReleaseYear = next.ReleaseYear;
                // 元数据能力可独立关闭；先采用当前候选快照，避免沿用上一音源的信息。
                task.Title = next.Title;
                task.ArtistsJson = JsonSerializer.Serialize(next.Artists);
                task.AlbumTitle = next.AlbumTitle;
                task.DurationMs = next.DurationMs;
                task.CoverUrl = next.CoverUrl;
                task.License = next.License;
                task.SourcePageUrl = next.SourcePageUrl;
                task.AudioQualityTier = next.Quality;
                task.GenresJson = JsonSerializer.Serialize(next.Genres);

                // 仅更新当前候选元数据；原始目标身份留在 Expected* 字段，供评分和完整曲名校验使用。
                try
                {
                    var snapshot = await providerAccess.GetMetadataAsync(next.Provider, next.ProviderTrackId, cancellationToken);
                    if (snapshot is not null)
                    {
                        task.Title = snapshot.Title;
                        task.ArtistsJson = JsonSerializer.Serialize(snapshot.Artists);
                        task.AlbumTitle = snapshot.AlbumTitle;
                        task.DurationMs = snapshot.DurationMs;
                        task.CoverUrl = snapshot.CoverUrl;
                        task.License = snapshot.License;
                        task.SourcePageUrl = snapshot.SourcePageUrl;
                        task.AudioQualityTier = snapshot.Quality;
                        task.GenresJson = JsonSerializer.Serialize(snapshot.Genres);
                        task.ReleaseYear = snapshot.ReleaseYear ?? next.ReleaseYear;
                    }
                }
                catch (Exception snapshotEx) when (snapshotEx is not OperationCanceledException)
                {
                    logger.LogWarning(snapshotEx,
                        "Source download task {TaskId}: detail snapshot for fallback provider {Provider} failed; proceeding with current snapshot.",
                        taskId, next.Provider);
                }

                task.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
                await messageBus.PublishAsync(QueueNames.Downloads, task.Id, cancellationToken);
                return;
            }

            // 所有 candidate 都耗尽：聚合每个 provider 的尝试错误，终态 failed。
            var finalMessage = BuildFinalFailureMessage(task, candidates);
            logger.LogWarning(exception,
                "Source download task {TaskId} exhausted all {Count} candidate providers. Final: {Message}",
                taskId, candidates.Count, finalMessage);
            task.Status = ExternalDownloadTaskStatuses.Failed;
            task.NextRetryAtUtc = null;
            task.ErrorCode = ErrorCodes.SourceUnavailable;
            task.ErrorMessage = finalMessage;
            task.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 从任务的 CandidateProvidersJson 解析多候选列表。
    /// 旧模型（单 provider 无候选）返回只含一个条目、index -1 的列表。
    /// </summary>
    private static List<SourceSearchItemDto> LoadCandidateList(ExternalDownloadTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.CandidateProvidersJson))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<SourceSearchItemDto>>(task.CandidateProvidersJson);
                if (list is { Count: > 0 }) return list;
            }
            catch (JsonException) { /* 损坏 JSON → 回退单条 */ }
        }
        return [new SourceSearchItemDto(
            task.SourceProvider, task.ProviderTrackId,
            task.Title ?? string.Empty, DeserializeList(task.ArtistsJson),
            task.AlbumTitle, task.DurationMs, DeserializeList(task.GenresJson),
            task.CoverUrl, task.SourcePageUrl, task.License,
            task.AudioQualityTier ?? SourceQualities.Standard, false, task.ReleaseYear)];
    }

    /// <summary>
    /// 向任务的 AttemptErrorsJson 追加一条 provider 尝试错误记录。
    /// </summary>
    private static void RecordAttemptError(
        ExternalDownloadTask task, int attemptIndex, string provider, string code, string message)
    {
        var errors = new List<AttemptErrorEntry>();
        if (!string.IsNullOrWhiteSpace(task.AttemptErrorsJson))
        {
            try { errors = JsonSerializer.Deserialize<List<AttemptErrorEntry>>(task.AttemptErrorsJson) ?? []; }
            catch (JsonException) { errors = []; }
        }
        // 同一 index 可能重试多次，只保留最近一次。
        errors.RemoveAll(e => e.AttemptIndex == attemptIndex);
        errors.Add(new AttemptErrorEntry(provider, attemptIndex, code, message));
        task.AttemptErrorsJson = JsonSerializer.Serialize(errors);
    }

    /// <summary>
    /// 聚合所有 candidate 的尝试错误为一段可读最终错误消息。
    /// 格式："所有音源均不可下载：netease(high): VIP无版权; kuwo(standard): 占位试听资源..."
    /// </summary>
    private static string BuildFinalFailureMessage(ExternalDownloadTask task, IReadOnlyList<SourceSearchItemDto> candidates)
    {
        List<AttemptErrorEntry> errors = [];
        if (!string.IsNullOrWhiteSpace(task.AttemptErrorsJson))
        {
            try { errors = JsonSerializer.Deserialize<List<AttemptErrorEntry>>(task.AttemptErrorsJson) ?? []; }
            catch (JsonException) { errors = []; }
        }
        if (errors.Count == 0)
            return "所有候选音源均不可下载。";
        var details = errors
            .OrderBy(e => e.AttemptIndex >= 0 ? e.AttemptIndex : int.MaxValue)
            .Select(e =>
            {
                var candidate = candidates.FirstOrDefault(c =>
                    string.Equals(c.Provider, e.Provider, StringComparison.OrdinalIgnoreCase));
                var suffix = candidate?.Quality is { } quality ? $"({quality})" : string.Empty;

                // 附上候选本身的客观事实，避免只给「可能需要 VIP」这类猜测：
                // 搜到的是几十秒试听、平台标注 VIP、或者根本没搜到——三种原因处置方式完全不同。
                var facts = new List<string>();
                if (candidate is null)
                    facts.Add("未搜到候选");
                else
                {
                    if (candidate.DurationMs is > 0 and < 30_000)
                        facts.Add($"仅搜到 {candidate.DurationMs / 1000} 秒试听");
                    if (!string.IsNullOrWhiteSpace(candidate.License)
                        && candidate.License.Contains("VIP", StringComparison.OrdinalIgnoreCase))
                        facts.Add($"平台标注 {candidate.License}");
                }
                var factText = facts.Count > 0 ? $"［{string.Join("、", facts)}］" : string.Empty;
                return $"{e.Provider}{suffix}: {e.Message}{factText}";
            })
            .ToList();
        return $"所有候选音源均不可下载：{string.Join("; ", details)}";
    }

    /// <summary>单候选尝试的错误记录（AttemptErrorsJson 的条目类型）。</summary>
    private sealed record AttemptErrorEntry(string Provider, int AttemptIndex, string ErrorCode, string Message);

    /// <summary>校验下载文件的有效性（Phase 3 增强版，由 AudioValidator 统一执行）：
    ///   1. 绝对下限：正常歌曲不会短于 30s，更短只可能是平台返回的试听片段/占位音频；
    ///   2. 基准偏差：ffprobe 实测时长与校验基准（详情接口优先、快照兜底）偏差 &gt; 25% 且 &gt; 15s 判占位；
    ///   3. 有损转无损伪装：容器声称无损（.flac/.wav）但 ffprobe 检测实际编码为有损（MP3/AAC 等）；
    ///   4. 跨源佐证：短音频（&lt; 2min）再与其他平台同名同艺人曲目的时长交叉比对——堵住
    ///      「源平台搜索/详情/文件三方自洽地错标」的情况（实测酷我有整曲被一致错标成 60s 试听的案例），
    ///      实测时长不足跨源参照的一半即判试听/占位。
    /// 探测失败或无法验证质量时拒绝；跨源取不到参照不覆盖原文件验证结论。
    /// </summary>
    private async Task<(bool Valid, string? Reason)> EvaluateDownloadedAudioAsync(
        ExternalDownloadTask task,
        long? expectedDurationMs,
        string audioKey,
        CancellationToken cancellationToken,
        string? expectedSha256 = null)
    {
        if (!storagePaths.TryResolve(audioKey, out var audioPath) || !File.Exists(audioPath))
            return (false, "下载文件不存在或隔离路径无效。");

        // Phase 3：使用 AudioValidator 做编码级验证（有损转无损 + 试听片段 + 时长偏差）。
        var result = await _audioValidator.ValidateAsync(
            audioPath, expectedDurationMs, expectedSha256, null, cancellationToken).ConfigureAwait(false);

        if (!result.Accepted)
        {
            logger.LogWarning("Source download task {TaskId}: rejected {ErrorCode} ({Reason}).",
                task.Id, result.ErrorCode, result.Reason);
            return (false, result.Reason);
        }

        if (result.Quality is not { } measured)
            return (false, "缺少实测音频质量。");
        if (!_qualityGate.IsAcceptableForLibrary(measured.Codec, measured.Format.ToString(), measured.BitrateKbps))
            return (false, $"实测编码 {measured.Codec} / {measured.BitrateKbps}kbps 未达到配置入库门槛。");

        // 跨源佐证：AudioValidator 的时长校验通过后，仍需对短音频做跨源交叉验证。
        // 这部分逻辑依赖 searchAggregator，不适合放进 AudioValidator，保留在 SourceDownloadJob 中。
        if (result.Quality?.DurationMs is > 0 and < 120_000)
        {
            var actual = result.Quality.DurationMs.Value;
            var referenceMs = await FindCrossSourceDurationMsAsync(task, cancellationToken).ConfigureAwait(false);
            if (referenceMs is > 0 && actual * 2 < referenceMs.Value)
            {
                return (false,
                    $"下载文件仅 {actual / 1000.0:0.#}s，其他音源同名曲目时长约 {referenceMs.Value / 1000.0:0.#}s，判定为试听/占位资源。");
            }
        }

        return (true, null);
    }

    /// <summary>自下载 provider 的单产物校验：不合格直接抛异常终止任务。</summary>
    private async Task EnsureDownloadedAudioMatchesTaskAsync(
        ExternalDownloadTask task,
        long? expectedDurationMs,
        string audioKey,
        CancellationToken cancellationToken,
        string? expectedSha256 = null)
    {
        var (valid, reason) = await EvaluateDownloadedAudioAsync(task, expectedDurationMs, audioKey, cancellationToken, expectedSha256);
        if (!valid)
            throw new SourceUnavailableException(reason!);
    }

    /// <summary>
    /// 跨源时长佐证：在其他已启用平台搜索同名曲目，返回「标题与主艺人都匹配」条目的最大时长。
    /// 平台自身可能把整曲一致地错标成短试听（搜索/详情/文件三方都错，单源校验失效），
    /// 此时其他平台的真实时长是唯一可信参照；取不到参照返回 null（跳过校验，不误伤）。
    /// 结果按任务记忆化：多档音质候选共享一次搜索（job 实例按消息 DI 作用域隔离，无跨任务污染）。
    /// </summary>
    private async Task<long?> FindCrossSourceDurationMsAsync(ExternalDownloadTask task, CancellationToken cancellationToken)
    {
        if (_crossSourceDurationLoaded)
            return _crossSourceDurationMs;
        _crossSourceDurationLoaded = true;
        try
        {
            var primaryArtist = TryGetPrimaryArtist(task.ArtistsJson);
            if (string.IsNullOrWhiteSpace(primaryArtist) || string.IsNullOrWhiteSpace(task.Title))
                return null;
            var query = $"{TrackIdentityMatcher.NormalizeTitle(task.Title)} {primaryArtist}";
            // Phase 2：源清单以 V2 Registry 为准，扇出 / 超时 / 异常收敛统一由聚合器承担。
            var otherProviders = searchAggregator.Providers
                .Where(descriptor => descriptor.ConfigEnabled
                    && descriptor.SearchEnabled
                    && descriptor.Declares(ProviderCapability.Search)
                    && !string.Equals(descriptor.ProviderName, task.SourceProvider, StringComparison.OrdinalIgnoreCase))
                .Select(descriptor => descriptor.ProviderName)
                .ToArray();
            if (otherProviders.Length == 0)
                return null;

            var aggregated = await searchAggregator.SearchAsync(
                query, otherProviders, 5, cancellationToken);
            long maxMs = 0;
            foreach (var entry in aggregated.AllResults)
            {
                // 身份判定统一走唯一决策点；本类不得再自带标题/艺人匹配实现。
                var expectedIdentity = new TrackIdentity(
                    task.Title,
                    string.IsNullOrWhiteSpace(primaryArtist) ? [] : [primaryArtist]);
                var candidateIdentity = new TrackIdentity(entry.Title, entry.Artists, entry.DurationMs);
                if (TrackIdentityMatcher.Evaluate(expectedIdentity, candidateIdentity).Verdict != IdentityVerdict.Matched)
                    continue;
                if (entry.DurationMs is > 0 && entry.DurationMs.Value > maxMs)
                    maxMs = entry.DurationMs.Value;
            }
            _crossSourceDurationMs = maxMs > 0 ? maxMs : null;
            return _crossSourceDurationMs;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Cross-source duration check skipped for source download task {TaskId}.", task.Id);
            return null;
        }
    }

    /// <summary>从艺人快照 JSON 数组中取主艺人（第一位非空项）；解析失败返回 null。</summary>
    private static string? TryGetPrimaryArtist(string? artistsJson)
    {
        try
        {
            return string.IsNullOrWhiteSpace(artistsJson)
                ? null
                : JsonSerializer.Deserialize<string[]>(artistsJson)?.FirstOrDefault(artist => !string.IsNullOrWhiteSpace(artist));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 曲目身份判定（TitleMatches / ArtistsMatch / NormalizeTitle）已收归
    // TrackIdentityMatcher —— 本类不得再持有本地副本（见 SingleDecisionPointTests 反射断言）。

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
    internal static bool ApplyConfirmedMetadata(ExternalDownloadTask task, SourceSearchResult detail)
    {
        if (!string.Equals(task.SourceProvider, detail.Provider, StringComparison.OrdinalIgnoreCase)
            || task.ProviderTrackId != detail.ProviderTrackId) return false;
        var expected = new TrackIdentity(task.ExpectedTitle ?? task.Title ?? "",
            DeserializeList(task.ExpectedArtistsJson ?? task.ArtistsJson), task.ExpectedDurationMs ?? task.DurationMs,
            task.ExpectedAlbumTitle ?? task.AlbumTitle);
        if (expected.Artists.Count == 0 || detail.Artists.Count == 0) return false;
        if (TrackIdentityMatcher.Evaluate(expected,
            new(detail.Title, detail.Artists, detail.DurationMs, detail.AlbumTitle)).Verdict != IdentityVerdict.Matched) return false;
        if (string.IsNullOrWhiteSpace(task.Title)) task.Title = detail.Title;
        if (DeserializeList(task.ArtistsJson).Count == 0) task.ArtistsJson = JsonSerializer.Serialize(detail.Artists);
        if (string.IsNullOrWhiteSpace(task.AlbumTitle)) task.AlbumTitle = detail.AlbumTitle;
        task.DurationMs ??= detail.DurationMs;
        task.CoverUrl ??= detail.CoverUrl;
        task.ReleaseYear ??= detail.ReleaseYear;
        task.License ??= detail.License;
        task.SourcePageUrl ??= detail.SourcePageUrl;
        if (DeserializeList(task.GenresJson).Count == 0) task.GenresJson = JsonSerializer.Serialize(detail.Genres);
        return true;
    }


    /// <summary>生成展示文件名（流水线扩展名白名单校验依赖它）：艺人 - 标题.扩展名，剔除非法字符。</summary>
    private static string BuildDisplayName(ExternalDownloadTask task, IReadOnlyList<string> artists, string extension)
    {
        var baseName = artists.Count > 0 && !string.IsNullOrWhiteSpace(task.Title)
            ? $"{artists[0]} - {task.Title}"
            : task.Title ?? $"{task.SourceProvider}-{task.ProviderTrackId}";
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
