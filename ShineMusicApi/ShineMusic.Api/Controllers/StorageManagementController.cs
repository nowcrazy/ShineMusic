using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Controllers;

/// <summary>
/// 管理员存储治理端点：曲库磁盘对账审计、孤儿文件回收、曲目物理删除。
/// 三个操作均只影响正式曲库根目录（data/library）下的文件，隔离区文件由导入流水线自行管理。
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "admin")]
[Tags("Admin Storage")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
public sealed class StorageManagementController : ControllerBase
{
    /// <summary>【GET /api/v1/admin/storage/audit】对账审计：输出孤儿/缺失/空目录明细（只读）。</summary>
    [HttpGet("storage/audit", Name = "GetStorageAudit")]
    public async Task<IResult> GetAudit(
        ISqlSugarClient db,
        StorageGovernanceService storage,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await storage.AuditAsync(db, cancellationToken));
    }

    /// <summary>
    /// 【POST /api/v1/admin/storage/cleanup】回收审计发现的孤儿文件并清理空目录。
    /// 幂等：无孤儿时返回全 0 结果。清理结果写入管理员审计日志。
    /// </summary>
    [HttpPost("storage/cleanup", Name = "CleanupStorage")]
    public async Task<IResult> Cleanup(
        ISqlSugarClient db,
        StorageGovernanceService storage,
        CancellationToken cancellationToken)
    {
        var result = await storage.CleanupAsync(db, cancellationToken);
        // 清理是批量破坏性操作，用一次性 Guid 作为本次清理的审计资源标识，与 track.deleted 等单资源操作区分。
        AdminAudit.Add(HttpContext, db, "storage.cleanup", "storage", Guid.NewGuid(), new
        {
            result.DeletedOrphanCount,
            result.DeletedBytes,
            result.RemovedEmptyDirectoryCount
        });
        return Results.Ok(result);
    }

    /// <summary>
    /// 【DELETE /api/v1/admin/tracks/{trackId}】物理删除曲目：数据库行与磁盘文件一并清理。
    /// 与其他曲目/专辑共享的封面会保留；删除结果写入管理员审计日志。
    /// </summary>
    [HttpDelete("tracks/{trackId:guid}", Name = "DeleteAdminTrack")]
    public async Task<IResult> DeleteTrack(
        Guid trackId,
        ISqlSugarClient db,
        StorageGovernanceService storage,
        CancellationToken cancellationToken)
    {
        var result = await storage.DeleteTrackAsync(db, trackId, cancellationToken);
        if (result is null)
            return Results.NotFound(new ApiErrorResponse(
                new ApiErrorDetail(ErrorCodes.ResourceNotFound, "The requested track was not found.", HttpContext.TraceIdentifier)));

        AdminAudit.Add(HttpContext, db, "track.deleted", "track", trackId, new
        {
            result.Title,
            result.DeletedFileCount,
            result.DeletedBytes,
            result.ArtworkDeleted
        });
        return Results.Ok(result);
    }
}
