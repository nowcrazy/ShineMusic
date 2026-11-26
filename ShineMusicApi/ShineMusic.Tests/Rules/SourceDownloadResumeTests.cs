using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class SourceDownloadResumeTests : IDisposable
{
    private static readonly byte[] Original = [1, 2, 3, 4, 5, 6, 7, 8];
    private readonly string directory = Directory.CreateTempSubdirectory("shine-resume-").FullName;
    private string AudioPath => Path.Combine(directory, "attempt.mp3");
    private static SourceDownloadInfo Candidate => new("https://cdn.example.test/old", "mp3", false, 320, SourceQualities.High)
        { ResolverId = "resolver", ResourceId = "provider:track:320" };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Interrupted_or_short_body_resumes_at_written_offset_and_hashes_the_whole_file(bool throwOnRead)
    {
        using var handler = new Handler((_, call) => call == 1
            ? Full(new Body(Original[..3], throwOnRead), Original.Length, "\"v1\"")
            : Partial(Original[3..], 3, 7, 8));
        using var client = new HttpClient(handler);

        var result = await DownloadAsync(client);

        Assert.Equal([null, "bytes=3-"], handler.Ranges);
        Assert.Equal([null, "\"v1\""], handler.IfRanges);
        Assert.Equal(Original, await File.ReadAllBytesAsync(AudioPath));
        Assert.Equal(8, result.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Original)).ToLowerInvariant(), result.ContentHash);
        Assert.All(handler.Contents, content => Assert.True(content.Body.IsDisposed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"v1\"")]
    public async Task Missing_or_weak_validator_restarts_the_file_without_range(string? tag)
    {
        byte[] replacement = [8, 7, 6, 5];
        using var handler = new Handler((_, call) => call == 1
            ? Full(new Body(Original[..3], true), Original.Length, tag)
            : Full(new Body(replacement), replacement.Length, "\"v2\""));
        using var client = new HttpClient(handler);

        var result = await DownloadAsync(client);

        Assert.Equal([null, null], handler.Ranges);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(AudioPath));
        Assert.Equal(4, result.SizeBytes);
    }

    [Fact]
    public async Task Range_ignored_with_200_truncates_the_previous_partial_representation()
    {
        byte[] replacement = [8, 7, 6, 5];
        using var handler = new Handler((_, call) => call == 1
            ? Full(new Body(Original[..3], true), Original.Length, "\"v1\"")
            : Full(new Body(replacement), replacement.Length, "\"v2\""));
        using var client = new HttpClient(handler);

        var result = await DownloadAsync(client);

        Assert.Equal([null, "bytes=3-"], handler.Ranges);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(AudioPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(replacement)).ToLowerInvariant(), result.ContentHash);
    }

    [Theory]
    [InlineData("\"v2\"")]
    [InlineData("W/\"v1\"")]
    [InlineData(null)]
    public async Task Changed_or_missing_resume_validator_discards_partial_and_requests_a_new_full_file(string? tag)
    {
        byte[] replacement = [8, 7, 6, 5, 4, 3, 2, 1];
        using var handler = new Handler((_, call) => call switch
        {
            1 => Full(new Body(Original[..3], true), Original.Length, "\"v1\""),
            2 => Partial(replacement[3..], 3, 7, 8, tag),
            _ => Full(new Body(replacement), replacement.Length, "\"v2\"")
        });
        using var client = new HttpClient(handler);

        await DownloadAsync(client);

        Assert.Equal([null, "bytes=3-", null], handler.Ranges);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(AudioPath));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("total")]
    [InlineData("length")]
    [InlineData("missing")]
    [InlineData("unit")]
    public async Task Invalid_206_headers_are_rejected_before_the_body_is_appended(string invalid)
    {
        using var handler = new Handler((_, call) =>
        {
            if (call == 1) return Full(new Body(Original[..3], true), Original.Length, "\"v1\"");
            var response = Partial(Original[3..], invalid == "start" ? 2 : 3,
                invalid == "end" ? 6 : 7, invalid == "total" ? 9 : 8);
            if (invalid == "length") response.Content.Headers.ContentLength = 4;
            if (invalid == "missing") response.Content.Headers.ContentRange = null;
            if (invalid == "unit") response.Content.Headers.ContentRange!.Unit = "items";
            return response;
        });
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client));

        Assert.Equal(HttpStatusCode.PartialContent, error.StatusCode);
        Assert.Equal(2, handler.Ranges.Count);
        Assert.Equal(Original[..3], await File.ReadAllBytesAsync(AudioPath));
        Assert.All(handler.Contents, content => Assert.True(content.Body.IsDisposed));
    }

    [Fact]
    public async Task A_second_interruption_resumes_the_updated_offset_with_the_same_validator()
    {
        using var handler = new Handler((_, call) => call switch
        {
            1 => Full(new Body(Original[..3], true), Original.Length, "\"v1\""),
            2 => Partial(Original[3..5], 3, 7, 8, bodyFails: true),
            _ => Partial(Original[5..], 5, 7, 8)
        });
        using var client = new HttpClient(handler);

        await DownloadAsync(client);

        Assert.Equal([null, "bytes=3-", "bytes=5-"], handler.Ranges);
        Assert.Equal(Original, await File.ReadAllBytesAsync(AudioPath));
    }

    [Fact]
    public async Task Recovery_attempts_are_bounded_even_when_each_request_makes_progress()
    {
        using var handler = new Handler((_, call) => call == 1
            ? Full(new Body(Original[..1], true), Original.Length, "\"v1\"")
            : Partial(Original[(call - 1)..call], call - 1, 7, 8, bodyFails: true));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client));

        Assert.Equal([null, "bytes=1-", "bytes=2-"], handler.Ranges);
        Assert.All(handler.Contents, content => Assert.True(content.Body.IsDisposed));
    }

    [Fact]
    public async Task Expired_link_during_resume_refreshes_once_and_restarts_without_range()
    {
        byte[] replacement = [8, 7, 6, 5];
        var provider = new RefreshProvider();
        using var handler = new Handler((_, call) => call switch
        {
            1 => Full(new Body(Original[..3], true), Original.Length, "\"v1\""),
            2 => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => Full(new Body(replacement), replacement.Length, "\"v2\"")
        });
        using var client = new HttpClient(handler);

        var result = await DownloadAsync(client, provider);

        Assert.Equal(1, provider.RefreshCount);
        Assert.Equal(["/old", "/old", "/new"], handler.Paths);
        Assert.Equal([null, "bytes=3-", null], handler.Ranges);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(AudioPath));
        Assert.EndsWith("/new", result.Candidate.DownloadUrl);
    }

    [Fact]
    public async Task Repeated_expired_status_after_resume_does_not_refresh_again()
    {
        var provider = new RefreshProvider();
        using var handler = new Handler((_, call) => call == 1
            ? Full(new Body(Original[..3], true), Original.Length, "\"v1\"")
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(client, provider));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(1, provider.RefreshCount);
        Assert.Equal(3, handler.Paths.Count);
    }

    [Fact]
    public async Task Cancellation_after_partial_progress_never_starts_a_recovery_request()
    {
        using var cancellation = new CancellationTokenSource();
        var body = new Body(Original[..3], true) { OnEnd = cancellation.Cancel };
        using var handler = new Handler((_, _) => Full(body, Original.Length, "\"v1\""));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadAsync(client, cancellationToken: cancellation.Token));

        Assert.Single(handler.Ranges);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task Replacement_full_response_still_obeys_the_total_file_size_limit()
    {
        using var handler = new Handler((_, call) => call == 1
            ? Full(new Body(Original[..3], true), Original.Length, "\"v1\"")
            : Full(new Body(new byte[9]), 9, "\"v2\""));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<SourceDownloadTooLargeException>(() => DownloadAsync(client, maxBytes: 8));

        Assert.Equal(Original[..3], await File.ReadAllBytesAsync(AudioPath));
        Assert.All(handler.Contents, content => Assert.True(content.Body.IsDisposed));
    }

    [Fact]
    public async Task Transient_server_error_is_retried_but_not_a_permanent_not_found()
    {
        using var handler = new Handler((_, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Full(new Body(Original), Original.Length, "\"v1\""));
        using var client = new HttpClient(handler);
        await DownloadAsync(client);
        Assert.Equal(2, handler.Paths.Count);

        using var missing = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var missingClient = new HttpClient(missing);
        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(missingClient));
        Assert.Single(missing.Paths);
    }

    private Task<(SourceDownloadInfo Candidate, string ContentHash, long SizeBytes)> DownloadAsync(
        HttpClient client, ILazySourceDownloadProvider? provider = null, long maxBytes = 100, CancellationToken cancellationToken = default)
        => SourceDownloadJob.DownloadCandidateAsync(client, Candidate, provider, "track", AudioPath, maxBytes, cancellationToken);

    private static HttpResponseMessage Full(Body body, long length, string? tag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(body, length) };
        if (tag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
        return response;
    }

    private static HttpResponseMessage Partial(byte[] bytes, long from, long to, long length, string? tag = "\"v1\"", bool bodyFails = false)
    {
        var response = Full(new Body(bytes, bodyFails), to - from + 1, tag);
        response.StatusCode = HttpStatusCode.PartialContent;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, length);
        return response;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string?> Ranges { get; } = [];
        public List<string?> IfRanges { get; } = [];
        public List<string> Paths { get; } = [];
        public List<Content> Contents { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Ranges.Add(request.Headers.Range?.ToString());
            IfRanges.Add(request.Headers.IfRange?.ToString());
            Paths.Add(request.RequestUri!.AbsolutePath);
            var response = respond(request, Ranges.Count);
            if (response.Content is Content content) Contents.Add(content);
            return Task.FromResult(response);
        }
    }

    private sealed class Content(Body body, long length) : HttpContent
    {
        public Body Body { get; } = body;
        protected override bool TryComputeLength(out long computed) { computed = length; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(Body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Body.CopyToAsync(stream);
        protected override void Dispose(bool disposing) { if (disposing) Body.Dispose(); base.Dispose(disposing); }
    }

    private sealed class Body(byte[] bytes, bool failAtEnd = false) : MemoryStream(bytes)
    {
        public bool IsDisposed { get; private set; }
        public Action? OnEnd { get; init; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length)
            {
                OnEnd?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (failAtEnd) throw new IOException("interrupted https://cdn.example.test/audio?token=secret");
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) IsDisposed = true; base.Dispose(disposing); }
    }

    private sealed class RefreshProvider : IMusicSourceProvider, IQualityLadderProvider, ILazySourceDownloadProvider
    {
        public int RefreshCount { get; private set; }
        public string Provider => "test";
        public bool IsEnabled => true;
        public string? DisabledReason => null;
        public Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<SourceSearchResult>());
        public Task<SourceSearchResult?> GetTrackAsync(string id, CancellationToken cancellationToken) => Task.FromResult<SourceSearchResult?>(null);
        public Task<SourceDownloadInfo> ResolveDownloadAsync(string id, CancellationToken cancellationToken) => Task.FromResult(Candidate);
        public Task<SourceDownloadInfo?> RefreshDownloadAsync(string id, SourceDownloadInfo previous, CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.FromResult<SourceDownloadInfo?>(previous with { DownloadUrl = "https://cdn.example.test/new" });
        }
        public Task<IReadOnlyList<SourceDownloadInfo>> ResolveDownloadCandidatesAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SourceDownloadInfo>>([Candidate]);
        public async IAsyncEnumerable<SourceDownloadInfo> ResolveDownloadCandidatesLazilyAsync(string id,
            IReadOnlyList<AudioQualityLevel> qualityChain, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return Candidate;
        }
    }
}
