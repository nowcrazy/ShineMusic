// 职责：用户上传本地音频（扫码导入）与“我的曲库”管理。
// 上传与管理员导入共用隔离暂存（ImportStagingService）与后台处理队列；
// 内容哈希命中已有曲库的音频直接建曲库引用（秒级、不入队、不重复存储），
// 未命中的新歌进入处理队列，完成后自动私有入库（仅上传者可听），
// 是否转公开进入公共曲库由管理员在审核队列决定。

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Contracts;
using ShineMusic.Api.Data;
using ShineMusic.Api.Extensions;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;

namespace ShineMusic.Api.Endpoints;

/// <summary>用户上传本地音频与“我的曲库”管理。</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[FeatureGate(nameof(FeatureOptions.UserUploads))]
[Tags("My Uploads")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class UserUploadController : ControllerBase
{
    // 预检与上传匹配的时长容差（毫秒），与歌词外部源容差保持一致。
    private const long DurationToleranceMs = 5000;
    // 单次预检的文件数量上限。
    private const int MaxPrecheckFiles = 100;

    /// <summary>
    /// 上传本地音频（可同时携带同名 .lrc 旁挂歌词）。
    /// 内容哈希命中已有曲库的音频直接建曲库引用（秒级加入曲库、不重复存储），
    /// 未命中的新歌进入处理队列（转码 + 歌词自动匹配），完成后自动私有入库。
    /// </summary>
    [HttpPost("uploads", Name = "UploadUserTracks")]
    public async Task<IResult> Upload(
        MusicDbContext db,
        ImportStagingService staging,
        LibraryImportQueue queue,
        IOptions<UploadOptions> uploadOptions,
        ILogger<UserUploadController> logger,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (!MediaTypeHeaderValue.TryParse(HttpContext.Request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return ApiResults.ValidationError(HttpContext, "contentType", "A multipart/form-data request is required.");
        if (HttpContext.Request.ContentLength is > 0 && HttpContext.Request.ContentLength > uploadOptions.Value.MaxBatchSizeBytes)
            return ApiResults.Error(HttpContext, StatusCodes.Status413PayloadTooLarge, ErrorCodes.UploadTooLarge, "The upload batch exceeded the configured limit.");

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

        if (staged.OrphanLyrics.Count > 0)
            logger.LogWarning("Ignored {Count} lyrics file(s) without a matching audio track: {Orphans} (user {UserId}).",
                staged.OrphanLyrics.Count, string.Join(", ", staged.OrphanLyrics), userId);

        var referencedTrackIds = new List<Guid>();
        var referencedTitles = new List<ReferencedTrackDto>();
        var queued = new List<QueuedUploadDto>();
        var rejected = new List<RejectedUploadDto>();
        var pendingItems = new List<LibraryImportItem>();

        foreach (var file in staged.Audio)
        {
            // 强去重：内容哈希命中已有媒体资产 → 直接建曲库引用（不重复存储、不入处理队列）。
            var existing = await db.MediaAssets.AsNoTracking()
                .Where(asset => asset.ContentHash == file.ContentHash)
                .Select(asset => new { asset.TrackId, asset.Track!.PublishedAt, asset.Track.OwnerUserId })
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                // 他人私有曲目不可引用（避免私有内容泄露）。
                if (existing.PublishedAt is null && existing.OwnerUserId != userId)
                {
                    rejected.Add(new RejectedUploadDto(file.DisplayName, "private_track_of_another_user"));
                    staging.DeleteStagedFile(file.StorageKey);
                    continue;
                }
                if (!await db.UserLibraryItems.AnyAsync(item => item.UserId == userId && item.TrackId == existing.TrackId, cancellationToken))
                {
                    db.UserLibraryItems.Add(new UserLibraryItem { UserId = userId, TrackId = existing.TrackId });
                    referencedTrackIds.Add(existing.TrackId);
                }
                staging.DeleteStagedFile(file.StorageKey);
                continue;
            }

            // 未命中：新歌进入处理队列（后台完成转码与歌词匹配后自动私有入库）。
            pendingItems.Add(new LibraryImportItem
            {
                Id = file.ItemId,
                DisplayName = file.DisplayName,
                SourceStorageKey = file.StorageKey,
                ContentHash = file.ContentHash,
                SizeBytes = file.SizeBytes,
                Status = LibraryImportItemStatuses.Uploaded,
                IsUserUpload = true
            });
            queued.Add(new QueuedUploadDto(file.ItemId, file.DisplayName));
        }

        if (pendingItems.Count > 0)
        {
            var batch = new LibraryImportBatch
            {
                Id = staged.BatchId,
                CreatedByUserId = userId,
                Status = LibraryImportStatuses.Queued,
                Items = pendingItems
            };
            db.LibraryImportBatches.Add(batch);
        }
        else
        {
            // 全部文件均已引用：批次目录已空，直接清理。
            staging.DeleteBatchDirectory(staged.BatchDirectory);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var item in pendingItems)
            await queue.QueueAsync(item.Id, cancellationToken);

        // 为引用结果补齐曲目摘要（数量少，直接查库）。
        if (referencedTrackIds.Count > 0)
        {
            var tracks = await db.Tracks.AsNoTracking()
                .Include(track => track.TrackArtists).ThenInclude(link => link.Artist)
                .Where(track => referencedTrackIds.Contains(track.Id))
                .ToListAsync(cancellationToken);
            referencedTitles.AddRange(tracks.Select(track => new ReferencedTrackDto(
                track.Id,
                track.Title,
                track.TrackArtists.Select(link => link.Artist.Name).ToArray())));
        }

        return Results.Accepted($"/api/v1/me/library", new UserUploadResponseDto(
            pendingItems.Count > 0 ? staged.BatchId : null,
            referencedTitles,
            queued,
            rejected));
    }

    /// <summary>
    /// 上传前批量预检：客户端把本地文件指纹（标题/艺人/时长）发给服务端，
    /// 返回曲库中已存在的曲目，命中者可跳过上传、直接建曲库引用。
    /// </summary>
    [HttpPost("uploads/precheck", Name = "PrecheckUserUploads")]
    public async Task<IResult> Precheck(
        UploadPrecheckRequestDto request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        if (request.Files.Count == 0)
            return ApiResults.ValidationError(HttpContext, "files", "At least one file fingerprint is required.");
        if (request.Files.Count > MaxPrecheckFiles)
            return ApiResults.ValidationError(HttpContext, "files", $"At most {MaxPrecheckFiles} file fingerprints per request.");

        // 按标题粗筛候选（SQL 侧小写精确匹配），再在内存中按时长/艺人精确匹配。
        var normalizedTitles = request.Files
            .Select(file => NormalizeTitle(file.Title))
            .Where(title => title.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates = normalizedTitles.Length == 0
            ? []
            : await db.Tracks.AsNoTracking()
                .Include(track => track.TrackArtists).ThenInclude(link => link.Artist)
                .Include(track => track.Album)
                // 仅匹配“公开曲目”与“本人私有曲目”。
                .Where(track => track.PublishedAt != null || track.OwnerUserId == userId)
                .Where(track => normalizedTitles.Contains(track.Title.ToLower()))
                .ToListAsync(cancellationToken);

        var results = new List<UploadPrecheckResultDto>(request.Files.Count);
        foreach (var file in request.Files)
        {
            var normalized = NormalizeTitle(file.Title);
            PrecheckMatchDto? match = null;
            if (normalized.Length > 0)
            {
                var candidate = candidates
                    .Where(track => string.Equals(NormalizeTitle(track.Title), normalized, StringComparison.OrdinalIgnoreCase))
                    .Where(track => file.DurationMs is null || track.DurationMs is null ||
                        Math.Abs(track.DurationMs.Value - file.DurationMs.Value) <= DurationToleranceMs)
                    .Where(track => string.IsNullOrWhiteSpace(file.Artist) ||
                        track.TrackArtists.Any(link => link.Artist.Name.Contains(file.Artist!, StringComparison.OrdinalIgnoreCase)))
                    // 公共曲目优先，其次标题字典序，保证结果稳定。
                    .OrderBy(track => track.PublishedAt == null ? 1 : 0)
                    .ThenBy(track => track.Title)
                    .FirstOrDefault();
                if (candidate is not null)
                    match = new PrecheckMatchDto(
                        candidate.Id,
                        candidate.Title,
                        candidate.TrackArtists.Select(link => link.Artist.Name).ToArray(),
                        candidate.PublishedAt != null,
                        ArtworkUrl(candidate.ArtworkId ?? candidate.Album?.ArtworkId));
            }
            results.Add(new UploadPrecheckResultDto(file.FileName, match));
        }
        return Results.Ok(new UploadPrecheckResponseDto(results));
    }

    /// <summary>我的曲库：列出当前用户引用的全部曲目（含私有与公开）。</summary>
    [HttpGet("library", Name = "GetMyLibrary")]
    public async Task<IResult> GetLibrary(
        MusicDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var pageNumber = Math.Clamp(page ?? 1, 1, 1000);
        var size = Math.Clamp(pageSize ?? 50, 1, 200);

        // 注意：SQLite 的 EF 提供程序不支持对 DateTimeOffset 列做 ORDER BY 翻译（会抛 NotSupportedException）。
        // 因此先把当前用户的引用记录整体物化到内存，再在客户端按 AddedAtUtc 倒序分页，
        // 既保留“最近添加优先”语义，又规避了数据库端翻译限制（单用户曲库规模可控）。
        var allRows = await db.UserLibraryItems.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Include(item => item.Track).ThenInclude(track => track.TrackArtists).ThenInclude(link => link.Artist)
            .Include(item => item.Track).ThenInclude(track => track.Album)
            .Include(item => item.Track).ThenInclude(track => track.Artwork)
            .ToListAsync(cancellationToken);
        var ordered = allRows.OrderByDescending(item => item.AddedAtUtc).ToList();
        var total = ordered.Count;
        var rows = ordered.Skip((pageNumber - 1) * size).Take(size).ToList();

        var items = rows.Select(row => new UserLibraryItemDto(
            row.Track.Id,
            row.Track.Title,
            row.Track.TrackArtists.Select(link => link.Artist.Name).ToArray(),
            row.Track.Album?.Title,
            row.Track.DurationMs,
            ArtworkUrl(row.Track.ArtworkId ?? row.Track.Album?.ArtworkId),
            row.Track.PublishedAt != null,
            DateTime.SpecifyKind(row.AddedAtUtc.UtcDateTime, DateTimeKind.Utc))).ToArray();
        return Results.Ok(new PageResult<UserLibraryItemDto>(items, pageNumber, size, total));
    }

    /// <summary>批量加入曲库：预检命中公共曲库的歌曲可直接引用（无需上传文件）。</summary>
    [HttpPost("library", Name = "AddTracksToMyLibrary")]
    public async Task<IResult> AddToLibrary(
        AddToLibraryRequestDto request,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var trackIds = request.TrackIds.Distinct().ToArray();
        if (trackIds.Length == 0)
            return ApiResults.ValidationError(HttpContext, "trackIds", "At least one track id is required.");
        if (trackIds.Length > 200)
            return ApiResults.ValidationError(HttpContext, "trackIds", "At most 200 tracks per request.");

        // 仅允许引用公开曲目与本人私有曲目。
        var referenceable = await db.Tracks.AsNoTracking()
            .Where(track => trackIds.Contains(track.Id) && (track.PublishedAt != null || track.OwnerUserId == userId))
            .Select(track => track.Id)
            .ToListAsync(cancellationToken);

        var existing = await db.UserLibraryItems.AsNoTracking()
            .Where(item => item.UserId == userId && referenceable.Contains(item.TrackId))
            .Select(item => item.TrackId)
            .ToListAsync(cancellationToken);
        var existingSet = existing.ToHashSet();

        foreach (var trackId in referenceable.Where(trackId => !existingSet.Contains(trackId)))
            db.UserLibraryItems.Add(new UserLibraryItem { UserId = userId, TrackId = trackId });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发重复插入由唯一索引兜底，忽略冲突。
            db.ChangeTracker.Clear();
        }
        return Results.NoContent();
    }

    /// <summary>从我的曲库移除引用（不影响曲目本体与其他用户的引用）。</summary>
    [HttpDelete("library/{trackId:guid}", Name = "RemoveTrackFromMyLibrary")]
    public async Task<IResult> RemoveFromLibrary(
        Guid trackId,
        MusicDbContext db,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.User.TryGetUserId(out var userId))
            return Results.Unauthorized();
        var item = await db.UserLibraryItems.FirstOrDefaultAsync(
            candidate => candidate.UserId == userId && candidate.TrackId == trackId, cancellationToken);
        if (item is null)
            return ApiResults.NotFound(HttpContext);
        db.UserLibraryItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>归一化标题：去首尾空白、转小写（预检粗筛的规范化口径）。</summary>
    private static string NormalizeTitle(string? title) => title?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? ArtworkUrl(Guid? artworkId) => artworkId is null ? null : $"/api/v1/artwork/{artworkId.Value}";

}
