// 记录本次访问本站/API 实际使用的请求源地址，供操作日志、登录审计和限流共用。
// ForwardedHeaders 中间件先验证可信代理，再回填 Connection.RemoteIpAddress；
// 不直接读取客户端可伪造的转发头，也不查询服务器自身的公网出口。
// 请求经过翻墙代理时记录该请求的代理出口；分流直连或 Tailscale 内网访问时，
// 只能记录该连接的地址，不能据此推断设备的其他公网出口或是否开启 VPN。

using System.Net;
using System.Net.Sockets;

namespace ShineMusic.Common;

/// <summary>本次请求源 IP：兼容可信反向代理及 IPv4/IPv6，不根据地址猜测是否翻墙。</summary>
public static class ClientIp
{
    /// <summary>读取已由可信代理中间件处理的地址，并归一化 IPv4-mapped IPv6。</summary>
    public static string Resolve(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return "-";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return "本机";
        if (IsPrivateNetwork(ip)) return $"内网 {ip}";
        if (ip.AddressFamily == AddressFamily.InterNetwork && IsCgnat(ip))
            return $"共享地址 {ip}";
        return ip.ToString();
    }

    /// <summary>100.64.0.0/10 为运营商 CGNAT / Tailscale 等使用的共享地址，不能据此判断 VPN。</summary>
    private static bool IsCgnat(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    /// <summary>RFC 1918 私网以及 IPv6 本地地址。</summary>
    private static bool IsPrivateNetwork(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc;
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || bytes[0] == 192 && bytes[1] == 168;
    }
}
