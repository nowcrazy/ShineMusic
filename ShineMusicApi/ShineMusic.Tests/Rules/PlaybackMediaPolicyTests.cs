using ShineMusic.Model.Entities;
using ShineMusic.Service;
using Xunit;

namespace ShineMusic.Tests.Rules;

/// <summary>
/// 播放主资产策略：确保 320k/无损原文件能真正参与播放，
/// 同时防止「原文件已被转码产物兜底替换」「容器不可流」「比转码副本还差」三种误判。
/// </summary>
public sealed class PlaybackMediaPolicyTests
{
    private static LibraryImportItem NewItem() => new()
    {
        DisplayName = "test.m4a",
        SourceStorageKey = "sourcedl/test.m4a",
        ContentHash = "hash",
        TranscodeBitrateKbps = 192
    };

    [Fact]
    public void Lossless_original_becomes_primary()
    {
        var item = NewItem();
        item.Codec = "flac";
        item.Container = "flac";
        item.BitrateKbps = 935;

        Assert.True(PlaybackMediaPolicy.ShouldPreferOriginal(item, originalFileAvailable: true));
    }

    [Fact]
    public void High_bitrate_lossy_original_beats_192k_transcode()
    {
        var item = NewItem();
        item.Codec = "mp3";
        item.Container = "mp3";
        item.BitrateKbps = 320;

        Assert.True(PlaybackMediaPolicy.ShouldPreferOriginal(item, originalFileAvailable: true));
    }

    [Fact]
    public void Low_bitrate_original_keeps_transcode_primary()
    {
        var item = NewItem();
        item.Codec = "mp3";
        item.Container = "mp3";
        item.BitrateKbps = 128;

        Assert.False(PlaybackMediaPolicy.ShouldPreferOriginal(item, originalFileAvailable: true));
    }

    [Fact]
    public void Equal_bitrate_prefers_original_to_avoid_double_transcode()
    {
        // 192kbps 与转码副本持平时选原文件：副本是二次有损编码，原文件至少少一次损失。
        var item = NewItem();
        item.Codec = "mp3";
        item.Container = "mp3";
        item.BitrateKbps = 192;

        Assert.True(PlaybackMediaPolicy.ShouldPreferOriginal(item, originalFileAvailable: true));
    }

    [Fact]
    public void Fallback_filled_original_never_becomes_primary()
    {
        // 原下载文件被去重清理时，original 资产里装的是转码 m4a，
        // 但 item.Container 仍记录着 flac；此时必须继续以转码副本为主资产。
        var item = NewItem();
        item.Codec = "flac";
        item.Container = "flac";
        item.BitrateKbps = 900;

        Assert.False(PlaybackMediaPolicy.ShouldPreferOriginal(item, originalFileAvailable: false));
    }

    [Fact]
    public void Unstreamable_container_never_becomes_primary()
    {
        var item = NewItem();
        item.Codec = "unknown";
        item.Container = "unknown";
        item.BitrateKbps = 900;

        Assert.False(PlaybackMediaPolicy.ShouldPreferOriginal(item, originalFileAvailable: true));
        Assert.Null(PlaybackMediaPolicy.TryGetContentType("unknown"));
    }

    [Theory]
    [InlineData("flac", "audio/flac")]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("m4a", "audio/mp4")]
    [InlineData("WAV", "audio/wav")]
    public void Content_type_table_covers_high_quality_containers(string container, string expected)
    {
        Assert.Equal(expected, PlaybackMediaPolicy.TryGetContentType(container));
    }
}
