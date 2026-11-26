using System.Net;
using System.Text;
using System.Text.Json;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class HttpAudioUrlResolverTests
{
    [Theory]
    [InlineData(VipFallbackEndpointTypes.GdStudio, "{\"url\":\"https://media.example/audio.mp3\"}")]
    [InlineData(VipFallbackEndpointTypes.HaiTangW, "{\"data\":{\"url\":\"https://media.example/audio.mp3\"}}")]
    [InlineData(VipFallbackEndpointTypes.ZMusic, "{\"data\":[{\"url\":\"https://media.example/audio.mp3\"}]}")]
    public async Task Existing_get_endpoint_types_keep_their_response_shapes(string type, string json)
    {
        using var factory = new TestHttpFactory((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Contains("id=track%2F1", message.RequestUri!.Query, StringComparison.Ordinal);
            Assert.Contains("level=320", message.RequestUri.Query, StringComparison.Ordinal);
            return Task.FromResult(Json(json));
        });
        var resolver = new HttpAudioUrlResolver(factory);
        var endpoint = new NeteaseVipFallbackEndpoint { Type = type, Url = "https://resolver.example/url?id={id}&level={level}" };

        var result = await resolver.ResolveAsync(Request(endpoint), TestContextToken());

        Assert.NotNull(result);
        Assert.Equal("https://media.example/audio.mp3", result.DownloadUrl);
        Assert.Equal("resolver-one", result.ResolverId);
        Assert.Equal("netease:track/1:320", result.ResourceId);
        Assert.True(result.UseIsolatedDownloadClient);
        Assert.Equal(HttpAudioUrlResolver.HttpClientName, Assert.Single(factory.RequestedNames));
    }

    [Fact]
    public async Task Existing_two_step_endpoint_posts_the_returned_ip_without_changing_track_identity()
    {
        var calls = 0;
        using var factory = new TestHttpFactory(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            using var document = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            calls++;
            if (calls == 1)
            {
                Assert.Equal("/api/ip", message.RequestUri!.AbsolutePath);
                Assert.True(document.RootElement.TryGetProperty("timestamp", out _));
                return Json("{\"data\":{\"ip\":\"192.0.2.5\"}}");
            }
            Assert.Equal("/api/getSongUrl", message.RequestUri!.AbsolutePath);
            Assert.Equal("track/1", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("320", document.RootElement.GetProperty("level").GetString());
            Assert.Equal("192.0.2.5", document.RootElement.GetProperty("ip").GetString());
            Assert.Equal("https://wyapi.toubiec.cn", Assert.Single(message.Headers.GetValues("Origin")));
            return Json("{\"data\":{\"url\":\"https://media.example/audio.mp3\"}}");
        });
        var endpoint = new NeteaseVipFallbackEndpoint { Type = VipFallbackEndpointTypes.NextMusic, BaseUrl = "https://resolver.example/" };

        var result = await new HttpAudioUrlResolver(factory).ResolveAsync(Request(endpoint), TestContextToken());

        Assert.NotNull(result);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Generic_mapping_supports_safe_request_fields_and_nested_response_paths(string method)
    {
        const string trackId = "id\"},\"admin\":true";
        using var factory = new TestHttpFactory(async (message, token) =>
        {
            Assert.Equal(method, message.Method.Method);
            Assert.Equal("configured-secret", Assert.Single(message.Headers.GetValues("Authorization")));
            if (method == "POST")
            {
                using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                Assert.Equal(trackId, body.RootElement.GetProperty("track").GetString());
                Assert.Equal("netease", body.RootElement.GetProperty("platform").GetString());
                Assert.False(body.RootElement.TryGetProperty("admin", out _));
            }
            var response = Json("{\"data\":[{\"audio\":{\"src\":\"https://media.example/song.flac\",\"container\":\"flac\",\"rate\":945,\"expires\":1800000000}}]}");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "must-not-propagate=secret");
            return response;
        });
        var endpoint = Generic();
        endpoint.Method = method;
        endpoint.Body = new() { ["track"] = "{id}", ["platform"] = "{provider}" };
        endpoint.Headers["Authorization"] = "configured-secret";
        endpoint.DownloadHeaders["Referer"] = "https://music.example/";
        endpoint.ResponseUrlPath = "data[0].audio.src";
        endpoint.ResponseContainerPath = "data[0].audio.container";
        endpoint.ResponseBitrateKbpsPath = "data[0].audio.rate";
        endpoint.ResponseExpiresAtPath = "data[0].audio.expires";

        var result = await new HttpAudioUrlResolver(factory).ResolveAsync(Request(endpoint) with { ProviderTrackId = trackId, Level = "999" }, TestContextToken());

        Assert.NotNull(result);
        Assert.Equal("flac", result.Container);
        Assert.Equal(945, result.BitrateKbps);
        Assert.True(result.IsLossless);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), result.ExpiresAtUtc);
        var header = Assert.Single(result.RequestHeaders!);
        Assert.Equal("Referer", header.Key);
        Assert.Equal("https://music.example/", header.Value);
    }

    [Fact]
    public async Task Diagnostic_codes_do_not_include_request_secrets_urls_or_response_bodies()
    {
        var diagnostics = new List<AudioUrlResolveDiagnostic>();
        using var factory = new TestHttpFactory((_, _) => Task.FromResult(Json("secret-response-not-json")));
        var endpoint = Generic();
        endpoint.Url += "?token=secret-query";
        endpoint.Headers["Cookie"] = "secret-cookie";

        var result = await new HttpAudioUrlResolver(factory).ResolveAsync(Request(endpoint) with { Diagnostic = diagnostics.Add }, TestContextToken());

        Assert.Null(result);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "invalid_json");
        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///audio.mp3")]
    [InlineData("ftp://media.example/audio.mp3")]
    [InlineData("https://user:password@media.example/audio.mp3")]
    public async Task Non_http_or_embedded_credential_download_addresses_are_rejected(string url)
    {
        using var factory = new TestHttpFactory((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new { url }))));

        Assert.Null(await new HttpAudioUrlResolver(factory).ResolveAsync(Request(Generic()), TestContextToken()));
    }

    [Theory]
    [InlineData("http://localhost/audio.mp3")]
    [InlineData("http://localhost./audio.mp3")]
    [InlineData("http://music.local/audio.mp3")]
    [InlineData("http://music.internal/audio.mp3")]
    [InlineData("http://127.0.0.1/audio.mp3")]
    [InlineData("http://2130706433/audio.mp3")]
    [InlineData("http://0x7f000001/audio.mp3")]
    [InlineData("http://10.1.2.3/audio.mp3")]
    [InlineData("http://172.16.1.2/audio.mp3")]
    [InlineData("http://192.168.1.2/audio.mp3")]
    [InlineData("http://100.100.100.200/audio.mp3")]
    [InlineData("http://169.254.169.254/audio.mp3")]
    [InlineData("http://[::1]/audio.mp3")]
    [InlineData("http://[::ffff:127.0.0.1]/audio.mp3")]
    [InlineData("http://[fc00::1]/audio.mp3")]
    [InlineData("http://[fe80::1]/audio.mp3")]
    public async Task Non_public_download_addresses_and_resolver_endpoints_are_rejected(string url)
    {
        var calls = 0;
        using var factory = new TestHttpFactory((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(JsonSerializer.Serialize(new { url })));
        });
        var resolver = new HttpAudioUrlResolver(factory);
        Assert.Null(await resolver.ResolveAsync(Request(Generic()), TestContextToken()));
        Assert.Equal(1, calls);

        var endpoint = Generic();
        endpoint.Url = url;
        Assert.Null(await resolver.ResolveAsync(Request(endpoint), TestContextToken()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Resolver_redirects_are_not_treated_as_audio_resolution_responses()
    {
        var calls = 0;
        using var factory = new TestHttpFactory((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data/");
            return Task.FromResult(response);
        });
        var endpoint = Generic();
        endpoint.Headers["Cookie"] = "resolver-only=secret";
        Assert.Null(await new HttpAudioUrlResolver(factory).ResolveAsync(Request(endpoint), TestContextToken()));
        Assert.Equal(1, calls);
        using var resolverHandler = PublicAudioHttpTransport.CreateHandler();
        Assert.False(resolverHandler.AllowAutoRedirect);
        Assert.False(resolverHandler.UseCookies);
        Assert.False(resolverHandler.UseProxy);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:20::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("3fff::1")]
    public async Task Dns_answers_containing_non_public_addresses_are_rejected_even_with_a_public_answer(string address)
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => PublicAudioHttpTransport.ResolvePublicAddressesAsync(
            "media.example", CancellationToken.None, (host, token) =>
            {
                calls++;
                Assert.Equal("media.example", host);
                return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address) });
            }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Public_dns_answers_are_returned_for_connection_without_resolving_the_host_again()
    {
        var expected = new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700:4700::1111") };
        var calls = 0;
        var addresses = await PublicAudioHttpTransport.ResolvePublicAddressesAsync("media.example.", CancellationToken.None,
            (host, _) =>
            {
                calls++;
                Assert.Equal("media.example", host);
                return Task.FromResult(expected);
            });
        Assert.Same(expected, addresses);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Address_validation_propagates_dns_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublicAudioHttpTransport.ResolvePublicAddressesAsync(
            "media.example", cancellation.Token, async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            }));
    }

    [Fact]
    public async Task Generic_configuration_is_disabled_by_default_and_invalid_requests_make_no_http_call()
    {
        using var factory = new TestHttpFactory((_, _) => throw new InvalidOperationException("HTTP must not be called"));
        var resolver = new HttpAudioUrlResolver(factory);
        Assert.False(new HttpAudioUrlEndpointOptions().Enabled);
        Assert.True(new NeteaseVipFallbackEndpoint().Enabled);
        Assert.Null(await resolver.ResolveAsync(Request(new HttpAudioUrlEndpointOptions()), TestContextToken()));
        var endpoint = Generic();
        endpoint.Method = "DELETE";
        Assert.Null(await resolver.ResolveAsync(Request(endpoint), TestContextToken()));
        endpoint.Method = "GET";
        endpoint.Url = "file:///resolver.json";
        Assert.Null(await resolver.ResolveAsync(Request(endpoint), TestContextToken()));
    }

    [Fact]
    public async Task A_platform_http_client_cannot_be_selected_for_resolver_requests()
    {
        using var factory = new TestHttpFactory((_, _) => throw new InvalidOperationException("HTTP must not be called"));
        var request = Request(Generic()) with { HttpClientName = NeteaseSourceProvider.HttpClientName };
        Assert.Null(await new HttpAudioUrlResolver(factory).ResolveAsync(request, TestContextToken()));
        Assert.Empty(factory.RequestedNames);
    }

    [Theory]
    [InlineData("Cookie", "session=secret")]
    [InlineData("Authorization", "Bearer secret")]
    [InlineData("Referer", "https://music.example/\r\nX-Injected: secret")]
    public async Task Sensitive_or_injected_download_headers_are_rejected(string name, string value)
    {
        using var factory = new TestHttpFactory((_, _) => Task.FromResult(Json("{\"url\":\"https://media.example/audio.mp3\"}")));
        var endpoint = Generic();
        endpoint.DownloadHeaders[name] = value;

        Assert.Null(await new HttpAudioUrlResolver(factory).ResolveAsync(Request(endpoint), TestContextToken()));
    }

    [Fact]
    public async Task Download_headers_exceeding_the_transfer_limit_are_rejected_during_resolution()
    {
        using var factory = new TestHttpFactory((_, _) => Task.FromResult(Json("{\"url\":\"https://media.example/audio.mp3\"}")));
        var endpoint = Generic();
        endpoint.DownloadHeaders["Referer"] = new string('x', 4097);
        Assert.Null(await new HttpAudioUrlResolver(factory).ResolveAsync(Request(endpoint), TestContextToken()));
    }

    [Fact]
    public async Task Oversized_json_is_rejected_before_parsing()
    {
        var diagnostics = new List<AudioUrlResolveDiagnostic>();
        using var factory = new TestHttpFactory((_, _) => Task.FromResult(Json(new string('x', 1024 * 1024 + 1))));

        var result = await new HttpAudioUrlResolver(factory).ResolveAsync(Request(Generic()) with { Diagnostic = diagnostics.Add }, TestContextToken());

        Assert.Null(result);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "response_too_large");
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_an_unavailable_source()
    {
        using var factory = new TestHttpFactory(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpAudioUrlResolver(factory).ResolveAsync(Request(Generic()), cancellation.Token));
    }

    private static HttpAudioUrlEndpointOptions Generic() => new() { Enabled = true, Url = "https://resolver.example/url" };
    [Fact]
    public async Task A_256_level_preserves_its_bitrate_declaration()
    {
        using var factory = new TestHttpFactory((_, _) => Task.FromResult(Json("{\"url\":\"https://media.example/audio.mp3\"}")));
        var result = await new HttpAudioUrlResolver(factory).ResolveAsync(Request(Generic()) with { Level = "256" }, TestContextToken());

        Assert.NotNull(result);
        Assert.Equal(256, result.BitrateKbps);
        Assert.False(result.IsLossless);
    }

    private static AudioUrlResolveRequest Request(HttpAudioUrlEndpointOptions endpoint) => new("netease", "track/1", "320", "resolver-one", endpoint, 5);
    private static CancellationToken TestContextToken() => CancellationToken.None;
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class TestHttpFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client;
        internal List<string> RequestedNames { get; } = [];

        internal TestHttpFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => client = new HttpClient(new TestHandler(respond));

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return client;
        }

        public void Dispose() => client.Dispose();
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
