using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

/// <summary>存储键来自数据库/上传流程，属于不可信输入，必须 fail-closed 拒绝路径穿越。</summary>
public sealed class StoragePathSecurityTests
{
    private static LibraryStoragePaths CreatePaths(string root)
    {
        var options = Options.Create(new StorageOptions { LibraryRoot = root, QuarantineRoot = root });
        return new LibraryStoragePaths(options, new FakeWebHostEnvironment(root));
    }

    [Fact]
    public void Accepts_relative_nested_keys()
    {
        var root = Path.Combine(Path.GetTempPath(), "shine-music-test-library");
        var paths = CreatePaths(root);
        Assert.True(paths.TryResolve("playback/abc/song.m4a", out var resolved));
        Assert.StartsWith(paths.Root, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("song.m4a", resolved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.m4a")]
    [InlineData("../../etc/passwd")]
    [InlineData("playback/../../escape.m4a")]
    [InlineData("/etc/passwd")]
    public void Rejects_path_traversal_and_rooted_keys(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "shine-music-test-library");
        var paths = CreatePaths(root);
        Assert.False(paths.TryResolve(key, out var resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void Quarantine_root_uses_the_same_fail_closed_rule()
    {
        var root = Path.Combine(Path.GetTempPath(), "shine-music-test-quarantine");
        var options = Options.Create(new StorageOptions { LibraryRoot = root, QuarantineRoot = root });
        var paths = new QuarantineStoragePaths(options, new FakeWebHostEnvironment(root));

        Assert.True(paths.TryResolve("sourcedl/task-1.m4a", out _));
        Assert.False(paths.TryResolve("../library/playback/secret.m4a", out _));
    }

    private sealed class FakeWebHostEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShineMusic.Tests";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
