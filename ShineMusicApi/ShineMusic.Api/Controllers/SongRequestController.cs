using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Domain;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>Serves public song requests and authenticated voting and moderation operations.</summary>
[ApiController]
[Route("api/v1")]
[FeatureGate(nameof(FeatureOptions.SongRequests))]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class SongRequestController : ControllerBase
{
    [HttpGet("song-requests", Name = "GetSongRequests")]
    [Tags("Song Requests")]
    public async Task<IResult> GetRequests(
        ISqlSugarClient db,
        string? sort,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var error, out var pageNumber, out var size))
            return error!;
        if (!TryGetSort(sort, out var sortBy))
            return ApiResults.ValidationError(HttpContext, "sort", "sort must be votes or createdAt.");

        var userId = TryGetUserId(HttpContext.User, out var authenticatedUserId) ? authenticatedUserId : (Guid?)null;
        // 公共需求池保留已满足/已归档的需求，但沉到列表底部；被拒绝/撤回的记录不展示。
        var query = db.Queryable<SongRequestRecord>()
            .Where(request =>
                request.Status != SongRequestStatuses.Declined &&
                request.Status != SongRequestStatuses.Withdrawn);
        var total = await query.CountAsync(cancellationToken);
        // 排序优先级：进行中 > 已满足 > 已归档（false 排在 true 前），再按用户选择的票数/时间排序。
        // 票数排序使用子查询计数（SqlSugar 无导航属性，Votes.Count 改由 Subqueryable 表达）。
        var ordered = sortBy == "createdat"
            ? query
                .OrderBy(request => request.ArchivedAtUtc != null)
                .OrderBy(request => request.Status == SongRequestStatuses.Fulfilled)
                .OrderByDescending(request => request.CreatedAtUtc)
                .OrderBy(request => request.Id)
            : query
                .OrderBy(request => request.ArchivedAtUtc != null)
                .OrderBy(request => request.Status == SongRequestStatuses.Fulfilled)
                .OrderByDescending(request => SqlFunc.Subqueryable<SongRequestVote>().Where(vote => vote.RequestId == request.Id).Count())
                .OrderByDescending(request => request.CreatedAtUtc);
        var requests = await ordered.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = await ToDtosAsync(db, requests, userId, cancellationToken);
        return Results.Ok(new PageResult<SongRequestDto>(items, pageNumber, size, total));
    }

    [HttpGet("song-requests/{requestId:guid}", Name = "GetSongRequest")]
    [Tags("Song Requests")]
    public async Task<IResult> GetRequest(
        Guid requestId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        var userId = TryGetUserId(HttpContext.User, out var authenticatedUserId) ? authenticatedUserId : (Guid?)null;
        var request = await db.Queryable<SongRequestRecord>().FirstAsync(item => item.Id == requestId, cancellationToken);
        if (request is null)
            return ApiResults.NotFound(HttpContext);
        var items = await ToDtosAsync(db, [request], userId, cancellationToken);
        return Results.Ok(items[0]);
    }

    [HttpPost("song-requests", Name = "CreateSongRequest")]
    [Authorize]
    [Tags("Song Requests")]
    public async Task<IResult> CreateRequest(
        CreateSongRequestRequest request,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(HttpContext.User, out var userId))
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
            return ApiResults.ValidationError(HttpContext, "title", "Title is required and must be at most 200 characters.");
        if (string.IsNullOrWhiteSpace(request.ArtistName) || request.ArtistName.Trim().Length > 200)
            return ApiResults.ValidationError(HttpContext, "artistName", "Artist name is required and must be at most 200 characters.");
        if (request.AlbumTitle?.Length > 200 || request.Note?.Length > 1000)
            return ApiResults.ValidationError(HttpContext, "details", "Album title must be at most 200 characters and note at most 1000 characters.");

        var title = request.Title.Trim();
        var artist = request.ArtistName.Trim();
        var album = NormalizeOptional(request.AlbumTitle);
        var normalizedTitle = Normalize(title);
        var normalizedArtist = Normalize(artist);
        var normalizedAlbum = album is null ? null : Normalize(album);

        var alreadyInLibrary = await CatalogQueries.PublishedTracks(db).AnyAsync(track =>
            track.Title.ToUpper() == normalizedTitle &&
            SqlFunc.Subqueryable<TrackArtist>().LeftJoin<Artist>((link, linkArtist) => link.ArtistId == linkArtist.Id)
                .Where((link, linkArtist) => link.TrackId == track.Id && linkArtist.Name.ToUpper() == normalizedArtist)
                .Any(), cancellationToken);
        if (alreadyInLibrary)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "This song is already in the library.", HttpContext.TraceIdentifier)));

        var duplicate = await db.Queryable<SongRequestRecord>().FirstAsync(item =>
            item.NormalizedTitle == normalizedTitle &&
            item.NormalizedArtistName == normalizedArtist &&
            item.NormalizedAlbumTitle == normalizedAlbum &&
            item.Status != SongRequestStatuses.Declined &&
            item.Status != SongRequestStatuses.Withdrawn, cancellationToken);
        if (duplicate is not null)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "An active request for this song already exists.", HttpContext.TraceIdentifier)));

        var now = DateTime.UtcNow;
        var entity = new SongRequestRecord
        {
            Title = title,
            NormalizedTitle = normalizedTitle,
            ArtistName = artist,
            NormalizedArtistName = normalizedArtist,
            AlbumTitle = album,
            NormalizedAlbumTitle = normalizedAlbum,
            Note = NormalizeOptional(request.Note),
            CreatedByUserId = userId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        // 需求记录 + 时间线起点事件同一事务提交，保证轨迹完整。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Insertable(entity).ExecuteCommandAsync(cancellationToken);
            SongRequestEvents.Add(db, entity, SongRequestEventKinds.Submitted, null, SongRequestStatuses.Open, userId, SongRequestActorKinds.User);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.Created($"/api/v1/song-requests/{entity.Id}", ToDto(entity, 0, false, true));
    }

    /// <summary>
    /// 为需求点赞。产品规则：一人一条需求只能点一次，且**不可取消**，
    /// 因此没有对应的取消投票端点；重复调用幂等（已点过直接返回 204，不重复计数）。
    /// </summary>
    [HttpPut("song-requests/{requestId:guid}/votes/me", Name = "VoteForSongRequest")]
    [Authorize]
    [Tags("Song Requests")]
    public async Task<IResult> AddVote(
        Guid requestId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(HttpContext.User, out var userId))
            return Results.Unauthorized();
        var request = await db.Queryable<SongRequestRecord>().FirstAsync(item => item.Id == requestId, cancellationToken);
        if (request is null)
            return ApiResults.NotFound(HttpContext);
        if (request.Status is SongRequestStatuses.Fulfilled or SongRequestStatuses.Declined or SongRequestStatuses.Withdrawn)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "Voting is closed for this request.", HttpContext.TraceIdentifier)));

        // 已点过：幂等返回成功，既不报错也不新增计数（点赞不可取消，重复点击无副作用）。
        if (!await db.Queryable<SongRequestVote>().AnyAsync(vote => vote.RequestId == requestId && vote.UserId == userId, cancellationToken))
        {
            try
            {
                await db.Insertable(new SongRequestVote { RequestId = requestId, UserId = userId }).ExecuteCommandAsync(cancellationToken);
            }
            catch (Exception)
            {
                // 并发重复点赞命中 (RequestId, UserId) 复合主键：视为已点赞，保持幂等。
            }
        }
        return Results.NoContent();
    }

    [HttpGet("me/song-requests", Name = "GetMySongRequests")]
    [Authorize]
    [Tags("Song Requests")]
    public async Task<IResult> GetMyRequests(
        ISqlSugarClient db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(HttpContext.User, out var userId))
            return Results.Unauthorized();
        if (!TryGetPaging(HttpContext, page, pageSize, out var error, out var pageNumber, out var size))
            return error!;
        var query = db.Queryable<SongRequestRecord>()
            .Where(request => request.CreatedByUserId == userId ||
                SqlFunc.Subqueryable<SongRequestVote>().Where(vote => vote.RequestId == request.Id && vote.UserId == userId).Any());
        var total = await query.CountAsync(cancellationToken);
        var requests = await query.OrderByDescending(request => request.UpdatedAtUtc)
            .Skip((pageNumber - 1) * size).Take(size)
            .ToListAsync(cancellationToken);
        var items = await ToDtosAsync(db, requests, userId, cancellationToken);
        return Results.Ok(new PageResult<SongRequestDto>(items, pageNumber, size, total));
    }

    [HttpDelete("song-requests/{requestId:guid}", Name = "WithdrawSongRequest")]
    [Authorize]
    [Tags("Song Requests")]
    public async Task<IResult> WithdrawRequest(
        Guid requestId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(HttpContext.User, out var userId))
            return Results.Unauthorized();
        var request = await db.Queryable<SongRequestRecord>().FirstAsync(item => item.Id == requestId, cancellationToken);
        if (request is null)
            return ApiResults.NotFound(HttpContext);

        var isAdmin = HttpContext.User.IsInRole("admin");
        // 非本人且非管理员：按不存在处理，避免泄露记录存在性。
        if (!isAdmin && request.CreatedByUserId != userId)
            return ApiResults.NotFound(HttpContext);

        // 已结束（已满足/已拒绝/已撤回）的记录不可再撤回。
        if (request.Status is SongRequestStatuses.Fulfilled or SongRequestStatuses.Declined or SongRequestStatuses.Withdrawn)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "This request can no longer be withdrawn.", HttpContext.TraceIdentifier)));

        // 提交人只能在 open 阶段撤回；管理员可在 open/planned 阶段代为撤回（尚未启动处理，避免导入任务孤儿）。
        if (!isAdmin && request.Status != SongRequestStatuses.Open)
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "Only open requests can be withdrawn by the submitter.", HttpContext.TraceIdentifier)));
        if (isAdmin && request.Status is not (SongRequestStatuses.Open or SongRequestStatuses.Planned))
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, "Only open or planned requests can be withdrawn.", HttpContext.TraceIdentifier)));

        var previousStatus = request.Status;
        request.Status = SongRequestStatuses.Withdrawn;
        request.UpdatedAtUtc = DateTime.UtcNow;
        // 状态变更 + 时间线事件 + 审计/站内信同一事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(request).UpdateColumns(item => new { item.Status, item.UpdatedAtUtc }).ExecuteCommandAsync(cancellationToken);
            SongRequestEvents.Add(db, request, SongRequestEventKinds.Withdrawn, previousStatus, SongRequestStatuses.Withdrawn,
                userId, isAdmin ? SongRequestActorKinds.Admin : SongRequestActorKinds.User);
            if (isAdmin)
            {
                AdminAudit.Add(HttpContext, db, "song_request.withdrawn", "song_request", request.Id, new { byAdmin = true });
                // 管理员代为撤回时通知提交人，避免需求凭空消失。
                NotificationWriter.Push(db, request.CreatedByUserId, NotificationKinds.SongRequestWithdrawn,
                    $"需求《{request.Title}》已被管理员撤回",
                    "如需继续，可在需求池重新提交。",
                    NotificationWriter.RequestsPageUrl, "song_request", request.Id.ToString());
            }
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return Results.NoContent();
    }

    /// <summary>
    /// 管理员重新开启一条已结束的需求：declined/withdrawn → open，清空当前处理备注
    /// （拒绝/撤回原因保留在时间线中），写审计并通知提交人。
    /// 用于「当时缺音源，现在找到了」「误撤回后恢复」这类回补场景。
    /// </summary>
    [HttpPost("admin/song-requests/{requestId:guid}/reopen", Name = "ReopenSongRequest")]
    [Authorize(Roles = "admin")]
    [Tags("Admin Song Requests")]
    public async Task<IResult> ReopenRequest(
        Guid requestId,
        ReopenSongRequestRequest reopen,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(HttpContext.User, out var adminUserId))
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(reopen.Reason) || reopen.Reason.Trim().Length > 2000)
            return ApiResults.ValidationError(HttpContext, "reason", "A reason is required and must be at most 2000 characters.");

        var request = await db.Queryable<SongRequestRecord>().FirstAsync(item => item.Id == requestId, cancellationToken);
        if (request is null)
            return ApiResults.NotFound(HttpContext);
        // 已拒绝/已撤回的需求可以重新开启；已入库属于不可逆结果。
        if (request.Status is not (SongRequestStatuses.Declined or SongRequestStatuses.Withdrawn))
            return Results.Conflict(new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict,
                "Only declined or withdrawn requests can be reopened.", HttpContext.TraceIdentifier)));

        var reason = reopen.Reason.Trim();
        var previousStatus = request.Status;
        var previousNote = request.AdminNote;
        request.Status = SongRequestStatuses.Open;
        request.AdminNote = null;
        request.UpdatedAtUtc = DateTime.UtcNow;
        // 状态变更 + 时间线事件 + 审计 + 站内信同一事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(request).UpdateColumns(item => new { item.Status, item.AdminNote, item.UpdatedAtUtc }).ExecuteCommandAsync(cancellationToken);
            SongRequestEvents.Add(db, request, SongRequestEventKinds.Reopened, previousStatus, SongRequestStatuses.Open,
                adminUserId, SongRequestActorKinds.Admin, reason);
            AdminAudit.Add(HttpContext, db, "song_request.reopened", "song_request", request.Id, new { reason, previousNote });
            NotificationWriter.Push(db, request.CreatedByUserId, NotificationKinds.SongRequestReopened,
                $"需求《{request.Title}》已重新开启",
                $"管理员重新受理了这条需求：{reason}",
                NotificationWriter.RequestsPageUrl, "song_request", request.Id.ToString());
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        var voteCount = await db.Queryable<SongRequestVote>().Where(vote => vote.RequestId == requestId).CountAsync(cancellationToken);
        return Results.Ok(ToDto(request, voteCount, false, request.CreatedByUserId == adminUserId));
    }

    /// <summary>
    /// 需求处理时间线：按时间正序返回状态事件，供卡片展开查看「提交 → 纳入计划 → 处理中 → 已入库」。
    /// 备注（拒绝原因/重新开启原因）仅对提交人本人与管理员可见。
    /// </summary>
    [HttpGet("song-requests/{requestId:guid}/timeline", Name = "GetSongRequestTimeline")]
    [Tags("Song Requests")]
    public async Task<IResult> GetTimeline(
        Guid requestId,
        ISqlSugarClient db,
        CancellationToken cancellationToken)
    {
        var request = await db.Queryable<SongRequestRecord>()
            .Where(item => item.Id == requestId)
            .Select(item => new { item.Id, item.CreatedAtUtc, item.CreatedByUserId, item.Status })
            .FirstAsync(cancellationToken);
        if (request is null)
            return ApiResults.NotFound(HttpContext);

        var viewerId = TryGetUserId(HttpContext.User, out var authenticatedUserId) ? authenticatedUserId : (Guid?)null;
        var canSeeNotes = HttpContext.User.IsInRole("admin") || (viewerId.HasValue && request.CreatedByUserId == viewerId.Value);

        var events = await db.Queryable<SongRequestEventRecord>()
            .Where(evt => evt.RequestId == requestId)
            .OrderBy(evt => evt.CreatedAtUtc)
            .OrderBy(evt => evt.Id)
            .Select(evt => new { evt.Id, evt.Kind, evt.FromStatus, evt.ToStatus, evt.ActorKind, evt.Note, evt.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        // 历史数据兜底：事件表上线前创建的记录没有事件，用创建时间合成一条「提交」事件。
        var timeline = events.Count > 0
            ? events.Select(evt => new SongRequestTimelineItemDto(
                evt.Id,
                evt.Kind,
                evt.FromStatus,
                evt.ToStatus,
                evt.ActorKind,
                canSeeNotes ? evt.Note : null,
                AsUtc(evt.CreatedAtUtc))).ToList()
            :
            [
                new SongRequestTimelineItemDto(
                    request.Id,
                    SongRequestEventKinds.Submitted,
                    null,
                    SongRequestStatuses.Open,
                    SongRequestActorKinds.User,
                    null,
                    AsUtc(request.CreatedAtUtc))
            ];

        return Results.Ok(timeline);
    }

    [HttpGet("admin/song-requests", Name = "GetAdminSongRequests")]
    [Authorize(Roles = "admin")]
    [Tags("Admin Song Requests")]
    public async Task<IResult> GetAdminRequests(
        ISqlSugarClient db,
        string? status,
        string? q,
        string? sort,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetPaging(HttpContext, page, pageSize, out var error, out var pageNumber, out var size))
            return error!;
        if (!TryGetSort(sort, out var sortBy))
            return ApiResults.ValidationError(HttpContext, "sort", "sort must be votes or createdAt.");
        var query = db.Queryable<SongRequestRecord>();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToLowerInvariant();
            if (!SongRequestStatuses.All.Contains(normalizedStatus))
                return ApiResults.ValidationError(HttpContext, "status", "The song request status is not supported.");
            query = query.Where(request => request.Status == normalizedStatus);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(request =>
                request.Title.Contains(term) ||
                request.ArtistName.Contains(term) ||
                (request.AlbumTitle != null && request.AlbumTitle.Contains(term)));
        }
        var total = await query.CountAsync(cancellationToken);
        var ordered = sortBy == "createdAt"
            ? query.OrderByDescending(request => request.CreatedAtUtc)
            : query
                .OrderByDescending(request => SqlFunc.Subqueryable<SongRequestVote>().Where(vote => vote.RequestId == request.Id).Count())
                .OrderByDescending(request => request.CreatedAtUtc);
        var requests = await ordered.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = await ToDtosAsync(db, requests, null, cancellationToken);
        return Results.Ok(new PageResult<SongRequestDto>(items, pageNumber, size, total));
    }

    [HttpPatch("admin/song-requests/{requestId:guid}", Name = "UpdateSongRequest")]
    [Authorize(Roles = "admin")]
    [Tags("Admin Song Requests")]
    public async Task<IResult> UpdateRequest(
        Guid requestId,
        UpdateSongRequestRequest update,
        ISqlSugarClient db,
        IOptions<SongRequestFulfillmentOptions> fulfillmentOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(update.Status) || !SongRequestStatuses.All.Contains(update.Status.Trim().ToLowerInvariant()))
            return ApiResults.ValidationError(HttpContext, "status", "A valid song request status is required.");
        if (update.AdminNote?.Length > 2000)
            return ApiResults.ValidationError(HttpContext, "adminNote", "Admin note must be at most 2000 characters.");

        var request = await db.Queryable<SongRequestRecord>().FirstAsync(item => item.Id == requestId, cancellationToken);
        if (request is null)
            return ApiResults.NotFound(HttpContext);
        var status = update.Status.Trim().ToLowerInvariant();

        // 状态流转白名单：禁止任意跳转（如 fulfilled→open），并强制拒绝时填写原因。
        if (status != request.Status && !SongRequestStateMachine.IsTransitionAllowed(request.Status, status))
            return ApiResults.ValidationError(HttpContext, "status", $"Cannot transition from {request.Status} to {status}.");
        if (status != request.Status && status == SongRequestStatuses.Declined && string.IsNullOrWhiteSpace(update.AdminNote))
            return ApiResults.ValidationError(HttpContext, "adminNote", "An admin note (reason) is required when declining a request.");

        if (status == SongRequestStatuses.Fulfilled && update.MatchedTrackId is null)
            return ApiResults.ValidationError(HttpContext, "matchedTrackId", "A published track must be linked when fulfilling a request.");
        if (update.MatchedTrackId is Guid trackId)
        {
            var track = await CatalogQueries.PublishedTracks(db).FirstAsync(candidate => candidate.Id == trackId, cancellationToken);
            if (track is null)
                return ApiResults.NotFound(HttpContext);
            // 水合艺人导航（SqlSugar 无 Include，联表数据按 Id 批量回填）。
            await CatalogQueries.HydrateTracksAsync(db, [track], cancellationToken);
            var trackArtistNames = track.TrackArtists.Select(link => link.Artist.Name).ToList();

            // 与「联动上传发布」同款校验：PATCH 直接关联曲目时也须确保曲目与需求一致，防止绕过。
            var matchCheck = fulfillmentOptions.Value.Check("TitleArtistMatch");
            if (matchCheck.IsEnabled && status == SongRequestStatuses.Fulfilled && request.Status != SongRequestStatuses.Fulfilled)
            {
                var verdict = SongRequestMatchValidator.Validate(track.Title, trackArtistNames, request, fulfillmentOptions.Value.TitleArtistMatch);
                if (verdict.Kind == MatchVerdictKind.Mismatch && !matchCheck.AllowAdminOverride)
                    return ApiResults.ValidationError(HttpContext, "matchedTrackId",
                        $"曲目《{verdict.TrackDisplay}》与需求《{verdict.RequestDisplay}》不一致，无法关联。");
            }
        }

        var previousStatus = request.Status;
        request.Status = status;
        // 备注仅在显式传入时覆盖：避免推进状态（未填备注）时把历史拒绝原因清空。
        if (update.AdminNote is not null)
            request.AdminNote = NormalizeOptional(update.AdminNote);
        request.MatchedTrackId = update.MatchedTrackId;
        request.UpdatedAtUtc = DateTime.UtcNow;
        // 状态变更 + 时间线事件 + 站内信 + 审计同一事务提交。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable(request)
                .UpdateColumns(item => new { item.Status, item.AdminNote, item.MatchedTrackId, item.UpdatedAtUtc })
                .ExecuteCommandAsync(cancellationToken);
            if (status != previousStatus)
            {
                // declined/withdrawn → open 属于「重新开启」，事件类型不能退化成「提交」。
                var eventKind = status == SongRequestStatuses.Open &&
                    (previousStatus is SongRequestStatuses.Declined or SongRequestStatuses.Withdrawn)
                    ? SongRequestEventKinds.Reopened
                    : SongRequestEvents.KindForStatus(status);
                SongRequestEvents.Add(db, request, eventKind, previousStatus, status,
                    TryGetUserId(HttpContext.User, out var eventActorId) ? eventActorId : (Guid?)null,
                    SongRequestActorKinds.Admin,
                    status == SongRequestStatuses.Declined ? request.AdminNote : null);
                PushStatusNotification(db, request, previousStatus, status);
            }
            AdminAudit.Add(HttpContext, db, "song_request.updated", "song_request", request.Id, new { status, update.MatchedTrackId });
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        var actingUserId = TryGetUserId(HttpContext.User, out var currentUserId) ? currentUserId : (Guid?)null;
        var voteCount = await db.Queryable<SongRequestVote>().Where(vote => vote.RequestId == requestId).CountAsync(cancellationToken);
        return Results.Ok(ToDto(request, voteCount, false, actingUserId.HasValue && request.CreatedByUserId == actingUserId.Value));
    }

    /// <summary>按目标状态向提交人投递站内信：仅对用户需要主动知晓的结果（拒绝/入库/重新开启）发信。</summary>
    private static void PushStatusNotification(ISqlSugarClient db, SongRequestRecord request, string previousStatus, string status)
    {
        switch (status)
        {
            case SongRequestStatuses.Declined:
                NotificationWriter.Push(db, request.CreatedByUserId, NotificationKinds.SongRequestDeclined,
                    $"需求《{request.Title}》暂不收录",
                    string.IsNullOrWhiteSpace(request.AdminNote) ? "管理员暂未收录这首歌。" : $"原因：{request.AdminNote}",
                    NotificationWriter.RequestsPageUrl, "song_request", request.Id.ToString());
                break;
            case SongRequestStatuses.Fulfilled when previousStatus != SongRequestStatuses.Fulfilled:
                NotificationWriter.Push(db, request.CreatedByUserId, NotificationKinds.SongRequestFulfilled,
                    $"你想要的《{request.Title}》已入库",
                    "现在可以在曲库里找到并播放了。",
                    NotificationWriter.RequestsPageUrl, "song_request", request.Id.ToString());
                break;
            case SongRequestStatuses.Open when previousStatus == SongRequestStatuses.Declined || previousStatus == SongRequestStatuses.Withdrawn:
                NotificationWriter.Push(db, request.CreatedByUserId, NotificationKinds.SongRequestReopened,
                    $"需求《{request.Title}》已重新开启",
                    "管理员重新受理了这条需求。",
                    NotificationWriter.RequestsPageUrl, "song_request", request.Id.ToString());
                break;
        }
    }

    /// <summary>
    /// 批量构造需求 DTO：票数与「我是否已投」各一次批量查询（GROUP BY / IN），避免逐行 N+1。
    /// </summary>
    private static async Task<List<SongRequestDto>> ToDtosAsync(
        ISqlSugarClient db, List<SongRequestRecord> requests, Guid? userId, CancellationToken cancellationToken)
    {
        var ids = requests.Select(request => request.Id).ToList();
        var voteCounts = await LoadVoteCountsAsync(db, ids, cancellationToken);
        var myVotes = userId.HasValue && ids.Count > 0
            ? (await db.Queryable<SongRequestVote>()
                .Where(vote => ids.Contains(vote.RequestId) && vote.UserId == userId.Value)
                .Select(vote => vote.RequestId)
                .ToListAsync(cancellationToken)).ToHashSet()
            : [];
        return requests
            .Select(request => ToDto(
                request,
                voteCounts.GetValueOrDefault(request.Id),
                myVotes.Contains(request.Id),
                userId.HasValue && request.CreatedByUserId == userId.Value))
            .ToList();
    }

    /// <summary>批量统计各需求的票数（DB 端 GROUP BY 聚合）。</summary>
    private static async Task<Dictionary<Guid, int>> LoadVoteCountsAsync(
        ISqlSugarClient db, List<Guid> requestIds, CancellationToken cancellationToken)
    {
        if (requestIds.Count == 0)
            return [];
        var rows = await db.Queryable<SongRequestVote>()
            .Where(vote => requestIds.Contains(vote.RequestId))
            .GroupBy(vote => vote.RequestId)
            .Select(vote => new { vote.RequestId, Count = SqlFunc.AggregateCount(vote.RequestId) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.RequestId, row => row.Count);
    }

    private static SongRequestDto ToDto(SongRequestRecord request, int voteCount, bool myVote, bool createdByMe) => new(
        request.Id,
        request.Title,
        request.ArtistName,
        request.AlbumTitle,
        request.Status,
        voteCount,
        myVote,
        request.MatchedTrackId,
        AsUtc(request.CreatedAtUtc),
        AsUtc(request.UpdatedAtUtc),
        createdByMe);


    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId) => Guid.TryParse(principal.FindFirstValue("sub"), out userId);
    private static bool TryGetPaging(HttpContext ctx, int? page, int? pageSize, out IResult? error, out int pageNumber, out int size)
    {
        pageNumber = page ?? 1;
        size = pageSize ?? 20;
        error = null;
        if (pageNumber < 1)
            error = ApiResults.ValidationError(ctx, "page", "page must be at least 1.");
        else if (size < 1 || size > 1000)
            error = ApiResults.ValidationError(ctx, "pageSize", "pageSize must be between 1 and 1000.");
        return error is null;
    }
    private static bool TryGetSort(string? sort, out string sortBy)
    {
        sortBy = string.IsNullOrWhiteSpace(sort) ? "votes" : sort.Trim().ToLowerInvariant();
        return sortBy is "votes" or "createdat";
    }
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
