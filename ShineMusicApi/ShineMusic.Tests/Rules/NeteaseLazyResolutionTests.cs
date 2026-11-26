using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class NeteaseLazyResolutionTests
{
    [Fact]
    public async Task Taking_official_candidate_does_not_resolve_fallbacks()
    {
        using var factory = new FakeHttpClientFactory((_, _) => Official("https://cdn.example/official.mp3"));
        var resolver = new RecordingResolver((_, _) => throw new InvalidOperationException("Fallback must remain lazy."));
        var provider = Create(factory, resolver);

        await using var candidates = provider.ResolveDownloadCandidatesLazilyAsync("42", [], default).GetAsyncEnumerator();
        Assert.Equal(0, factory.RequestCount);
        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal("https://cdn.example/official.mp3", candidates.Current.DownloadUrl);
        Assert.True(candidates.Current.UseIsolatedDownloadClient);
        Assert.Empty(resolver.Requests);
        Assert.Equal(1, factory.RequestCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"url\":null}]}")]
    public async Task Missing_official_data_continues_to_first_fallback_only(string officialJson)
    {
        using var factory = new FakeHttpClientFactory((_, _) => officialJson);
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(Candidate(request.Level)));
        var provider = Create(factory, resolver);

        var result = await provider.ResolveDownloadAsync("42", default);

        Assert.Equal("https://cdn.example/320.mp3", result.DownloadUrl);
        Assert.Equal("320", Assert.Single(resolver.Requests).Level);
        Assert.Equal(HttpAudioUrlResolver.HttpClientName, resolver.Requests[0].HttpClientName);
    }

    [Fact]
    public async Task Exhausted_candidate_enumeration_resolves_each_tier_then_sources_in_configuration_order()
    {
        using var factory = new FakeHttpClientFactory((request, _) => OfficialForRequest(request));
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(
            Candidate(request.Level) with { DownloadUrl = $"https://cdn.example/{request.Endpoint.Id}-{request.Level}.{Candidate(request.Level).Container}" }));
        var options = Settings();
        options.Netease.VipFallback.Endpoints.Add(new NeteaseVipFallbackEndpoint
        {
            Id = "second", Url = "https://resolver.example/second?id={id}", Levels = ["320", "999"]
        });

        var results = new List<SourceDownloadInfo>();
        await foreach (var candidate in Create(factory, resolver, options).ResolveDownloadCandidatesLazilyAsync(
            "42", [AudioQualityLevel.High320K, AudioQualityLevel.Lossless], default))
            results.Add(candidate);

        Assert.Equal(6, results.Count);
        Assert.Equal(["first:320", "second:320", "first:999", "second:999"],
            resolver.Requests.Select(request => $"{request.Endpoint.Id}:{request.Level}").ToArray());
        Assert.Equal(["https://cdn.example/official-320.mp3", "https://cdn.example/first-320.mp3",
            "https://cdn.example/second-320.mp3", "https://cdn.example/official-flac.flac",
            "https://cdn.example/first-999.flac", "https://cdn.example/second-999.flac"],
            results.Select(result => result.DownloadUrl).ToArray());
        Assert.Equal(["320000", "999000"], factory.RequestUris.Select(uri =>
            Uri.UnescapeDataString(uri.Query).Split("br=")[1]).ToArray());
    }

    [Theory]
    [InlineData(false, "320,999")]
    [InlineData(true, "999,320")]
    public async Task Quality_chain_controls_lazy_candidate_order(bool losslessFirst, string expectedLevels)
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(Candidate(request.Level)));
        var provider = Create(factory, resolver);
        var chain = losslessFirst
            ? new[] { AudioQualityLevel.Lossless, AudioQualityLevel.High320K }
            : new[] { AudioQualityLevel.High320K, AudioQualityLevel.Lossless };

        await using var candidates = provider.ResolveDownloadCandidatesLazilyAsync("42", chain, default).GetAsyncEnumerator();
        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal(expectedLevels.Split(',')[0], resolver.Requests[0].Level);
        Assert.Single(resolver.Requests);
        Assert.Contains(expectedLevels.Split(',')[0] == "999" ? "br=999000" : "br=320000",
            factory.RequestUris[0].Query);
        Assert.Single(factory.RequestUris);
    }

    [Fact]
    public async Task Official_downgrade_does_not_preempt_requested_tier_fallback()
    {
        using var factory = new FakeHttpClientFactory((_, _) => Official("https://cdn.example/preview.mp3", 128000));
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(Candidate(request.Level)));
        var provider = Create(factory, resolver);

        await using var candidates = provider.ResolveDownloadCandidatesLazilyAsync(
            "42", [AudioQualityLevel.High320K], default).GetAsyncEnumerator();

        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal("https://cdn.example/320.mp3", candidates.Current.DownloadUrl);
        Assert.Equal("320", Assert.Single(resolver.Requests).Level);
        Assert.Equal(1, factory.RequestCount);
        Assert.False(await candidates.MoveNextAsync());
    }

    [Fact]
    public async Task Official_256_downgrade_is_retained_until_below320_tier_before_requesting_192()
    {
        using var factory = new FakeHttpClientFactory((request, _) => request.RequestUri!.Query switch
        {
            var query when query.Contains("br=320000", StringComparison.Ordinal) => Official("https://cdn.example/256.mp3", 256000),
            var query when query.Contains("br=192000", StringComparison.Ordinal) => Official("https://cdn.example/192.mp3", 192000),
            _ => "{}"
        });
        var resolver = new RecordingResolver((_, _) => throw new InvalidOperationException("Fallback is disabled."));
        var options = Settings();
        options.Netease.VipFallback.Enabled = false;
        options.ProviderFramework.QualityPolicy.MinimumQuality = "256K";
        var qualityGate = new QualityGate(Options.Create(options));
        await using var candidates = Create(factory, resolver, options).ResolveDownloadCandidatesLazilyAsync(
            "42", qualityGate.GetAllowedQualityChain(), default).GetAsyncEnumerator();

        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal(256, candidates.Current.BitrateKbps);
        Assert.True(qualityGate.IsCandidateAllowed(candidates.Current.IsLossless, candidates.Current.BitrateKbps));
        Assert.Equal(2, factory.RequestCount);
        Assert.DoesNotContain(factory.RequestUris, uri => uri.Query.Contains("br=192000", StringComparison.Ordinal));

        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal(192, candidates.Current.BitrateKbps);
        Assert.False(qualityGate.IsCandidateAllowed(candidates.Current.IsLossless, candidates.Current.BitrateKbps));
        Assert.Equal(3, factory.RequestCount);
        Assert.False(await candidates.MoveNextAsync());
    }

    [Fact]
    public async Task Fallback_320_request_returning_256_is_retained_for_allowed_below320_tier()
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(
            Candidate(request.Level) with { BitrateKbps = 256 }));
        var options = Settings();
        options.Netease.VipFallback.Endpoints[0].Levels = ["320"];
        options.ProviderFramework.QualityPolicy.MinimumQuality = "256K";
        var qualityGate = new QualityGate(Options.Create(options));
        await using var candidates = Create(factory, resolver, options).ResolveDownloadCandidatesLazilyAsync(
            "42", qualityGate.GetAllowedQualityChain(), default).GetAsyncEnumerator();

        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal(256, candidates.Current.BitrateKbps);
        Assert.True(qualityGate.IsCandidateAllowed(candidates.Current.IsLossless, candidates.Current.BitrateKbps));
        Assert.Equal("320", Assert.Single(resolver.Requests).Level);
        Assert.Equal(2, factory.RequestCount);
    }

    [Fact]
    public async Task Higher_tier_fallbacks_finish_before_any_deferred_downgrade_is_yielded()
    {
        using var factory = new FakeHttpClientFactory((_, _) => Official("https://cdn.example/official-256.mp3", 256000));
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(
            Candidate(request.Level) with
            {
                DownloadUrl = $"https://cdn.example/{request.Endpoint.Id}.mp3",
                BitrateKbps = request.Endpoint.Id == "first" ? 256 : 320
            }));
        var options = Settings();
        options.Netease.VipFallback.Endpoints[0].Levels = ["320"];
        options.Netease.VipFallback.Endpoints.Add(new NeteaseVipFallbackEndpoint
        {
            Id = "second", Url = "https://resolver.example/second?id={id}", Levels = ["320"]
        });
        await using var candidates = Create(factory, resolver, options).ResolveDownloadCandidatesLazilyAsync(
            "42", [AudioQualityLevel.High320K, AudioQualityLevel.Below320K], default).GetAsyncEnumerator();

        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal("https://cdn.example/second.mp3", candidates.Current.DownloadUrl);
        Assert.Equal(320, candidates.Current.BitrateKbps);
        Assert.Collection(resolver.Requests,
            request => Assert.Equal("first", request.Endpoint.Id),
            request => Assert.Equal("second", request.Endpoint.Id));
        Assert.Equal(1, factory.RequestCount);

        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal("https://cdn.example/official-256.mp3", candidates.Current.DownloadUrl);
        Assert.Equal(256, candidates.Current.BitrateKbps);
        Assert.Equal(1, factory.RequestCount);
    }

    [Fact]
    public async Task Deferred_candidate_does_not_hide_current_tier_candidate_with_the_same_url()
    {
        const string sharedUrl = "https://cdn.example/shared.mp3";
        using var factory = new FakeHttpClientFactory((_, _) => Official(sharedUrl, 256000));
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(
            Candidate(request.Level) with { DownloadUrl = sharedUrl }));
        var options = Settings();
        options.Netease.VipFallback.Endpoints[0].Levels = ["320"];
        await using var candidates = Create(factory, resolver, options).ResolveDownloadCandidatesLazilyAsync(
            "42", [AudioQualityLevel.High320K, AudioQualityLevel.Below320K], default).GetAsyncEnumerator();

        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal(sharedUrl, candidates.Current.DownloadUrl);
        Assert.Equal(320, candidates.Current.BitrateKbps);
        Assert.Single(resolver.Requests);
        Assert.Equal(1, factory.RequestCount);
        Assert.False(await candidates.MoveNextAsync());
    }

    [Fact]
    public async Task Numeric_256_fallback_level_is_requested_in_below320_tier()
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(
            Candidate(request.Level) with { BitrateKbps = 256 }));
        var options = Settings();
        options.Netease.VipFallback.Endpoints[0].Levels = ["256"];
        await using var candidates = Create(factory, resolver, options).ResolveDownloadCandidatesLazilyAsync(
            "42", [AudioQualityLevel.Below320K], default).GetAsyncEnumerator();

        Assert.True(await candidates.MoveNextAsync());
        Assert.Equal(256, candidates.Current.BitrateKbps);
        Assert.Equal("256", Assert.Single(resolver.Requests).Level);
    }

    [Fact]
    public async Task Lossless_only_chain_does_not_request_or_yield_lossy_fallbacks()
    {
        using var factory = new FakeHttpClientFactory((_, _) => Official("https://cdn.example/downgrade.mp3"));
        var resolver = new RecordingResolver((_, _) => Task.FromResult<SourceDownloadInfo?>(Candidate("320")));
        var provider = Create(factory, resolver);

        await using var candidates = provider.ResolveDownloadCandidatesLazilyAsync(
            "42", [AudioQualityLevel.Lossless], default).GetAsyncEnumerator();

        Assert.False(await candidates.MoveNextAsync());
        Assert.Equal("999", Assert.Single(resolver.Requests).Level);
        Assert.Contains("br=999000", Assert.Single(factory.RequestUris).Query);
    }

    [Theory]
    [InlineData(192000)]
    [InlineData(256000)]
    [InlineData(320000)]
    [InlineData(999000)]
    public async Task Compatibility_resolution_preserves_configured_official_bitrate(int bitrate)
    {
        using var factory = new FakeHttpClientFactory((_, _) => bitrate == 999000
            ? "{\"data\":[{\"url\":\"https://cdn.example/original.flac\",\"type\":\"flac\",\"br\":900000}]}"
            : Official("https://cdn.example/original.mp3", bitrate));
        var resolver = new RecordingResolver((_, _) => throw new InvalidOperationException("Official must remain first."));
        var options = Settings();
        options.Netease.Bitrate = bitrate;

        var candidate = await Create(factory, resolver, options).ResolveDownloadAsync("42", default);

        Assert.Contains($"br={bitrate}", Assert.Single(factory.RequestUris).Query);
        Assert.Equal(bitrate == 999000, candidate.IsLossless);
        Assert.True(candidate.UseIsolatedDownloadClient);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task Compatibility_resolution_retains_configured_below320_fallback()
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(
            Candidate(request.Level) with { BitrateKbps = 192, Quality = SourceQualities.Standard }));
        var options = Settings();
        options.Netease.VipFallback.Endpoints[0].Levels = ["192"];

        var candidate = await Create(factory, resolver, options).ResolveDownloadAsync("42", default);

        Assert.Equal(192, candidate.BitrateKbps);
        Assert.Equal("192", Assert.Single(resolver.Requests).Level);
        Assert.Equal(3, factory.RequestCount);
    }

    [Fact]
    public async Task Refresh_resolves_only_original_endpoint_and_level()
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        var sequence = 0;
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(
            Candidate(request.Level) with { DownloadUrl = $"https://cdn.example/{++sequence}.mp3" }));
        var provider = Create(factory, resolver);
        var original = await provider.ResolveDownloadAsync("42", default);

        var refreshed = await provider.RefreshDownloadAsync("42", original, default);

        Assert.NotNull(refreshed);
        Assert.Equal(original.ResourceId, refreshed.ResourceId);
        Assert.Equal(original.ResolverId, refreshed.ResolverId);
        Assert.NotEqual(original.DownloadUrl, refreshed.DownloadUrl);
        Assert.Equal(["320", "320"], resolver.Requests.Select(request => request.Level).ToArray());
        Assert.Equal(1, factory.RequestCount);
    }

    [Fact]
    public async Task Refresh_rejects_same_endpoint_id_after_configuration_changes()
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(Candidate(request.Level)));
        var options = Settings();
        var provider = Create(factory, resolver, options);
        var original = await provider.ResolveDownloadAsync("42", default);
        options.Netease.VipFallback.Endpoints[0].Url = "https://resolver.example/other?id={id}";

        var refreshed = await provider.RefreshDownloadAsync("42", original, default);

        Assert.Null(refreshed);
        Assert.Single(resolver.Requests);
    }

    [Fact]
    public async Task Refresh_rejects_a_changed_format_or_quality()
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        var calls = 0;
        var resolver = new RecordingResolver((request, _) => Task.FromResult<SourceDownloadInfo?>(
            ++calls == 1 ? Candidate(request.Level) : Candidate("999")));
        var provider = Create(factory, resolver);
        var original = await provider.ResolveDownloadAsync("42", default);

        Assert.Null(await provider.RefreshDownloadAsync("42", original, default));
        Assert.Equal(["320", "320"], resolver.Requests.Select(request => request.Level).ToArray());
    }

    [Fact]
    public async Task Official_refresh_rejects_a_lower_bitrate_and_does_not_switch_source()
    {
        using var factory = new FakeHttpClientFactory((request, index) => Official($"https://cdn.example/{index}.mp3",
            request.RequestUri!.Query.Contains("br=320000", StringComparison.Ordinal) && index == 1 ? 320000 : 128000));
        var resolver = new RecordingResolver((_, _) => throw new InvalidOperationException("Refresh cannot switch sources."));
        var provider = Create(factory, resolver);
        var original = await provider.ResolveDownloadAsync("42", default);

        Assert.Null(await provider.RefreshDownloadAsync("42", original, default));
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task Official_refresh_retains_original_lossless_request_after_configuration_changes()
    {
        using var factory = new FakeHttpClientFactory((request, _) => OfficialForRequest(request));
        var resolver = new RecordingResolver((_, _) => throw new InvalidOperationException("Refresh cannot switch sources."));
        var options = Settings();
        options.Netease.Bitrate = 999000;
        var provider = Create(factory, resolver, options);
        var original = await provider.ResolveDownloadAsync("42", default);
        options.Netease.Bitrate = 320000;

        var refreshed = await provider.RefreshDownloadAsync("42", original, default);

        Assert.NotNull(refreshed);
        Assert.True(refreshed.IsLossless);
        Assert.Equal(original.ResourceId, refreshed.ResourceId);
        Assert.All(factory.RequestUris, uri => Assert.Contains("br=999000", uri.Query));
        Assert.Equal(2, factory.RequestCount);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task Official_refresh_rejects_a_changed_endpoint_without_requesting_it()
    {
        using var factory = new FakeHttpClientFactory((_, _) => Official("https://cdn.example/original.mp3"));
        var resolver = new RecordingResolver((_, _) => throw new InvalidOperationException("Refresh cannot switch sources."));
        var options = Settings();
        var provider = Create(factory, resolver, options);
        var original = await provider.ResolveDownloadAsync("42", default);
        options.Netease.PlayerUrlBaseUrl = "https://other.example/player";

        Assert.Null(await provider.RefreshDownloadAsync("42", original, default));
        Assert.Single(factory.RequestUris);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task Cancellation_before_enumeration_does_not_resolve_any_source()
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        var resolver = new RecordingResolver((_, _) => throw new InvalidOperationException("No source may be called."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var candidates = Create(factory, resolver).ResolveDownloadCandidatesLazilyAsync(
            "42", [AudioQualityLevel.Unknown], cancellation.Token).GetAsyncEnumerator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await candidates.MoveNextAsync(); });
        Assert.Equal(0, factory.RequestCount);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task Cancellation_from_fallback_is_propagated_without_trying_next_level()
    {
        using var factory = new FakeHttpClientFactory((_, _) => "{}");
        using var cancellation = new CancellationTokenSource();
        var resolver = new RecordingResolver((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<SourceDownloadInfo?>(null);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(factory, resolver).ResolveDownloadAsync("42", cancellation.Token));
        Assert.Single(resolver.Requests);
    }

    private static NeteaseSourceProvider Create(FakeHttpClientFactory factory, IAudioUrlResolver resolver,
        SourceDownloadOptions? options = null)
        => new(factory, Options.Create(options ?? Settings()), NullLogger<NeteaseSourceProvider>.Instance, resolver);

    private static SourceDownloadOptions Settings()
    {
        var options = new SourceDownloadOptions();
        options.Netease.PlayerUrlBaseUrl = "https://official.example/player";
        options.Netease.VipFallback.Endpoints =
        [
            new NeteaseVipFallbackEndpoint
            {
                Id = "first", Url = "https://resolver.example/url?id={id}&level={level}", Levels = ["320", "999"]
            }
        ];
        return options;
    }

    private static SourceDownloadInfo Candidate(string level)
        => level == "999"
            ? new($"https://cdn.example/{level}.flac", "flac", true, 900, SourceQualities.Lossless)
            : new($"https://cdn.example/{level}.mp3", "mp3", false, 320, SourceQualities.High);

    private static string Official(string url, int bitrate = 320000)
        => $"{{\"data\":[{{\"url\":\"{url}\",\"type\":\"mp3\",\"br\":{bitrate}}}]}}";

    private static string OfficialForRequest(HttpRequestMessage request)
        => request.RequestUri!.Query.Contains("br=999000", StringComparison.Ordinal)
            ? "{\"data\":[{\"url\":\"https://cdn.example/official-flac.flac\",\"type\":\"flac\",\"br\":900000}]}"
            : "{\"data\":[{\"url\":\"https://cdn.example/official-320.mp3\",\"type\":\"mp3\",\"br\":320000}]}";

    private sealed class RecordingResolver(
        Func<AudioUrlResolveRequest, CancellationToken, Task<SourceDownloadInfo?>> resolve) : IAudioUrlResolver
    {
        public List<AudioUrlResolveRequest> Requests { get; } = [];

        public Task<SourceDownloadInfo?> ResolveAsync(AudioUrlResolveRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return resolve(request, cancellationToken);
        }
    }

    private sealed class FakeHttpClientFactory(Func<HttpRequestMessage, int, string> response) : IHttpClientFactory, IDisposable
    {
        private readonly FakeHandler handler = new(response);
        public int RequestCount => handler.RequestCount;
        public IReadOnlyList<Uri> RequestUris => handler.RequestUris;
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        public void Dispose() => handler.Dispose();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, int, string> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request, ++RequestCount), Encoding.UTF8, "application/json")
            });
        }
    }
}
