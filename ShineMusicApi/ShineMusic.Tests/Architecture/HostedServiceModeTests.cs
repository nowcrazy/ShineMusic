// 职责：锁定 P5 的后台任务分组，防止 Web 与 Media Worker 同时消费同一条队列。

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShineMusic.Api.Extensions;
using Xunit;

namespace ShineMusic.Tests.Architecture;

public class HostedServiceModeTests
{
    [Theory]
    [InlineData(null, HostedServiceMode.All)]
    [InlineData("all", HostedServiceMode.All)]
    [InlineData("media", HostedServiceMode.MediaOnly)]
    [InlineData("web", HostedServiceMode.NonMediaOnly)]
    [InlineData("none", HostedServiceMode.None)]
    [InlineData("unknown-value", HostedServiceMode.All)]
    public void Parses_worker_mode_with_fail_safe_default(string? value, HostedServiceMode expected)
    {
        Assert.Equal(expected, HostedServiceModeParser.Parse(value));
    }

    [Theory]
    [InlineData(HostedServiceMode.All, 7)]
    [InlineData(HostedServiceMode.MediaOnly, 4)]
    [InlineData(HostedServiceMode.NonMediaOnly, 3)]
    [InlineData(HostedServiceMode.None, 0)]
    public void Registers_only_the_requested_hosted_service_group(HostedServiceMode mode, int expected)
    {
        // 只做容器注册，不解析任何服务：不会连接 PostgreSQL / Redis / RabbitMQ。
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddShineMusicInfrastructure(configuration, mode);
        var hosted = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToList();
        Assert.Equal(expected, hosted.Count);
    }

    [Fact]
    public void Media_and_web_groups_are_disjoint_and_cover_all_services()
    {
        static string[] Names(HostedServiceMode mode)
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder().Build();
            services.AddShineMusicInfrastructure(configuration, mode);
            return services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(descriptor => descriptor.ImplementationType?.Name ?? string.Empty)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        var all = Names(HostedServiceMode.All);
        var media = Names(HostedServiceMode.MediaOnly);
        var web = Names(HostedServiceMode.NonMediaOnly);
        Assert.Equal(4, media.Length);
        Assert.Equal(3, web.Length);
        Assert.Empty(media.Intersect(web));
        Assert.Equal(all.OrderBy(n => n, StringComparer.Ordinal), media.Concat(web).OrderBy(n => n, StringComparer.Ordinal));
    }
}
