using ShineMusic.Model.Options;

namespace ShineMusic.Tests;

/// <summary>Shares explicit FFmpeg paths across real media tests without changing the process PATH.</summary>
internal static class TestFfmpeg
{
    internal static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    internal static string? FindBin()
    {
        var candidates = new List<string?> { Environment.GetEnvironmentVariable("SHINEMUSIC_TEST_FFMPEG_BIN") };
        foreach (var startingDirectory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "ShineMusicApi", "ShineMusic.slnx"))) continue;
                candidates.Add(Path.Combine(directory.FullName, "ShineMusicApi", "tools", "ffmpeg", "bin"));
                break;
            }
        }
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)
            && File.Exists(Path.Combine(path, Executable("ffmpeg")))
            && File.Exists(Path.Combine(path, Executable("ffprobe"))));
    }

    internal static FfmpegOptions Options()
    {
        var bin = FindBin() ?? throw new InvalidOperationException(
            "Real media tests require ffmpeg and ffprobe. Set SHINEMUSIC_TEST_FFMPEG_BIN, add both tools to PATH, or place them in ShineMusicApi/tools/ffmpeg/bin.");
        return new FfmpegOptions
        {
            ExecutablePath = Path.Combine(bin, Executable("ffmpeg")),
            ProbeExecutablePath = Path.Combine(bin, Executable("ffprobe"))
        };
    }
}
