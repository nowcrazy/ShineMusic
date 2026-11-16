using ShineMusic.Model.Entities;
using ShineMusic.Service;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Metadata;

public sealed class SourceMetadataHandoffTests
{
    private static ExternalDownloadTask TaskSnapshot() => new()
    {
        SourceProvider = "netease", ProviderTrackId = "328076", Title = "偏爱",
        ArtistsJson = "[\"张芸京\"]", DurationMs = 212800
    };
    private static SourceSearchResult Detail(string title = "偏爱", string id = "328076")
        => new("netease", id, title, ["张芸京"], "破天荒", 212800, [], "https://example.test/cover.jpg", null, null, "high", false, 2009);

    [Fact]
    public void ExistingTitle_DoesNotPreventMissingSourceFieldsBeingFilled()
    {
        var task = TaskSnapshot();
        Assert.True(SourceDownloadJob.ApplyConfirmedMetadata(task, Detail()));
        Assert.Equal(2009, task.ReleaseYear);
        Assert.Equal("破天荒", task.AlbumTitle);
        Assert.NotNull(task.CoverUrl);
        task.ReleaseYear = 2010;
        SourceDownloadJob.ApplyConfirmedMetadata(task, Detail());
        Assert.Equal(2010, task.ReleaseYear);
    }

    [Theory]
    [InlineData("另一首歌", "328076")]
    [InlineData("偏爱", "999")]
    public void DifferentIdentity_DoesNotContributeFields(string title, string id)
    {
        var task = TaskSnapshot();
        Assert.False(SourceDownloadJob.ApplyConfirmedMetadata(task, Detail(title, id)));
        Assert.Null(task.ReleaseYear);
        Assert.Null(task.CoverUrl);
    }

    [Fact]
    public void AllMissingFieldsManuallyCleared_DoNotTriggerExternalLookup()
    {
        var item = new LibraryImportItem();
        foreach (var field in new[] { "cover", "album", "releaseDate", "releaseYear", "trackNumber", "discNumber", "originalGenres" })
            MetadataMerge.Manual(item, field, null);
        Assert.False(ImportMetadataEnricher.NeedsEnrichment(item));
        Assert.True(ImportMetadataEnricher.NeedsEnrichment(new LibraryImportItem()));
    }
    [Fact]
    public void LyricsId_IsOnlyForwardedForMatchingCurrentIdentity()
    {
        var task = TaskSnapshot();
        var item = new LibraryImportItem { Title = task.Title, ArtistsJson = task.ArtistsJson,
            SourceProvider = task.SourceProvider, DurationMs = task.DurationMs };
        var query = LibraryImportJob.CreateLyricsQuery(item, null, null, task);
        Assert.Equal("netease", query.SourceProvider);
        Assert.Equal("328076", query.SourceTrackId);
        item.Title = "人工改为另一首歌";
        Assert.Null(LibraryImportJob.CreateLyricsQuery(item, null, null, task).SourceTrackId);
    }

}
