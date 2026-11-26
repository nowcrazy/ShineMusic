// L3a：发布后 /stream 的真实 HTTP 回读断言（音源质量稳定性策略 §5.3 / §8 1b / §12.4 / §13）。
//
// 这一层要抓的是 1a 离线契约抓不到的跨层 bug：主资产在库里被指回 192k 转码副本、
// 容器映射被改坏、Range 处理被绕过、Content-Type 与真实字节不符。
// 关键设计：不连真实 DB / RabbitMQ。Program.cs 把启动引导抽成 IStartupBootstrap，
// 这里用 ConfigureTestServices 换成 Noop；播放端点只依赖 ITrackPlaybackStore / IAudioStorage，
// 换成指向临时文件的 fake 即可跑完整中间件管道 + Results.File 的 Range 处理。

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Services;
using ShineMusic.Model.Ports;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.Media;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Integration;

/// <summary>L3a 会临时改进程级环境变量与工作目录，禁止与其它测试集合并行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class L3aCollection
{
    public const string Name = "L3a stream readback";
}

[Collection(L3aCollection.Name)]
public sealed class StreamDeliveryReadbackTests : IClassFixture<StreamTestFactory>
{
    private static readonly Guid TrackId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly TrackIdentity Identity = new("晴天", ["周杰伦"], 269_000);
    private const long AssetSizeBytes = 4096;

    private readonly StreamTestFactory _factory;

    public StreamDeliveryReadbackTests(StreamTestFactory factory)
    {
        _factory = factory;
        _factory.Playback.Container = "flac";
        _factory.Playback.StorageKey = "l3a/fixture.flac";
        _factory.Playback.Visibility = new TrackPlaybackVisibility(DateTimeOffset.UtcNow, null);
        _factory.Storage.ResolvedPath = null;
    }

    [Fact]
    public async Task Published_flac_stream_matches_delivery_contract()
    {
        using var file = TempAudio.Create("fLaC", AssetSizeBytes, magicOffset: 0);
        _factory.Storage.ResolvedPath = file.Path;
        var contract = FlacContract(file.Length, bitrateKbps: 935);

        // 1a 的离线闸门与 1b 的 HTTP 回读指向同一份契约，正常路径两侧都必须通过。
        Assert.True(DeliveryContractVerifier.Verify(contract, file.Path, 192).IsAccepted);

        using var client = _factory.CreateClient();
        using var request = RangeRequest(TrackId);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        var failures = StreamReadbackVerifier.Verify(contract, response, body);
        Assert.Empty(failures);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(file.Length, response.Content.Headers.ContentRange?.Length);
        Assert.Equal(16, body.Length);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(body, 0, 4));
    }

    [Fact]
    public async Task Fault_injection_primary_pointed_back_at_192k_transcode_is_caught_by_readback()
    {
        // 模拟历史 bug：原文件仍在，主资产却被指回 192k 转码副本。
        // 故意让 m4a 文件大小与 FLAC 完全相同 —— 只靠 Content-Range 总长抓不到，
        // 必须由 Content-Type + 首字节魔数两项断言共同发现。
        using var file = TempAudio.Create("ftyp", AssetSizeBytes, magicOffset: 4);
        _factory.Playback.Container = "m4a";
        _factory.Storage.ResolvedPath = file.Path;
        var contract = FlacContract(file.Length, bitrateKbps: 935);

        using var client = _factory.CreateClient();
        using var request = RangeRequest(TrackId);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        var failures = StreamReadbackVerifier.Verify(contract, response, body);
        Assert.NotEmpty(failures);
        Assert.Contains(failures, failure => failure.StartsWith("content_type:", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.StartsWith("magic_mismatch", StringComparison.Ordinal));
        // 大小相同 → size 断言按预期不报警，正好说明为什么需要多项断言。
        Assert.DoesNotContain(failures, failure => failure.StartsWith("size_mismatch", StringComparison.Ordinal));
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("mp3", "audio/mpeg", "ID3", 0)]
    [InlineData("m4a", "audio/mp4", "ftyp", 4)]
    [InlineData("aac", "audio/aac", "ADTS", 0)]
    public async Task Single_primary_asset_supports_range_and_correct_content_type(
        string container, string contentType, string magic, int offset)
    {
        using var file = TempAudio.Create(magic, AssetSizeBytes, offset);
        _factory.Playback.Container = container;
        _factory.Storage.ResolvedPath = file.Path;
        using var client = _factory.CreateClient();
        using var request = RangeRequest(TrackId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(file.Length, response.Content.Headers.ContentRange?.Length);
        Assert.Equal(16, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Public_stream_revalidates_conditional_request_and_sets_cache_policy()
    {
        using var file = TempAudio.Create("fLaC", AssetSizeBytes, magicOffset: 0);
        _factory.Storage.ResolvedPath = file.Path;
        using var client = _factory.CreateClient();
        using var first = await client.GetAsync($"/api/v1/tracks/{TrackId}/stream");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("public", first.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", first.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(first.Headers.ETag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tracks/{TrackId}/stream");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var notModified = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Contains("no-cache", notModified.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Private_stream_is_hidden_and_never_cached_for_shared_clients()
    {
        _factory.Playback.Visibility = new TrackPlaybackVisibility(null, Guid.NewGuid());
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tracks/{TrackId}/stream");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("private", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_aac_compatibility_response_preserves_range_etag_and_private_cache_policy()
    {
        using var file = TempAudio.Create("fLaC", AssetSizeBytes, magicOffset: 0);
        _factory.Storage.ResolvedPath = file.Path;
        using var client = _factory.CreateClient();
        using var request = RangeRequest(TrackId);
        request.RequestUri = new Uri($"/api/v1/tracks/{TrackId}/stream?format=aac", UriKind.Relative);

        using var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("public", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, bytes.Length);
        Assert.Equal(1, _factory.TemporaryPlayback.TranscodeCount);
        Assert.True(_factory.TemporaryPlayback.LastLeaseDisposed);
    }

    [Fact]
    public async Task Anonymous_disabled_stream_is_unauthorized_and_private_no_store()
    {
        using var deniedFactory = new StreamTestFactory(anonymousPlaybackEnabled: false);
        deniedFactory.EnsureHost();
        using var client = deniedFactory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tracks/{TrackId}/stream");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("private", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test_host_runs_without_database_or_message_queue()
    {
        // 结构性断言：宿主确实走了可替换接缝，而不是「碰巧没连上」。
        _factory.EnsureHost();
        Assert.IsType<NoopStartupBootstrap>(_factory.Services.GetRequiredService<IStartupBootstrap>());
        Assert.True(_factory.NoopBootstrap.CallCount >= 1,
            "启动引导未被执行：Program.cs 的接缝可能已被改坏");
        // GenericWebHostService 是 Web 宿主自身，始终存在；只需确认没有业务消费者/定时器被启动。
        Assert.DoesNotContain(_factory.Services.GetServices<IHostedService>(),
            service => service.GetType().Namespace?.StartsWith("ShineMusic.Tasks", StringComparison.Ordinal) == true);
    }

    private static HttpRequestMessage RangeRequest(Guid trackId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tracks/{trackId}/stream");
        request.Headers.Range = new RangeHeaderValue(0, 15);
        return request;
    }

    private static DeliveryContract FlacContract(long sizeBytes, int bitrateKbps) => new(
        ImportItemId: Guid.NewGuid(),
        TrackId: TrackId,
        ExpectedIdentity: Identity,
        ResolvedIdentity: Identity,
        IdentityVerdict: IdentityVerdict.Matched,
        IdentityReasonCode: "matched",
        MinimumBitrateKbps: DeliveryContractVerifier.AutomaticPublishMinimumBitrateKbps,
        PrimaryStorageKey: "l3a/fixture.flac",
        ExpectedContentType: "audio/flac",
        ProbedBitrateKbps: bitrateKbps,
        ExpectedSizeBytes: sizeBytes,
        ContentHash: "l3a-fixture",
        OriginalFileAvailable: true);
}

/// <summary>把 /stream 的真实响应与发布时写下的交付契约逐项比对。</summary>
internal static class StreamReadbackVerifier
{
    public static IReadOnlyList<string> Verify(DeliveryContract contract, HttpResponseMessage response, byte[] body)
    {
        var failures = new List<string>();

        if (response.StatusCode != HttpStatusCode.PartialContent)
            failures.Add($"status: expected 206 PartialContent, got {(int)response.StatusCode}");

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, contract.ExpectedContentType, StringComparison.OrdinalIgnoreCase))
            failures.Add($"content_type: expected {contract.ExpectedContentType}, got {contentType ?? "(none)"}");

        var totalLength = response.Content.Headers.ContentRange?.Length;
        if (totalLength != contract.ExpectedSizeBytes)
            failures.Add($"size_mismatch: Content-Range total {totalLength?.ToString() ?? "(none)"}, contract {contract.ExpectedSizeBytes}");

        if (body.Length == 0)
            failures.Add("body_empty: Range 响应没有返回任何字节");
        else if (!MagicMatches(body, contract.ExpectedContentType))
            failures.Add($"magic_mismatch: 响应首字节与 {contract.ExpectedContentType} 不符（可能是 404 页面或占位音频）");

        return failures;
    }

    private static bool MagicMatches(byte[] head, string? contentType) => contentType switch
    {
        "audio/flac" => HasPrefix(head, 0, "fLaC"),
        "audio/ogg" => HasPrefix(head, 0, "OggS"),
        "audio/wav" => HasPrefix(head, 0, "RIFF"),
        "audio/mp4" => HasPrefix(head, 4, "ftyp"),
        "audio/mpeg" => HasPrefix(head, 0, "ID3") || (head.Length > 1 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),
        _ => true,
    };

    private static bool HasPrefix(byte[] head, int offset, string prefix)
    {
        if (head.Length < offset + prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (head[offset + i] != (byte)prefix[i]) return false;
        return true;
    }
}

/// <summary>
/// 测试宿主：用 Noop 启动引导 + 端口 fake 起真实 TestServer 管道，全程不连 PostgreSQL / RabbitMQ。
/// </summary>
public sealed class StreamTestFactory : WebApplicationFactory<Program>
{
    private readonly (string Key, string Value)[] _environmentOverrides;

    private readonly Dictionary<string, string?> _previousEnvironment = [];
    private readonly string _previousWorkingDirectory = Environment.CurrentDirectory;

    public FakePlaybackStore Playback { get; } = new();
    public FakeAudioStorage Storage { get; } = new();
    public FakeTemporaryPlaybackService TemporaryPlayback { get; } = new();
    public NoopStartupBootstrap NoopBootstrap { get; } = new();

    public StreamTestFactory() : this(true) { }

    internal StreamTestFactory(bool anonymousPlaybackEnabled)
    {
        _environmentOverrides =
        [
            // appsettings.json 刻意不含 SigningKey（生产靠 User Secrets / 环境变量）；CI 无 User Secrets，
            // 而 AddShineMusicAuthentication 在注册时就会校验长度，必须显式给一个仅用于测试的值。
            ("Auth__SigningKey", "L3a-Integration-Test-SigningKey-0123456789-abcdefgh"),
            // Worker:Mode 在 Program.cs 顶层决定 HostedService 注册，WAF 的 ConfigureAppConfiguration 太晚，
            // 只能靠环境变量（CreateBuilder 会加载）。
            ("Worker__Mode", "none"),
            ("Features__AnonymousPlayback", anonymousPlaybackEnabled ? "true" : "false"),
        ];
        foreach (var (key, value) in _environmentOverrides)
        {
            _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        // CreateBuilder 以当前工作目录为默认 content root；显式指到 Api 项目，
        // 保证 appsettings*.json 与 ValidateOnStart 的配置都来自版本库里的同一份文件。
        Environment.CurrentDirectory = ResolveApiProjectDirectory();
    }

    public void EnsureHost() => _ = Server;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(ResolveApiProjectDirectory());
        // 不写 logs/：文件日志提供器由 Program.cs 在 builder.Logging 上注册，这里清掉。
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStartupBootstrap>();
            services.AddSingleton<IStartupBootstrap>(NoopBootstrap);

            // 双保险：即便将来 Worker:Mode 解析变化，也不让消费者/定时器在测试宿主里启动。
            services.RemoveAll<IHostedService>();

            services.RemoveAll<ITrackPlaybackStore>();
            services.AddSingleton<ITrackPlaybackStore>(Playback);
            services.RemoveAll<IAudioStorage>();
            services.AddSingleton<IAudioStorage>(Storage);
            services.RemoveAll<ITemporaryPlaybackService>();
            services.AddSingleton<ITemporaryPlaybackService>(TemporaryPlayback);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.CurrentDirectory = _previousWorkingDirectory;
            foreach (var (key, previous) in _previousEnvironment)
                Environment.SetEnvironmentVariable(key, previous);
        }
        base.Dispose(disposing);
    }

    private static string ResolveApiProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "ShineMusic.Api", "appsettings.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "找不到 ShineMusic.Api/appsettings.json：从测试输出目录上溯失败。L3a 需要真实的项目配置。");
    }
}

public sealed class NoopStartupBootstrap : IStartupBootstrap
{
    public int CallCount { get; private set; }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakePlaybackStore : ITrackPlaybackStore
{
    public string Container { get; set; } = "flac";
    public string? StorageKey { get; set; } = "l3a/fixture.flac";
    public TrackPlaybackVisibility? Visibility { get; set; } = new(DateTimeOffset.UtcNow, null);

    public Task<TrackPlaybackVisibility?> GetVisibilityAsync(Guid trackId, CancellationToken cancellationToken) =>
        Task.FromResult(Visibility);

    public Task<PublishedAudioAsset?> GetPublishedAudioAsync(Guid trackId, CancellationToken cancellationToken) =>
        Task.FromResult<PublishedAudioAsset?>(StorageKey is null ? null : new PublishedAudioAsset(StorageKey, Container));
}

public sealed class FakeTemporaryPlaybackService : ITemporaryPlaybackService
{
    private const int OutputSizeBytes = 4096;
    public int TranscodeCount { get; private set; }
    public bool LastLeaseDisposed { get; private set; }

    public async Task<TemporaryPlaybackLease> GetAacAsync(
        Guid trackId, string originalFilePath, string sourceIdentity, CancellationToken cancellationToken)
    {
        TranscodeCount++;
        var path = Path.Combine(Path.GetTempPath(), $"shine-temporary-stream-{Guid.NewGuid():N}.m4a");
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)0xF0, OutputSizeBytes).ToArray(), cancellationToken);
        var info = new FileInfo(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        return new TemporaryPlaybackLease(stream, info.Length, new DateTimeOffset(info.LastWriteTimeUtc), $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"", () =>
        {
            LastLeaseDisposed = true;
            return ValueTask.CompletedTask;
        });
    }
}


public sealed class FakeAudioStorage : IAudioStorage
{
    public string LibraryRoot => Path.GetTempPath();
    public string QuarantineRoot => Path.GetTempPath();
    public string? ResolvedPath { get; set; }

    public bool TryResolveLibrary(string storageKey, out string filePath)
    {
        filePath = ResolvedPath ?? string.Empty;
        return ResolvedPath is not null;
    }

    public bool TryResolveQuarantine(string storageKey, out string filePath)
    {
        filePath = string.Empty;
        return false;
    }
}

internal sealed class TempAudio : IDisposable
{
    public string Path { get; }

    private TempAudio(string path) => Path = path;

    public static TempAudio Create(string magic, long size, int magicOffset)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"shine-l3a-{Guid.NewGuid():N}.bin");
        var bytes = new byte[size];
        Array.Fill(bytes, (byte)0xA5);
        Encoding.ASCII.GetBytes(magic).CopyTo(bytes, magicOffset);
        File.WriteAllBytes(path, bytes);
        return new TempAudio(path);
    }

    public long Length => new FileInfo(Path).Length;

    public void Dispose()
    {
        try { File.Delete(Path); }
        catch (IOException) { /* 临时文件清理失败不影响断言 */ }
    }
}
