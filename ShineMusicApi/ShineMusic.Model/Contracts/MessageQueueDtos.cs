// 职责：定义消息队列监控相关的 DTO。
// 挂载点：MessageQueueStatusService（填充）→ MessageQueueAdminController（输出）→ 前端 admin-mq.ts（消费）。
// 数据来源：RabbitMQ Management HTTP API（GET /api/queues）。

namespace ShineMusic.Model.Contracts;

/// <summary>RabbitMQ 单个队列的运行状态。</summary>
/// <param name="Name">队列名（vhost 内唯一）。</param>
/// <param name="Role">业务角色说明（导入/下载队列等），未登记的队列回退为"未登记队列"。</param>
/// <param name="State">运行状态（running / down / idle 等，来自 Management API）。</param>
/// <param name="MessagesReady">待消费消息数（积压）。</param>
/// <param name="MessagesUnacknowledged">已投递未确认数（正在处理中）。</param>
/// <param name="Consumers">在线消费者数量。</param>
/// <param name="Prefetch">消费端预取数（QoS），同时作为该通道的并行处理上限。</param>
/// <param name="Concurrency">实际并行处理条数 = 在线消费者数 × 预取数。</param>
/// <param name="Mode">消费模式：serial 串行 / parallel 并行 / offline 无消费者。</param>
/// <param name="PublishRate">每秒发布速率；队列从未有流量时 Management API 不返回该字段，为 null。</param>
/// <param name="DeliverRate">每秒投递速率；同上，无流量历史时为 null。</param>
public sealed record MessageQueueStatusDto(
    string Name,
    string Role,
    string State,
    long MessagesReady,
    long MessagesUnacknowledged,
    int Consumers,
    int Prefetch,
    int Concurrency,
    string Mode,
    double? PublishRate,
    double? DeliverRate);
