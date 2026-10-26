// 职责：导入条目「发布入库」核心逻辑的独立服务。
// 原本内嵌在 LibraryImportController.PublishItem 端点中，因「外部音源抓歌评分直通」
// 需要在后台流水线里走同一条发布路径，故抽取为服务供两处复用：
// - 人工发布：管理端点调用，actor 为实际操作的管理员；
// - 自动发布：导入后台服务在评分达标时调用，actor 为系统（Guid.Empty），审计动作记为 track.auto_published。
// 服务不感知 HttpContext：错误以结果对象返回，由调用方映射为 HTTP 响应或后台日志。

using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>发布动作的执行者上下文。</summary>
/// <param name="ActorUserId">操作人用户 Id；系统自动发布时为 <see cref="Guid.Empty"/>。</param>
/// <param name="TraceId">链路追踪 Id（人工发布取 HttpContext.TraceIdentifier，自动发布取合成标识）。</param>
/// <param name="IsAutomatic">是否评分直通自动发布（决定审计动作名 track.published / track.auto_published）。</param>
public sealed record ImportPublishContext(Guid ActorUserId, string TraceId, bool IsAutomatic);

/// <summary>发布失败类别，供调用方映射为 HTTP 响应。</summary>
public enum ImportPublishErrorKind
{
    /// <summary>资源不存在 → 404。</summary>
    NotFound,
    /// <summary>状态冲突 → 409（resource_conflict）。</summary>
    Conflict,
    /// <summary>参数校验失败 → 400（validation_error）。</summary>
    Validation,
    /// <summary>带专用错误码的 409（点歌不匹配等）。</summary>
    Coded,
    /// <summary>服务端内部错误 → 500。</summary>
    Internal
}

/// <summary>发布失败详情。</summary>
public sealed record ImportPublishError(ImportPublishErrorKind Kind, string Message, string? Code = null, string? Field = null);

/// <summary>发布结果：成功时携带新曲目 Id 与条目状态；失败时携带错误详情。</summary>
public sealed record ImportPublishResult(Guid? TrackId, string? Status, ImportPublishError? Error)
{
    public bool Succeeded => Error is null;
    public static ImportPublishResult Success(Guid trackId, string status) => new(trackId, status, null);
    public static ImportPublishResult Failure(ImportPublishErrorKind kind, string message, string? code = null, string? field = null)
        => new(null, null, new ImportPublishError(kind, message, code, field));
}

/// <summary>导入条目发布服务：建 Track、关联艺人/专辑/流派、封面处理、双媒体资产、歌词落库、点歌联动、事务与文件清理。</summary>
public sealed class ImportPublishService(
    ISqlSugarClient db,
    IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
    QuarantineStoragePaths quarantinePaths,
    LibraryStoragePaths libraryPaths,
    ArtworkFetcher artworkFetcher,
    ILogger<ImportPublishService> logger)
{
    /// <summary>
    /// 发布单条「管理导入/音源抓歌」条目（用户上传转公开走控制器内的 PublishUserUploadAsync，不经本方法）。
    /// 调用前调用方需已加载（并跟踪）条目实体。
    /// </summary>
    public async Task<ImportPublishResult> PublishAsync(
        LibraryImportItem item,
        ImportPublishContext context,
        bool forceFulfill,
        string? overrideReason,
        CancellationToken cancellationToken)
    {
        if (item.Status != LibraryImportItemStatuses.ReadyToPublish ||
            item.TranscodeStatus != "succeeded" || !item.MetadataReviewed ||
            string.IsNullOrWhiteSpace(item.TranscodedStorageKey))
            return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "The item must be transcoded successfully and reviewed before publishing.");

        // 发布前完整性校验：歌名、艺人、时长缺一不可；歌词是可选内容
        // （用户上传往往没有歌词，缺失时播放器显示“暂无歌词”，不影响播放与上架）。
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(item.Title)) missing.Add("title");
        if (DeserializeList(item.ArtistsJson).Count == 0) missing.Add("artists");
        if (item.DurationMs is null or <= 0) missing.Add("duration");
        if (missing.Count > 0)
            return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, $"Cannot publish: missing required fields — {string.Join(", ", missing)}.");

        if (!quarantinePaths.TryResolve(item.SourceStorageKey, out var sourcePath) || !System.IO.File.Exists(sourcePath) ||
            !quarantinePaths.TryResolve(item.TranscodedStorageKey, out var transcodedPath) || !System.IO.File.Exists(transcodedPath))
            return ImportPublishResult.Failure(ImportPublishErrorKind.NotFound, "The staged files are no longer available.");

        // 审计先暂存，随发布事务一并提交；发布失败返回时丢弃（与原「仅 SaveChanges 才落库」语义一致）。
        var pendingAudits = new List<AdminAuditEntry>();

        if (await db.Queryable<MediaAsset>().AnyAsync(asset => asset.ContentHash == item.ContentHash, cancellationToken))
            return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "This audio has already been published.");

        // 点歌需求联动：发布前校验「待上架曲目」是否确实是「需求要的那首歌」，防止张冠李戴。
        if (item.SongRequestId.HasValue)
        {
            var request = await db.Queryable<SongRequestRecord>().FirstAsync(
                candidate => candidate.Id == item.SongRequestId.Value, cancellationToken);
            if (request is null)
                return ImportPublishResult.Failure(ImportPublishErrorKind.NotFound, "The linked song request was not found.");

            var matchCheck = fulfillmentOptions.Value.Check("TitleArtistMatch");
            if (matchCheck.IsEnabled)
            {
                var verdict = SongRequestMatchValidator.Validate(
                    item.Title, DeserializeList(item.ArtistsJson), request, fulfillmentOptions.Value.TitleArtistMatch);

                if (verdict.Kind == MatchVerdictKind.Mismatch)
                {
                    // 核心检查：是否允许管理员覆盖由全局配置决定（TitleArtistMatch.AllowAdminOverride）。
                    if (!matchCheck.AllowAdminOverride)
                        return ImportPublishResult.Failure(ImportPublishErrorKind.Coded,
                            $"曲目《{verdict.TrackDisplay}》与需求《{verdict.RequestDisplay}》不一致，无法关联。请修正元数据后再发布。",
                            ErrorCodes.SongRequestMismatch);

                    // 允许覆盖时：必须显式 forceFulfill + 原因，并强制留痕。
                    if (!forceFulfill)
                        return ImportPublishResult.Failure(ImportPublishErrorKind.Coded,
                            $"曲目《{verdict.TrackDisplay}》与需求《{verdict.RequestDisplay}》不一致。若确认无误，请以 forceFulfill=true 并填写 overrideReason 强制关联。",
                            ErrorCodes.SongRequestMismatchOverrideRequired);
                    if (fulfillmentOptions.Value.AdminBypassRequiresReason && string.IsNullOrWhiteSpace(overrideReason))
                        return ImportPublishResult.Failure(ImportPublishErrorKind.Validation, "强制关联时必须填写原因。", field: "overrideReason");

                    AddAudit(pendingAudits, context, "song_request.forced_match", "song_request", request.Id, new
                    {
                        trackTitle = item.Title,
                        requestTitle = request.Title,
                        reason = overrideReason,
                        titleDistance = verdict.TitleDistance,
                        artistDistance = verdict.ArtistDistance
                    });
                }
                else if (verdict.Kind == MatchVerdictKind.FuzzyMatch)
                {
                    // 模糊匹配放行，但留痕便于复盘。
                    AddAudit(pendingAudits, context, "song_request.fuzzy_match", "song_request", request.Id, new
                    {
                        trackTitle = item.Title,
                        requestTitle = request.Title,
                        titleDistance = verdict.TitleDistance,
                        artistDistance = verdict.ArtistDistance
                    });
                }
            }
        }

        var track = new Track
        {
            Title = item.Title!,
            TrackNumber = item.TrackNumber,
            DiscNumber = item.DiscNumber,
            ReleaseYear = item.ReleaseYear,
            DurationMs = item.DurationMs,
            AddedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow
        };
        var artistNames = DeserializeList(item.ArtistsJson);
        var genreNames = DeserializeList(item.GenresJson);
        var artworkId = Guid.NewGuid();
        var artworkSource = string.Empty;
        var hasEmbeddedArtwork = item.ArtworkStorageKey is not null && quarantinePaths.TryResolve(item.ArtworkStorageKey, out artworkSource) && System.IO.File.Exists(artworkSource);
        var copiedPaths = new List<string>();
        var assetId = Guid.NewGuid();
        var originalKey = $"original/{track.Id:N}/{assetId:N}.{Path.GetExtension(item.DisplayName).TrimStart('.').ToLowerInvariant()}";
        var playbackKey = $"playback/{track.Id:N}/{assetId:N}.m4a";
        if (!libraryPaths.TryResolve(originalKey, out var finalOriginalPath) || !libraryPaths.TryResolve(playbackKey, out var finalPlaybackPath))
            return ImportPublishResult.Failure(ImportPublishErrorKind.Internal, "Unable to resolve the publish destination.", ErrorCodes.InternalError);
        Directory.CreateDirectory(Path.GetDirectoryName(finalOriginalPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPlaybackPath)!);
        try
        {
            // —— iTunes 元数据获取：只要有艺人就调一次，无论有没有内嵌封面 ——
            ItunesMetadataResult? itunesResult = null;
            var firstArtist = artistNames.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstArtist))
            {
                var artworkDir = libraryPaths.TryResolve("artwork/.placeholder", out var placeholderPath)
                    ? Path.GetDirectoryName(placeholderPath)!
                    : Path.Combine(Path.GetDirectoryName(finalOriginalPath)!, "..", "..", "artwork");
                artworkDir = Path.GetFullPath(artworkDir);

                itunesResult = await artworkFetcher.FetchAsync(
                    firstArtist,
                    item.AlbumTitle,
                    item.Title,
                    "artwork",
                    artworkDir,
                    cancellationToken);
            }

            // 封面处理：内嵌封面优先，iTunes 封面兜底
            if (hasEmbeddedArtwork)
            {
                var extension = Path.GetExtension(artworkSource);
                var artworkKey = $"artwork/{artworkId:N}{extension}";
                if (libraryPaths.TryResolve(artworkKey, out var artworkDestination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(artworkDestination)!);
                    System.IO.File.Copy(artworkSource, artworkDestination, overwrite: false);
                    copiedPaths.Add(artworkDestination);
                    track.Artwork = new Artwork { Id = artworkId, StorageKey = artworkKey, ContentType = GetImageContentType(extension) };
                }
            }
            else if (itunesResult is { Success: true, StorageKey: not null })
            {
                if (libraryPaths.TryResolve(itunesResult.StorageKey, out var fetchedPath) && System.IO.File.Exists(fetchedPath))
                {
                    copiedPaths.Add(fetchedPath);
                    track.Artwork = new Artwork
                    {
                        Id = artworkId,
                        StorageKey = itunesResult.StorageKey,
                        ContentType = itunesResult.ContentType ?? "image/jpeg"
                    };
                }
            }

            // —— iTunes 元数据回填：只补空字段，不覆盖用户已手动填写的 ——
            if (itunesResult is { Success: true })
            {
                // 流派：ImportItem.GenresJson 为空时，把 iTunes 的英文流派经 GenreMoodMapper 映射成中文 mood 标签
                var currentGenres = DeserializeList(item.GenresJson);
                if (currentGenres.Count == 0 && !string.IsNullOrWhiteSpace(itunesResult.Genre))
                {
                    var mood = GenreMoodMapper.Map(itunesResult.Genre);
                    if (mood is not null)
                    {
                        item.GenresJson = JsonSerializer.Serialize(new[] { mood });
                        genreNames.Add(mood);
                    }
                }

                // 专辑名：空时回填
                if (string.IsNullOrWhiteSpace(item.AlbumTitle) && !string.IsNullOrWhiteSpace(itunesResult.AlbumTitle))
                {
                    item.AlbumTitle = itunesResult.AlbumTitle.Trim();
                }

                // 发行年：空时回填
                if (!item.ReleaseYear.HasValue && itunesResult.ReleaseYear.HasValue)
                {
                    item.ReleaseYear = itunesResult.ReleaseYear.Value;
                }
            }
            System.IO.File.Copy(sourcePath, finalOriginalPath, overwrite: false);
            copiedPaths.Add(finalOriginalPath);
            System.IO.File.Copy(transcodedPath, finalPlaybackPath, overwrite: false);
            copiedPaths.Add(finalPlaybackPath);
            // 艺人/专辑/流派：与导入流水线一致的查重建档（名称大小写不敏感；SqlSugar 不级联保存，需显式逐表写入）。
            var resolvedArtists = new List<Artist>();
            var newArtists = new List<Artist>();
            foreach (var artistName in artistNames)
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
                    album = new Album { Title = albumName, ReleaseYear = item.ReleaseYear, ArtworkId = track.Artwork?.Id };
                    albumIsNew = true;
                }
                else if (track.Artwork is not null && album.ArtworkId is null)
                {
                    album.ArtworkId = track.Artwork.Id;
                    albumArtworkNeedsUpdate = true;
                }
                track.AlbumId = album.Id;
            }

            var resolvedGenres = new List<Genre>();
            var newGenres = new List<Genre>();
            foreach (var genreName in genreNames)
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

            // 若导入项携带有歌词（内嵌标签 / 旁挂 .lrc / 外部源自动匹配 / 人工补充），
            // 则为该曲目创建一份默认歌词，并记录来源与置信度便于后续追溯。
            // 自动检测 LRC 时间轴格式。无歌词时不建记录，播放器按“暂无歌词”处理。
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

            var originalAsset = new MediaAsset
            {
                Id = assetId,
                TrackId = track.Id,
                StorageKey = originalKey,
                Container = item.Container ?? "unknown",
                Codec = item.Codec ?? "unknown",
                ContentHash = item.ContentHash,
                BitrateKbps = item.BitrateKbps,
                IsPublished = true,
                IsPrimary = false
            };
            var playbackAsset = new MediaAsset
            {
                TrackId = track.Id,
                StorageKey = playbackKey,
                Container = "m4a",
                Codec = "aac",
                ContentHash = $"transcoded:{item.Id:N}",
                BitrateKbps = item.TranscodeBitrateKbps,
                IsPublished = true,
                IsPrimary = true
            };

            // SqlSugar 不做导航属性外键修正：曲目封面外键需显式回填。
            track.ArtworkId = track.Artwork?.Id;

            item.Status = LibraryImportItemStatuses.Published;
            // 回填曲目关联：管理员发布路径也记录 PublishedTrackId，供音源任务列表反查歌词有无/预览回退等场景使用。
            item.PublishedTrackId = track.Id;
            item.UpdatedAtUtc = DateTime.UtcNow;
            SongRequestRecord? request = null;
            string? previousRequestStatus = null;
            if (item.SongRequestId.HasValue)
            {
                request = await db.Queryable<SongRequestRecord>().FirstAsync(candidate => candidate.Id == item.SongRequestId.Value, cancellationToken);
                if (request is not null)
                {
                    previousRequestStatus = request.Status;
                    request.Status = SongRequestStatuses.Fulfilled;
                    request.MatchedTrackId = track.Id;
                    request.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
            AddAudit(pendingAudits, context, context.IsAutomatic ? "track.auto_published" : "track.published", "track", track.Id, new { importItemId = item.Id });

            // 曲目创建链路（封面/专辑/艺人/流派/媒体资产/歌词/条目/点歌联动/审计）同事务提交，
            // 等价原先单次 SaveChanges 的原子性。
            await db.Ado.BeginTranAsync();
            try
            {
                if (track.Artwork is not null)
                    await db.Insertable(track.Artwork).ExecuteCommandAsync(cancellationToken);
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
                foreach (var artist in resolvedArtists)
                    await db.Insertable(new TrackArtist { TrackId = track.Id, ArtistId = artist.Id }).ExecuteCommandAsync(cancellationToken);
                if (album is not null)
                    foreach (var artist in resolvedArtists)
                    {
                        // 已有专辑 + 已有艺人的组合可能已建过关联（同专辑多首曲目分批发布），跳过避免主键冲突。
                        var albumId2 = album.Id;
                        var artistId = artist.Id;
                        if (!await db.Queryable<AlbumArtist>().AnyAsync(link => link.AlbumId == albumId2 && link.ArtistId == artistId, cancellationToken))
                            await db.Insertable(new AlbumArtist { AlbumId = albumId2, ArtistId = artistId }).ExecuteCommandAsync(cancellationToken);
                    }
                foreach (var genre in resolvedGenres)
                    await db.Insertable(new TrackGenre { TrackId = track.Id, GenreId = genre.Id }).ExecuteCommandAsync(cancellationToken);
                await db.Insertable(originalAsset).ExecuteCommandAsync(cancellationToken);
                await db.Insertable(playbackAsset).ExecuteCommandAsync(cancellationToken);
                if (lyric is not null)
                    await db.Insertable(lyric).ExecuteCommandAsync(cancellationToken);
                await db.Updateable(item).ExecuteCommandAsync(cancellationToken);

                if (request is not null)
                {
                    await db.Updateable(request).ExecuteCommandAsync(cancellationToken);
                    // 时间线 + 站内信：让提交人知道「这首歌已经上架了」。
                    SongRequestEvents.Add(db, request, SongRequestEventKinds.Fulfilled, previousRequestStatus, SongRequestStatuses.Fulfilled,
                        context.ActorUserId == Guid.Empty ? null : context.ActorUserId,
                        context.IsAutomatic ? SongRequestActorKinds.System : SongRequestActorKinds.Admin, $"通过上传《{track.Title}》满足");
                    NotificationWriter.Push(db, request.CreatedByUserId, NotificationKinds.SongRequestFulfilled,
                        $"你想要的《{request.Title}》已入库",
                        "现在可以在曲库里找到并播放了。",
                        NotificationWriter.RequestsPageUrl, "song_request", request.Id.ToString());
                }

                foreach (var audit in pendingAudits)
                    await db.Insertable(audit).ExecuteCommandAsync(cancellationToken);
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
            await UpdateBatchStatusAsync(db, item.BatchId, cancellationToken);
            TryDelete(sourcePath);
            TryDelete(transcodedPath);
            if (item.ArtworkStorageKey is not null && quarantinePaths.TryResolve(item.ArtworkStorageKey, out var artworkPath))
                TryDelete(artworkPath);
            return ImportPublishResult.Success(track.Id, item.Status);
        }
        // DbException 覆盖 Npgsql 的数据层异常（如内容哈希唯一冲突），与原先 DbUpdateException 的处理范围一致。
        catch (Exception exception) when (exception is IOException or SqlSugarException or System.Data.Common.DbException)
        {
            logger.LogError(exception, "Failed to publish import item {ItemId}.", item.Id);
            foreach (var path in copiedPaths) TryDelete(path);
            return ImportPublishResult.Failure(ImportPublishErrorKind.Internal, "The item could not be published.", ErrorCodes.InternalError);
        }
    }

    /// <summary>
    /// 批次状态收口：批次内所有条目均到达终态时，把批次标记为 completed / completed_with_errors。
    /// 同时被管理端点（删除条目）与发布流程复用。
    /// </summary>
    public static async Task UpdateBatchStatusAsync(ISqlSugarClient db, Guid batchId, CancellationToken cancellationToken)
    {
        var statuses = await db.Queryable<LibraryImportItem>()
            .Where(item => item.BatchId == batchId)
            .Select(item => item.Status)
            .ToListAsync(cancellationToken);
        if (statuses.Count == 0 || statuses.Any(status => status is not (
            LibraryImportItemStatuses.PendingReview or
            LibraryImportItemStatuses.ReadyToPublish or
            LibraryImportItemStatuses.PendingPublication or
            LibraryImportItemStatuses.Published or
            LibraryImportItemStatuses.Rejected or
            LibraryImportItemStatuses.ProcessingFailed)))
            return;

        var hasErrors = statuses.Any(status => status is LibraryImportItemStatuses.ProcessingFailed or LibraryImportItemStatuses.Rejected);
        var batchStatus = hasErrors ? LibraryImportStatuses.CompletedWithErrors : LibraryImportStatuses.Completed;
        await db.Updateable<LibraryImportBatch>()
            .SetColumns(batch => batch.Status == batchStatus)
            .SetColumns(batch => batch.CompletedAtUtc == DateTime.UtcNow)
            .Where(batch => batch.Id == batchId)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>暂存审计条目（随发布事务一并提交）。系统 actor 用 <see cref="Guid.Empty"/> 标识。</summary>
    private static void AddAudit(List<AdminAuditEntry> pendingAudits, ImportPublishContext context, string action, string resourceType, Guid resourceId, object? details = null)
    {
        pendingAudits.Add(new AdminAuditEntry
        {
            ActorUserId = context.ActorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId.ToString(),
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            TraceId = context.TraceId
        });
    }

    private static List<string> DeserializeList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string GetImageContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException) { }
    }
}
