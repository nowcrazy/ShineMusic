// RabbitMQ 消息队列配置。
// 口令不得写进仓库：生产环境用 RabbitMQ__Password 环境变量覆盖。

namespace ShineMusic.Model.Options;

/// <summary>RabbitMQ 连接配置。</summary>
public sealed class RabbitOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "RabbitMQ";

    /// <summary>主机名。</summary>
    public string HostName { get; set; } = "localhost";

    /// <summary>AMQP 端口。</summary>
    public int Port { get; set; } = 5672;

    /// <summary>Management HTTP API 端口（管理后台队列监控用；需启用 rabbitmq_management 插件）。</summary>
    public int ManagementPort { get; set; } = 15672;

    /// <summary>用户名。</summary>
    public string UserName { get; set; } = "guest";

    /// <summary>密码。</summary>
    public string Password { get; set; } = "guest";
}
