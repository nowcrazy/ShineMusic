// 职责：导入条目「发布入库」核心逻辑的独立服务。
// 原本内嵌在 LibraryImportController.PublishItem 端点中，因「外部音源抓歌评分直通」
// 需要在后台流水线里走同一条发布路径，故抽取为服务供两处复用：
// - 人工发布：管理端点调用，actor 为实际操作的管理员；
// - 自动发布：导入后台服务在评分达标时调用，actor 为系统（Guid.Empty），审计动作记为 track.auto_published。
// 服务不感知 HttpContext：错误以结果对象返回，由调用方映射为 HTTP 响应或后台日志。

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Domain;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service.SourceDownload;
using SqlSugar;
using static ShineMusic.Service.ImportFormatting;

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
    ILogger<ImportPublishService> logger,
    FfmpegMediaService mediaService,
    Microsoft.Extensions.Configuration.IConfiguration configuration)
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
        // 闸门与必填字段判定统一交给领域层，人工发布与后台自动发布共用同一份规则。
        var expected = await ResolveExpectedIdentityAsync(item, cancellationToken);
        var candidate = new TrackIdentity(item.Title ?? "", PublishGate.DeserializeList(item.ArtistsJson), item.DurationMs, item.AlbumTitle);
        var decision = expected is null ? null : TrackIdentityMatcher.Evaluate(expected, candidate);
        if (context.IsAutomatic && item.SourceProvider is not null)
        {
            var reason = AutoPublishPolicy.SourceRejectionReason(expected is not null,
                decision?.Verdict == IdentityVerdict.Matched, item.TranscodeStatus == "succeeded",
                item.Codec, item.BitrateKbps, PublishGate.MissingRequiredFields(item).Count == 0, item.Container);
            if (MetadataMerge.HasBlockingConflicts(item)) reason = "identity_metadata_conflict";
            if (reason is not null)
            {
                item.WarningsJson = AppendWarning(item.WarningsJson, reason);
                item.ErrorMessage = reason;
                await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
                return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, reason);
            }
        }
        if (!PublishGate.IsEligible(item))
            return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "The item must be transcoded successfully and reviewed before publishing.");
        if (context.IsAutomatic && MetadataMerge.HasBlockingConflicts(item))
            return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "Identity metadata conflicts require manual review.");

        // 发布前完整性校验：歌名、艺人、时长缺一不可；歌词是可选内容
        // （用户上传往往没有歌词，缺失时播放器显示“暂无歌词”，不影响播放与上架）。
        var missing = PublishGate.MissingRequiredFields(item);
        if (missing.Count > 0)
            return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, $"Cannot publish: missing required fields — {string.Join(", ", missing)}.");

        // 闸门已在领域层校验过非空，这里再取一次局部变量让编译器的空值流分析保持可证。
        var transcodedStorageKey = item.TranscodedStorageKey;
        if (string.IsNullOrWhiteSpace(transcodedStorageKey) ||
            !quarantinePaths.TryResolve(transcodedStorageKey, out var transcodedPath) || !System.IO.File.Exists(transcodedPath))
            return ImportPublishResult.Failure(ImportPublishErrorKind.NotFound, "The staged files are no longer available.");

        // 原始下载文件可能被后台任务的重复投递/去重清理误删（同一个 taskId 复用 sourcedl/<id>.* 文件名），
        // 但转码产物仍是完整可播放的音频。此时用转码产物兜底作为 original 资产，
        // 避免条目永久卡在 ready_to_publish、发布恒返回 404（2026-11-10 下游链路实测修复）。
        // 同时记录原文件是否真的存在：兜底场景下 original 资产里装的是 m4a，不能按原始容器标为播放主资产。
        var originalFileAvailable = quarantinePaths.TryResolve(item.SourceStorageKey, out var sourcePath)
            && System.IO.File.Exists(sourcePath);
        if (!originalFileAvailable)
            sourcePath = transcodedPath;
        if (context.IsAutomatic && item.SourceProvider is not null)
        {
            ProbedMedia measured;
            try
            {
                await mediaService.ValidateCompleteAsync(sourcePath, cancellationToken);
                measured = await mediaService.ProbeAsync(sourcePath, cancellationToken);
            }
            catch (Exception ex) when (ex is MediaProcessingException or IOException)
            {
                item.ErrorMessage = "audio_validation_failed";
                item.WarningsJson = AppendWarning(item.WarningsJson, item.ErrorMessage);
                await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
                return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, item.ErrorMessage);
            }
            var qualityReason = AutoPublishPolicy.SourceRejectionReason(true, true, measured.DurationMs is > 0,
                measured.Codec, measured.BitrateKbps, true, measured.Container);
            if (expected?.DurationMs is > 0 && measured.DurationMs < expected.DurationMs * 0.9)
                qualityReason = "preview_fragment";
            if (qualityReason is not null || !originalFileAvailable)
            {
                item.ErrorMessage = qualityReason ?? "original_unavailable";
                item.WarningsJson = AppendWarning(item.WarningsJson, item.ErrorMessage);
                await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
                return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, item.ErrorMessage);
            }
            item.Codec = measured.Codec;
            item.Container = measured.Container;
            item.BitrateKbps = measured.BitrateKbps;
            item.DurationMs = measured.DurationMs;
        }
        Track? reuseTrack = null;
        if (originalFileAvailable && expected is not null && decision?.Verdict == IdentityVerdict.Matched
            && !string.IsNullOrWhiteSpace(item.Isrc))
        {
            var existing = await db.Queryable<Track>().FirstAsync(t => t.PublishedAt != null && t.Isrc == item.Isrc, cancellationToken);
            if (existing is not null && existing.DurationMs is > 0 && item.DurationMs is > 0
                && Math.Abs(existing.DurationMs.Value - item.DurationMs.Value) <= 2000
                && NormalizeMetadata(existing.Title) == NormalizeMetadata(item.Title))
            {
                var existingArtists = await db.Queryable<TrackArtist, Artist>((link, artist) => link.ArtistId == artist.Id)
                    .Where((link, artist) => link.TrackId == existing.Id)
                    .Select((link, artist) => artist.Name).ToListAsync(cancellationToken);
                if (TrackIdentityMatcher.Evaluate(new TrackIdentity(existing.Title, existingArtists, existing.DurationMs), candidate)
                    .Verdict != IdentityVerdict.Matched)
                    return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "existing_identity_not_matched");
                reuseTrack = existing;
            }
        }

        // 审计先暂存，随发布事务一并提交；发布失败返回时丢弃（与原「仅 SaveChanges 才落库」语义一致）。
        var pendingAudits = new List<AdminAuditEntry>();

        if (reuseTrack is null && await db.Queryable<MediaAsset>().AnyAsync(asset => asset.ContentHash == item.ContentHash, cancellationToken))
            return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict, "This audio has already been published.");

        // 元数据级去重：标题 + 艺人 + 专辑均相同的已发布曲目视为同一首歌（非翻唱），
        // 避免同一首歌的不同音频文件（不同码率/压制）重复入库。
        var itemTitle = NormalizeMetadata(item.Title);
        var itemArtists = PublishGate.DeserializeList(item.ArtistsJson).Select(NormalizeMetadata).Where(v => v.Length > 0).ToList();
        var itemAlbum = NormalizeMetadata(item.AlbumTitle);
        if (reuseTrack is null && itemTitle.Length > 0 && itemArtists.Count > 0)
        {
            var titleCandidates = await db.Queryable<Track>()
                .Where(track => track.PublishedAt != null && track.Title.ToLower() == itemTitle)
                .Select(track => track.Id)
                .ToListAsync(cancellationToken);
            if (titleCandidates.Count > 0)
            {
                var candidateArtists = await db.Queryable<TrackArtist, Artist>((link, artist) => link.ArtistId == artist.Id)
                    .Where((link, artist) => titleCandidates.Contains(link.TrackId))
                    .Select((link, artist) => new { link.TrackId, ArtistName = artist.Name })
                    .ToListAsync(cancellationToken);
                var candidateAlbums = await db.Queryable<Track, Album>((track, album) => track.AlbumId == album.Id)
                    .Where((track, album) => titleCandidates.Contains(track.Id))
                    .Select((track, album) => new { track.Id, AlbumTitle = album.Title })
                    .ToListAsync(cancellationToken);
                var albumByTrack = candidateAlbums.ToDictionary(c => c.Id, c => NormalizeMetadata(c.AlbumTitle));

                foreach (var candidateId in titleCandidates)
                {
                    var candidateArtistsNorm = candidateArtists
                        .Where(c => c.TrackId == candidateId)
                        .Select(c => NormalizeMetadata(c.ArtistName))
                        .Where(v => v.Length > 0)
                        .ToList();
                    // 艺人有交集且专辑相同（任一侧无专辑信息时退化为标题+艺人匹配）。
                    var artistOverlap = candidateArtistsNorm.Any(a => itemArtists.Contains(a));
                    if (!artistOverlap)
                        continue;
                    var candidateAlbum = albumByTrack.GetValueOrDefault(candidateId) ?? string.Empty;
                    var albumMatches = itemAlbum.Length == 0 || candidateAlbum.Length == 0 || itemAlbum == candidateAlbum;
                    if (albumMatches)
                        return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict,
                            $"曲库中已存在相同曲目（标题、艺人、专辑一致），无需重复发布。如为翻唱版本请修改艺人或专辑信息。");
                }
            }
        }

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
                    item.Title, PublishGate.DeserializeList(item.ArtistsJson), request, fulfillmentOptions.Value.TitleArtistMatch);

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
        if (reuseTrack is not null)
        {
            var hours = double.TryParse(configuration["MediaStorage:ReplacementRetentionHours"],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var configured)
                && double.IsFinite(configured) ? Math.Clamp(configured, 0, 8760) : 24;
            var result = await new AssetReplacementService(db, libraryPaths, mediaService, TimeSpan.FromHours(hours))
                .ReplaceAsync(reuseTrack, item, sourcePath, cancellationToken,
                    token => CompletePublicationAsync(item, reuseTrack, context, pendingAudits, token));
            if (result.Succeeded) await FinishPublicationAsync(item);
            return result;
        }

        var track = new Track
        {
            Title = item.Title!,
            TrackNumber = item.TrackNumber,
            DiscNumber = item.DiscNumber,
            ReleaseYear = item.ReleaseYear,
            ReleaseDate = item.ReleaseDate,
            Isrc = item.Isrc,
            AlbumArtistsJson = item.AlbumArtistsJson,
            OriginalGenresJson = item.OriginalGenresJson,
            MetadataAuditJson = item.MetadataAuditJson,
            DurationMs = item.DurationMs,
            AddedAtUtc = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow,
            // OwnerUserId：曲目的"归属用户"。
            // 自动发布直通时 actor 是系统（Guid.Empty），需要从批次反查实际导入人。
            OwnerUserId = await db.Queryable<LibraryImportBatch>()
                .Where(batch => batch.Id == item.BatchId)
                .Select(batch => batch.CreatedByUserId)
                .FirstAsync(cancellationToken)
        };
        var artistNames = PublishGate.DeserializeList(item.ArtistsJson);
        var genreNames = PublishGate.DeserializeList(item.GenresJson);
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
            System.IO.File.Copy(sourcePath, finalOriginalPath, overwrite: false);
            copiedPaths.Add(finalOriginalPath);
            var keepCompatibility = originalFileAvailable && PlaybackMediaPolicy.NeedsCompatibilityCopy(item);
            if (keepCompatibility)
            {
                System.IO.File.Copy(transcodedPath, finalPlaybackPath, overwrite: false);
                copiedPaths.Add(finalPlaybackPath);
            }
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

            // 音质优先（2026-11-12）：原实现固定把 192kbps 转码副本设为 primary，
            // 导致 320k MP3 / 无损 FLAC 下载下来后播放端仍按 192kbps 输出。
            // 现在由 PlaybackMediaPolicy 判定：原始文件存在、容器可流、且音质不低于转码副本时，
            // 让原始文件当播放主资产；转码副本保留为兼容副本。
            var preferOriginal = originalFileAvailable &&
                (PlaybackMediaPolicy.CanReuseOriginal(item) || PlaybackMediaPolicy.ShouldPreferOriginal(item, true));
            var originalAsset = new MediaAsset
            {
                Id = assetId,
                TrackId = track.Id,
                StorageKey = originalKey,
                Container = originalFileAvailable ? item.Container ?? "unknown" : "m4a",
                Codec = originalFileAvailable ? item.Codec ?? "unknown" : "aac",
                ContentHash = item.ContentHash,
                BitrateKbps = item.BitrateKbps,
                IsPublished = true,
                IsPrimary = preferOriginal || !keepCompatibility
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
                IsPrimary = !preferOriginal
            };

            // —— 交付契约（音源质量稳定性策略 §8 第 1 步）——
            // 发布前把「用户将实际拿到什么」写成显式承诺，并对主资产做离线验证。
            // 失败直接阻断发布，不让低码率 / 错容器 / 身份不符的条目静默到达用户；
            // 不自动回滚到 192k 兼容副本（§5.4），也不改状态机（条目保持原状态可重试）。
            // 真实 HTTP 回读（L3a）留给 K5 启动引导接缝方案，见 docs 文档 §12.4。
            var primaryStorageKey = preferOriginal || !keepCompatibility ? originalKey : playbackKey;
            var primarySourcePath = preferOriginal ? sourcePath : transcodedPath;
            var primaryContainer = preferOriginal ? item.Container : "m4a";
            var candidateArtists = PublishGate.DeserializeList(item.ArtistsJson);
            var candidateIdentity = new TrackIdentity(item.Title ?? string.Empty, candidateArtists, item.DurationMs);
            // V2.0 Phase 2：身份比对从「自评（Expected==Resolved）」改为真实候选 vs 期望。
            // 期望身份来源：ExternalDownloadTask 的快照（歌单导入时从 pending 行复制的原曲信息）。
            // 无关联任务时回退为自评（Expected==Candidate），身份维度给 Matched。
            var expectedIdentity = expected ?? new TrackIdentity("", [], null);
            var identityDecision = TrackIdentityMatcher.Evaluate(expectedIdentity, candidateIdentity);
            var contract = new DeliveryContract(
                item.Id,
                track.Id,
                expectedIdentity,
                candidateIdentity,
                identityDecision.Verdict,
                identityDecision.ReasonCode,
                DeliveryContractVerifier.AutomaticPublishMinimumBitrateKbps,
                primaryStorageKey,
                PlaybackMediaPolicy.TryGetContentType(primaryContainer),
                preferOriginal ? item.BitrateKbps : item.TranscodeBitrateKbps,
                System.IO.File.Exists(primarySourcePath) ? new FileInfo(primarySourcePath).Length : 0,
                item.ContentHash,
                originalFileAvailable);
            var verification = DeliveryContractVerifier.Verify(contract, primarySourcePath, item.TranscodeBitrateKbps);
            if (!verification.IsAccepted)
            {
                logger.LogWarning("Delivery contract rejected for import item {ItemId}: {Failures}",
                    item.Id, string.Join(" | ", verification.Failures));
                foreach (var path in copiedPaths) TryDelete(path);
                // 失败留痕：消息与日志逐条列出差异；条目不改状态、不落库，管理员修正后可重发。
                return ImportPublishResult.Failure(ImportPublishErrorKind.Conflict,
                    $"交付契约未通过：{string.Join("；", verification.Failures)}");
            }

            // 验证结果零 DDL 落库：WarningsJson 记机器可读代码，AutoScoreBreakdownJson 记明细，
            // 供审核页展示与后续「未验证占比 / 契约失败数」指标聚合（§2 / §7.3）。
            item.WarningsJson = AppendWarning(item.WarningsJson, verification.ReasonCode);
            foreach (var warning in verification.Warnings)
                item.WarningsJson = AppendWarning(item.WarningsJson, warning);
            item.AutoScoreBreakdownJson = WithVerification(item.AutoScoreBreakdownJson, new
            {
                reason = verification.ReasonCode,
                accepted = verification.IsAccepted,
                effectiveBitrateFloorKbps = verification.EffectiveBitrateFloorKbps,
                primaryStorageKey = contract.PrimaryStorageKey,
                expectedContentType = contract.ExpectedContentType,
                expectedSizeBytes = contract.ExpectedSizeBytes,
                probedBitrateKbps = contract.ProbedBitrateKbps,
                originalFileAvailable = contract.OriginalFileAvailable,
                warnings = verification.Warnings,
                failures = verification.Failures
            });

            // SqlSugar 不做导航属性外键修正：曲目封面外键需显式回填。
            track.ArtworkId = track.Artwork?.Id;

            item.Status = LibraryImportItemStatuses.Published;
            // 回填曲目关联：管理员发布路径也记录 PublishedTrackId，供音源任务列表反查歌词有无/预览回退等场景使用。
            item.PublishedTrackId = track.Id;
            item.UpdatedAtUtc = DateTime.UtcNow;
            // Shared transactional fulfillment applies equally to new tracks and reuse/upgrades.

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

                // 关联表写入：元数据里艺人/流派名可能重复，先按 Id 去重避免复合主键冲突；
                // 用 Storageable+WhereColumns 走"不存在则插入"，替代原先 AnyAsync 先查再插的竞态窗口。
                var distinctArtists = resolvedArtists.DistinctBy(a => a.Id).ToList();
                var distinctGenres = resolvedGenres.DistinctBy(g => g.Id).ToList();

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
                await db.Insertable(originalAsset).ExecuteCommandAsync(cancellationToken);
                if (keepCompatibility)
                    await db.Insertable(playbackAsset).ExecuteCommandAsync(cancellationToken);
                if (lyric is not null)
                    await db.Insertable(lyric).ExecuteCommandAsync(cancellationToken);
                await db.Updateable(item).ExecuteCommandAsync(cancellationToken);

                await CompletePublicationAsync(item, track, context, pendingAudits, cancellationToken);
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
            // The transaction is durable: later housekeeping failure must never remove live assets.
            copiedPaths.Clear();
            await FinishPublicationAsync(item);
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

    private async Task CompletePublicationAsync(LibraryImportItem item, Track track, ImportPublishContext context,
        List<AdminAuditEntry> audits, CancellationToken ct)
    {
        if (item.SongRequestId is { } requestId)
        {
            var request = await db.Queryable<SongRequestRecord>().FirstAsync(r => r.Id == requestId, ct)
                ?? throw new InvalidOperationException("linked_request_missing");
            var previous = request.Status;
            if (previous != SongRequestStatuses.Fulfilled || request.MatchedTrackId != track.Id)
            {
                request.Status = SongRequestStatuses.Fulfilled;
                request.MatchedTrackId = track.Id;
                request.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(request).ExecuteCommandAsync(ct);
                SongRequestEvents.Add(db, request, SongRequestEventKinds.Fulfilled, previous, SongRequestStatuses.Fulfilled,
                    context.ActorUserId == Guid.Empty ? null : context.ActorUserId,
                    context.IsAutomatic ? SongRequestActorKinds.System : SongRequestActorKinds.Admin, $"通过发布《{track.Title}》满足");
                NotificationWriter.Push(db, request.CreatedByUserId, NotificationKinds.SongRequestFulfilled,
                    $"你想要的《{request.Title}》已入库", "现在可以在曲库里找到并播放了。",
                    NotificationWriter.RequestsPageUrl, "song_request", request.Id.ToString());
            }
        }
        AddAudit(audits, context, context.IsAutomatic ? "track.auto_published" : "track.published", "track", track.Id,
            new { importItemId = item.Id });
        foreach (var audit in audits) await db.Insertable(audit).ExecuteCommandAsync(ct);
        await db.Updateable<LibraryImportItem>().SetColumns(i => i.ErrorCode == "publication_housekeeping_pending")
            .Where(i => i.Id == item.Id).ExecuteCommandAsync(ct);
    }

    // Committed publication stays successful; pending housekeeping is persisted in the same transaction.
    public async Task FinishPublicationAsync(LibraryImportItem item)
    {
        try
        {
            await UpdateBatchStatusAsync(db, item.BatchId, CancellationToken.None);
            foreach (var key in new[] { item.SourceStorageKey, item.TranscodedStorageKey, item.ArtworkStorageKey }.Distinct())
                if (key is not null && quarantinePaths.TryResolve(key, out var path)) File.Delete(path);
            await db.Updateable<LibraryImportItem>().SetColumns(i => i.ErrorCode == (string?)null)
                .Where(i => i.Id == item.Id && i.Status == LibraryImportItemStatuses.Published).ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publication housekeeping will retry for {ItemId}", item.Id);
        }
    }

    public async Task RepairPublicationHousekeepingAsync(CancellationToken ct)
    {
        var items = await db.Queryable<LibraryImportItem>()
            .Where(i => i.Status == LibraryImportItemStatuses.Published && i.ErrorCode == "publication_housekeeping_pending")
            .OrderBy(i => i.UpdatedAtUtc).Take(100).ToListAsync(ct);
        foreach (var item in items) await FinishPublicationAsync(item);
    }

    /// <summary>批次状态收口，供发布和管理端复用。</summary>
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
            LibraryImportItemStatuses.IdentityRejected or
            LibraryImportItemStatuses.ProcessingFailed)))
            return;

        var hasErrors = statuses.Any(status => status is
            LibraryImportItemStatuses.ProcessingFailed or
            LibraryImportItemStatuses.Rejected or
            LibraryImportItemStatuses.IdentityRejected);
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

    /// <summary>元数据归一化：压缩空白 + trim + 小写，用于标题/艺人/专辑的宽松比较。</summary>
    private static string NormalizeMetadata(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>向 WarningsJson 追加一个机器可读代码（去重；历史非数组内容退化为重新累积）。</summary>
    internal static string AppendWarning(string? warningsJson, string code)
    {
        List<string> codes;
        try
        {
            codes = string.IsNullOrWhiteSpace(warningsJson)
                ? []
                : JsonSerializer.Deserialize<List<string>>(warningsJson) ?? [];
        }
        catch (JsonException) { codes = []; }
        if (!codes.Contains(code, StringComparer.Ordinal))
            codes.Add(code);
        return JsonSerializer.Serialize(codes);
    }

    /// <summary>把 verification 节点并入评分分项 JSON（零 DDL，复用既有列）。</summary>
    internal static string WithVerification(string? breakdownJson, object verification)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(breakdownJson)
                ? new JsonObject()
                : JsonNode.Parse(breakdownJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException) { root = new JsonObject(); }
        root["verification"] = JsonSerializer.SerializeToNode(verification);
        return root.ToJsonString();
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException) { }
    }

    /// <summary>
    /// 反查期望身份（V2.0 Phase 2 评分重构）。
    /// 期望身份来源：ExternalDownloadTask 的 Title/ArtistsJson/DurationMs（歌单导入时从 pending 行复制的原曲信息）。
    /// 无关联任务或快照缺失时回退为导入条目自身的身份（自评 Matched）。
    /// </summary>
    private async Task<TrackIdentity?> ResolveExpectedIdentityAsync(
        LibraryImportItem item, CancellationToken cancellationToken)
    {
        var taskSnapshot = await db.Queryable<ExternalDownloadTask>()
            .Where(task => task.ImportItemId == item.Id)
            .Select(task => new { task.Title, task.ArtistsJson, task.DurationMs,
                task.ExpectedTitle, task.ExpectedArtistsJson, task.ExpectedDurationMs, task.ExpectedAlbumTitle })
            .FirstAsync(cancellationToken);
        if (taskSnapshot is not null && !string.IsNullOrWhiteSpace(taskSnapshot.ExpectedTitle ?? taskSnapshot.Title))
        {
            var expectedArtists = PublishGate.DeserializeList(taskSnapshot.ExpectedArtistsJson ?? taskSnapshot.ArtistsJson);
            if (expectedArtists.Count > 0)
                return new TrackIdentity(taskSnapshot.ExpectedTitle ?? taskSnapshot.Title!, expectedArtists,
                    taskSnapshot.ExpectedTitle is not null ? taskSnapshot.ExpectedDurationMs : taskSnapshot.DurationMs,
                    taskSnapshot.ExpectedAlbumTitle);
        }
        return null;
    }
}
