using Microsoft.Extensions.DependencyInjection;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Architecture;

public sealed class AudioHttpClientIsolationTests
{
    [Theory]
    [InlineData(HttpAudioUrlResolver.HttpClientName, false)]
    [InlineData(HttpAudioUrlResolver.DownloadHttpClientName, true)]
    public void Audio_clients_use_the_public_transport_without_platform_credentials(string name, bool allowRedirect)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<SourceDownloadOptions>(options => options.Netease.Cookie = "platform-session=private");
        services.AddShineMusicMetadataModule();
        services.AddShineMusicMediaModule();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var platform = factory.CreateClient(NeteaseSourceProvider.HttpClientName);
        Assert.True(platform.DefaultRequestHeaders.Contains("Cookie"));
        using var client = factory.CreateClient(name);
        Assert.False(client.DefaultRequestHeaders.Contains("Cookie"));
        Assert.False(client.DefaultRequestHeaders.Contains("Authorization"));

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;
        var transport = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(transport.UseCookies);
        Assert.False(transport.UseProxy);
        Assert.Equal(allowRedirect, transport.AllowAutoRedirect);
        Assert.NotNull(transport.ConnectCallback);
    }
}
