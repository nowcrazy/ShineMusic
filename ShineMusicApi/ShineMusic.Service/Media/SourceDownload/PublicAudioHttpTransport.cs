using System.Net;
using System.Net.Sockets;

namespace ShineMusic.Service.SourceDownload;

/// <summary>解析服务与其返回的音频地址只允许连接公网；连接时验证 DNS 并固定所用 IP。</summary>
public static class PublicAudioHttpTransport
{
    public static SocketsHttpHandler CreateHandler(bool allowAutoRedirect = false) => new()
    {
        UseCookies = false,
        UseProxy = false,
        AllowAutoRedirect = allowAutoRedirect,
        MaxAutomaticRedirections = 5,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync
    };

    /// <summary>无需 DNS 的前置检查；域名最终仍须经过连接时的公网 IP 检查。</summary>
    public static bool IsAllowedUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo) && IsAllowedHost(uri.IdnHost);

    private static bool IsAllowedHost(string host)
    {
        host = host.TrimEnd('.').Trim('[', ']');
        if (IPAddress.TryParse(host, out var address)) return IsPublicAddress(address);
        return Uri.CheckHostName(host) == UriHostNameType.Dns && host.Contains('.')
            && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".home", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2
                    || bytes[1] == 88 && bytes[2] == 99))
                && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100))
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        // 只接受 IPv6 全球单播；排除特殊分配块、文档和 6to4 转换地址。
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0
            && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2)
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }

    internal static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<IPAddress[]>>? lookup = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAllowedHost(host)) throw new HttpRequestException("Audio HTTP target is not a public address.");
        var normalizedHost = host.TrimEnd('.').Trim('[', ']');
        lookup ??= static (name, token) => Dns.GetHostAddressesAsync(name, token);
        IPAddress[] addresses = IPAddress.TryParse(normalizedHost, out var literal)
            ? [literal]
            : await lookup(normalizedHost, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // 混合公私网应答整体拒绝，不能在失败时回落到未经验证的地址。
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Audio HTTP target did not resolve exclusively to public addresses.");
        return addresses;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("Could not connect to a public audio HTTP target.");
    }
}
