using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Domain;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using SqlSugar;

namespace ShineMusic.Service;

public sealed class LibraryWorkflowConflictException(string message) : InvalidOperationException(message);

/// <summary>Private originals, scoped replacement, public nominations and their bounded retention.</summary>
public sealed class UserLibraryWorkflowService(
    ISqlSugarClient db,
    LibraryStoragePaths paths,
    QuarantineStoragePaths quarantine,
    IOptions<LibraryWorkflowOptions> options,
    ILogger<UserLibraryWorkflowService> logger)
{
    // One mutation at a time per host; PostgreSQL advisory transaction lock provides
    // the same ordering across Web and independent workers sharing the database.
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private const long MutationLockId = 586809626057119800L;

    public async Task CompleteUploadAsync(LibraryImportItem item, Guid ownerUserId, string originalSourcePath,
        Guid attemptId, CancellationToken ct)
    {
        await using (var input = File.OpenRead(originalSourcePath))
        {
            item.ContentHash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
            item.SizeBytes = input.Length;
        }
        var key = $"original/{item.ContentHash}{SafeExtension(item.Container)}";
        item.OriginalStorageKey = key;
        var previousCompletion = (item.PublishedTrackId, item.Status, item.TranscodeStatus,
            item.TranscodedStorageKey, item.TranscodeFormat, item.TranscodeBitrateKbps);
        // Copy/hash verification may be slow. Keep it outside the row-locking final
        // transaction so this worker's independent heartbeat can keep renewing.
        try
        {
        await SaveOriginalAsync(originalSourcePath, key, item.ContentHash, ct);
        await InTransactionAsync(async () =>
        {
            // The conditional update takes the same row lock as recovery/claim. A
            // losing attempt cannot create a track, replace an asset or add reviews.
            await ImportExecutionGuard.LockAsync(db, item, attemptId, ct);
            // Cleanup uses this same mutation/advisory lock. It may have released
            // the previously copied unreferenced original while we waited for it.
            if (!paths.TryResolve(key, out var storedOriginal) || !File.Exists(storedOriginal))
                await SaveOriginalAsync(originalSourcePath, key, item.ContentHash, ct);
            var candidates = await FindMatchesAsync(item, ownerUserId, ct);
            var exactHash = await db.Queryable<MediaAsset>()
                .LeftJoin<Track>((asset, track) => asset.TrackId == track.Id)
                .Where((asset, track) => asset.IsOriginal && asset.ContentHash == item.ContentHash && track.OwnerUserId == ownerUserId
                    && track.PublishedAt == null && !track.IsRecycled)
                .Select((asset, track) => track.Id).FirstAsync(ct);
            if (exactHash != Guid.Empty)
            {
                item.PublishedTrackId = exactHash;
                await EnsureLibraryReferenceAsync(ownerUserId, exactHash, ct);
            }
            else if (candidates.Count == 0)
            {
                var created = await CreateTrackAsync(item, ownerUserId, false, ct);
                item.PublishedTrackId = created.Id;
            }
            else
            {
                var existing = candidates[0];
                var asset = await OriginalAsync(existing.Id, ct);
                var sameRecording = candidates.Count == 1 && await IsSameRecordingAsync(item, existing, ct);
                var comparison = LibraryWorkflowPolicy.Compare(item, asset);
                if (sameRecording && comparison is OriginalQualityComparison.Equal or OriginalQualityComparison.Worse)
                    item.PublishedTrackId = existing.Id;
                else if (sameRecording && comparison == OriginalQualityComparison.Better)
                {
                    await ReplaceOriginalAsync(existing, item, ct);
                    item.PublishedTrackId = existing.Id;
                }
                else
                {
                    await CreateReviewAsync(item, ownerUserId, "private", null, existing.Id,
                        sameRecording ? "quality_uncertain" : "identity_uncertain", ct);
                    item.PublishedTrackId = null;
                }
            }
            // Processing completion is distinct from public nomination. Pending private
            // confirmation must not be presented as a second playable library track.
            item.Status = LibraryImportItemStatuses.Published;
            item.TranscodeStatus = "succeeded";
            item.TranscodedStorageKey = null;
            item.TranscodeFormat = null;
            item.TranscodeBitrateKbps = null;
            item.ErrorCode = null;
            item.ErrorMessage = null;
            item.UpdatedAtUtc = DateTime.UtcNow;
            if (item.ParticipatesInPublicReview && item.PublishedTrackId.HasValue)
            {
                var privateAsset = await OriginalAsync(item.PublishedTrackId.Value, ct);
                if (privateAsset is not null && privateAsset.ContentHash == item.ContentHash)
                    await EnqueueCoreAsync(ownerUserId, item.PublishedTrackId.Value, item, ct);
                else if (LibraryWorkflowPolicy.CanNominate(item, options.Value))
                {
                    var publicMatches = await FindMatchesAsync(item, null, ct);
                    await CreateReviewAsync(item, ownerUserId, "public", item.PublishedTrackId,
                        publicMatches.FirstOrDefault()?.Id, publicMatches.Count == 0 ? "new_public" : "public_comparison", ct);
                }
            }
            await ImportExecutionGuard.SaveAsync(db, item, attemptId, ct);
        }, ct);
        }
        catch
        {
            // A rolled-back publication must not leak success fields into the caller's
            // subsequent failure save, especially a track ID that no longer exists.
            (item.PublishedTrackId, item.Status, item.TranscodeStatus, item.TranscodedStorageKey,
                item.TranscodeFormat, item.TranscodeBitrateKbps) = previousCompletion;
            item.OriginalStorageKey = null;
            // The pre-transaction copy must not become an uncharged orphan after rejection.
            try { await ReleaseUnreferencedAsync(key, CancellationToken.None); }
            catch (Exception cleanup) { logger.LogWarning(cleanup, "Rejected upload original cleanup will retry for import {ItemId}.", item.Id); }
            throw;
        }
        // Publication is already durable. Housekeeping cannot turn it into a failed
        // processing result that would overwrite the committed row in the caller.
        try { await ReleaseUnreferencedAsync(key, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Original cleanup will retry for import {ItemId}.", item.Id); }
    }

    public async Task<PrivateOriginalStorageDto> GetPrivateOriginalStorageAsync(Guid ownerUserId, CancellationToken ct)
    {
        PrivateOriginalStorageDto? result = null;
        await InTransactionAsync(async () =>
        {
            var usage = await PrivateOriginalStorageMeter.MeasureAsync(db, paths, ownerUserId, ct);
            var limit = options.Value.MaxPrivateOriginalBytesPerUser;
            result = new(usage.Bytes, limit, limit == 0 ? null : Math.Max(0, limit - usage.Bytes), usage.Originals.Count);
        }, ct);
        return result!;
    }

    public async Task<LibraryWorkflowReviewDto?> EnqueuePublicReviewAsync(Guid ownerUserId, Guid sourceTrackId, CancellationToken ct)
    {
        LibraryWorkflowReview? review = null;
        await InTransactionAsync(async () =>
        {
            var track = await db.Queryable<Track>().FirstAsync(t => t.Id == sourceTrackId && t.OwnerUserId == ownerUserId
                && t.PublishedAt == null && !t.IsRecycled, ct);
            if (track is null) return;
            var original = await OriginalAsync(track.Id, ct);
            if (original is null) throw new LibraryWorkflowConflictException("A retained original is required for public nomination.");
            review = await db.Queryable<LibraryWorkflowReview>().FirstAsync(r => r.Kind == "public" && r.Status == "pending"
                && r.OwnerUserId == ownerUserId && r.SourceTrackId == sourceTrackId && r.ContentHash == original.ContentHash, ct);
            if (review is not null) return;
            var item = await ItemForTrackAsync(track, ct);
            item.ParticipatesInPublicReview = true;
            await db.Updateable(item).ExecuteCommandAsync(ct);
            review = await EnqueueCoreAsync(ownerUserId, sourceTrackId, item, ct);
        }, ct);
        return review is null ? null : await ToDtoAsync(review, ct);
    }

    public async Task<LibraryWorkflowPageDto<LibraryWorkflowReviewDto>> GetReviewsAsync(
        Guid? ownerUserId, string? kind, string? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Queryable<LibraryWorkflowReview>();
        if (ownerUserId.HasValue) query = query.Where(r => r.OwnerUserId == ownerUserId.Value && r.Kind == "private");
        else query = query.Where(r => r.Kind == "public");
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(r => r.Kind == kind);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.CreatedAtUtc).OrderBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = new List<LibraryWorkflowReviewDto>();
        foreach (var row in rows) items.Add(await ToDtoAsync(row, ct));
        return new(items, page, pageSize, count);
    }

    public async Task<LibraryWorkflowReviewDto?> GetReviewAsync(Guid id, Guid? ownerUserId, CancellationToken ct)
    {
        var row = await AuthorizedReviewAsync(id, ownerUserId, ct);
        return row is null ? null : await ToDtoAsync(row, ct);
    }

    public async Task<LibraryWorkflowReviewDto?> ResolveReviewAsync(Guid id, Guid? ownerUserId,
        string decision, string? reason, CancellationToken ct)
    {
        LibraryWorkflowReview? result = null;
        await InTransactionAsync(async () =>
        {
            var review = await AuthorizedReviewAsync(id, ownerUserId, ct);
            if (review is null) return;
            if (review.Status != "pending" || review.ExpiresAtUtc <= DateTime.UtcNow)
                throw new LibraryWorkflowConflictException("This review is no longer pending.");
            var item = await db.Queryable<LibraryImportItem>().FirstAsync(i => i.Id == review.ImportItemId, ct);
            if (item is null) throw new LibraryWorkflowConflictException("The retained original is unavailable.");
            if (decision == "reject")
            {
                review.Status = "rejected";
            }
            else if (review.Kind == "private")
            {
                if (decision is not ("replace" or "separate")) throw new LibraryWorkflowConflictException("Choose replace, separate, or reject.");
                if (decision == "separate" && review.ReviewReason != "identity_uncertain")
                    throw new LibraryWorkflowConflictException("A separate private track is only allowed after confirming a different recording.");
                Track track;
                if (decision == "separate") track = await CreateTrackAsync(item, review.OwnerUserId, false, ct);
                else
                {
                    track = await db.Queryable<Track>().FirstAsync(t => t.Id == review.ExistingTrackId
                        && t.OwnerUserId == review.OwnerUserId && t.PublishedAt == null && !t.IsRecycled, ct)
                        ?? throw new LibraryWorkflowConflictException("The original private version is no longer available.");
                    if (LibraryWorkflowPolicy.Compare(item, await OriginalAsync(track.Id, ct)) == OriginalQualityComparison.Worse)
                        throw new LibraryWorkflowConflictException("A known lower quality version cannot replace the original.");
                    await ReplaceOriginalAsync(track, item, ct);
                }
                item.PublishedTrackId = track.Id;
                item.UpdatedAtUtc = DateTime.UtcNow;
                await db.Updateable(item).ExecuteCommandAsync(ct);
                review.SourceTrackId = track.Id;
                review.Status = "approved";
                if (item.ParticipatesInPublicReview) await EnqueueCoreAsync(review.OwnerUserId, track.Id, item, ct);
            }
            else
            {
                if (decision is not ("approve" or "separate")) throw new LibraryWorkflowConflictException("Choose approve, separate, or reject.");
                if (!item.ParticipatesInPublicReview || !LibraryWorkflowPolicy.CanNominate(item, options.Value))
                    throw new LibraryWorkflowConflictException("The upload is not eligible for public review.");
                if (!review.SourceTrackId.HasValue || !await db.Queryable<Track>().AnyAsync(t => t.Id == review.SourceTrackId.Value
                        && t.OwnerUserId == review.OwnerUserId && t.PublishedAt == null && !t.IsRecycled, ct))
                    throw new LibraryWorkflowConflictException("The private source is no longer available.");
                var matches = await FindMatchesAsync(item, null, ct);
                var same = new List<Track>();
                foreach (var match in matches)
                    if (await IsSameRecordingAsync(item, match, ct)) same.Add(match);
                Track published;
                if (decision == "separate") published = await CreateTrackAsync(item, null, true, ct);
                else if (matches.Count > 1 || (matches.Count == 1 && same.Count == 0))
                    throw new LibraryWorkflowConflictException("Choose separate only after confirming this is a different recording.");
                else if (same.Count == 0) published = await CreateTrackAsync(item, null, true, ct);
                else
                {
                    published = same[0];
                    var comparison = LibraryWorkflowPolicy.Compare(item, await OriginalAsync(published.Id, ct));
                    if (comparison == OriginalQualityComparison.Worse)
                        throw new LibraryWorkflowConflictException("The public library already has a better original.");
                    if (comparison != OriginalQualityComparison.Equal) await ReplaceOriginalAsync(published, item, ct);
                }
                review.PublicTrackId = published.Id;
                review.Status = "approved";
            }
            review.ResolutionReason = SafeReason(reason);
            review.UpdatedAtUtc = DateTime.UtcNow;
            await db.Updateable(review).ExecuteCommandAsync(ct);
            await NotifyAsync(review.OwnerUserId, "曲库审核已处理", $"{item.Title ?? item.DisplayName}：{review.Status}", review, ct);
            result = review;
        }, ct);
        if (result is not null) await ReleaseUnreferencedAsync(result.OriginalStorageKey, ct);
        return result is null ? null : await ToDtoAsync(result, ct);
    }

    public async Task<LibraryWorkflowTrackDto?> MoveToRecycleAsync(Guid trackId, Guid ownerUserId, CancellationToken ct)
    {
        Track? result = null;
        await InTransactionAsync(async () =>
        {
            var track = await db.Queryable<Track>().FirstAsync(t => t.Id == trackId, ct);
            if (track is null) return;
            if (track.PublishedAt.HasValue)
            {
                await db.Deleteable<UserLibraryItem>().Where(r => r.TrackId == trackId && r.UserId == ownerUserId).ExecuteCommandAsync(ct);
                result = track;
                return;
            }
            if (track.OwnerUserId != ownerUserId) return;
            if (!track.IsRecycled)
            {
                track.IsRecycled = true;
                track.RecycledAtUtc = DateTime.UtcNow;
                track.RecycleExpiresAtUtc = DateTime.UtcNow.AddDays(options.Value.RecycleDays);
                await db.Updateable(track).ExecuteCommandAsync(ct);
                var pending = await db.Queryable<LibraryWorkflowReview>().Where(r => r.SourceTrackId == track.Id && r.Status == "pending").ToListAsync(ct);
                foreach (var review in pending)
                {
                    review.Status = "rejected"; review.ResolutionReason = "source_recycled"; review.UpdatedAtUtc = DateTime.UtcNow;
                    await db.Updateable(review).ExecuteCommandAsync(ct);
                }
            }
            result = track;
        }, ct);
        return result is null ? null : await TrackDtoAsync(result, ct);
    }

    public async Task<LibraryWorkflowTrackDto?> RestoreAsync(Guid trackId, Guid ownerUserId, CancellationToken ct)
    {
        Track? result = null;
        await InTransactionAsync(async () =>
        {
            var track = await db.Queryable<Track>().FirstAsync(t => t.Id == trackId && t.OwnerUserId == ownerUserId
                && t.PublishedAt == null && t.IsRecycled, ct);
            if (track is null) return;
            if (track.RecycleExpiresAtUtc <= DateTime.UtcNow) throw new LibraryWorkflowConflictException("The recycle retention has expired.");
            var item = await ItemForTrackAsync(track, ct);
            if ((await FindMatchesAsync(item, ownerUserId, ct)).Count > 0)
                throw new LibraryWorkflowConflictException("An active version already exists. Keep it or resolve the duplicate before restoring.");
            track.IsRecycled = false; track.RecycledAtUtc = null; track.RecycleExpiresAtUtc = null;
            await db.Updateable(track).ExecuteCommandAsync(ct);
            await EnsureLibraryReferenceAsync(ownerUserId, track.Id, ct);
            result = track;
        }, ct);
        return result is null ? null : await TrackDtoAsync(result, ct);
    }

    public async Task<LibraryWorkflowPageDto<LibraryWorkflowTrackDto>> GetRecycleBinAsync(Guid ownerUserId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Queryable<Track>().Where(t => t.OwnerUserId == ownerUserId && t.PublishedAt == null && t.IsRecycled);
        var total = await query.CountAsync(ct);
        var tracks = await query.OrderByDescending(t => t.RecycledAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = new List<LibraryWorkflowTrackDto>();
        foreach (var track in tracks) items.Add(await TrackDtoAsync(track, ct));
        return new(items, page, pageSize, total);
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>();
        await InTransactionAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var pending = await db.Queryable<LibraryWorkflowReview>().Where(r => r.Status == "pending").ToListAsync(ct);
            foreach (var review in pending)
            {
                if (review.ExpiresAtUtc <= now)
                {
                    review.Status = "expired"; review.UpdatedAtUtc = now;
                    await db.Updateable(review).ExecuteCommandAsync(ct); keys.Add(review.OriginalStorageKey);
                }
                else if (!review.ReminderSentAtUtc.HasValue && review.ExpiresAtUtc <= now.AddHours(options.Value.ReminderHours))
                {
                    if (review.Kind == "private")
                        await NotifyAsync(review.OwnerUserId, "上传版本即将到期", $"待确认记录将在 {review.ExpiresAtUtc:O} 到期。", review, ct);
                    else
                    {
                        var adminRoleId = await db.Queryable<ApplicationRole>().Where(r => r.NormalizedName == "ADMIN")
                            .Select(r => r.Id).FirstAsync(ct);
                        var admins = adminRoleId == Guid.Empty ? [] : await db.Queryable<ApplicationUserRole>()
                            .Where(r => r.RoleId == adminRoleId).Select(r => r.UserId).ToListAsync(ct);
                        foreach (var admin in admins.Distinct()) await NotifyAsync(admin, "公共候选即将到期", $"待审核记录将在 {review.ExpiresAtUtc:O} 到期。", review, ct);
                    }
                    review.ReminderSentAtUtc = now; await db.Updateable(review).ExecuteCommandAsync(ct);
                }
            }
            var expired = await db.Queryable<Track>().Where(t => t.IsRecycled && t.RecycleExpiresAtUtc != null && t.RecycleExpiresAtUtc <= now).ToListAsync(ct);
            foreach (var track in expired)
            {
                var assets = await db.Queryable<MediaAsset>().Where(a => a.TrackId == track.Id).ToListAsync(ct);
                foreach (var asset in assets) keys.Add(asset.StorageKey);
                await DeleteTrackRowsAsync(track.Id, ct);
            }
        }, ct);
        foreach (var key in keys) await ReleaseUnreferencedAsync(key, ct);
    }

    private async Task<LibraryWorkflowReview?> EnqueueCoreAsync(Guid owner, Guid trackId, LibraryImportItem item, CancellationToken ct)
    {
        if (!item.ParticipatesInPublicReview || !LibraryWorkflowPolicy.CanNominate(item, options.Value)) return null;
        var asset = await OriginalAsync(trackId, ct);
        if (asset is null || !paths.TryResolve(asset.StorageKey, out var path) || !File.Exists(path))
            throw new LibraryWorkflowConflictException("A retained original is required for public nomination.");
        // Snapshot the exact retained bytes instead of keeping a pointer to a future
        // private upgrade. A public review remains reproducible until resolved.
        item.OriginalStorageKey = asset.StorageKey;
        item.ContentHash = asset.ContentHash;
        var pending = await db.Queryable<LibraryWorkflowReview>().FirstAsync(r => r.Kind == "public"
            && r.OwnerUserId == owner && r.SourceTrackId == trackId && r.ContentHash == asset.ContentHash && r.Status == "pending", ct);
        if (pending is not null) return pending;
        var matches = await FindMatchesAsync(item, null, ct);
        return await CreateReviewAsync(item, owner, "public", trackId, matches.FirstOrDefault()?.Id,
            matches.Count == 0 ? "new_public" : "public_comparison", ct);
    }

    private async Task<LibraryWorkflowReview> CreateReviewAsync(LibraryImportItem item, Guid owner, string kind,
        Guid? sourceTrackId, Guid? existingTrackId, string reason, CancellationToken ct)
    {
        var pending = await db.Queryable<LibraryWorkflowReview>().FirstAsync(r => r.ImportItemId == item.Id && r.Kind == kind && r.Status == "pending", ct);
        if (pending is not null) return pending;
        await EnsurePrivateCapacityAsync(item, owner, ct);
        var review = new LibraryWorkflowReview
        {
            Kind = kind, OwnerUserId = owner, ImportItemId = item.Id, SourceTrackId = sourceTrackId,
            ExistingTrackId = existingTrackId, OriginalStorageKey = item.OriginalStorageKey!, ContentHash = item.ContentHash,
            ReviewReason = reason, ExpiresAtUtc = DateTime.UtcNow.AddDays(kind == "private" ? options.Value.PrivateReviewDays : options.Value.PublicReviewDays)
        };
        await db.Insertable(review).ExecuteCommandAsync(ct);
        return review;
    }

    private async Task<LibraryWorkflowReview?> AuthorizedReviewAsync(Guid id, Guid? owner, CancellationToken ct) => owner.HasValue
        ? await db.Queryable<LibraryWorkflowReview>().FirstAsync(r => r.Id == id && r.Kind == "private" && r.OwnerUserId == owner.Value, ct)
        : await db.Queryable<LibraryWorkflowReview>().FirstAsync(r => r.Id == id && r.Kind == "public", ct);

    private async Task<List<Track>> FindMatchesAsync(LibraryImportItem item, Guid? owner, CancellationToken ct)
    {
        var query = db.Queryable<Track>().Where(t => !t.IsRecycled);
        query = owner.HasValue ? query.Where(t => t.OwnerUserId == owner.Value && t.PublishedAt == null) : query.Where(t => t.PublishedAt != null);
        // Query by title first, then preserve version markers and artists in the
        // decision. No global hash match can collapse a user's private library.
        var title = item.Title?.Trim() ?? Path.GetFileNameWithoutExtension(item.DisplayName);
        var rows = await query.Where(t => t.Title == title || (item.Isrc != null && t.Isrc == item.Isrc)).ToListAsync(ct);
        var result = new List<Track>();
        var incomingArtists = Deserialize(item.ArtistsJson).Select(LibraryWorkflowPolicy.Normalize).ToHashSet();
        foreach (var track in rows)
        {
            var artists = await ArtistsAsync(track.Id, ct);
            if ((!string.IsNullOrWhiteSpace(item.Isrc) && item.Isrc == track.Isrc)
                || incomingArtists.Count == 0 || artists.Count == 0
                || artists.Any(a => incomingArtists.Contains(LibraryWorkflowPolicy.Normalize(a)))) result.Add(track);
        }
        return result;
    }

    private async Task<bool> IsSameRecordingAsync(LibraryImportItem item, Track track, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.Isrc) && !string.IsNullOrWhiteSpace(track.Isrc)) return item.Isrc == track.Isrc;
        if (LibraryWorkflowPolicy.Normalize(item.Title) != LibraryWorkflowPolicy.Normalize(track.Title)
            || item.DurationMs is null || track.DurationMs is null || Math.Abs(item.DurationMs.Value - track.DurationMs.Value) > 2000) return false;
        var artists = await ArtistsAsync(track.Id, ct);
        var incoming = Deserialize(item.ArtistsJson).Select(LibraryWorkflowPolicy.Normalize).ToHashSet();
        return incoming.Count > 0 && artists.Any(a => incoming.Contains(LibraryWorkflowPolicy.Normalize(a)));
    }

    private async Task<Track> CreateTrackAsync(LibraryImportItem item, Guid? owner, bool published, CancellationToken ct)
    {
        if (owner.HasValue && !published) await EnsurePrivateCapacityAsync(item, owner.Value, ct);
        var title = (item.Title ?? Path.GetFileNameWithoutExtension(item.DisplayName)).Trim();
        if (title.Length == 0) title = "未命名曲目";
        var track = new Track
        {
            Title = title[..Math.Min(300, title.Length)], OwnerUserId = owner,
            PublishedAt = published ? DateTimeOffset.UtcNow : null, DurationMs = item.DurationMs,
            TrackNumber = item.TrackNumber, DiscNumber = item.DiscNumber, ReleaseYear = item.ReleaseYear,
            ReleaseDate = item.ReleaseDate, Isrc = item.Isrc, AlbumArtistsJson = item.AlbumArtistsJson,
            OriginalGenresJson = item.OriginalGenresJson, MetadataAuditJson = item.MetadataAuditJson
        };
        await db.Insertable(track).ExecuteCommandAsync(ct);
        await CopyMetadataAsync(track, item, ct);
        await db.Insertable(AssetFor(item, track.Id)).ExecuteCommandAsync(ct);
        if (owner.HasValue) await EnsureLibraryReferenceAsync(owner.Value, track.Id, ct);
        return track;
    }

    private async Task ReplaceOriginalAsync(Track track, LibraryImportItem item, CancellationToken ct)
    {
        if (track.OwnerUserId.HasValue && !track.PublishedAt.HasValue)
            await EnsurePrivateCapacityAsync(item, track.OwnerUserId.Value, ct);
        var old = await db.Queryable<MediaAsset>().Where(a => a.TrackId == track.Id).ToListAsync(ct);
        // Keep inactive rows until the sweeper safely checks shared references.
        await db.Updateable<MediaAsset>().SetColumns(a => a.IsPrimary == false).SetColumns(a => a.IsPublished == false)
            .SetColumns(a => a.RetainUntilUtc == DateTime.UtcNow).Where(a => a.TrackId == track.Id && a.StorageKey != item.OriginalStorageKey).ExecuteCommandAsync(ct);
        await db.Insertable(AssetFor(item, track.Id)).ExecuteCommandAsync(ct);
        track.DurationMs = item.DurationMs ?? track.DurationMs;
        await db.Updateable(track).ExecuteCommandAsync(ct);
    }

    private async Task EnsurePrivateCapacityAsync(LibraryImportItem item, Guid owner, CancellationToken ct)
    {
        if (options.Value.MaxPrivateOriginalBytesPerUser == 0) return;
        if (!paths.TryResolve(item.OriginalStorageKey ?? string.Empty, out var source) || !File.Exists(source))
            throw new MediaProcessingException(ErrorCodes.PrivateStorageUsageUnavailable, "The retained original storage usage is unavailable.");
        long bytes;
        try { bytes = new FileInfo(source).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new MediaProcessingException(ErrorCodes.PrivateStorageUsageUnavailable, "The retained original storage usage is unavailable."); }
        // Allocation and publication share the same mutation/advisory lock, including across workers.
        await PrivateOriginalStorageMeter.EnsureCapacityAsync(db, paths, owner, item.ContentHash,
            bytes, options.Value.MaxPrivateOriginalBytesPerUser, ct, item.OriginalStorageKey);
    }

    private static MediaAsset AssetFor(LibraryImportItem item, Guid trackId) => new()
    {
        TrackId = trackId, StorageKey = item.OriginalStorageKey!, Container = item.Container ?? "unknown", Codec = item.Codec ?? "unknown",
        ContentHash = item.ContentHash, BitrateKbps = item.BitrateKbps, SampleRateHz = item.SampleRateHz,
        BitsPerSample = item.BitsPerSample, Channels = item.Channels, CodecProfile = item.CodecProfile,
        IsOriginal = true, IsPrimary = true, IsPublished = true
    };

    private async Task CopyMetadataAsync(Track track, LibraryImportItem item, CancellationToken ct)
    {
        foreach (var name in Deserialize(item.ArtistsJson).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = name.ToUpperInvariant();
            var artist = await db.Queryable<Artist>().FirstAsync(a => a.Name.ToUpper() == normalized, ct);
            if (artist is null) { artist = new Artist { Name = name }; await db.Insertable(artist).ExecuteCommandAsync(ct); }
            await db.Insertable(new TrackArtist { TrackId = track.Id, ArtistId = artist.Id }).ExecuteCommandAsync(ct);
        }
        if (!string.IsNullOrWhiteSpace(item.AlbumTitle))
        {
            var normalized = item.AlbumTitle.Trim().ToUpperInvariant();
            var album = await db.Queryable<Album>().FirstAsync(a => a.Title.ToUpper() == normalized, ct);
            if (album is null) { album = new Album { Title = item.AlbumTitle.Trim(), ReleaseYear = item.ReleaseYear }; await db.Insertable(album).ExecuteCommandAsync(ct); }
            track.AlbumId = album.Id; await db.Updateable(track).ExecuteCommandAsync(ct);
        }
        foreach (var name in Deserialize(item.GenresJson).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = name.ToUpperInvariant();
            var genre = await db.Queryable<Genre>().FirstAsync(g => g.Name.ToUpper() == normalized, ct);
            if (genre is null) { genre = new Genre { Name = name }; await db.Insertable(genre).ExecuteCommandAsync(ct); }
            await db.Insertable(new TrackGenre { TrackId = track.Id, GenreId = genre.Id }).ExecuteCommandAsync(ct);
        }
        if (!string.IsNullOrWhiteSpace(item.Lyrics))
            await db.Insertable(new Lyric { TrackId = track.Id, Content = item.Lyrics, IsDefault = true,
                Format = item.Lyrics.Contains('[') ? LyricFormats.Lrc : LyricFormats.Plain,
                Source = item.LyricsSource ?? LyricsSources.Manual, Confidence = item.LyricsConfidence }).ExecuteCommandAsync(ct);
        if (item.ArtworkStorageKey is not null && quarantine.TryResolve(item.ArtworkStorageKey, out var source) && File.Exists(source))
        {
            var key = $"artwork/{Guid.NewGuid():N}{Path.GetExtension(source)}";
            if (paths.TryResolve(key, out var target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, false);
                var art = new Artwork { StorageKey = key, ContentType = "image/jpeg" }; await db.Insertable(art).ExecuteCommandAsync(ct);
                track.ArtworkId = art.Id; await db.Updateable(track).ExecuteCommandAsync(ct);
            }
        }
    }

    private async Task<LibraryImportItem> ItemForTrackAsync(Track track, CancellationToken ct)
    {
        var asset = await OriginalAsync(track.Id, ct) ?? throw new LibraryWorkflowConflictException("The original audio is unavailable.");
        var batch = new LibraryImportBatch { CreatedByUserId = track.OwnerUserId ?? Guid.Empty, Status = LibraryImportStatuses.Completed };
        await db.Insertable(batch).ExecuteCommandAsync(ct);
        var artists = await ArtistsAsync(track.Id, ct);
        var album = track.AlbumId.HasValue ? await db.Queryable<Album>().FirstAsync(a => a.Id == track.AlbumId.Value, ct) : null;
        var item = new LibraryImportItem
        {
            BatchId = batch.Id, IsUserUpload = true, DisplayName = track.Title, Title = track.Title,
            SourceStorageKey = string.Empty, OriginalStorageKey = asset.StorageKey, ContentHash = asset.ContentHash,
            Container = asset.Container, Codec = asset.Codec, BitrateKbps = asset.BitrateKbps, SampleRateHz = asset.SampleRateHz,
            BitsPerSample = asset.BitsPerSample, Channels = asset.Channels, CodecProfile = asset.CodecProfile,
            DurationMs = track.DurationMs, ArtistsJson = JsonSerializer.Serialize(artists), AlbumTitle = album?.Title,
            Isrc = track.Isrc, PublishedTrackId = track.Id, Status = LibraryImportItemStatuses.Published,
            ReleaseYear = track.ReleaseYear, ReleaseDate = track.ReleaseDate, AlbumArtistsJson = track.AlbumArtistsJson
        };
        await db.Insertable(item).ExecuteCommandAsync(ct);
        return item;
    }

    private async Task<LibraryWorkflowReviewDto> ToDtoAsync(LibraryWorkflowReview review, CancellationToken ct)
    {
        var item = await db.Queryable<LibraryImportItem>().FirstAsync(i => i.Id == review.ImportItemId, ct);
        var existing = review.ExistingTrackId.HasValue ? await db.Queryable<Track>().FirstAsync(t => t.Id == review.ExistingTrackId.Value, ct) : null;
        var asset = existing is not null ? await OriginalAsync(existing.Id, ct) : null;
        return new(review.Id, review.Kind, review.Status, item?.Title ?? item?.DisplayName ?? "", Deserialize(item?.ArtistsJson), item?.AlbumTitle,
            item?.DurationMs, item?.Container, item?.Codec, item?.BitrateKbps, item?.SampleRateHz, item?.BitsPerSample, item?.Channels,
            item?.CodecProfile, review.ExpiresAtUtc, review.SourceTrackId, review.ExistingTrackId, existing?.Title,
            existing is not null ? await ArtistsAsync(existing.Id, ct) : [], asset?.Codec, asset?.BitrateKbps, review.ReviewReason, review.ResolutionReason);
    }

    private async Task<LibraryWorkflowTrackDto> TrackDtoAsync(Track track, CancellationToken ct) => new(track.Id, track.Title,
        await ArtistsAsync(track.Id, ct), track.PublishedAt.HasValue, track.AddedAtUtc, track.RecycleExpiresAtUtc);

    private Task<MediaAsset?> OriginalAsync(Guid trackId, CancellationToken ct) => db.Queryable<MediaAsset>()
        .Where(a => a.TrackId == trackId && a.IsPublished && a.IsOriginal).OrderByDescending(a => a.IsPrimary).FirstAsync(ct)!;

    private async Task<List<string>> ArtistsAsync(Guid trackId, CancellationToken ct) => await db.Queryable<TrackArtist>()
        .LeftJoin<Artist>((link, artist) => link.ArtistId == artist.Id).Where((link, artist) => link.TrackId == trackId)
        .Select((link, artist) => artist.Name).ToListAsync(ct);

    private async Task EnsureLibraryReferenceAsync(Guid owner, Guid trackId, CancellationToken ct)
    {
        if (!await db.Queryable<UserLibraryItem>().AnyAsync(r => r.UserId == owner && r.TrackId == trackId, ct))
            await db.Insertable(new UserLibraryItem { UserId = owner, TrackId = trackId }).ExecuteCommandAsync(ct);
    }

    private async Task SaveOriginalAsync(string source, string key, string hash, CancellationToken ct)
    {
        if (!paths.TryResolve(key, out var target)) throw new InvalidOperationException("The original audio path is invalid.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
        {
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var output = File.Create(temporary))
                await using (var input = File.OpenRead(source)) await input.CopyToAsync(output, ct);
                try { File.Move(temporary, target, false); }
                catch (IOException) when (File.Exists(target)) { /* Another attempt stored the same immutable content. */ }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        await using var check = File.OpenRead(target);
        if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(check, ct)), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The stored original did not match the uploaded audio.");
    }

    private async Task ReleaseUnreferencedAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key)) return;
        await InTransactionAsync(async () =>
        {
            if (await db.Queryable<MediaAsset>().AnyAsync(a => a.StorageKey == key, ct)
                || await db.Queryable<LibraryWorkflowReview>().AnyAsync(r => r.OriginalStorageKey == key && r.Status == "pending", ct)) return;
            if (paths.TryResolve(key, out var path))
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Original cleanup will retry."); }
        }, ct);
    }

    private async Task DeleteTrackRowsAsync(Guid id, CancellationToken ct)
    {
        await db.Deleteable<MediaAsset>().Where(a => a.TrackId == id).ExecuteCommandAsync(ct);
        await db.Deleteable<Lyric>().Where(a => a.TrackId == id).ExecuteCommandAsync(ct);
        await db.Deleteable<UserLibraryItem>().Where(a => a.TrackId == id).ExecuteCommandAsync(ct);
        await db.Deleteable<UserPlaylistTrack>().Where(a => a.TrackId == id).ExecuteCommandAsync(ct);
        await db.Deleteable<TrackArtist>().Where(a => a.TrackId == id).ExecuteCommandAsync(ct);
        await db.Deleteable<TrackGenre>().Where(a => a.TrackId == id).ExecuteCommandAsync(ct);
        await db.Deleteable<Favorite>().Where(a => a.TrackId == id).ExecuteCommandAsync(ct);
        await db.Deleteable<CollectionTrack>().Where(a => a.TrackId == id).ExecuteCommandAsync(ct);
        await db.Deleteable<Track>().Where(a => a.Id == id).ExecuteCommandAsync(ct);
    }

    private Task<int> NotifyAsync(Guid userId, string title, string body, LibraryWorkflowReview review, CancellationToken ct) =>
        db.Insertable(new UserNotification { UserId = userId, Title = title, Body = body, ResourceType = "library_review",
            ResourceId = review.Id.ToString(), LinkUrl = review.Kind == "private" ? "/pages/library/reviews" : "/library-candidates" }).ExecuteCommandAsync(ct);

    private async Task InTransactionAsync(Func<Task> action, CancellationToken ct)
    {
        await MutationGate.WaitAsync(ct);
        try
        {
            await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                if (db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
                    await db.Ado.ExecuteCommandAsync($"SELECT pg_advisory_xact_lock({MutationLockId})");
                await action();
                await db.Ado.CommitTranAsync();
            }
            catch { await db.Ado.RollbackTranAsync(); throw; }
        }
        finally { MutationGate.Release(); }
    }

    private static List<string> Deserialize(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json ?? "[]")?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() ?? []; }
        catch (JsonException) { return []; }
    }
    private static string? SafeReason(string? reason) => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 1000)];
    private static string SafeExtension(string? container) => container?.ToLowerInvariant() switch
    {
        "mp3" => ".mp3", "flac" => ".flac", "mp4" or "m4a" => ".m4a", "wav" or "wave" => ".wav",
        "aac" => ".aac", "ogg" or "oga" => ".ogg", "aiff" or "aif" => ".aiff", "ape" => ".ape", "wv" => ".wv", _ => ".audio"
    };
}
