// 客户端真实 IP 解析：供操作日志、登录审计、限流等横切关注点共用。
// 放在 ShineMusic.Common 而非 Service，是因为中间件（Common 层）与业务服务（Service 层）
// 都需要取 IP，而 Service 已依赖 Common，不能反向依赖。
//
// 取值顺序：
// 1. HttpContext.Connection.RemoteIpAddress —— 经 ForwardedHeaders 中间件回填后即为真实客户端 IP；
// 2. X-Forwarded-For 首跳 —— 兜底，应对未配置 ForwardedHeaders 的场景；
// 3. 本机非回环 IPv4 —— 浏览器经 localhost 访问时前两步均为 127.0.0.1，
//    回退到本机 LAN IP（如 192.168.100.107），避免日志全是 127.0.0.1。
//
// IPv6 处理：
// - ::1（IPv6 回环）→ 127.0.0.1；
// - ::ffff:1.2.3.4（IPv4-mapped IPv6）→ 1.2.3.4；
// - 纯 IPv6 地址原样返回（极少数客户端仅有 IPv6 公网地址）。

using System.Net;
using System.Net.Sockets;

namespace ShineMusic.Common;

/// <summary>客户端 IP 解析：优先返回 IPv4，兼容反向代理与 IPv6 映射地址。</summary>
public static class ClientIp
{
    /// <summary>从 HTTP 上下文解析客户端真实 IP（优先 IPv4）。</summary>
    public static string Resolve(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        // 兜底：ForwardedHeaders 未生效时，手动读 X-Forwarded-For 首跳。
        if (ip is null || IPAddress.IsLoopback(ip))
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                var first = forwarded.Split(',')[0].Trim();
                if (IPAddress.TryParse(first, out var forwardedIp))
                    ip = forwardedIp;
                else
                    return Truncate(first, 64);
            }
        }
        // 仍为回环（浏览器经 localhost 访问 + 代理也未转发真实 IP）→ 回退本机 LAN IPv4。
        if (ip is null || IPAddress.IsLoopback(ip))
            ip = ResolveLocalLanIp();
        return Truncate(Normalize(ip), 64);
    }

    /// <summary>解析本机用于出站的 IPv4 地址（即真正上网的网卡 IP，如 WiFi 的 192.168.100.107）。</summary>
    /// <remarks>
    /// 用 UDP socket connect 到公网地址的技巧：OS 会按路由表选择出站接口，
    /// LocalEndPoint 即为该接口的 IP。不实际发送数据，仅用于探测路由。
    /// 拿不到时回退到遍历网卡取第一个私网 IPv4。
    /// </remarks>
    private static IPAddress? ResolveLocalLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // 8.8.8.8 是公网 DNS，connect 不发包，仅让 OS 选定出站接口
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint localEp && !IPAddress.IsLoopback(localEp.Address))
                return localEp.Address;
        }
        catch { /* 忽略，走回退 */ }

        // 回退：遍历网卡，取第一个处于 Up 状态的非虚拟网卡的私网 IPv4
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && ni.NetworkInterfaceType is not System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    and not System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Select(addr => addr.Address)
                .FirstOrDefault(a =>
                    a.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a) &&
                    !IsApipa(a) &&
                    !IsCgnat(a));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>169.254.0.0/16 —— APIPA 链路本地地址（未连上网络的网卡）。</summary>
    private static bool IsApipa(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>100.64.0.0/10 —— CGNAT 段（Tailscale 等 VPN 常用），不作为真实局域网 IP。</summary>
    private static bool IsCgnat(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    /// <summary>把 IPAddress 归一化为 IPv4 字符串（IPv6 回环/映射地址转 IPv4，纯 IPv6 保留）。</summary>
    private static string Normalize(IPAddress? ip)
    {
        if (ip is null) return "-";
        // IPv4 直接返回
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return ip.ToString();
        // ::1 → 127.0.0.1
        if (IPAddress.IsLoopback(ip))
            return "127.0.0.1";
        // ::ffff:1.2.3.4 → 1.2.3.4
        if (ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4().ToString();
        // 纯 IPv6：原样返回
        return ip.ToString();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
