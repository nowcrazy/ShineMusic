using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using ShineMusic.Service.Messaging;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

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
        ISqlSugarClient db,
        ImportStagingService staging,
        IRabbitMessageBus messageBus,
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
            if (songRequestId.HasValue && !await db.Queryable<SongRequestRecord>().AnyAsync(request => request.Id == songRequestId.Value, cancellationToken))
                return ApiResults.NotFound(HttpContext);

            var hashes = pendingFiles.Select(file => file.ContentHash).ToList();
            var duplicateInLibrary = await db.Queryable<MediaAsset>().AnyAsync(asset => hashes.Contains(asset.ContentHash), cancellationToken);
            var duplicateInQueue = await db.Queryable<LibraryImportItem>().AnyAsync(item => hashes.Contains(item.ContentHash) &&
                item.Status != LibraryImportItemStatuses.Rejected && item.Status != LibraryImportItemStatuses.ProcessingFailed,
                cancellationToken);
            if (duplicateInLibrary || duplicateInQueue || hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Count)
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
                    BatchId = staged.BatchId,
                    DisplayName = file.DisplayName,
                    SourceStorageKey = file.StorageKey,
                    ContentHash = file.ContentHash,
                    SizeBytes = file.SizeBytes,
                    SongRequestId = songRequestId,
                    Status = LibraryImportItemStatuses.Uploaded
                }).ToList()
            };
            // 批次 + 条目 + 审计同事务提交。
            await db.Ado.BeginTranAsync();
            try
            {
                await db.Insertable(batch).ExecuteCommandAsync(cancellationToken);
                await db.Insertable(batch.Items.ToList()).ExecuteCommandAsync(cancellationToken);
                AdminAudit.Add(HttpContext, db, "library_import.created", "import_batch", batch.Id, new { itemCount = batch.Items.Count });
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
            batchSaved = true;
            foreach (var file in pendingFiles)
                await messageBus.PublishAsync(QueueNames.Imports, file.Id, cancellationToken);
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
        ISqlSugarClient db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        // 仅拉取未被软删除的条目；若批次内所有条目都已被删除，则该批次也不展示。
        var query = db.Queryable<LibraryImportBatch>()
            .Where(batch => SqlFunc.Subqueryable<LibraryImportItem>()
                .Where(item => item.BatchId == batch.Id && !item.IsDeleted).Any());
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(batch => batch.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var batches = await query
            .OrderByDescending(batch => batch.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        // 批次内条目一次批量查出后内存水合（替代 Include）。
        var batchIds = batches.Select(batch => batch.Id).ToList();
        var batchItems = batchIds.Count == 0
            ? new List<LibraryImportItem>()
            : await db.Queryable<LibraryImportItem>()
                .Where(item => batchIds.Contains(item.BatchId) && !item.IsDeleted)
                .ToListAsync(cancellationToken);
        var itemsByBatch = batchItems.ToLookup(item => item.BatchId);
        foreach (var batch in batches)
            batch.Items = itemsByBatch[batch.Id].ToList();
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
        ISqlSugarClient db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var batch = await db.Queryable<LibraryImportBatch>().FirstAsync(item => item.Id == importId, cancellationToken);
        if (batch is null)
            return ApiResults.NotFound(HttpContext);
        batch.Items = await db.Queryable<LibraryImportItem>()
            .Where(item => item.BatchId == batch.Id && !item.IsDeleted)
            .ToListAsync(cancellationToken);
        var requests = await LoadRequestsAsync(db, batch.Items.Select(item => item.SongRequestId), cancellationToken);
        return Results.Ok(ToDto(batch, requests, fulfillmentOptions.Value));
    }

    [HttpGet("import-items/{itemId:guid}", Name = "GetLibraryImportItem")]
    public async Task<IResult> GetItem(
        Guid itemId,
        ISqlSugarClient db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId && !candidate.IsDeleted, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        var requests = await LoadRequestsAsync(db, [item.SongRequestId], cancellationToken);
        var request = item.SongRequestId is Guid requestId && requests.TryGetValue(requestId, out var matched) ? matched : null;
        return Results.Ok(ToDto(item, request, fulfillmentOptions.Value));
    }

    [HttpGet("import-items/{itemId:guid}/preview", Name = "PreviewLibraryImportItem")]
    public async Task<IResult> PreviewItem(
        Guid itemId,
        ISqlSugarClient db,
        QuarantineStoragePaths storagePaths,
        LibraryStoragePaths libraryPaths,
        CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        var storageKey = item.TranscodedStorageKey ?? item.SourceStorageKey;
        if (storagePaths.TryResolve(storageKey, out var path) && System.IO.File.Exists(path))
            return Results.File(path, GetAudioContentType(item.TranscodedStorageKey is null ? item.Container : "m4a"), enableRangeProcessing: true);

        // 用户上传已私有入库：隔离区文件已随入库清理，回退到曲库中的播放资产。
        if (item.PublishedTrackId is null)
            return ApiResults.NotFound(HttpContext);
        var asset = await db.Queryable<MediaAsset>()
            .Where(candidate => candidate.TrackId == item.PublishedTrackId.Value && candidate.IsPublished)
            .OrderByDescending(candidate => candidate.IsPrimary)
            .Select(candidate => new { candidate.StorageKey, candidate.Container })
            .FirstAsync(cancellationToken);
        if (asset is null || !libraryPaths.TryResolve(asset.StorageKey, out path) || !System.IO.File.Exists(path))
            return ApiResults.NotFound(HttpContext);
        return Results.File(path, GetAudioContentType(asset.Container), enableRangeProcessing: true);
    }

    [HttpPatch("import-items/{itemId:guid}", Name = "UpdateLibraryImportItem")]
    public async Task<IResult> UpdateItem(
        Guid itemId,
        UpdateImportItemRequest update,
        ISqlSugarClient db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
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
            if (!await db.Queryable<SongRequestRecord>().AnyAsync(request => request.Id == update.SongRequestId.Value, cancellationToken))
                return ApiResults.NotFound(HttpContext);
            item.SongRequestId = update.SongRequestId;
        }
        item.MetadataReviewed = !string.IsNullOrWhiteSpace(item.Title) && DeserializeList(item.ArtistsJson).Count > 0;
        if (item.Status == LibraryImportItemStatuses.PendingReview && item.MetadataReviewed)
            item.Status = LibraryImportItemStatuses.ReadyToPublish;
        item.UpdatedAtUtc = DateTime.UtcNow;
        // 条目更新 + 审计同事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "library_import_item.updated", "import_item", item.Id, new { fields = new[] { "metadata" } });
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.Ok(await ToDtoAsync(item, db, fulfillmentOptions.Value, cancellationToken));
    }

    /// <summary>
    /// 一键补全：对单个导入项即时调度「歌词链（内嵌→旁挂→LRCLIB→酷狗→咪咕）+ iTunes 元数据」两条外部源，
    /// 只补空字段、不覆盖人工已填内容，补齐后立即落库并把更新后的条目返回给审核界面刷新表单。
    /// </summary>
    [HttpPost("import-items/{itemId:guid}/enrich", Name = "EnrichLibraryImportItem")]
    public async Task<IResult> EnrichItem(
        Guid itemId,
        ISqlSugarClient db,
        ILyricsResolver lyricsResolver,
        ArtworkFetcher artworkFetcher,
        QuarantineStoragePaths storagePaths,
        LibraryStoragePaths libraryPaths,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        if (item.Status is LibraryImportItemStatuses.Published or LibraryImportItemStatuses.Rejected)
            return ApiResults.Conflict(HttpContext, "This import item can no longer be edited.");

        var filled = new List<string>();
        var artists = DeserializeList(item.ArtistsJson);

        // —— 歌词链：仅当当前无歌词时才跑外部源 ——
        if (string.IsNullOrWhiteSpace(item.Lyrics))
        {
            storagePaths.TryResolve(item.SourceStorageKey, out var sourcePath);
            var match = await lyricsResolver.ResolveAsync(
                new LyricsQuery(item.Title, artists, item.AlbumTitle, item.DurationMs, EmbeddedLyrics: null, SourcePath: sourcePath),
                cancellationToken);
            if (match is not null && !string.IsNullOrWhiteSpace(match.Content))
            {
                item.Lyrics = match.Content;
                item.LyricsSource = match.Source;
                item.LyricsConfidence = match.Confidence;
                item.LyricsProviderId = match.ProviderId;
                filled.Add("lyrics");
            }
        }

        // —— iTunes 元数据：有艺人或标题即尝试，只补空字段 ——
        var needsMetadata = string.IsNullOrWhiteSpace(item.AlbumTitle)
            || item.ReleaseYear is null
            || DeserializeList(item.GenresJson).Count == 0
            || string.IsNullOrWhiteSpace(item.ArtworkStorageKey)
            || item.TrackNumber is null;
        var firstArtist = artists.FirstOrDefault();
        if (needsMetadata && (!string.IsNullOrWhiteSpace(firstArtist) || !string.IsNullOrWhiteSpace(item.Title)))
        {
            var artworkDir = libraryPaths.TryResolve("artwork/.placeholder", out var placeholderPath)
                ? Path.GetDirectoryName(placeholderPath)!
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "data", "library", "artwork"));
            var itunes = string.IsNullOrWhiteSpace(firstArtist)
                ? await artworkFetcher.FetchByTitleAsync(item.Title!, "artwork", artworkDir, cancellationToken)
                : await artworkFetcher.FetchAsync(firstArtist, item.AlbumTitle, item.Title, "artwork", artworkDir, cancellationToken);
            if (itunes.Success)
            {
                if (string.IsNullOrWhiteSpace(item.AlbumTitle) && !string.IsNullOrWhiteSpace(itunes.AlbumTitle))
                {
                    item.AlbumTitle = itunes.AlbumTitle.Trim();
                    filled.Add("album");
                }
                if (item.ReleaseYear is null && itunes.ReleaseYear is >= 1800 and <= 2200)
                {
                    item.ReleaseYear = itunes.ReleaseYear;
                    filled.Add("releaseYear");
                }
                if (DeserializeList(item.GenresJson).Count == 0 && GenreMoodMapper.Map(itunes.Genre) is { } mood)
                {
                    item.GenresJson = JsonSerializer.Serialize(new[] { mood });
                    filled.Add("genres");
                }
                if (string.IsNullOrWhiteSpace(item.ArtworkStorageKey) && !string.IsNullOrWhiteSpace(itunes.StorageKey))
                {
                    item.ArtworkStorageKey = itunes.StorageKey;
                    filled.Add("cover");
                }
                if (item.TrackNumber is null && itunes.TrackNumber is > 0)
                {
                    item.TrackNumber = itunes.TrackNumber;
                    filled.Add("trackNumber");
                }
                if (item.DiscNumber is null && itunes.DiscNumber is > 0)
                {
                    item.DiscNumber = itunes.DiscNumber;
                    filled.Add("discNumber");
                }
                if (item.ItunesTrackId is null && itunes.ItunesTrackId is > 0)
                    item.ItunesTrackId = itunes.ItunesTrackId;
                if (string.IsNullOrWhiteSpace(item.ItunesCountry) && !string.IsNullOrWhiteSpace(itunes.Country))
                    item.ItunesCountry = itunes.Country.Trim();
                if (item.Explicit is null && !string.IsNullOrWhiteSpace(itunes.Explicitness))
                {
                    item.Explicit = ItunesMetadataHelper.ParseExplicit(itunes.Explicitness);
                    filled.Add("explicit");
                }
            }
        }

        if (filled.Count > 0)
        {
            item.MetadataReviewed = !string.IsNullOrWhiteSpace(item.Title) && DeserializeList(item.ArtistsJson).Count > 0;
            if (item.Status == LibraryImportItemStatuses.PendingReview && item.MetadataReviewed)
                item.Status = LibraryImportItemStatuses.ReadyToPublish;
            item.UpdatedAtUtc = DateTime.UtcNow;
            // 条目更新 + 审计同事务提交。
            await db.Ado.BeginTranAsync();
            try
            {
                await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
                AdminAudit.Add(HttpContext, db, "library_import_item.enriched", "import_item", item.Id, new { fields = filled });
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }

        var dto = await ToDtoAsync(item, db, fulfillmentOptions.Value, cancellationToken);
        return Results.Ok(new EnrichImportItemResultDto(dto, filled));
    }

    /// <summary>
    /// 上传歌词文件（.lrc / .txt）并解析内容写入导入条目。
    /// 自动检测格式：内容含 [mm:ss] 时间轴标记则按 LRC 处理，否则为纯文本。
    /// </summary>
    [HttpPost("import-items/{itemId:guid}/lyrics-file", Name = "UploadImportItemLyricsFile")]
    public async Task<IResult> UploadLyricsFile(
        Guid itemId,
        ISqlSugarClient db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
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
        // 条目更新 + 审计同事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "library_import_item.lyrics_uploaded", "import_item", item.Id, new { extension, length = content.Length });
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.Ok(await ToDtoAsync(item, db, fulfillmentOptions.Value, cancellationToken));
    }

    [HttpPost("import-items/{itemId:guid}/publish", Name = "PublishLibraryImportItem")]
    public async Task<IResult> PublishItem(
        Guid itemId,
        [FromQuery] bool forceFulfill,
        [FromQuery] string? overrideReason,
        ISqlSugarClient db,
        ImportPublishService publisher,
        CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
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

    /// <summary>
    /// 批量发布所有「自动发布候选」条目：pending_review + AutoScore ≥ 阈值 + 转码成功 + 非用户上传。
    /// 管理员在审核面板点「一键发布高分」时调用；逐条走 ImportPublishService.PublishAsync，
    /// 失败条目记录原因后继续下一条，最终返回成功/失败明细供前端展示。
    /// </summary>
    [HttpPost("import-items/bulk-publish-candidates", Name = "BulkPublishImportCandidates")]
    public async Task<IResult> BulkPublishCandidates(
        [FromQuery] int? threshold,
        ISqlSugarClient db,
        ImportPublishService publisher,
        IOptions<SourceDownloadOptions> sourceOptions,
        IOptions<LibraryImportOptions> importOptions,
        CancellationToken cancellationToken)
    {
        var actorId = Guid.Parse(HttpContext.User.FindFirstValue("sub")!);
        var traceId = HttpContext.TraceIdentifier;

        // 阈值优先 SourceDownload（音源抓歌），fallback LibraryImport（手动上传），最低 50
        var effectiveThreshold = threshold ?? Math.Max(50, sourceOptions.Value.AutoPublishThreshold);

        var candidates = await db.Queryable<LibraryImportItem>()
            .Where(item =>
                item.Status == LibraryImportItemStatuses.PendingReview &&
                item.AutoScore >= effectiveThreshold &&
                item.TranscodeStatus == "succeeded" &&
                !item.IsUserUpload &&
                item.MetadataReviewed &&
                !string.IsNullOrWhiteSpace(item.TranscodedStorageKey))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return Results.Ok(new BulkPublishResultDto(0, 0, Array.Empty<BulkPublishItemResult>()));

        var results = new List<BulkPublishItemResult>(candidates.Count);
        var success = 0;
        foreach (var item in candidates)
        {
            try
            {
                var publishResult = await publisher.PublishAsync(
                    item,
                    new ImportPublishContext(actorId, traceId, IsAutomatic: false),
                    forceFulfill: false,
                    overrideReason: null,
                    cancellationToken);

                if (publishResult.Error is null)
                {
                    success++;
                    results.Add(new BulkPublishItemResult(item.Id, item.DisplayName, true, publishResult.TrackId, null));
                }
                else
                {
                    results.Add(new BulkPublishItemResult(item.Id, item.DisplayName, false, null, publishResult.Error.Message));
                }
            }
            catch (Exception ex)
            {
                results.Add(new BulkPublishItemResult(item.Id, item.DisplayName, false, null, ex.Message));
            }
        }

        return Results.Ok(new BulkPublishResultDto(candidates.Count, success, results.ToArray()));
    }

    [HttpDelete("import-items/{itemId:guid}/song-request", Name = "UnlinkImportItemSongRequest")]
    public async Task<IResult> UnlinkSongRequest(
        Guid itemId,
        ISqlSugarClient db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        if (item.SongRequestId is null)
            return Results.NoContent();

        // 解除“本次导入满足某条点歌”的关联；不影响已发布曲目，仅避免发布时误把需求标记为已满足。
        item.SongRequestId = null;
        item.UpdatedAtUtc = DateTime.UtcNow;
        // 条目更新 + 审计同事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "library_import_item.song_request_unlinked", "import_item", item.Id);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.Ok(await ToDtoAsync(item, db, fulfillmentOptions.Value, cancellationToken));
    }

    /// <summary>
    /// 用户上传条目的转公开：私有曲目已存在（处理完成时自动入库、仅上传者可听），
    /// 管理员审核通过后设置发布时间进入公共曲库，并把审核中修正过的元数据同步到曲目。
    /// 拒绝（不调用本端点而调用删除端点）时曲目保持私有，上传者可继续收听。
    /// </summary>
    private async Task<IResult> PublishUserUploadAsync(
        LibraryImportItem item,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (item.Status != LibraryImportItemStatuses.PendingPublication || item.PublishedTrackId is null)
            return ApiResults.Conflict(HttpContext, "Only user uploads awaiting publication review can be promoted.");
        var track = await db.Queryable<Track>().FirstAsync(candidate => candidate.Id == item.PublishedTrackId.Value, cancellationToken);
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
        Lyric? lyricToInsert = null;
        Lyric? lyricToUpdate = null;
        if (!string.IsNullOrWhiteSpace(item.Lyrics))
        {
            var isLrc = System.Text.RegularExpressions.Regex.IsMatch(item.Lyrics, @"\[\d{1,2}:\d{1,2}");
            var existing = await db.Queryable<Lyric>().FirstAsync(
                lyric => lyric.TrackId == track.Id && lyric.IsDefault, cancellationToken);
            if (existing is null)
            {
                lyricToInsert = new Lyric
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
            else if (existing.Content != item.Lyrics)
            {
                existing.Content = item.Lyrics;
                existing.Format = isLrc ? LyricFormats.Lrc : LyricFormats.Plain;
                existing.Source = item.LyricsSource ?? LyricsSources.Manual;
                existing.Confidence = item.LyricsConfidence ?? 1.0;
                existing.ProviderId = item.LyricsProviderId;
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
                lyricToUpdate = existing;
            }
        }

        track.PublishedAt = DateTimeOffset.UtcNow;
        item.Status = LibraryImportItemStatuses.Published;
        item.UpdatedAtUtc = DateTime.UtcNow;
        // 曲目 + 歌词 + 条目 + 审计同事务提交（等价原先单次 SaveChanges 的原子性）。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(track).ExecuteCommandAsync(cancellationToken);
            if (lyricToInsert is not null)
                await db.Insertable(lyricToInsert).ExecuteCommandAsync(cancellationToken);
            else if (lyricToUpdate is not null)
                await db.Updateable(lyricToUpdate).ExecuteCommandAsync(cancellationToken);
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "track.published", "track", track.Id, new { importItemId = item.Id, promoted = true });
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        await ImportPublishService.UpdateBatchStatusAsync(db, item.BatchId, cancellationToken);
        return Results.Ok(new PublishImportItemResultDto(track.Id, item.Status));
    }

    /// <summary>
    /// 管理端导入条目列表：按状态筛选（如 pending_publication 为待审核转公开的用户上传），
    /// 附带上批次上传者信息，供审核界面展示来源；支持按 标题/评分/状态/时长/创建时间 排序。
    /// </summary>
    /// <param name="sort">排序字段：created（默认）/ title / score / status / duration。</param>
    /// <param name="order">排序方向：desc（默认）/ asc。</param>
    /// <param name="db">数据库上下文。</param>
    /// <param name="fulfillmentOptions">点歌履约配置（用于附带可发布信息）。</param>
    /// <param name="status">条目状态过滤，空表示全部。</param>
    /// <param name="q">关键词：匹配曲名 / 艺人 / 专辑 / 源文件名，空表示不过滤。</param>
    /// <param name="page">页码，默认 1。</param>
    /// <param name="pageSize">每页条数，1~1000，默认 20。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("import-items", Name = "ListLibraryImportItems")]
    public async Task<IResult> ListItems(
        ISqlSugarClient db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromQuery] string? sort,
        [FromQuery] string? order,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var query = db.Queryable<LibraryImportItem>().Where(item => !item.IsDeleted);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(item => item.Status == status);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(item =>
                (item.Title != null && item.Title.Contains(term)) ||
                (item.AlbumTitle != null && item.AlbumTitle.Contains(term)) ||
                item.ArtistsJson.Contains(term) ||
                item.DisplayName.Contains(term));
        }
        var total = await query.CountAsync(cancellationToken);
        var ascending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
        // 空值（未评分/无标题）统一排在末尾：先按是否存在排序，再按字段排序。
        query = sort?.ToLowerInvariant() switch
        {
            "title" => ascending
                ? query.OrderBy(item => item.Title == null).OrderBy(item => item.Title)
                : query.OrderBy(item => item.Title == null).OrderByDescending(item => item.Title),
            "score" => ascending
                ? query.OrderBy(item => item.AutoScore == null).OrderBy(item => item.AutoScore)
                : query.OrderBy(item => item.AutoScore == null).OrderByDescending(item => item.AutoScore),
            "status" => ascending
                ? query.OrderBy(item => item.Status)
                : query.OrderByDescending(item => item.Status),
            "duration" => ascending
                ? query.OrderBy(item => item.DurationMs == null).OrderBy(item => item.DurationMs)
                : query.OrderBy(item => item.DurationMs == null).OrderByDescending(item => item.DurationMs),
            _ => ascending
                ? query.OrderBy(item => item.CreatedAtUtc)
                : query.OrderByDescending(item => item.CreatedAtUtc),
        };
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        // 批次一次批量查出后内存水合（替代 Include）。
        var batchIds = items.Select(item => item.BatchId).Distinct().ToList();
        var batches = batchIds.Count == 0
            ? new List<LibraryImportBatch>()
            : await db.Queryable<LibraryImportBatch>().Where(batch => batchIds.Contains(batch.Id)).ToListAsync(cancellationToken);
        var batchMap = batches.ToDictionary(batch => batch.Id);
        foreach (var item in items)
            if (batchMap.TryGetValue(item.BatchId, out var batch))
                item.Batch = batch;
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
    public async Task<IResult> DeleteItem(Guid itemId, ISqlSugarClient db, QuarantineStoragePaths storagePaths, CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
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
        // 条目更新 + 审计同事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "library_import_item.rejected", "import_item", item.Id);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
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
    public async Task<IResult> DismissItem(Guid itemId, ISqlSugarClient db, CancellationToken cancellationToken)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(candidate => candidate.Id == itemId, cancellationToken);
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
        // 条目更新 + 审计同事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(item).ExecuteCommandAsync(cancellationToken);
            AdminAudit.Add(HttpContext, db, "library_import_item.dismissed", "import_item", item.Id, new { item.Status });
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
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
        BuildMatch(item, request, options),
        item.AutoScore,
        // autoPublished：本记录是自动发布的（Published 且 SourceProvider != null 且 MetadataReviewed 在直通路径上被标记）
        item.Status == LibraryImportItemStatuses.Published && item.MetadataReviewed,
        ParseBreakdown(item.AutoScoreBreakdownJson));

    /// <summary>把 AutoScoreBreakdownJson 解析成强类型 DTO；JSON 缺失或不完整时返回 null。</summary>
    private static ImportScoreBreakdownDto? ParseBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 兼容两种命名：早期写入的 PascalCase（SourceScoreDimension 直接序列化）与 camelCase。
            static bool TryGet(JsonElement el, string camelName, out JsonElement value)
            {
                if (el.TryGetProperty(camelName, out value)) return true;
                var pascal = char.ToUpperInvariant(camelName[0]) + camelName[1..];
                return el.TryGetProperty(pascal, out value);
            }

            if (!TryGet(root, "total", out var total)) return null;

            static ScoreSegmentDto? ReadSeg(JsonElement root, string name)
            {
                if (!TryGet(root, name, out var seg)) return null;
                return new ScoreSegmentDto(
                    TryGet(seg, "score", out var s) ? s.GetDouble() : 0,
                    TryGet(seg, "maxScore", out var m) ? m.GetDouble() : 0,
                    TryGet(seg, "reason", out var r) ? r.GetString() ?? string.Empty : string.Empty);
            }

            var quality = ReadSeg(root, "quality");
            var metadata = ReadSeg(root, "metadata");
            var lyrics = ReadSeg(root, "lyrics");
            if (quality is null || metadata is null || lyrics is null) return null;

            return new ImportScoreBreakdownDto(total.GetDouble(), quality, metadata, lyrics);
        }
        catch { return null; }
    }

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
        ISqlSugarClient db,
        SongRequestFulfillmentOptions options,
        CancellationToken cancellationToken)
    {
        var requests = await LoadRequestsAsync(db, [item.SongRequestId], cancellationToken);
        var request = item.SongRequestId is Guid requestId && requests.TryGetValue(requestId, out var matched) ? matched : null;
        return ToDto(item, request, options);
    }

    /// <summary>批量加载导入项关联的需求，避免逐个查询（N+1）。</summary>
    private static async Task<Dictionary<Guid, SongRequestRecord>> LoadRequestsAsync(
        ISqlSugarClient db,
        IEnumerable<Guid?> requestIds,
        CancellationToken cancellationToken)
    {
        var ids = requestIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return [];
        var requests = await db.Queryable<SongRequestRecord>()
            .Where(request => ids.Contains(request.Id))
            .ToListAsync(cancellationToken);
        return requests.ToDictionary(request => request.Id);
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
