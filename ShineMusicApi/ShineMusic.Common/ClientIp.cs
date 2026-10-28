// 客户端真实 IP 解析：供操作日志、登录审计、限流等横切关注点共用。
// 放在 ShineMusic.Common 而非 Service，是因为中间件（Common 层）与业务服务（Service 层）
// 都需要取 IP，而 Service 已依赖 Common，不能反向依赖。
//
// 取值顺序：
// 1. X-Forwarded-For 首跳 —— 反向代理（Vite dev proxy 的 xfwd、Nginx 等）注入的真实客户端 IP；
// 2. HttpContext.Connection.RemoteIpAddress —— 直连或 ForwardedHeaders 已回填的地址；
// 3. 两者都是回环（浏览器经 localhost 访问）→ 显示"本机"。
//
// 注意：不再"猜测"本机出口网卡 IP 充当客户端 IP。那样在开着 VPN 的开发机上会得到
// 100.64/10 这类 Tailscale 地址，看似是客户端 IP、实则与服务端无关，属于误导性数据。
// 拿不到真实客户端时如实显示"本机"。
//
// IPv6 处理：
// - ::1（IPv6 回环）→ 本机；
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
        var ip = ResolveForwarded(context) ?? context.Connection.RemoteIpAddress;
        return Truncate(Normalize(ip), 64);
    }

    /// <summary>读取 X-Forwarded-For 首跳（最靠近客户端的一跳）；回环地址视为无效。</summary>
    private static IPAddress? ResolveForwarded(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (string.IsNullOrWhiteSpace(forwarded))
            return null;
        var first = forwarded.Split(',')[0].Trim().Trim('"');
        if (!IPAddress.TryParse(first, out var parsed))
            return null;
        return IPAddress.IsLoopback(parsed) ? null : parsed;
    }

    /// <summary>100.64.0.0/10 —— CGNAT 段（Tailscale 等 VPN 常用），标注为 VPN 便于识别。</summary>
    private static bool IsCgnat(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    /// <summary>把 IPAddress 归一化为人类可读的显示字符串。
    /// IPv6 做地址压缩（最长连续 0000 段折叠为 ::）；
    /// 内网/VPN/回环地址自动加中文标签，便于管理端日志阅读。</summary>
    private static string Normalize(IPAddress? ip)
    {
        if (ip is null) return "-";
        // IPv4 公网：直接返回
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var str = ip.ToString();
            if (IsPrivateNetwork(ip)) return $"内网 {str}";
            if (IsCgnat(ip)) return $"VPN {str}";
            if (IPAddress.IsLoopback(ip)) return "本机";
            return str;
        }
        // ::ffff:1.2.3.4 → 1.2.3.4（按 IPv4 规则处理）
        if (ip.IsIPv4MappedToIPv6)
        {
            var ipv4 = ip.MapToIPv4();
            var str = ipv4.ToString();
            if (IsPrivateNetwork(ipv4)) return $"内网 {str}";
            if (IsCgnat(ipv4)) return $"VPN {str}";
            return str;
        }
        // ::1 → 本机
        if (IPAddress.IsLoopback(ip))
            return "本机";
        // 纯 IPv6：压缩显示
        return CompressIPv6(ip);
    }

    /// <summary>把 IPv6 地址压缩为标准最短形式（最长连续 0000 段折叠为 ::）。</summary>
    /// <remarks>
    /// .NET 的 IPAddress.ToString() 不自动折叠 ::，需要手动实现 RFC 5952 的压缩规则：
    /// 1. 地址按 16-bit 分段；
    /// 2. 找最长连续零段；若有并列，选最左边的；
    /// 3. 折叠处用 :: 替换。
    /// </remarks>
    private static string CompressIPv6(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        // 分 8 段，每段 2 字节 → ushort
        Span<ushort> segments = stackalloc ushort[8];
        for (int i = 0; i < 8; i++)
            segments[i] = (ushort)(bytes[i * 2] << 8 | bytes[i * 2 + 1]);

        // 找最长连续零段
        int bestStart = -1, bestLen = 0;
        int curStart = -1, curLen = 0;
        for (int i = 0; i < 8; i++)
        {
            if (segments[i] == 0)
            {
                if (curStart == -1) { curStart = i; curLen = 1; }
                else curLen++;
                if (curLen > bestLen && curLen >= 2) { bestLen = curLen; bestStart = curStart; }
            }
            else { curStart = -1; curLen = 0; }
        }

        var parts = new System.Text.StringBuilder(40);
        if (bestStart == -1 || bestLen < 2)
        {
            // 无零段可折叠：全部小写 hex 输出
            for (int i = 0; i < 8; i++)
            {
                if (i > 0) parts.Append(':');
                parts.Append(segments[i].ToString("x"));
            }
            return parts.ToString();
        }

        // 折叠前半段
        if (bestStart > 0)
        {
            for (int i = 0; i < bestStart; i++)
            {
                if (i > 0) parts.Append(':');
                parts.Append(segments[i].ToString("x"));
            }
            parts.Append("::");
        }
        else
        {
            parts.Append("::");
        }

        // 折叠后半段
        int after = bestStart + bestLen;
        if (after < 8)
        {
            if (bestStart > 0) parts.Append(':');
            for (int i = after; i < 8; i++)
            {
                if (i > after) parts.Append(':');
                parts.Append(segments[i].ToString("x"));
            }
        }

        return parts.ToString();
    }

    /// <summary>RFC 1918 私网段：10.0.0.0/8、172.16.0.0/12、192.168.0.0/16。</summary>
    private static bool IsPrivateNetwork(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || bytes[0] == 192 && bytes[1] == 168;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
