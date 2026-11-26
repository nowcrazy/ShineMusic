using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShineMusic.Api.Extensions;
using ShineMusic.Common;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

public sealed class ClientIpTests
{
    [Theory]
    [InlineData("198.51.100.27", "198.51.100.27")]
    [InlineData("203.0.113.42", "203.0.113.42")]
    [InlineData("::ffff:198.51.100.27", "198.51.100.27")]
    [InlineData("127.0.0.1", "本机")]
    [InlineData("127.12.34.56", "本机")]
    [InlineData("::1", "本机")]
    [InlineData("::ffff:127.0.0.1", "本机")]
    [InlineData("10.20.30.40", "内网 10.20.30.40")]
    [InlineData("172.16.0.1", "内网 172.16.0.1")]
    [InlineData("172.31.255.254", "内网 172.31.255.254")]
    [InlineData("192.168.1.2", "内网 192.168.1.2")]
    [InlineData("::ffff:192.168.1.2", "内网 192.168.1.2")]
    [InlineData("fc00::1", "内网 fc00::1")]
    [InlineData("fd12:3456:789a::1", "内网 fd12:3456:789a::1")]
    [InlineData("fe80::1", "内网 fe80::1")]
    [InlineData("100.64.0.1", "共享地址 100.64.0.1")]
    [InlineData("100.127.255.254", "共享地址 100.127.255.254")]
    [InlineData("::ffff:100.100.100.100", "共享地址 100.100.100.100")]
    [InlineData("100.63.255.254", "100.63.255.254")]
    [InlineData("100.128.0.1", "100.128.0.1")]
    [InlineData("172.15.255.254", "172.15.255.254")]
    [InlineData("172.32.0.1", "172.32.0.1")]
    [InlineData(null, "-")]
    public void Resolve_formats_only_the_connection_address(string? address, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);

        Assert.Equal(expected, ClientIp.Resolve(context));
    }

    [Theory]
    [InlineData("2001:DB8:0000:0000:0001:0002:0003:0004")]
    [InlineData("2001:db8:1:0:0:0:0:1")]
    [InlineData("2001:db8:1:2:0:0:0:0")]
    [InlineData("2001:db8:1:2:3:4:5:6")]
    [InlineData("::1234")]
    public void Resolve_preserves_valid_standard_compressed_ipv6(string address)
    {
        var ip = IPAddress.Parse(address);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = ip;

        var resolved = ClientIp.Resolve(context);

        Assert.Equal(ip.ToString(), resolved);
        Assert.True(IPAddress.TryParse(resolved, out var parsed));
        Assert.Equal(ip, parsed);
        Assert.DoesNotContain(":::", resolved);
    }

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Real-IP")]
    [InlineData("CF-Connecting-IP")]
    [InlineData("X-Client-IP")]
    [InlineData("X-Client-Public-IP")]
    public void Resolve_does_not_trust_unprocessed_or_client_supplied_ip_headers(string header)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.27");
        context.Request.Headers[header] = "203.0.113.66";

        Assert.Equal("198.51.100.27", ClientIp.Resolve(context));
    }

    [Fact]
    public void Missing_connection_address_is_not_replaced_with_an_untrusted_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.66";
        context.Request.Headers["X-Real-IP"] = "203.0.113.66";

        Assert.Equal("-", ClientIp.Resolve(context));
    }

    [Theory]
    [InlineData("127.0.0.1", "198.51.100.27")]
    [InlineData("::1", "198.51.100.27")]
    [InlineData("::ffff:127.0.0.1", "198.51.100.27")]
    [InlineData("127.0.0.1", "203.0.113.42")]
    [InlineData("127.0.0.1", "2001:db8:0:0:1:2:3:4")]
    public async Task Trusted_local_proxy_records_the_actual_ipv4_or_ipv6_exit(
        string proxy, string exitAddress)
    {
        var resolved = await ResolveThroughForwardedHeadersAsync(proxy, exitAddress);

        Assert.Equal(IPAddress.Parse(exitAddress).ToString(), resolved);
    }

    [Theory]
    [InlineData("198.51.100.27")]
    [InlineData("2001:db8::27")]
    public async Task Untrusted_remote_peer_cannot_spoof_its_address_with_forwarded_headers(string peer)
    {
        var resolved = await ResolveThroughForwardedHeadersAsync(peer, "203.0.113.66");

        Assert.Equal(IPAddress.Parse(peer).ToString(), resolved);
    }

    [Fact]
    public async Task Explicitly_trusted_two_hop_proxy_chain_preserves_the_visitor_exit()
    {
        var resolved = await ResolveThroughForwardedHeadersAsync(
            "127.0.0.1", "198.51.100.27, 10.20.30.40", "10.20.30.40");

        Assert.Equal("198.51.100.27", resolved);
    }

    [Fact]
    public async Task Untrusted_intermediate_proxy_is_the_last_verifiable_source()
    {
        var resolved = await ResolveThroughForwardedHeadersAsync(
            "127.0.0.1", "203.0.113.66, 10.20.30.40");

        Assert.Equal("内网 10.20.30.40", resolved);
    }

    [Fact]
    public async Task Spoofed_leftmost_header_cannot_override_the_actual_public_request_source()
    {
        var resolved = await ResolveThroughForwardedHeadersAsync(
            "127.0.0.1", "203.0.113.66, 198.51.100.27");

        Assert.Equal("198.51.100.27", resolved);
    }

    [Fact]
    public async Task Two_hop_limit_does_not_walk_into_an_extra_unverified_header_prefix()
    {
        var resolved = await ResolveThroughForwardedHeadersAsync(
            "127.0.0.1", "203.0.113.66, 198.51.100.27, 10.20.30.40", "10.20.30.40");

        Assert.Equal("198.51.100.27", resolved);
    }

    [Fact]
    public async Task Forwarding_stops_after_two_hops_even_when_another_proxy_is_trusted()
    {
        var resolved = await ResolveThroughForwardedHeadersAsync(
            "127.0.0.1", "198.51.100.27, 10.20.30.41, 10.20.30.40", "10.20.30.40", "10.20.30.41");

        Assert.Equal("内网 10.20.30.41", resolved);
    }

    private static async Task<string> ResolveThroughForwardedHeadersAsync(
        string peer, string forwardedFor, params string[] knownProxies)
    {
        var settings = knownProxies.Select((proxy, index) =>
            new KeyValuePair<string, string?>($"ForwardedHeaders:KnownProxies:{index}", proxy));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddShineMusicCrossCutting(configuration);
        using var provider = services.BuildServiceProvider();

        string? resolved = null;
        var app = new ApplicationBuilder(provider);
        app.UseForwardedHeaders();
        app.Run(context =>
        {
            resolved = ClientIp.Resolve(context);
            return Task.CompletedTask;
        });

        var request = new DefaultHttpContext { RequestServices = provider };
        request.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        request.Connection.RemotePort = 45123;
        request.Request.Headers["X-Forwarded-For"] = forwardedFor;
        // 这些客户端可自行填写的头不能绕过可信代理链。
        request.Request.Headers["X-Real-IP"] = "203.0.113.99";
        request.Request.Headers["X-Client-Public-IP"] = "203.0.113.99";

        await app.Build()(request);

        return resolved ?? throw new InvalidOperationException("The request did not reach the IP resolver.");
    }
}
