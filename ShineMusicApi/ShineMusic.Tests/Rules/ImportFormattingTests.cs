// 职责：锁定 P3 从上帝类抽出的 Media 纯函数，防止收敛成单一事实源后行为漂移。

using ShineMusic.Model.Contracts;
using ShineMusic.Service;
using Xunit;

namespace ShineMusic.Tests.Rules;

public class ImportFormattingTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("not-json", 0)]
    [InlineData("[]", 0)]
    [InlineData("[\"周杰伦\",\"方文山\"]", 2)]
    public void DeserializeList_is_fail_closed(string? json, int expected)
    {
        Assert.Equal(expected, ImportFormatting.DeserializeList(json).Count);
    }

    [Theory]
    [InlineData(".JPG", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".bmp", "application/octet-stream")]
    public void Image_content_type_is_whitelisted(string extension, string expected)
    {
        Assert.Equal(expected, ImportFormatting.GetImageContentType(extension));
    }

    [Theory]
    [InlineData("flac", new[] { "flac" }, true)]
    [InlineData("mov,mp4", new[] { "m4a" }, true)]
    [InlineData("matroska", new[] { "mka" }, true)]
    [InlineData("asf", new[] { "wma" }, true)]
    [InlineData("flac", new[] { "mp3" }, false)]
    [InlineData("", new[] { "mp3" }, false)]
    public void Container_aliases_are_respected(string formatNames, string[] allowed, bool expected)
    {
        Assert.Equal(expected, ImportFormatting.MatchesAllowedContainer(formatNames, allowed));
    }

    [Fact]
    public void Error_messages_never_leak_internal_details()
    {
        Assert.Equal("The media file could not be parsed.",
            ImportFormatting.SafeErrorMessage(ErrorCodes.MediaParseFailed));
        Assert.Equal("The import item could not be processed.",
            ImportFormatting.SafeErrorMessage("secret_internal_exception_type"));
    }
}
