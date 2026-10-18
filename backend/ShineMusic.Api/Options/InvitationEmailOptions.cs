// 邀请邮件发送配置选项：绑定 "InvitationEmail" 配置节，包含对外站点地址与 SMTP 服务器连接参数。
namespace ShineMusic.Api.Options;

/// <summary>
/// 管理员邀请邮件的链接拼接与 SMTP 发送配置，对应配置节 "InvitationEmail"。
/// </summary>
public sealed class InvitationEmailOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "InvitationEmail";

    /// <summary>客户端/站点对外可访问的基础 URL，用于拼接邀请注册链接（链接需从外部网络打开）。</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>邀请邮件的发件人邮箱地址。</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>SMTP 服务器主机名或 IP 地址。</summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>SMTP 服务端口，默认 587（STARTTLS 常用端口）；465/25 等视服务商而定。</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>SMTP 认证用户名，通常与发件邮箱一致。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>SMTP 认证密码或应用专用密码，属于敏感信息，应通过用户机密/环境变量注入。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>与 SMTP 服务器通信时是否启用 SSL/TLS 加密连接。</summary>
    public bool UseSsl { get; set; }
}