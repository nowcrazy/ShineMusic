// 职责：通过 SMTP（MailKit）发送注册邀请邮件。
// 将一次性邀请令牌拼接进前端注册页链接（置于 URL fragment 中，避免经服务端访问日志/Referer 泄露），
// 并负责发件配置校验、邮件正文（纯文本 + HTML）构建、SMTP 连接/认证/发送/断开全流程。

using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using ShineMusic.Model.Options;

namespace ShineMusic.Service;

/// <summary>
/// 邀请邮件发送抽象，便于管理端服务注入与测试替身替换。
/// </summary>
public interface IInvitationEmailSender
{
    /// <summary>
    /// 向受邀邮箱发送带一次性注册令牌的邀请邮件。
    /// </summary>
    /// <param name="email">收件人邮箱。</param>
    /// <param name="token">明文邀请令牌（数据库中仅存其哈希）。</param>
    /// <param name="expiresAtUtc">邀请过期时间（UTC），写入邮件正文提醒有效期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SendInvitationAsync(string email, string token, DateTime expiresAtUtc, CancellationToken cancellationToken);
}

/// <summary>
/// 基于 MailKit 的邀请邮件发送实现（主构造函数注入邀请邮件配置）。
/// </summary>
public sealed class InvitationEmailSender(IOptions<InvitationEmailOptions> emailOptions, ILogger<InvitationEmailSender> logger) : IInvitationEmailSender
{
    /// <inheritdoc />
    public async Task SendInvitationAsync(string email, string token, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        var options = emailOptions.Value;
        // 配置完整性校验：站点基址必须是绝对 URI，发件地址、SMTP 主机必填，端口必须在合法范围。
        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var baseUri) ||
            string.IsNullOrWhiteSpace(options.FromAddress) ||
            string.IsNullOrWhiteSpace(options.SmtpHost) ||
            options.SmtpPort is < 1 or > 65535)
        {
            logger.LogError("Invitation email not sent to {Email}: settings incomplete.", email);
            throw new InvalidOperationException("Invitation email settings are incomplete.");
        }

        // 用户名与密码必须“成对配置”：要么都提供（需认证的 SMTP），要么都留空（内网免认证中继）。
        if ((string.IsNullOrWhiteSpace(options.Username)) != (string.IsNullOrWhiteSpace(options.Password)))
        {
            logger.LogError("Invitation email not sent to {Email}: SMTP username and password must be configured together.", email);
            throw new InvalidOperationException("Both SMTP username and password must be configured together.");
        }

        // 邀请注册链接：令牌放在 URL 的 fragment（# 之后）——fragment 不会发送到服务器、不会出现在服务端日志与 Referer 中。
        // 先清空 Query 再设置 Fragment，避免继承基址上的多余查询串。
        var registrationUrl = new UriBuilder(baseUri)
        {
            Query = string.Empty,
            Fragment = $"/pages/auth/register?token={Uri.EscapeDataString(token)}"
        }.Uri;
        // 提前转义为字符串，避免在多个插值点重复调用与 HTML 编码不一致。
        var registrationUrlStr = registrationUrl.ToString();
        var registrationUrlHtml = System.Net.WebUtility.HtmlEncode(registrationUrlStr);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.FromAddress));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = "🎉 你被邀请加入 Shine Music";

        // 邮件正文：纯文本 + HTML 双版本。
        // HTML 采用品牌化设计（品牌头、邀请语、CTA 按钮、有效期提醒），引导用户点击按钮而非复制纯文本链接，
        // 避免部分邮件客户端渲染纯文本链接时丢失 http:// 协议导致无法打开。
        var expiresLocal = expiresAtUtc.ToLocalTime();
        var htmlBody = $@"
<div style=""max-width:560px;margin:0 auto;font-family:'PingFang SC','Microsoft YaHei',Avenir,sans-serif;background:#f5f6f2;padding:32px 20px;"">
  <div style=""background:#fff;border-radius:20px;overflow:hidden;box-shadow:0 8px 30px rgba(28,33,16,0.08);"">
    <!-- 品牌头 -->
    <div style=""background:#c8f36a;padding:36px 32px 28px;text-align:center;"">
      <div style=""display:inline-grid;place-items:center;width:56px;height:56px;border-radius:16px;background:#20221e;color:#c8f36a;font-size:24px;font-weight:900;letter-spacing:-1px;"">S</div>
      <div style=""margin-top:16px;color:#20221e;font-size:26px;font-weight:800;letter-spacing:-0.5px;"">Shine Music</div>
      <div style=""margin-top:6px;color:#5f6b4a;font-size:14px;"">让好音乐，刚好遇见你</div>
    </div>
    <!-- 正文 -->
    <div style=""padding:32px 32px 28px;"">
      <div style=""color:#262821;font-size:20px;font-weight:800;line-height:1.5;"">你好，有人邀请你加入 Shine Music</div>
      <p style=""margin:14px 0 0;color:#555a4f;font-size:15px;line-height:1.7;"">
        Shine Music 采用管理员邀请制，不开放公开注册。你收到这封邮件，是因为有人为你创建了专属邀请。点击下方按钮即可完成注册，开启你的音乐之旅。
      </p>
      <!-- CTA 按钮 -->
      <div style=""margin:28px 0;text-align:center;"">
        <a href=""{registrationUrlHtml}"" style=""display:inline-block;padding:16px 48px;background:#c8f36a;color:#20221e;font-size:16px;font-weight:800;text-decoration:none;border-radius:30px;letter-spacing:1px;"">立即注册</a>
      </div>
      <p style=""margin:0;color:#9aa093;font-size:13px;line-height:1.6;text-align:center;"">
        如果按钮无法点击，请复制下方链接到浏览器打开：
      </p>
      <div style=""margin:10px 0 0;padding:12px 16px;background:#f5f6f2;border-radius:10px;word-break:break-all;color:#6a7060;font-size:12px;line-height:1.5;"">{registrationUrlHtml}</div>
      <!-- 有效期提醒 -->
      <div style=""margin-top:24px;padding:14px 16px;background:#fff8e6;border-radius:12px;border-left:3px solid #f0b429;"">
        <div style=""color:#8a6100;font-size:13px;font-weight:700;"">⏰ 邀请有效期</div>
        <div style=""margin-top:4px;color:#8a6100;font-size:13px;line-height:1.6;"">
          请在 {expiresLocal:yyyy-MM-dd HH:mm}（本地时间）前完成注册，过期后邀请链接将失效。
        </div>
      </div>
    </div>
    <!-- 页脚 -->
    <div style=""padding:20px 32px 28px;border-top:1px solid #eef0ea;text-align:center;"">
      <div style=""color:#a6aba1;font-size:12px;line-height:1.6;"">
        此邮件由系统自动发送，请勿回复。<br>
        © {DateTime.Now.Year} Shine Music
      </div>
    </div>
  </div>
</div>";

        var textBody = $"你被邀请加入 Shine Music。\n\n" +
                       $"Shine Music 采用管理员邀请制，请点击下方链接完成注册：\n" +
                       $"{registrationUrlStr}\n\n" +
                       $"邀请有效期至 {expiresLocal:yyyy-MM-dd HH:mm}（本地时间），过期后链接失效。\n\n" +
                       $"此邮件由系统自动发送，请勿回复。";

        message.Body = new BodyBuilder
        {
            TextBody = textBody,
            HtmlBody = htmlBody
        }.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            // 加密策略：UseSsl=true 表示连接即 TLS（SMTPS/465）；否则使用 STARTTLS 升级明文连接（587）。
            var socketOptions = options.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(options.SmtpHost, options.SmtpPort, socketOptions, cancellationToken);
            // 仅在配置了用户名时才进行认证（与上面的成对校验呼应）。
            if (!string.IsNullOrWhiteSpace(options.Username))
                await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            // quit=true：发送 QUIT 命令优雅断开连接。
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception exception)
        {
            // 任何 SMTP 连接/认证/发送异常都记录到日志（落入 logs/app），便于排查邮件发不出去的问题。
            logger.LogError(exception, "Failed to send invitation email to {Email}.", email);
            throw;
        }
    }
}
