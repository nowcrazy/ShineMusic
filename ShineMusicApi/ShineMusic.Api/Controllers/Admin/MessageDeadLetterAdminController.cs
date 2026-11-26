using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Service.Messaging;

namespace ShineMusic.Api.Controllers;

/// <summary>持久化死信查看及有审计记录的人工重放（仅管理员）。</summary>
[ApiController]
[Route("api/v1/admin/mq/dead-letters")]
[Authorize(Roles = "admin")]
public sealed class MessageDeadLetterAdminController : ControllerBase
{
    [HttpGet(Name = "ListMessageDeadLetters")]
    [ProducesResponseType(typeof(MessageDeadLetterListDto), StatusCodes.Status200OK)]
    public async Task<IResult> List(
        [FromServices] MessageDeadLetterStore store,
        CancellationToken cancellationToken,
        [FromQuery] string? queue = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (queue is not null && queue is not (QueueNames.Downloads or QueueNames.Imports))
            return ApiResults.ValidationError(HttpContext, "queue", "queue 必须是下载队列或导入队列。");
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100)
            return ApiResults.ValidationError(HttpContext, "page", "page 必须在 1–100000，pageSize 必须在 1–100。");
        return Results.Ok(await store.ListAsync(queue, page, pageSize, cancellationToken));
    }

    /// <summary>重放可解析且业务状态可认领的任务 Id；安排 outbox 后不能重复发起。</summary>
    [HttpPost("{id:guid}/replay", Name = "ReplayMessageDeadLetter")]
    public async Task<IResult> Replay(Guid id, [FromServices] MessageDeadLetterStore store, CancellationToken cancellationToken)
    {
        var result = await store.ReplayAsync(id, HttpContext, cancellationToken);
        return ToResult(result);
    }

    private IResult ToResult(DeadLetterReplayResult result) => result.Status switch
        {
            DeadLetterReplayStatus.Scheduled => Results.Accepted(value: new { outboxId = result.OutboxId, status = "pending" }),
            DeadLetterReplayStatus.NotFound => ApiResults.NotFound(HttpContext),
            DeadLetterReplayStatus.InvalidMessage => ApiResults.ValidationError(HttpContext, "id", "消息格式或队列无效，仅能查看，不能重放。"),
            _ => ApiResults.Conflict(HttpContext, "消息已安排重放、正在处理，或任务状态不允许重试，请刷新查看结果。")
        };

    [HttpGet("outbox/failed", Name = "ListFailedMessageOutbox")]
    [ProducesResponseType(typeof(FailedMessageOutboxListDto), StatusCodes.Status200OK)]
    public async Task<IResult> ListFailedOutbox(
        [FromServices] MessageDeadLetterStore store,
        CancellationToken cancellationToken,
        [FromQuery] string? queue = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (queue is not null && queue is not (QueueNames.Downloads or QueueNames.Imports))
            return ApiResults.ValidationError(HttpContext, "queue", "queue 必须是下载队列或导入队列。");
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100)
            return ApiResults.ValidationError(HttpContext, "page", "page 必须在 1–100000，pageSize 必须在 1–100。");
        return Results.Ok(await store.ListFailedOutboxAsync(queue, page, pageSize, cancellationToken));
    }

    [HttpPost("outbox/{id:guid}/retry", Name = "RetryFailedMessageOutbox")]
    public async Task<IResult> RetryOutbox(Guid id, [FromServices] MessageDeadLetterStore store, CancellationToken cancellationToken) =>
        ToResult(await store.RetryOutboxAsync(id, HttpContext, cancellationToken));
}
