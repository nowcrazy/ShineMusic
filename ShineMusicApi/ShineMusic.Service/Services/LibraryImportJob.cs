// LibraryImportJob.cs
// 曲库导入的后台处理器（RabbitMQ 消费端按消息触发，每条消息一个 DI 作用域）。
// 该文件包含两个职责内聚的类型：
//   1. LibraryImportJob：单条导入项的处理（校验 → 解析元数据 → 转码 AAC → 入库待审核），
//      可直接注入 scoped 的 ISqlSugarClient / ImportPublishService；
//   2. LibraryImportRetrySweepJob：周期性扫描「处理失败且可重试」的条目重新投递（定时器驱动）。
//
// 业务状态机（LibraryImportItem.Status）仍是唯一事实源：重试节奏完全由业务表 + 扫描作业控制，
// 保证重启后状态可恢复、可观测；消息重复投递时状态守卫（仅 Uploaded 才处理）保证幂等。

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service.Messaging;
using ShineMusic.Service.SourceDownload;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>
/// 曲库导入处理作业：推动单条导入项的状态机从 Uploaded 到 PendingReview/PendingPublication/Published。
/// 完整流程：校验文件 → ffprobe 探测 → TagLib# 解析元数据 → 歌词多源匹配 → ffmpeg 转码 AAC → 持久化。
/// 任一阶段抛出 MediaProcessingException 时，将条目标记为 ProcessingFailed 并记录错误码；
/// 未知异常兜底同样落 ProcessingFailed（作业本身不再抛——业务表已完整记录，消费端直接 ack）。
/// </summary>
public sealed class LibraryImportJob(
    ISqlSugarClient db,
    IOptions<UploadOptions> uploadOptions,
    IOptions<TranscodingOptions> transcodingOptions,
    IOptions<SourceDownloadOptions> sourceDownloadOptions,
    IOptions<LibraryImportOptions> libraryImportOptions,
    FfmpegMediaService mediaService,
    AudioMetadataParser metadataParser,
    ILyricsResolver lyricsResolver,
    QuarantineStoragePaths storagePaths,
    LibraryStoragePaths libraryPaths,
    ImportPublishService publishService,
    ILogger<LibraryImportJob> logger)
{
    /// <summary>
    /// 处理单条导入项。由 RabbitMQ 消息触发（上传接口投递 / 启动恢复 / 失败重试扫描）。
    /// </summary>
    public async Task ProcessAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
        // 条目不存在或已被其他 worker 处理（状态非 Uploaded）则跳过。
        if (item is null || item.Status != LibraryImportItemStatuses.Uploaded)
            return;

        // 将所属批次状态推进为 Processing，并清空完成时间。
        await db.Updateable<LibraryImportBatch>()
            .SetColumns(batch => batch.Status == LibraryImportStatuses.Processing)
            .SetColumns(batch => batch.CompletedAtUtc == (DateTime?)null)
            .Where(batch => batch.Id == item.BatchId)
            .ExecuteCommandAsync(cancellationToken);

        try
        {
            // 阶段 1：校验 —— 状态置为 Validating，检查文件存在性、扩展名与大小。
            item.Status = LibraryImportItemStatuses.Validating;
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
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
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);

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
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);

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
                var uploaderId = await db.Queryable<LibraryImportBatch>()
                    .Where(batch => batch.Id == item.BatchId)
                    .Select(batch => batch.CreatedByUserId)
                    .FirstAsync(cancellationToken);
                var (privateTrackId, alreadyPublic) = await AutoPublishPrivateAsync(item, uploaderId, outputPath, cancellationToken);
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
                        await db.Updateable(item).ExecuteCommandAsync(cancellationToken);

                        var publishResult = await publishService.PublishAsync(
                            item,
                            new ImportPublishContext(Guid.Empty, $"auto-publish:{item.Id:N}", IsAutomatic: true),
                            forceFulfill: false,
                            overrideReason: null,
                            cancellationToken);
                        if (publishResult.Succeeded)
                        {
                            // PublishAsync 内部已完成事务提交、批次收口与隔离区清理，仅回写外部任务终态。
                            await UpdateExternalTaskAsync(item, autoPublished: true, cancellationToken);
                            return;
                        }
                        // 直通失败不阻断：降级转人工审核，由管理员在审核页处理。
                        logger.LogWarning("Auto-publish failed for import item {ImportItemId} (score {Score}): {Message}; falling back to manual review.",
                            item.Id, score.Total, publishResult.Error?.Message);
                        item.MetadataReviewed = false;
                        item.Status = LibraryImportItemStatuses.PendingReview;
                        await UpdateExternalTaskAsync(item, autoPublished: false, cancellationToken);
                    }
                    else
                    {
                        item.Status = LibraryImportItemStatuses.PendingReview;
                        await UpdateExternalTaskAsync(item, autoPublished: false, cancellationToken);
                    }
                }
                else
                {
                    // 管理员手动导入：与音源抓歌相同，转码完成后评分；达标直通发布，未达标转人工审核。
                    var score = SourceDownload.SourceScoreCalculator.Calculate(item);
                    item.AutoScore = score.Total;
                    item.AutoScoreBreakdownJson = score.ToBreakdownJson();

                    if (libraryImportOptions.Value.AutoPublishEnabled &&
                        score.Total >= libraryImportOptions.Value.AutoPublishThreshold)
                    {
                        item.MetadataReviewed = true;
                        item.Status = LibraryImportItemStatuses.ReadyToPublish;
                        item.UpdatedAtUtc = DateTime.UtcNow;
                        await db.Updateable(item).ExecuteCommandAsync(cancellationToken);

                        var publishResult = await publishService.PublishAsync(
                            item,
                            new ImportPublishContext(Guid.Empty, $"auto-publish:{item.Id:N}", IsAutomatic: true),
                            forceFulfill: false,
                            overrideReason: null,
                            cancellationToken);
                        if (publishResult.Succeeded)
                        {
                            // PublishAsync 内部已完成事务提交、批次收口与隔离区清理。
                            return;
                        }
                        // 直通失败不阻断：降级转人工审核，由管理员在审核页处理。
                        logger.LogWarning("Auto-publish failed for manual import item {ImportItemId} (score {Score}): {Message}; falling back to manual review.",
                            item.Id, score.Total, publishResult.Error?.Message);
                        item.MetadataReviewed = false;
                        item.Status = LibraryImportItemStatuses.PendingReview;
                    }
                    else
                    {
                        item.Status = LibraryImportItemStatuses.PendingReview;
                    }
                }
            }
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            // 入库成功后清理隔离区（源文件/转码产物/封面均已落库，不再保留）。
            if (item.IsUserUpload && item.Status is LibraryImportItemStatuses.PendingPublication or LibraryImportItemStatuses.Published)
            {
                TryDeleteQuarantineFile(item.SourceStorageKey);
                TryDeleteQuarantineFile(item.TranscodedStorageKey);
                if (item.ArtworkStorageKey is not null)
                    TryDeleteQuarantineFile(item.ArtworkStorageKey);
            }
            // 尝试将所属批次标记为完成。
            await UpdateBatchStatusAsync(item.BatchId, cancellationToken);
        }
        catch (MediaProcessingException exception)
        {
            // 业务校验或处理失败：标记为 ProcessingFailed 并保存错误码。
            item.Status = LibraryImportItemStatuses.ProcessingFailed;
            item.TranscodeStatus = "failed";
            item.ErrorCode = exception.Code;
            item.ErrorMessage = SafeErrorMessage(exception.Code);
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            await UpdateBatchStatusAsync(item.BatchId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 未知异常兜底：同样落业务失败态（作业不再外抛，消息消费者正常 ack，失败明细以业务表为准）。
            logger.LogError(exception, "Failed to process import item {ImportItemId}.", itemId);
            item.Status = LibraryImportItemStatuses.ProcessingFailed;
            item.TranscodeStatus = "failed";
            item.ErrorCode = ErrorCodes.InternalError;
            item.ErrorMessage = SafeErrorMessage(ErrorCodes.InternalError);
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(item).ExecuteCommandAsync(CancellationToken.None);
            await UpdateBatchStatusAsync(item.BatchId, CancellationToken.None);
        }
    }

    /// <summary>
    /// 用户上传条目的自动私有入库：创建私有曲目（仅上传者可听）+ 播放媒体资产 + 歌词 + 曲库引用。
    /// 发布时按内容哈希二次查重：命中公开曲目（或上传者自己的私有曲目）直接建引用、不重复存储；
    /// 命中其他用户的私有曲目时无法安全引用，返回 null（调用方标记处理失败）。
    /// </summary>
    /// <returns>(曲目 Id, 是否为已公开曲目)；无法入库时为 (null, false)。</returns>
    private async Task<(Guid? TrackId, bool AlreadyPublic)> AutoPublishPrivateAsync(
        LibraryImportItem item,
        Guid uploaderId,
        string transcodedPath,
        CancellationToken cancellationToken)
    {
        // 二次查重：内容哈希命中已有媒体资产。
        var existing = await db.Queryable<MediaAsset>()
            .LeftJoin<Track>((asset, track) => asset.TrackId == track.Id)
            .Where((asset, track) => asset.ContentHash == item.ContentHash)
            .Select((asset, track) => new { asset.TrackId, track.PublishedAt, track.OwnerUserId })
            .FirstAsync(cancellationToken);
        if (existing is not null)
        {
            var isPublic = existing.PublishedAt != null;
            if (!isPublic && existing.OwnerUserId != uploaderId)
                return (null, false);
            if (!await db.Queryable<UserLibraryItem>().AnyAsync(entry => entry.UserId == uploaderId && entry.TrackId == existing.TrackId, cancellationToken))
                await db.Insertable(new UserLibraryItem { UserId = uploaderId, TrackId = existing.TrackId }).ExecuteCommandAsync(cancellationToken);
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
        var playbackAsset = new MediaAsset
        {
            Id = assetId,
            TrackId = track.Id,
            StorageKey = playbackKey,
            Container = "m4a",
            Codec = "aac",
            ContentHash = item.ContentHash,
            BitrateKbps = item.TranscodeBitrateKbps,
            IsPublished = true,
            IsPrimary = true
        };

        // 封面：内嵌封面直接搬运；无内嵌封面时私有阶段不抓取外部图（转公开审核时可再补）。
        Artwork? artwork = null;
        if (item.ArtworkStorageKey is not null && storagePaths.TryResolve(item.ArtworkStorageKey, out var artworkSource) && System.IO.File.Exists(artworkSource))
        {
            var artworkId = Guid.NewGuid();
            var artworkExtension = Path.GetExtension(artworkSource);
            var artworkKey = $"artwork/{artworkId:N}{artworkExtension}";
            if (libraryPaths.TryResolve(artworkKey, out var artworkDestination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(artworkDestination)!);
                System.IO.File.Copy(artworkSource, artworkDestination, overwrite: false);
                artwork = new Artwork { Id = artworkId, StorageKey = artworkKey, ContentType = GetImageContentType(artworkExtension) };
                track.ArtworkId = artwork.Id;
            }
        }

        // 艺人/专辑/流派：与发布流程一致的查重建档（名称大小写不敏感；SqlSugar 不级联保存，需显式逐表写入）。
        var resolvedArtists = new List<Artist>();
        var newArtists = new List<Artist>();
        foreach (var artistName in DeserializeList(item.ArtistsJson))
        {
            var normalized = artistName.ToUpperInvariant();
            var artist = await db.Queryable<Artist>().FirstAsync(candidate => candidate.Name.ToUpper() == normalized, cancellationToken);
            if (artist is null)
            {
                artist = new Artist { Name = artistName };
                newArtists.Add(artist);
            }
            resolvedArtists.Add(artist);
        }

        Album? album = null;
        var albumIsNew = false;
        var albumArtworkNeedsUpdate = false;
        if (!string.IsNullOrWhiteSpace(item.AlbumTitle))
        {
            var albumName = item.AlbumTitle.Trim();
            album = await db.Queryable<Album>().FirstAsync(candidate => candidate.Title.ToUpper() == albumName.ToUpperInvariant(), cancellationToken);
            if (album is null)
            {
                album = new Album { Title = albumName, ReleaseYear = item.ReleaseYear, ArtworkId = artwork?.Id };
                albumIsNew = true;
            }
            else if (artwork is not null && album.ArtworkId is null)
            {
                album.ArtworkId = artwork.Id;
                albumArtworkNeedsUpdate = true;
            }
            track.AlbumId = album.Id;
        }

        var resolvedGenres = new List<Genre>();
        var newGenres = new List<Genre>();
        foreach (var genreName in DeserializeList(item.GenresJson))
        {
            var normalized = genreName.ToUpperInvariant();
            var genre = await db.Queryable<Genre>().FirstAsync(candidate => candidate.Name.ToUpper() == normalized, cancellationToken);
            if (genre is null)
            {
                genre = new Genre { Name = genreName };
                newGenres.Add(genre);
            }
            resolvedGenres.Add(genre);
        }

        // 歌词：内嵌/旁挂/外部源自动匹配的结果直接落库（来源与置信度一并保留）。
        Lyric? lyric = null;
        if (!string.IsNullOrWhiteSpace(item.Lyrics))
        {
            var isLrc = System.Text.RegularExpressions.Regex.IsMatch(item.Lyrics, @"\[\d{1,2}:\d{1,2}");
            lyric = new Lyric
            {
                TrackId = track.Id,
                Language = null,
                Format = isLrc ? LyricFormats.Lrc : LyricFormats.Plain,
                Content = item.Lyrics,
                IsDefault = true,
                Source = item.LyricsSource ?? LyricsSources.Manual,
                Confidence = item.LyricsConfidence ?? 1.0,
                ProviderId = item.LyricsProviderId
            };
        }

        // 曲目创建链路（封面/专辑/艺人/流派/歌词/资产/用户引用）同事务提交，等价原先单次 SaveChanges 的原子性。
        await db.Ado.BeginTranAsync();
        try
        {
            if (artwork is not null)
                await db.Insertable(artwork).ExecuteCommandAsync(cancellationToken);
            if (albumIsNew)
                await db.Insertable(album!).ExecuteCommandAsync(cancellationToken);
            else if (albumArtworkNeedsUpdate)
            {
                var albumId = album!.Id;
                var albumArtworkId = album.ArtworkId;
                await db.Updateable<Album>()
                    .SetColumns(candidate => candidate.ArtworkId == albumArtworkId)
                    .Where(candidate => candidate.Id == albumId)
                    .ExecuteCommandAsync(cancellationToken);
            }
            foreach (var artist in newArtists)
                await db.Insertable(artist).ExecuteCommandAsync(cancellationToken);
            foreach (var genre in newGenres)
                await db.Insertable(genre).ExecuteCommandAsync(cancellationToken);
            await db.Insertable(track).ExecuteCommandAsync(cancellationToken);

            // 关联表写入：元数据里艺人/流派名可能重复（同一艺人在 ArtistsJson 出现多次），
            // 先按 Id 去重，避免复合主键 (TrackId,ArtistId)/(TrackId,GenreId) 冲突。
            var distinctArtists = resolvedArtists.DistinctBy(a => a.Id).ToList();
            var distinctGenres = resolvedGenres.DistinctBy(g => g.Id).ToList();

            // 曲目是新建的，TrackArtist/TrackGenre 不可能预存；用 Storageable+WhereColumns
            // 统一走"不存在则插入"，同时把批内重复交给 WhereColumns 兜底去重。
            if (distinctArtists.Count > 0)
            {
                var trackArtists = distinctArtists
                    .Select(a => new TrackArtist { TrackId = track.Id, ArtistId = a.Id })
                    .ToList();
                await db.Storageable(trackArtists)
                    .WhereColumns(link => new { link.TrackId, link.ArtistId })
                    .ToStorage()
                    .AsInsertable.ExecuteCommandAsync(cancellationToken);
            }

            // AlbumArtist 需兼容"同专辑多首曲目分批导入/并发导入"：专辑已存在、关联可能已建。
            // 用 Storageable+WhereColumns(AlbumId,ArtistId) 只插入库中尚不存在的组合，
            // 替代原先 AnyAsync 先查再插的竞态窗口（并发事务互相看不到未提交行导致重复插入）。
            if (album is not null && distinctArtists.Count > 0)
            {
                var albumArtists = distinctArtists
                    .Select(a => new AlbumArtist { AlbumId = album.Id, ArtistId = a.Id })
                    .ToList();
                await db.Storageable(albumArtists)
                    .WhereColumns(link => new { link.AlbumId, link.ArtistId })
                    .ToStorage()
                    .AsInsertable.ExecuteCommandAsync(cancellationToken);
            }

            if (distinctGenres.Count > 0)
            {
                var trackGenres = distinctGenres
                    .Select(g => new TrackGenre { TrackId = track.Id, GenreId = g.Id })
                    .ToList();
                await db.Storageable(trackGenres)
                    .WhereColumns(link => new { link.TrackId, link.GenreId })
                    .ToStorage()
                    .AsInsertable.ExecuteCommandAsync(cancellationToken);
            }
            if (lyric is not null)
                await db.Insertable(lyric).ExecuteCommandAsync(cancellationToken);
            await db.Insertable(playbackAsset).ExecuteCommandAsync(cancellationToken);
            await db.Insertable(new UserLibraryItem { UserId = uploaderId, TrackId = track.Id }).ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        return (track.Id, false);
    }

    /// <summary>
    /// 回写外部音源下载任务的终态：导入流水线处理完成后，按 ImportItemId 反查任务，
    /// 写入评分与 completed 状态（AutoPublished 区分直通入库还是转人工审核）。任务不存在时静默跳过。
    /// </summary>
    private async Task UpdateExternalTaskAsync(LibraryImportItem item, bool autoPublished, CancellationToken cancellationToken)
    {
        // 先取到局部变量：SqlSugar 的 SetColumns 右侧按“值”解析，避免被误当成同名列。
        var autoScore = item.AutoScore;
        await db.Updateable<ExternalDownloadTask>()
            .SetColumns(task => task.Status == ExternalDownloadTaskStatuses.Completed)
            .SetColumns(task => task.AutoScore == autoScore)
            .SetColumns(task => task.AutoPublished == autoPublished)
            .SetColumns(task => task.UpdatedAtUtc == DateTime.UtcNow)
            .Where(task => task.ImportItemId == item.Id)
            .ExecuteCommandAsync(cancellationToken);
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
    /// 根据批次内所有导入项的状态，推进批次的整体状态。
    /// 当批次内所有条目都处于终态（待审核/已发布/已拒绝/处理失败）时，
    /// 将批次标记为 Completed 或 CompletedWithErrors，并记录完成时间。
    /// </summary>
    private async Task UpdateBatchStatusAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var statuses = await db.Queryable<LibraryImportItem>()
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
        var batchStatus = hasErrors ? LibraryImportStatuses.CompletedWithErrors : LibraryImportStatuses.Completed;
        await db.Updateable<LibraryImportBatch>()
            .SetColumns(batch => batch.Status == batchStatus)
            .SetColumns(batch => batch.CompletedAtUtc == DateTime.UtcNow)
            .Where(batch => batch.Id == batchId)
            .ExecuteCommandAsync(cancellationToken);
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

/// <summary>
/// 导入条目自动恢复扫描（每 5 分钟驱动）：
///   1. processing_failed 且错误码可重试的条目（ffmpeg 抖动/资源竞争等瞬时失败），
///      重置回 uploaded 并重新投递，最多重试 <see cref="MaxProcessingRetries"/> 次；
///   2. validating / parsing_metadata / transcoding 中间态卡住超过 10 分钟的条目
///      （进程重启/崩溃遗留），强制重置回 uploaded 并重新投递；
/// 两类都覆盖「消息已投递但消费端在业务流程中崩溃」的场景，避免条目永远卡死。
/// 注：Where 表达式里只用编译器内联的 const / 闭包局部变量；private static readonly 字段
///     不能被 SqlSugar Expression 解析器访问，会抛 "Field can't be private"。
/// </summary>
public sealed class LibraryImportRetrySweepJob(
    ISqlSugarClient db,
    IRabbitMessageBus messageBus,
    ImportPublishService publishService,
    IOptions<SourceDownloadOptions> sourceDownloadOptions,
    IOptions<LibraryImportOptions> libraryImportOptions,
    ILogger<LibraryImportRetrySweepJob> logger)
{
    /// <summary>processing_failed 的自动重试上限（超出后停留在 failed，需人工处理）。</summary>
    private const int MaxProcessingRetries = 2;

    /// <summary>中间态卡住判定阈值（分钟）：超过此时间未推进视为进程异常中断。</summary>
    private const int IntermediateStuckMinutes = 10;

    /// <summary>自动发布兜底重试判定阈值（分钟）：降级为 pending_review 的高分条目，
    /// 超过该时间未被发布 → 再次尝试自动 PublishAsync。</summary>
    private const int AutoPublishStuckMinutes = 5;

    /// <summary>每轮扫描的条目上限（避免瞬时批量投递压垮队列）。</summary>
    private const int BatchLimit = 20;

    /// <summary>执行一轮恢复扫描与重投递。</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        // 卡在 lambda 外预计算好 DateTime 阈值；SqlSugar 可解析闭包捕获的局部变量。
        var stuckThreshold = now.AddMinutes(-IntermediateStuckMinutes);
        var autoPublishStuckThreshold = now.AddMinutes(-AutoPublishStuckMinutes);

        // ===== 1) processing_failed 可重试条目 =====
        // 永久性失败错误码（unsupported / upload_too_large / resource_not_found / duplicate_private_upload）
        // 重试不会改变结果，直接在 WHERE 中显式排除（不用 HashSet.Contains，SqlSugar 解析不了）。
        var failedCandidates = await db.Queryable<LibraryImportItem>()
            .Where(item => item.Status == LibraryImportItemStatuses.ProcessingFailed
                && item.ProcessingRetryCount < MaxProcessingRetries
                && (item.ErrorCode == null
                    || (item.ErrorCode != ErrorCodes.UnsupportedMediaFormat
                        && item.ErrorCode != ErrorCodes.UploadTooLarge
                        && item.ErrorCode != ErrorCodes.ResourceNotFound
                        && item.ErrorCode != "duplicate_private_upload")))
            .OrderBy(item => item.UpdatedAtUtc)
            .Take(BatchLimit)
            .ToListAsync(cancellationToken);

        // ===== 2) 中间态卡住条目（UpdatedAtUtc 距今超过阈值）=====
        // 同样用显式 OR 枚举状态，不用 HashSet.Contains。
        var stuckCandidates = await db.Queryable<LibraryImportItem>()
            .Where(item =>
                    (item.Status == LibraryImportItemStatuses.Validating
                     || item.Status == LibraryImportItemStatuses.ParsingMetadata
                     || item.Status == LibraryImportItemStatuses.Transcoding)
                && item.UpdatedAtUtc < stuckThreshold)
            .OrderBy(item => item.UpdatedAtUtc)
            .Take(BatchLimit)
            .ToListAsync(cancellationToken);

        var totalRecovered = 0;

        foreach (var item in failedCandidates)
        {
            item.Status = LibraryImportItemStatuses.Uploaded;
            item.TranscodeStatus = "queued";
            item.ErrorCode = null;
            item.ErrorMessage = null;
            item.ProcessingRetryCount++;
            item.UpdatedAtUtc = now;
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            await messageBus.PublishAsync(QueueNames.Imports, item.Id, cancellationToken);
            logger.LogInformation(
                "Import item {ImportItemId} ({Status}) re-queued for processing retry {Attempt}/{Max}.",
                item.Id, item.Status, item.ProcessingRetryCount, MaxProcessingRetries);
            totalRecovered++;
        }

        foreach (var item in stuckCandidates)
        {
            var previousStatus = item.Status;
            item.Status = LibraryImportItemStatuses.Uploaded;
            item.TranscodeStatus = "queued";
            item.ErrorCode = null;
            item.ErrorMessage = null;
            item.UpdatedAtUtc = now;
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            await messageBus.PublishAsync(QueueNames.Imports, item.Id, cancellationToken);
            logger.LogInformation(
                "Import item {ImportItemId} ({Status}) stuck for {Minutes}m > {Threshold}m; reset to uploaded and re-published.",
                item.Id, previousStatus,
                (int)(now - item.UpdatedAtUtc).TotalMinutes, IntermediateStuckMinutes);
            totalRecovered++;
        }

        // ===== 3) 自动发布兜底：pending_review 但 AutoScore ≥ 阈值 且 转码成功 且 超过滞后期 =====
        // 场景：ImportJob 的自动发布条件满足但 PublishAsync 当时失败（文件短暂不可用、瞬态错误），
        // 降级为 pending_review；后续无人触发再次发布。这里周期重试，直到发布成功或耗尽重试机会。
        // 注意：用户上传 (IsUserUpload=true) 不走此自动发布路径（需要人工审核）。
        var srcThreshold = sourceDownloadOptions.Value.AutoPublishThreshold;
        var libThreshold = libraryImportOptions.Value.AutoPublishThreshold;
        var autoPublishCandidates = await db.Queryable<LibraryImportItem>()
            .Where(item => item.Status == LibraryImportItemStatuses.PendingReview
                && item.TranscodeStatus == "succeeded"
                && item.MetadataReviewed == false
                && item.IsUserUpload == false
                && item.UpdatedAtUtc < autoPublishStuckThreshold
                && item.AutoScore != null
                && (
                    // 音源抓歌条目：AutoPublishEnabled 开 + 分数 ≥ 阈值
                    (item.SourceProvider != null
                        && sourceDownloadOptions.Value.AutoPublishEnabled
                        && item.AutoScore >= srcThreshold)
                    ||
                    // 管理员手动导入条目：AutoPublishEnabled 开 + 分数 ≥ 阈值
                    (item.SourceProvider == null
                        && libraryImportOptions.Value.AutoPublishEnabled
                        && item.AutoScore >= libThreshold)
                ))
            .OrderBy(item => item.UpdatedAtUtc)
            .Take(BatchLimit)
            .ToListAsync(cancellationToken);

        foreach (var item in autoPublishCandidates)
        {
            // 标记为 ReadyToPublish + MetadataReviewed，与 LibraryImportJob 直通路径一致；
            // PublishAsync 内部仍会守卫校验转码文件完整性。
            item.MetadataReviewed = true;
            item.Status = LibraryImportItemStatuses.ReadyToPublish;
            item.UpdatedAtUtc = now;
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);

            var publishResult = await publishService.PublishAsync(
                item,
                new ImportPublishContext(Guid.Empty, $"auto-publish-sweep:{item.Id:N}", IsAutomatic: true),
                forceFulfill: false,
                overrideReason: null,
                cancellationToken);

            if (publishResult.Succeeded)
            {
                // 对音源抓歌条目，还需要把 ExternalDownloadTask 的终态回写
                if (item.SourceProvider is not null)
                    await UpdateExternalDownloadCompletedAsync(item, autoPublished: true, cancellationToken);
                logger.LogInformation(
                    "Import item {ImportItemId} auto-published via sweep (score {Score}).",
                    item.Id, item.AutoScore);
                totalRecovered++;
            }
            else
            {
                // 发布仍然失败：回退到 pending_review，下一轮继续重试（有 UpdatedAtUtc 滞后期门控）。
                item.MetadataReviewed = false;
                item.Status = LibraryImportItemStatuses.PendingReview;
                item.UpdatedAtUtc = now;
                await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
                logger.LogWarning(
                    "Import item {ImportItemId} sweep auto-publish failed (score {Score}): {Message}; will retry next round.",
                    item.Id, item.AutoScore, publishResult.Error?.Message);
            }
        }

        if (totalRecovered > 0)
            logger.LogInformation("Import retry sweep recovered {Total} item(s) (failed={Failed}, stuck={Stuck}, autoPublish={AutoPublish}).",
                totalRecovered, failedCandidates.Count, stuckCandidates.Count, autoPublishCandidates.Count);
    }

    /// <summary>
    /// 发布成功后回写音源下载任务的终态：匹配 ImportItemId 或 (Provider, ProviderTrackId) 关联的任务。
    /// 与 LibraryImportJob.UpdateExternalTaskAsync 逻辑一致但走 sweep 时的简化版本（不需要回扫评分）。
    /// </summary>
    private async Task UpdateExternalDownloadCompletedAsync(
        LibraryImportItem item, bool autoPublished, CancellationToken cancellationToken)
    {
        // 优先：任务记录里直接存了 ImportItemId
        var byImportItem = await db.Queryable<ExternalDownloadTask>()
            .Where(task => task.ImportItemId == item.Id)
            .ToListAsync(cancellationToken);

        foreach (var task in byImportItem)
        {
            task.Status = ExternalDownloadTaskStatuses.Completed;
            task.NextRetryAtUtc = null;
            task.AutoScore = item.AutoScore;
            task.AutoPublished = autoPublished;
            task.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(task).ExecuteCommandAsync(cancellationToken);
        }
    }
}
