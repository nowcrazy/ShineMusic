using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Service;
using ShineMusic.Service.Messaging;

namespace ShineMusic.Api.Controllers;

/// <summary>RabbitMQ 队列监控端点（仅管理员）。</summary>
[ApiController]
[Route("api/v1/admin/mq")]
[Authorize(Roles = "admin")]
public sealed class MessageQueueAdminController : ControllerBase
{
    /// <summary>查询 RabbitMQ 全部队列的积压、处理中、在线消费者与收发速率。</summary>
    /// <remarks>
    /// 数据来自 RabbitMQ Management HTTP API（默认 15672 端口，rabbitmq_management 插件）。
    /// 插件未启用或网络不通时返回 502 upstream_unavailable，区别于业务内部错误。
    /// </remarks>
    /// <param name="service">队列状态查询服务。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>200 + 队列状态数组；或 502 上游不可达。</returns>
    [HttpGet("queues")]
    [ProducesResponseType(typeof(Ok<MessageQueueStatusDto[]>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IResult> GetQueues([FromServices] MessageQueueStatusService service, CancellationToken cancellationToken)
    {
        try
        {
            var queues = await service.GetQueuesAsync(cancellationToken);
            return Results.Ok(queues);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApiResults.Error(
                HttpContext,
                StatusCodes.Status502BadGateway,
                ErrorCodes.UpstreamUnavailable,
                "无法连接 RabbitMQ Management API，请确认 rabbitmq_management 插件已启用（默认端口 15672）。");
        }
    }

    /// <summary>查询下载/导入队列中等待或处理中的具体业务任务。</summary>
    [HttpGet("tasks")]
    [ProducesResponseType(typeof(Ok<MessageQueueTaskListDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IResult> GetTasks(
        [FromQuery] string queue,
        [FromQuery] string state,
        [FromQuery] int limit,
        [FromServices] MessageQueueStatusService service,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1)
    {
        if (queue is not (QueueNames.Downloads or QueueNames.Imports))
            return ApiResults.Error(HttpContext, StatusCodes.Status400BadRequest, ErrorCodes.ValidationError, "queue 必须是下载队列或导入队列。");

        if (state is not ("all" or "waiting" or "processing"))
            return ApiResults.Error(HttpContext, StatusCodes.Status400BadRequest, ErrorCodes.ValidationError, "state 必须是 all、waiting 或 processing。");

        if (limit is < 1 or > 100)
            return ApiResults.Error(HttpContext, StatusCodes.Status400BadRequest, ErrorCodes.ValidationError, "limit 必须在 1 到 100 之间。");

        if (page is < 1 or > 100_000)
            return ApiResults.Error(HttpContext, StatusCodes.Status400BadRequest, ErrorCodes.ValidationError, "page 必须在 1 到 100000 之间。");

        var tasks = await service.GetTasksAsync(queue, state, limit, page, cancellationToken);
        return Results.Ok(tasks);
    }
}
