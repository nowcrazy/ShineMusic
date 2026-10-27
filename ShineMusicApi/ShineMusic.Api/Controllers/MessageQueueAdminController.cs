using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Service;

namespace ShineMusic.Api.Controllers;

/// <summary>RabbitMQ 队列监控端点（仅管理员）。</summary>
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
}
