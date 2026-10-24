using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>Handles administrator uploads, review, preview, publishing, and cleanup of imported media.</summary>
[ApiController]
[Route("api/v1/admin/library")]
[Authorize(Roles = "admin")]
[FeatureGate(nameof(FeatureOptions.AdminLibraryImport))]
[Tags("Admin Library Import")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class LibraryImportController : ControllerBase
{
    [HttpPost("imports", Name = "UploadLibraryImportBatch")]
    public async Task<IResult> UploadBatch(
        MusicDbContext db,
        ImportStagingService staging,
        LibraryImportQueue queue,
        IOptions<UploadOptions> uploadOptions,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        ILogger<LibraryImportController> logger,
        CancellationToken cancellationToken)
    {
        var options = uploadOptions.Value;
        if (!MediaTypeHeaderValue.TryParse(HttpContext.Request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return ApiResults.ValidationError(HttpContext, "contentType", "A multipart/form-data request is required.");

        if (HttpContext.Request.ContentLength is > 0 && HttpContext.Request.ContentLength > options.MaxBatchSizeBytes)
            return ApiResults.Error(HttpContext, StatusCodes.Status413PayloadTooLarge, ErrorCodes.UploadTooLarge, "The upload batch exceeded the configured limit.");

        Guid? songRequestId = null;
        ImportStagingService.StagedResult staged;
        try
        {
            staged = await staging.StageAsync(HttpContext.Request, cancellationToken);
        }
        catch (UploadLimitException)
        {
            return ApiResults.Error(HttpContext, StatusCodes.Status413PayloadTooLarge, ErrorCodes.UploadTooLarge, "The file or batch exceeded the configured upload limit.");
        }
        catch (EmptyUploadException)
        {
            return ApiResults.ValidationError(HttpContext, "files", "Empty files are not accepted.");
        }
        catch (UnsupportedFormatException)
        {
            return ApiResults.Error(HttpContext, StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaFormat, "The file extension is not allowed.");
        }
        catch (InvalidDataException)
        {
            return ApiResults.Error(HttpContext, StatusCodes.Status413PayloadTooLarge, ErrorCodes.UploadTooLarge, "The multipart request exceeded its configured limit.");
        }
        catch (InvalidOperationException)
        {
            return ApiResults.Error(HttpContext, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, "Unable to stage the upload.");
        }

        // 未与任何音频配对的歌词文件会被忽略；显式记告警，避免用户误以为歌词已挂上。
        if (staged.OrphanLyrics.Count > 0)
            logger.LogWarning("Ignored {Count} lyrics file(s) without a matching audio track: {Orphans}.",
                staged.OrphanLyrics.Count, string.Join(", ", staged.OrphanLyrics));

        var batchSaved = false;
        try
        {
            // 读取可选的 songRequestId 表单字段（表单已由暂存服务解析并缓冲）。
            var requestIdValue = staged.Form["songRequestId"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(requestIdValue))
            {
                if (requestIdValue.Length > 64)
                    return ApiResults.ValidationError(HttpContext, "songRequestId", "songRequestId must be at most 64 characters.");
                if (!Guid.TryParse(requestIdValue.Trim(), out var parsedRequestId))
                    return ApiResults.ValidationError(HttpContext, "songRequestId", "songRequestId must be a UUID.");
                songRequestId = parsedRequestId;
            }

            var pendingFiles = staged.Audio
                .Select(file => new PendingUpload(file.ItemId, file.DisplayName, file.StorageKey, file.ContentHash, file.SizeBytes))
                .ToList();
            if (pendingFiles.Count == 0)
                return ApiResults.ValidationError(HttpContext, "files", "At least one audio file is required.");
            if (songRequestId.HasValue && !await db.SongRequests.AnyAsync(request => request.Id == songRequestId.Value, cancellationToken))
                return ApiResults.NotFound(HttpContext);

            var hashes = pendingFiles.Select(file => file.ContentHash).ToArray();
            var duplicateInLibrary = await db.MediaAssets.AnyAsync(asset => hashes.Contains(asset.ContentHash), cancellationToken);
            var duplicateInQueue = await db.LibraryImportItems.AnyAsync(item => hashes.Contains(item.ContentHash) &&
                item.Status != LibraryImportItemStatuses.Rejected && item.Status != LibraryImportItemStatuses.ProcessingFailed,
                cancellationToken);
            if (duplicateInLibrary || duplicateInQueue || hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Length)
                return ApiResults.Error(HttpContext, StatusCodes.Status409Conflict, ErrorCodes.ResourceConflict, "The upload batch contains audio that is already published or being processed.");

            var createdByUserId = Guid.Parse(HttpContext.User.FindFirstValue("sub")!);
            var batch = new LibraryImportBatch
            {
                Id = staged.BatchId,
                CreatedByUserId = createdByUserId,
                Status = LibraryImportStatuses.Queued,
                Items = pendingFiles.Select(file => new LibraryImportItem
                {
                    Id = file.Id,
                    DisplayName = file.DisplayName,
                    SourceStorageKey = file.StorageKey,
                    ContentHash = file.ContentHash,
                    SizeBytes = file.SizeBytes,
                    SongRequestId = songRequestId,
                    Status = LibraryImportItemStatuses.Uploaded
                }).ToList()
            };
            db.LibraryImportBatches.Add(batch);
            AdminAudit.Add(HttpContext, db, "library_import.created", "import_batch", batch.Id, new { itemCount = batch.Items.Count });
            await db.SaveChangesAsync(cancellationToken);
            batchSaved = true;
            foreach (var file in pendingFiles)
                await queue.QueueAsync(file.Id, cancellationToken);
            var requests = await LoadRequestsAsync(db, [songRequestId], cancellationToken);
            return Results.Accepted($"/api/v1/admin/library/imports/{batch.Id}", ToDto(batch, requests, fulfillmentOptions.Value));
        }
        finally
        {
            if (!batchSaved)
                staging.DeleteBatchDirectory(staged.BatchDirectory);
        }
    }

    [HttpGet("imports", Name = "ListLibraryImportBatches")]
    public async Task<IResult> ListBatches(
        MusicDbContext db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        // 仅拉取未被软删除的条目；若批次内所有条目都已被删除，则该批次也不展示。
        var query = db.LibraryImportBatches.AsNoTracking()
            .Include(batch => batch.Items.Where(item => !item.IsDeleted))
            .Where(batch => batch.Items.Any(item => !item.IsDeleted))
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(batch => batch.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var batches = await query
            .OrderByDescending(batch => batch.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var requests = await LoadRequestsAsync(db, batches.SelectMany(batch => batch.Items).Select(item => item.SongRequestId), cancellationToken);
        return Results.Ok(new
        {
            items = batches.Select(batch => ToDto(batch, requests, fulfillmentOptions.Value)).ToArray(),
            page,
            pageSize,
            total
        });
    }

    [HttpGet("imports/{importId:guid}", Name = "GetLibraryImportBatch")]
    public async Task<IResult> GetBatch(
        Guid importId,
        MusicDbContext db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var batch = await db.LibraryImportBatches.AsNoTracking()
            .Include(item => item.Items.Where(item => !item.IsDeleted))
            .FirstOrDefaultAsync(item => item.Id == importId, cancellationToken);
        if (batch is null)
            return ApiResults.NotFound(HttpContext);
        var requests = await LoadRequestsAsync(db, batch.Items.Select(item => item.SongRequestId), cancellationToken);
        return Results.Ok(ToDto(batch, requests, fulfillmentOptions.Value));
    }

    [HttpGet("import-items/{itemId:guid}", Name = "GetLibraryImportItem")]
    public async Task<IResult> GetItem(
        Guid itemId,
        MusicDbContext db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.LibraryImportItems.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == itemId && !candidate.IsDeleted, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        var requests = await LoadRequestsAsync(db, [item.SongRequestId], cancellationToken);
        var request = item.SongRequestId is Guid requestId && requests.TryGetValue(requestId, out var matched) ? matched : null;
        return Results.Ok(ToDto(item, request, fulfillmentOptions.Value));
    }

    [HttpGet("import-items/{itemId:guid}/preview", Name = "PreviewLibraryImportItem")]
    public async Task<IResult> PreviewItem(
        Guid itemId,
        MusicDbContext db,
        QuarantineStoragePaths storagePaths,
        LibraryStoragePaths libraryPaths,
        CancellationToken cancellationToken)
    {
        var item = await db.LibraryImportItems.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        var storageKey = item.TranscodedStorageKey ?? item.SourceStorageKey;
        if (storagePaths.TryResolve(storageKey, out var path) && System.IO.File.Exists(path))
            return Results.File(path, GetAudioContentType(item.TranscodedStorageKey is null ? item.Container : "m4a"), enableRangeProcessing: true);

        // 用户上传已私有入库：隔离区文件已随入库清理，回退到曲库中的播放资产。
        if (item.PublishedTrackId is null)
            return ApiResults.NotFound(HttpContext);
        var asset = await db.MediaAssets.AsNoTracking()
            .Where(candidate => candidate.TrackId == item.PublishedTrackId.Value && candidate.IsPublished)
            .OrderByDescending(candidate => candidate.IsPrimary)
            .Select(candidate => new { candidate.StorageKey, candidate.Container })
            .FirstOrDefaultAsync(cancellationToken);
        if (asset is null || !libraryPaths.TryResolve(asset.StorageKey, out path) || !System.IO.File.Exists(path))
            return ApiResults.NotFound(HttpContext);
        return Results.File(path, GetAudioContentType(asset.Container), enableRangeProcessing: true);
    }

    [HttpPatch("import-items/{itemId:guid}", Name = "UpdateLibraryImportItem")]
    public async Task<IResult> UpdateItem(
        Guid itemId,
        UpdateImportItemRequest update,
        MusicDbContext db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.LibraryImportItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        if (item.Status is LibraryImportItemStatuses.Published or LibraryImportItemStatuses.Rejected)
            return ApiResults.Conflict(HttpContext, "This import item can no longer be edited.");
        if (update.Title is not null && (string.IsNullOrWhiteSpace(update.Title) || update.Title.Trim().Length > 300))
            return ApiResults.ValidationError(HttpContext, "title", "Title must be between 1 and 300 characters.");
        if (update.Artists is { Count: > 10 } || update.Artists?.Any(artist => string.IsNullOrWhiteSpace(artist) || artist.Trim().Length > 200) == true)
            return ApiResults.ValidationError(HttpContext, "artists", "At most 10 non-empty artist names of 200 characters are allowed.");
        if (update.AlbumTitle?.Length > 300 || update.Genres is { Count: > 30 } || update.Genres?.Any(genre => genre.Trim().Length > 100) == true)
            return ApiResults.ValidationError(HttpContext, "metadata", "Album or genre metadata exceeds its limit.");

        item.Title = update.Title?.Trim() ?? item.Title;
        if (update.Artists is not null)
            item.ArtistsJson = JsonSerializer.Serialize(update.Artists.Select(artist => artist.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        if (update.AlbumTitle is not null)
            item.AlbumTitle = string.IsNullOrWhiteSpace(update.AlbumTitle) ? null : update.AlbumTitle.Trim();
        // 歌词：显式传空字符串表示清空，不传(null)表示保持原值。
        // 一旦人工改动，来源统一标记为 manual，避免沿用此前的自动匹配标记。
        if (update.Lyrics is not null)
        {
            var lyrics = string.IsNullOrWhiteSpace(update.Lyrics) ? null : update.Lyrics.Trim();
            item.Lyrics = lyrics;
            item.LyricsSource = lyrics is null ? null : LyricsSources.Manual;
            item.LyricsConfidence = lyrics is null ? null : 1.0;
            item.LyricsProviderId = null;
        }
        if (update.TrackNumber.HasValue) item.TrackNumber = update.TrackNumber.Value > 0 ? update.TrackNumber : null;
        if (update.DiscNumber.HasValue) item.DiscNumber = update.DiscNumber.Value > 0 ? update.DiscNumber : null;
        if (update.ReleaseYear.HasValue) item.ReleaseYear = update.ReleaseYear.Value is >= 1800 and <= 2200 ? update.ReleaseYear : null;
        if (update.Genres is not null)
            item.GenresJson = JsonSerializer.Serialize(update.Genres.Select(genre => genre.Trim()).Where(genre => genre.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        if (update.SongRequestId.HasValue)
        {
            if (!await db.SongRequests.AnyAsync(request => request.Id == update.SongRequestId.Value, cancellationToken))
                return ApiResults.NotFound(HttpContext);
            item.SongRequestId = update.SongRequestId;
        }
        item.MetadataReviewed = !string.IsNullOrWhiteSpace(item.Title) && DeserializeList(item.ArtistsJson).Count > 0;
        if (item.Status == LibraryImportItemStatuses.PendingReview && item.MetadataReviewed)
            item.Status = LibraryImportItemStatuses.ReadyToPublish;
        item.UpdatedAtUtc = DateTime.UtcNow;
        AdminAudit.Add(HttpContext, db, "library_import_item.updated", "import_item", item.Id, new { fields = new[] { "metadata" } });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ToDtoAsync(item, db, fulfillmentOptions.Value, cancellationToken));
    }

    /// <summary>
    /// 上传歌词文件（.lrc / .txt）并解析内容写入导入条目。
    /// 自动检测格式：内容含 [mm:ss] 时间轴标记则按 LRC 处理，否则为纯文本。
    /// </summary>
    [HttpPost("import-items/{itemId:guid}/lyrics-file", Name = "UploadImportItemLyricsFile")]
    public async Task<IResult> UploadLyricsFile(
        Guid itemId,
        MusicDbContext db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.LibraryImportItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        if (item.Status is LibraryImportItemStatuses.Published or LibraryImportItemStatuses.Rejected)
            return ApiResults.Conflict(HttpContext, "This import item can no longer be edited.");

        if (!MediaTypeHeaderValue.TryParse(HttpContext.Request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return ApiResults.ValidationError(HttpContext, "contentType", "A multipart/form-data request is required.");

        var form = await HttpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.Count > 0 ? form.Files[0] : null;
        if (file is null || file.Length == 0)
            return ApiResults.ValidationError(HttpContext, "file", "A lyrics file is required.");

        var extension = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
        if (extension is not ("lrc" or "txt"))
            return ApiResults.Error(HttpContext, StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaFormat, "Only .lrc and .txt lyrics files are supported.");

        string content;
        await using (var stream = file.OpenReadStream())
        using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
        {
            content = await reader.ReadToEndAsync(cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(content))
            return ApiResults.ValidationError(HttpContext, "file", "The lyrics file is empty.");

        item.Lyrics = content.Trim();
        // 人工上传的歌词文件，来源标记为 manual，置信度视为最高。
        item.LyricsSource = LyricsSources.Manual;
        item.LyricsConfidence = 1.0;
        item.LyricsProviderId = null;
        item.UpdatedAtUtc = DateTime.UtcNow;
        AdminAudit.Add(HttpContext, db, "library_import_item.lyrics_uploaded", "import_item", item.Id, new { extension, length = content.Length });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ToDtoAsync(item, db, fulfillmentOptions.Value, cancellationToken));
    }

    [HttpPost("import-items/{itemId:guid}/publish", Name = "PublishLibraryImportItem")]
    public async Task<IResult> PublishItem(
        Guid itemId,
        [FromQuery] bool forceFulfill,
        [FromQuery] string? overrideReason,
        MusicDbContext db,
        ImportPublishService publisher,
        CancellationToken cancellationToken)
    {
        var item = await db.LibraryImportItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);

        // 用户上传的私有曲目转公开：曲目已在处理完成时私有入库（仅上传者可听），
        // 此处由管理员审核决定是否进入公共曲库（设置发布时间），并同步审核中修正过的元数据。
        if (item.IsUserUpload)
            return await PublishUserUploadAsync(item, db, cancellationToken);

        // 核心发布逻辑（建 Track、资产落库、点歌联动、事务与清理）在 ImportPublishService 中，
        // 与「外部音源评分直通自动发布」共用同一条路径。
        var actorId = Guid.Parse(HttpContext.User.FindFirstValue("sub")!);
        var result = await publisher.PublishAsync(
            item,
            new ImportPublishContext(actorId, HttpContext.TraceIdentifier, IsAutomatic: false),
            forceFulfill,
            overrideReason,
            cancellationToken);
        if (result.Error is { } error)
            return error.Kind switch
            {
                ImportPublishErrorKind.NotFound => ApiResults.NotFound(HttpContext),
                ImportPublishErrorKind.Validation => ApiResults.ValidationError(HttpContext, error.Field ?? "metadata", error.Message),
                ImportPublishErrorKind.Coded => ApiResults.Error(HttpContext, StatusCodes.Status409Conflict, error.Code ?? ErrorCodes.ResourceConflict, error.Message),
                ImportPublishErrorKind.Internal => ApiResults.Error(HttpContext, StatusCodes.Status500InternalServerError, error.Code ?? ErrorCodes.InternalError, error.Message),
                _ => ApiResults.Conflict(HttpContext, error.Message)
            };
        return Results.Ok(new PublishImportItemResultDto(result.TrackId!.Value, result.Status!));
    }

    [HttpDelete("import-items/{itemId:guid}/song-request", Name = "UnlinkImportItemSongRequest")]
    public async Task<IResult> UnlinkSongRequest(
        Guid itemId,
        MusicDbContext db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.LibraryImportItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        if (item.SongRequestId is null)
            return Results.NoContent();

        // 解除“本次导入满足某条点歌”的关联；不影响已发布曲目，仅避免发布时误把需求标记为已满足。
        item.SongRequestId = null;
        item.UpdatedAtUtc = DateTime.UtcNow;
        AdminAudit.Add(HttpContext, db, "library_import_item.song_request_unlinked", "import_item", item.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ToDtoAsync(item, db, fulfillmentOptions.Value, cancellationToken));
    }

    /// <summary>
    /// 用户上传条目的转公开：私有曲目已存在（处理完成时自动入库、仅上传者可听），
    /// 管理员审核通过后设置发布时间进入公共曲库，并把审核中修正过的元数据同步到曲目。
    /// 拒绝（不调用本端点而调用删除端点）时曲目保持私有，上传者可继续收听。
    /// </summary>
    private async Task<IResult> PublishUserUploadAsync(
        LibraryImportItem item,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (item.Status != LibraryImportItemStatuses.PendingPublication || item.PublishedTrackId is null)
            return ApiResults.Conflict(HttpContext, "Only user uploads awaiting publication review can be promoted.");
        var track = await db.Tracks.FirstOrDefaultAsync(candidate => candidate.Id == item.PublishedTrackId.Value, cancellationToken);
        if (track is null)
            return ApiResults.NotFound(HttpContext);
        if (track.PublishedAt != null)
            return ApiResults.Conflict(HttpContext, "This track has already been published.");

        // 同步审核中修正过的标量元数据（艺人/流派/专辑关联的变更需在审核界面一并修正后另行处理）。
        if (!string.IsNullOrWhiteSpace(item.Title) && item.Title != track.Title)
            track.Title = item.Title.Trim();
        track.TrackNumber = item.TrackNumber;
        track.DiscNumber = item.DiscNumber;
        track.ReleaseYear = item.ReleaseYear;
        track.DurationMs = item.DurationMs ?? track.DurationMs;

        // 歌词同步：审核中修改过歌词则以导入项为准（含来源与置信度）。
        if (!string.IsNullOrWhiteSpace(item.Lyrics))
        {
            var isLrc = System.Text.RegularExpressions.Regex.IsMatch(item.Lyrics, @"\[\d{1,2}:\d{1,2}");
            var existing = await db.Lyrics.FirstOrDefaultAsync(
                lyric => lyric.TrackId == track.Id && lyric.IsDefault, cancellationToken);
            if (existing is null)
            {
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
            else if (existing.Content != item.Lyrics)
            {
                existing.Content = item.Lyrics;
                existing.Format = isLrc ? LyricFormats.Lrc : LyricFormats.Plain;
                existing.Source = item.LyricsSource ?? LyricsSources.Manual;
                existing.Confidence = item.LyricsConfidence ?? 1.0;
                existing.ProviderId = item.LyricsProviderId;
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        track.PublishedAt = DateTimeOffset.UtcNow;
        item.Status = LibraryImportItemStatuses.Published;
        item.UpdatedAtUtc = DateTime.UtcNow;
        AdminAudit.Add(HttpContext, db, "track.published", "track", track.Id, new { importItemId = item.Id, promoted = true });
        await db.SaveChangesAsync(cancellationToken);
        await ImportPublishService.UpdateBatchStatusAsync(db, item.BatchId, cancellationToken);
        return Results.Ok(new PublishImportItemResultDto(track.Id, item.Status));
    }

    /// <summary>
    /// 管理端导入条目列表：按状态筛选（如 pending_publication 为待审核转公开的用户上传），
    /// 附带批次上传者信息，供审核界面展示来源。
    /// </summary>
    [HttpGet("import-items", Name = "ListLibraryImportItems")]
    public async Task<IResult> ListItems(
        MusicDbContext db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.LibraryImportItems.AsNoTracking().Where(item => !item.IsDeleted);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(item => item.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(item => item.Batch)
            .ToListAsync(cancellationToken);
        var requests = await LoadRequestsAsync(db, items.Select(item => item.SongRequestId), cancellationToken);
        return Results.Ok(new
        {
            items = items.Select(item => new LibraryImportItemWithUploaderDto(
                ToDto(item, item.SongRequestId is Guid requestId && requests.TryGetValue(requestId, out var request) ? request : null, fulfillmentOptions.Value),
                item.Batch?.CreatedByUserId)).ToArray(),
            page,
            pageSize,
            total
        });
    }

    [HttpDelete("import-items/{itemId:guid}", Name = "DeleteLibraryImportItem")]
    public async Task<IResult> DeleteItem(Guid itemId, MusicDbContext db, QuarantineStoragePaths storagePaths, CancellationToken cancellationToken)
    {
        var item = await db.LibraryImportItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        if (item.Status is LibraryImportItemStatuses.Published or
            LibraryImportItemStatuses.Validating or
            LibraryImportItemStatuses.ParsingMetadata or
            LibraryImportItemStatuses.Transcoding or
            LibraryImportItemStatuses.Uploaded)
            return ApiResults.Conflict(HttpContext, "Published items cannot be discarded through the import endpoint.");
        var batchId = item.BatchId;
        item.Status = LibraryImportItemStatuses.Rejected;
        item.UpdatedAtUtc = DateTime.UtcNow;
        AdminAudit.Add(HttpContext, db, "library_import_item.rejected", "import_item", item.Id);
        await db.SaveChangesAsync(cancellationToken);
        DeleteStorageKey(storagePaths, item.SourceStorageKey);
        if (item.TranscodedStorageKey is not null) DeleteStorageKey(storagePaths, item.TranscodedStorageKey);
        if (item.ArtworkStorageKey is not null) DeleteStorageKey(storagePaths, item.ArtworkStorageKey);
        await ImportPublishService.UpdateBatchStatusAsync(db, batchId, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// 软删除导入条目：仅对终态条目（已拒绝/已发布/处理失败）生效，
    /// 置 IsDeleted=true 后列表查询自动过滤，记录本身不物理删除以便审计与恢复。
    /// </summary>
    [HttpPost("import-items/{itemId:guid}/dismiss", Name = "DismissLibraryImportItem")]
    public async Task<IResult> DismissItem(Guid itemId, MusicDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.LibraryImportItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        // 仅终态条目可从列表移除；处理中/待审核的条目不允许 dismiss（应先拒绝或发布）。
        if (item.Status is not (
            LibraryImportItemStatuses.Rejected or
            LibraryImportItemStatuses.Published or
            LibraryImportItemStatuses.ProcessingFailed))
            return ApiResults.Conflict(HttpContext, "Only rejected, published, or failed items can be dismissed.");
        item.IsDeleted = true;
        item.UpdatedAtUtc = DateTime.UtcNow;
        AdminAudit.Add(HttpContext, db, "library_import_item.dismissed", "import_item", item.Id, new { item.Status });
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static LibraryImportBatchDto ToDto(
        LibraryImportBatch batch,
        IReadOnlyDictionary<Guid, SongRequestRecord> requests,
        SongRequestFulfillmentOptions options) => new(
        batch.Id,
        batch.Status,
        batch.Items.OrderBy(item => item.CreatedAtUtc)
            .Select(item => ToDto(item, item.SongRequestId is Guid requestId && requests.TryGetValue(requestId, out var request) ? request : null, options))
            .ToArray(),
        AsUtc(batch.CreatedAtUtc));

    private static LibraryImportItemDto ToDto(
        LibraryImportItem item,
        SongRequestRecord? request,
        SongRequestFulfillmentOptions options) => new(
        item.Id,
        item.Status,
        new ImportSourceFileDto(item.DisplayName, item.SizeBytes, item.Container, item.Codec),
        new ImportMetadataDto(
            item.Title,
            DeserializeList(item.ArtistsJson),
            item.AlbumTitle,
            item.TrackNumber,
            item.DiscNumber,
            item.ReleaseYear,
            DeserializeList(item.GenresJson),
            item.DurationMs,
            item.Container,
            item.Codec,
            item.BitrateKbps,
            item.ArtworkStorageKey is not null,
            item.Lyrics,
            item.LyricsSource,
            item.LyricsConfidence),
        new ImportTranscodeDto(item.TranscodeStatus, item.TranscodeFormat, item.TranscodeBitrateKbps, item.ErrorCode),
        DeserializeList(item.WarningsJson).Concat(item.ErrorCode is null ? [] : [item.ErrorCode]).ToArray(),
        AsUtc(item.CreatedAtUtc),
        AsUtc(item.UpdatedAtUtc),
        item.SongRequestId,
        BuildMatch(item, request, options));

    /// <summary>
    /// 读取时计算「待上架曲目」与「目标需求」的匹配结果：元数据被修正后提示立即刷新，
    /// 无需等到发布被拒绝才发现传错了歌。
    /// </summary>
    private static SongRequestMatchDto? BuildMatch(
        LibraryImportItem item,
        SongRequestRecord? request,
        SongRequestFulfillmentOptions options)
    {
        // 未关联需求、或元数据尚未解析出标题时无法比较，返回 null 让前端不展示提示。
        if (request is null || string.IsNullOrWhiteSpace(item.Title))
            return null;
        var check = options.Check("TitleArtistMatch");
        if (!check.IsEnabled)
            return null;

        var verdict = SongRequestMatchValidator.Validate(
            item.Title, DeserializeList(item.ArtistsJson), request, options.TitleArtistMatch);
        // blocking=true 表示即使管理员确认也无法强制关联（全局配置禁止覆盖）。
        var blocking = verdict.Kind == MatchVerdictKind.Mismatch && !check.AllowAdminOverride;
        var message = verdict.Kind switch
        {
            MatchVerdictKind.Match => $"与需求《{verdict.RequestDisplay}》一致，可直接发布。",
            MatchVerdictKind.FuzzyMatch => $"与需求《{verdict.RequestDisplay}》存在细微差异，请核对后再发布。",
            _ when blocking => $"与需求《{verdict.RequestDisplay}》不一致，发布会被拒绝。请修正歌曲名/艺人，或解绑该需求。",
            _ => $"与需求《{verdict.RequestDisplay}》不一致，发布时需填写原因强制关联。"
        };
        return new SongRequestMatchDto(
            verdict.Kind switch
            {
                MatchVerdictKind.Match => "match",
                MatchVerdictKind.FuzzyMatch => "fuzzy",
                _ => "mismatch"
            },
            verdict.TrackDisplay,
            verdict.RequestDisplay,
            blocking,
            message);
    }

    /// <summary>单条导入项的 DTO 构造（含关联需求匹配结果），供单条读写端点复用。</summary>
    private static async Task<LibraryImportItemDto> ToDtoAsync(
        LibraryImportItem item,
        MusicDbContext db,
        SongRequestFulfillmentOptions options,
        CancellationToken cancellationToken)
    {
        var requests = await LoadRequestsAsync(db, [item.SongRequestId], cancellationToken);
        var request = item.SongRequestId is Guid requestId && requests.TryGetValue(requestId, out var matched) ? matched : null;
        return ToDto(item, request, options);
    }

    /// <summary>批量加载导入项关联的需求，避免逐个查询（N+1）。</summary>
    private static async Task<Dictionary<Guid, SongRequestRecord>> LoadRequestsAsync(
        MusicDbContext db,
        IEnumerable<Guid?> requestIds,
        CancellationToken cancellationToken)
    {
        var ids = requestIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        if (ids.Length == 0)
            return [];
        return await db.SongRequests.AsNoTracking()
            .Where(request => ids.Contains(request.Id))
            .ToDictionaryAsync(request => request.Id, cancellationToken);
    }

    private static List<string> DeserializeList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string GetAudioContentType(string? container) => container?.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "wav" or "wave" => "audio/wav",
        "ogg" or "oga" => "audio/ogg",
        "flac" => "audio/flac",
        "m4a" or "aac" or "mp4" => "audio/mp4",
        _ => "application/octet-stream"
    };

    private static void DeleteStorageKey(QuarantineStoragePaths paths, string storageKey)
    {
        if (paths.TryResolve(storageKey, out var path)) TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException) { }
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed record PendingUpload(Guid Id, string DisplayName, string StorageKey, string ContentHash, long SizeBytes);
}