using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Model.Ports;
using ShineMusic.Service.Media;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

public sealed class TemporaryPlaybackServiceTests
{
    [Fact]
    public async Task Concurrent_requests_share_conversion_and_lease_the_temporary_file()
    {
        using var files = new TemporaryFiles();
        var source = files.CreateSource();
        var transcoder = new FakeTranscoder();
        using var service = files.CreateService(transcoder);

        var trackId = Guid.NewGuid();
        var leases = await Task.WhenAll(
            service.GetAacAsync(trackId, source, "asset-a", default),
            service.GetAacAsync(trackId, source, "asset-a", default));

        Assert.Equal(1, transcoder.TranscodeCount);
        Assert.All(leases, lease =>
        {
            Assert.True(lease.Content.CanRead);
            Assert.True(lease.Length > 0);
            Assert.StartsWith("\"", lease.EntityTag);
        });
        Assert.Equal(leases[0].EntityTag, leases[1].EntityTag);
        await Task.WhenAll(leases.Select(lease => lease.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task Cache_limit_failure_keeps_source_and_removes_partial_output()
    {
        using var files = new TemporaryFiles(maxEntryBytes: 64, maxCacheBytes: 128);
        var source = files.CreateSource();
        using var service = files.CreateService(new FakeTranscoder(outputBytes: 128));

        await Assert.ThrowsAsync<IOException>(() => service.GetAacAsync(Guid.NewGuid(), source, "asset", default));

        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(files.CacheRoot, "*.m4a"));
    }

    [Fact]
    public async Task Source_change_during_conversion_is_rejected_without_deleting_original()
    {
        using var files = new TemporaryFiles();
        var source = files.CreateSource();
        var transcoder = new FakeTranscoder(afterTranscode: () => File.AppendAllText(source, "changed source"));
        using var service = files.CreateService(transcoder);

        await Assert.ThrowsAsync<IOException>(() => service.GetAacAsync(Guid.NewGuid(), source, "asset", default));

        Assert.Contains("changed source", File.ReadAllText(source));
        Assert.Empty(Directory.EnumerateFiles(files.CacheRoot, "*.m4a"));
    }

    [Fact]
    public async Task Cache_limit_does_not_evict_an_active_range_lease()
    {
        using var files = new TemporaryFiles(maxEntryBytes: 256, maxCacheBytes: 300);
        var source = files.CreateSource();
        using var service = files.CreateService(new FakeTranscoder());
        var firstTrack = Guid.NewGuid();
        await using var active = await service.GetAacAsync(firstTrack, source, "asset-a", default);

        await Assert.ThrowsAsync<IOException>(() => service.GetAacAsync(Guid.NewGuid(), source, "asset-b", default));

        Assert.True(active.Content.CanRead);
        Assert.Single(Directory.EnumerateFiles(files.CacheRoot, "*.m4a"));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Cache_directory_may_not_overlap_the_permanent_library()
    {
        using var files = new TemporaryFiles();
        var options = new TemporaryPlaybackOptions { CacheDirectory = files.LibraryRoot };

        Assert.Throws<InvalidOperationException>(() => files.CreateService(new FakeTranscoder(), options));
    }

    private sealed class FakeTranscoder(int outputBytes = 256, Action? afterTranscode = null) : IMediaTranscoder
    {
        private int _transcodeCount;
        public int TranscodeCount => Volatile.Read(ref _transcodeCount);

        public async Task TranscodeToAacAsync(string sourcePath, string destinationPath, int bitrateKbps, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _transcodeCount);
            await Task.Delay(20, cancellationToken);
            await File.WriteAllBytesAsync(destinationPath, Enumerable.Repeat((byte)0xA4, outputBytes).ToArray(), cancellationToken);
            afterTranscode?.Invoke();
        }

        public Task<MediaProbe> ProbeAsync(string sourcePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbe("m4a", "aac", 192, 1000, "mov,mp4,m4a", 44100, null, 2, "LC"));
    }

    private sealed class TemporaryFiles(long maxEntryBytes = 1024, long maxCacheBytes = 4096) : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "shine-temporary-playback-" + Guid.NewGuid().ToString("N"));
        public string CacheRoot => Path.Combine(Root, "cache");
        public string LibraryRoot => Path.Combine(Root, "library");
        public string QuarantineRoot => Path.Combine(Root, "quarantine");

        public TemporaryPlaybackService CreateService(IMediaTranscoder transcoder, TemporaryPlaybackOptions? options = null)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LibraryRoot);
            Directory.CreateDirectory(QuarantineRoot);
            options ??= new TemporaryPlaybackOptions
            {
                CacheDirectory = CacheRoot,
                MaxCacheBytes = maxCacheBytes,
                MaxEntryBytes = maxEntryBytes,
            };
            return new TemporaryPlaybackService(Options.Create(options),
                Options.Create(new StorageOptions { LibraryRoot = LibraryRoot, QuarantineRoot = QuarantineRoot }),
                transcoder, new TestEnvironment { ContentRootPath = Root }, NullLogger<TemporaryPlaybackService>.Instance);
        }

        public string CreateSource()
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "original.flac");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 128).ToArray());
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ShineMusic.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
