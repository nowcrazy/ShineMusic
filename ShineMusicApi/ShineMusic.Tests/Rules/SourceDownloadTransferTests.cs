using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class SourceDownloadTransferTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("shine-transfer-").FullName;
    private string AudioPath => Path.Combine(directory, "attempt.mp3");

    [Fact]
    public async Task Stopping_after_first_lazy_candidate_does_not_resolve_later_candidates()
    {
        var first = Candidate("first");
        var provider = new LazyProvider(first, Candidate("second"));
        await using var iterator = SourceDownloadJob.EnumerateDownloadCandidatesAsync(
            provider, "track", null, [AudioQualityLevel.High320K], CancellationToken.None).GetAsyncEnumerator();

        Assert.Equal(0, provider.ResolvedCount);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Same(first, iterator.Current);
        Assert.Equal(1, provider.ResolvedCount);
        Assert.Equal(0, provider.MaterializedCount);
    }

    [Fact]
    public async Task Existing_ladder_providers_keep_the_configured_quality_order()
    {
        var high = Candidate("high");
        var lossless = Candidate("lossless") with { Container = "flac", IsLossless = true, BitrateKbps = 900, Quality = SourceQualities.Lossless };
        var provider = new LadderProvider(high, lossless);
        var actual = new List<SourceDownloadInfo>();
        await foreach (var candidate in SourceDownloadJob.EnumerateDownloadCandidatesAsync(
            provider, "track", null, [AudioQualityLevel.Lossless, AudioQualityLevel.High320K], CancellationToken.None))
            actual.Add(candidate);

        Assert.Equal(new[] { lossless, high }, actual);
        Assert.Equal(1, provider.MaterializedCount);
    }

    [Fact]
    public async Task An_expiring_link_is_refreshed_before_any_download_request()
    {
        var original = Candidate("old") with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var refreshed = original with { DownloadUrl = "https://cdn.example.test/new", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5) };
        var provider = new LazyProvider(original) { Refresh = _ => refreshed };
        using var handler = new RecordingHandler(_ => Response([1, 2, 3]));
        using var client = new HttpClient(handler);

        var result = await DownloadAsync(client, original, provider);

        Assert.Equal(1, provider.RefreshedCount);
        Assert.Equal(["/new"], handler.Paths);
        Assert.Same(refreshed, result.Candidate);
        Assert.Equal(3, result.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant(), result.ContentHash);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task Expired_status_refreshes_once_then_downloads_the_same_resource(HttpStatusCode status)
    {
        var original = Candidate("old");
        var refreshed = original with { DownloadUrl = "https://cdn.example.test/new" };
        var provider = new LazyProvider(original) { Refresh = _ => refreshed };
        var failedContent = new TrackingContent([9]);
        var succeededContent = new TrackingContent([1, 2]);
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/old"
            ? new HttpResponseMessage(status) { Content = failedContent }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = succeededContent });
        using var client = new HttpClient(handler);

        var result = await DownloadAsync(client, original, provider);

        Assert.Equal(1, provider.RefreshedCount);
        Assert.Equal(["/old", "/new"], handler.Paths);
        Assert.Same(refreshed, result.Candidate);
        Assert.True(failedContent.IsDisposed);
        Assert.True(succeededContent.IsDisposed);
        Assert.True(succeededContent.Body.IsDisposed);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task A_second_expired_status_does_not_start_another_refresh(HttpStatusCode status)
    {
        var original = Candidate("old");
        var provider = new LazyProvider(original) { Refresh = previous => previous with { DownloadUrl = "https://cdn.example.test/new" } };
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(status) { Content = new TrackingContent([9]) });
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client, original, provider));

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(1, provider.RefreshedCount);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task Proactive_refresh_and_status_refresh_share_one_budget()
    {
        var original = Candidate("old") with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var provider = new LazyProvider(original)
        {
            Refresh = previous => previous with { DownloadUrl = "https://cdn.example.test/new", ExpiresAtUtc = null }
        };
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client, original, provider));

        Assert.Equal(1, provider.RefreshedCount);
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("resolver")]
    [InlineData("quality")]
    [InlineData("container")]
    [InlineData("bitrate")]
    [InlineData("lossless")]
    [InlineData("isolation")]
    public async Task Refresh_cannot_switch_the_resource_quality_or_client_boundary(string changed)
    {
        var original = Candidate("old") with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var provider = new LazyProvider(original)
        {
            Refresh = previous => changed switch
            {
                "resource" => previous with { ResourceId = "different-track" },
                "resolver" => previous with { ResolverId = "different-source" },
                "quality" => previous with { Quality = SourceQualities.Standard },
                "container" => previous with { Container = "flac" },
                "bitrate" => previous with { BitrateKbps = 128 },
                "lossless" => previous with { IsLossless = true },
                _ => previous with { UseIsolatedDownloadClient = !previous.UseIsolatedDownloadClient }
            }
        };
        using var handler = new RecordingHandler(_ => Response([1]));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<SourceUnavailableException>(() => DownloadAsync(client, original, provider));

        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task A_status_without_a_refresh_provider_is_not_retried()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client, Candidate("old"), null));

        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task Allowed_headers_are_applied_to_the_download_request()
    {
        var candidate = Candidate("headers") with
        {
            RequestHeaders = new Dictionary<string, string>
            {
                ["Referer"] = "https://music.example.test/",
                ["User-Agent"] = "ShineTransferTest/1.0",
                ["Accept"] = "audio/*",
                ["Accept-Language"] = "zh-CN",
                ["Origin"] = "https://music.example.test"
            }
        };
        using var handler = new RecordingHandler(request =>
        {
            foreach (var (name, value) in candidate.RequestHeaders!)
                Assert.Equal(value, Assert.Single(request.Headers.GetValues(name)));
            return Response([1]);
        });
        using var client = new HttpClient(handler);

        await DownloadAsync(client, candidate, null);

        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("Cookie", "secret=value")]
    [InlineData("Authorization", "Bearer secret")]
    [InlineData("Host", "other.example.test")]
    [InlineData("Range", "bytes=10-")]
    [InlineData("Content-Length", "4")]
    [InlineData("Referer", "https://music.example.test/\r\nCookie: secret=value")]
    public async Task Unsupported_or_injected_headers_are_rejected_before_transmission(string name, string value)
    {
        var candidate = Candidate("headers") with { RequestHeaders = new Dictionary<string, string> { [name] = value } };
        using var handler = new RecordingHandler(_ => Response([1]));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<SourceUnavailableException>(() => DownloadAsync(client, candidate, null));

        Assert.Empty(handler.Paths);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Precancelled_transfer_neither_refreshes_nor_sends_a_request()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var candidate = Candidate("cancelled") with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var provider = new LazyProvider(candidate) { Refresh = previous => previous with { ExpiresAtUtc = null } };
        using var handler = new RecordingHandler(_ => Response([1]));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadAsync(client, candidate, provider, cancellationToken: cancellation.Token));

        Assert.Empty(handler.Paths);
        Assert.Equal(0, provider.RefreshedCount);
    }

    [Fact]
    public async Task Cancellation_during_body_read_disposes_content_and_stream()
    {
        using var cancellation = new CancellationTokenSource();
        var content = new TrackingContent([1, 2, 3]);
        content.Body.BeforeRead = cancellation.Cancel;
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadAsync(client, Candidate("cancelled"), null, cancellationToken: cancellation.Token));

        Assert.True(content.IsDisposed);
        Assert.True(content.Body.IsDisposed);
    }

    [Fact]
    public async Task Declared_size_limit_rejects_before_opening_the_output_and_disposes_response()
    {
        var content = new TrackingContent([1, 2, 3]);
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<SourceDownloadTooLargeException>(() => DownloadAsync(client, Candidate("large"), null, maxBytes: 2));

        Assert.False(File.Exists(AudioPath));
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task Undeclared_extra_bytes_still_obey_the_streaming_size_limit()
    {
        var content = new TrackingContent([1, 2, 3], declaredLength: 1);
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<SourceDownloadTooLargeException>(() => DownloadAsync(client, Candidate("large"), null, maxBytes: 2));

        Assert.True(content.IsDisposed);
        Assert.True(content.Body.IsDisposed);
    }

    [Fact]
    public async Task A_short_response_is_not_accepted_as_a_complete_download()
    {
        var contents = new List<TrackingContent>();
        using var handler = new RecordingHandler(_ =>
        {
            var content = new TrackingContent([1, 2], declaredLength: 3);
            contents.Add(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client, Candidate("short"), null));

        Assert.Equal(3, contents.Count);
        Assert.All(contents, content => { Assert.True(content.IsDisposed); Assert.True(content.Body.IsDisposed); });
    }

    [Fact]
    public async Task Download_errors_do_not_expose_signed_urls_in_task_logs()
    {
        using var handler = new RecordingHandler(_ => throw new HttpRequestException("failed https://cdn.example.test/audio?token=secret"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client, Candidate("error"), null));

        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task A_full_file_request_rejects_partial_or_empty_success_responses(HttpStatusCode status)
    {
        var content = new TrackingContent([1, 2]);
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(status) { Content = content });
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client, Candidate("partial"), null));

        Assert.Equal(status, exception.StatusCode);
        Assert.False(File.Exists(AudioPath));
        Assert.True(content.IsDisposed);
    }

    [Theory]
    [InlineData("http://127.0.0.1/audio")]
    [InlineData("http://169.254.169.254/audio")]
    [InlineData("http://[::ffff:192.168.1.1]/audio")]
    public async Task Isolated_audio_candidates_cannot_request_private_addresses(string url)
    {
        using var handler = new RecordingHandler(_ => Response([1]));
        using var client = new HttpClient(handler);
        var candidate = Candidate("private") with { DownloadUrl = url, UseIsolatedDownloadClient = true };

        await Assert.ThrowsAsync<SourceUnavailableException>(() => DownloadAsync(client, candidate, null));

        Assert.Empty(handler.Paths);
        Assert.False(File.Exists(AudioPath));
    }

    [Fact]
    public async Task Interrupted_network_body_is_a_retryable_request_failure_without_leaking_the_url()
    {
        var contents = new List<TrackingContent>();
        using var handler = new RecordingHandler(_ =>
        {
            var content = new TrackingContent([1, 2]);
            content.Body.BeforeRead = () => throw new IOException("failed https://cdn.example/audio?token=secret");
            contents.Add(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client, Candidate("interrupted"), null));

        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Equal(3, contents.Count);
        Assert.All(contents, content => { Assert.True(content.IsDisposed); Assert.True(content.Body.IsDisposed); });
    }

    private Task<(SourceDownloadInfo Candidate, string ContentHash, long SizeBytes)> DownloadAsync(
        HttpClient client, SourceDownloadInfo candidate, ILazySourceDownloadProvider? provider,
        long maxBytes = 100, CancellationToken cancellationToken = default)
        => SourceDownloadJob.DownloadCandidateAsync(client, candidate, provider, "track", AudioPath, maxBytes, cancellationToken);

    private static SourceDownloadInfo Candidate(string name) =>
        new($"https://cdn.example.test/{name}", "mp3", false, 320, SourceQualities.High)
        { ResolverId = "resolver", ResourceId = "provider:track:320" };

    private static HttpResponseMessage Response(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new TrackingContent(bytes) };

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private class TestProvider(SourceDownloadInfo[] candidates) : IMusicSourceProvider
    {
        protected SourceDownloadInfo[] Candidates { get; } = candidates;
        public string Provider => "test";
        public bool IsEnabled => true;
        public string? DisabledReason => null;
        public Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<SourceSearchResult>());
        public Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken) => Task.FromResult<SourceSearchResult?>(null);
        public Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken) => Task.FromResult(Candidates[0]);
    }

    private class LadderProvider(params SourceDownloadInfo[] candidates) : TestProvider(candidates), IQualityLadderProvider
    {
        public int MaterializedCount { get; private set; }
        public Task<IReadOnlyList<SourceDownloadInfo>> ResolveDownloadCandidatesAsync(string providerTrackId, CancellationToken cancellationToken)
        {
            MaterializedCount++;
            return Task.FromResult<IReadOnlyList<SourceDownloadInfo>>(Candidates);
        }
    }

    private sealed class LazyProvider(params SourceDownloadInfo[] candidates) : LadderProvider(candidates), ILazySourceDownloadProvider
    {
        public int ResolvedCount { get; private set; }
        public int RefreshedCount { get; private set; }
        public Func<SourceDownloadInfo, SourceDownloadInfo?>? Refresh { get; init; }
        public async IAsyncEnumerable<SourceDownloadInfo> ResolveDownloadCandidatesLazilyAsync(
            string providerTrackId, IReadOnlyList<AudioQualityLevel> qualityChain,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var candidate in Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResolvedCount++;
                yield return candidate;
                await Task.Yield();
            }
        }
        public Task<SourceDownloadInfo?> RefreshDownloadAsync(string providerTrackId, SourceDownloadInfo previous, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshedCount++;
            return Task.FromResult(Refresh?.Invoke(previous));
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        public TrackingStream Body { get; }
        public bool IsDisposed { get; private set; }
        public TrackingContent(byte[] bytes, long? declaredLength = null)
        {
            Body = new TrackingStream(bytes);
            Headers.ContentLength = declaredLength ?? bytes.LongLength;
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(Body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Body.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? 0;
            return true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
                Body.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool IsDisposed { get; private set; }
        public Action? BeforeRead { get; set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
