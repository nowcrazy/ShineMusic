using ShineMusic.Model.Entities;
using ShineMusic.Service;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Metadata;

public sealed class ProviderMetadataEnricherTests
{
    private static LibraryImportItem Item() => new() { Title = "偏爱", ArtistsJson = "[\"张芸京\"]", AlbumTitle = "破天荒", DurationMs = 212907 };
    private static SourceSearchResult Candidate(string album = "破天荒", string artist = "张芸京", long duration = 212800, int year = 2009)
        => new("netease", "328076", "偏爱", [artist], album, duration, ["Pop"], null, null, null, "high", false, year);

    [Fact]
    public void ConfirmedAlbumAndRecording_FillYearWithProvenance()
    {
        var item = Item();
        var fields = ProviderMetadataEnricher.Apply(item, Candidate());
        Assert.Equal(2009, item.ReleaseYear);
        Assert.Contains("releaseYear", fields);
        Assert.Contains(MetadataMerge.Audit(item.MetadataAuditJson), a => a.Field == "releaseYear" && a.Source == "netease" && a.SourceId == "328076");
        Assert.Null(item.ReleaseDate);
    }

    [Theory]
    [InlineData("精选集", "张芸京", 212800)]
    [InlineData("破天荒", "其他人", 212800)]
    [InlineData("破天荒", "张芸京", 230000)]
    public void OtherRecordingOrAlbum_IsNotUsed(string album, string artist, long duration)
    {
        var item = Item();
        Assert.Empty(ProviderMetadataEnricher.Apply(item, Candidate(album, artist, duration)));
        Assert.Null(item.ReleaseYear);
    }

    [Fact]
    public void ManualEmptyAndExistingValues_ArePreserved()
    {
        var item = Item();
        MetadataMerge.Manual(item, "releaseYear", null);
        ProviderMetadataEnricher.Apply(item, Candidate());
        Assert.Null(item.ReleaseYear);
        item = Item(); item.ReleaseYear = 2010;
        ProviderMetadataEnricher.Apply(item, Candidate());
        Assert.Equal(2010, item.ReleaseYear);
        Assert.True(MetadataMerge.HasConflicts(item));
    }
}
