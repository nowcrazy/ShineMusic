using System.Text.Json;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Metadata;

public sealed class TrackIdentityRegressionTests
{
    [Theory]
    [InlineData("谁 (Live版)", "廖俊涛", 254428)]
    [InlineData("开往早晨的午夜（烟嗓）", "烟(许佳豪)", 168116)]
    [InlineData("飞 (完整版) (翻自 王恩信Est / 二胖u)", "大神慧", 198086)]
    [InlineData("爱", "黄丽玲", 252995)]
    [InlineData("画 (Live Piano Session II)", "G.E.M.邓紫棋", 168739)]
    [InlineData("圆", "AGA", 249000)]
    public void SameRecording_ShortNamesAreValid(string title, string artist, long duration)
    {
        var identity = new TrackIdentity(title, [artist], duration);
        Assert.Equal(IdentityVerdict.Matched, TrackIdentityMatcher.Evaluate(identity, identity).Verdict);
    }

    [Theory]
    [InlineData("爱", "甲", "爱", "乙", 200000)]
    [InlineData("", "甲", "", "甲", 200000)]
    [InlineData("爱", "甲", "爱 (Live)", "甲", 200000)]
    [InlineData("爱情陷阱", "谭咏麟", "爱情陷阱(Special Club Mix)", "谭咏麟", 200000)]
    [InlineData("Stronger", "Inez", "Stronger (Jody Den Broeder Club Mix)", "Inez", 200000)]
    [InlineData("感谢你曾来过", "周思涵", "感谢你曾来过 (变速版)", "周思涵", 200000)]
    [InlineData("爱", "甲", "爱", "甲", 210000)]
    [InlineData("爱", "甲", "爱", "甲", 0)]
    [InlineData("爱", "烟(许佳豪)", "爱", "烟(其他人)", 200000)]
    [InlineData("爱", "烟(许佳豪)", "爱", "烟", 200000)]
    public void ShortNames_DoNotBypassIdentity(string title, string artist, string candidateTitle, string candidateArtist, long candidateDuration)
    {
        Assert.NotEqual(IdentityVerdict.Matched, TrackIdentityMatcher.Evaluate(
            new(title, [artist], 200000), new(candidateTitle, [candidateArtist], candidateDuration)).Verdict);
    }

    [Fact]
    public void ArtistWidthAndWhitespace_AreNormalizedWithoutDroppingParentheses()
    {
        Assert.Equal(IdentityVerdict.Matched, TrackIdentityMatcher.Evaluate(
            new("爱", ["烟（许佳豪）"], 200000), new("爱", [" 烟(许佳豪) "], 200000)).Verdict);
    }
    [Fact]
    public void All216OriginalPlaylistIdentities_MatchThemselves()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "playlist-900025217.json")));
        var tracks = document.RootElement.GetProperty("results")[0].GetProperty("orderedTracks").Deserialize<ParsedPlaylistTrack[]>()!;
        Assert.Equal(216, tracks.Length);
        foreach (var track in tracks)
        {
            var identity = new TrackIdentity(track.Title, track.Artists, track.DurationMs, track.AlbumTitle);
            Assert.Equal(IdentityVerdict.Matched, TrackIdentityMatcher.Evaluate(identity, identity).Verdict);
        }
    }

}
