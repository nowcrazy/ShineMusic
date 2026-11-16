using ShineMusic.Model.Entities;
using ShineMusic.Service;
using Xunit;

namespace ShineMusic.Tests.Metadata;

public sealed class MetadataMergeTests
{
    private static LibraryImportItem Item() => new() { DisplayName = "test.flac", SourceStorageKey = "test.flac", ContentHash = "hash" };

    [Fact]
    public void Manual_empty_values_are_not_refilled_and_conflict_is_visible()
    {
        var item = Item();
        MetadataMerge.Manual(item, "album", null);
        var filled = ImportMetadataEnricher.Apply(item, new(true, AlbumTitle: "候选专辑", ReleaseYear: 2003, ReleaseDate: "2003-07-31", ItunesTrackId: 42));
        Assert.Null(item.AlbumTitle);
        Assert.DoesNotContain("album", filled);
        var audit = Assert.Single(MetadataMerge.Audit(item.MetadataAuditJson), a => a.Field == "album");
        Assert.True(audit.ManualLocked);
        Assert.Equal("manual_locked", audit.Reason);
        Assert.Equal("itunes", audit.CandidateSource);
    }

    [Fact]
    public void Complete_date_and_original_genre_survive_with_source_id()
    {
        var item = Item();
        var fields = ImportMetadataEnricher.Apply(item, new(true, Genre: "Mandopop", ReleaseYear: 2003, ReleaseDate: "2003-07-31", ItunesTrackId: 42));
        Assert.Equal("2003-07-31", item.ReleaseDate);
        Assert.Equal(["Mandopop"], MetadataMerge.List(item.OriginalGenresJson));
        Assert.Contains("releaseDate", fields);
        Assert.Equal("42", MetadataMerge.Audit(item.MetadataAuditJson).Single(a => a.Field == "releaseDate").SourceId);
        Assert.Null(item.AlbumArtistsJson);
        Assert.Null(item.Isrc);
    }

    [Fact]
    public void Conflicting_year_does_not_create_inconsistent_complete_date()
    {
        var item = Item(); item.ReleaseYear = 2001;
        ImportMetadataEnricher.Apply(item, new(true, ReleaseYear: 2003, ReleaseDate: "2003-07-31"));
        Assert.Equal(2001, item.ReleaseYear);
        Assert.Null(item.ReleaseDate);
        Assert.Contains(MetadataMerge.Audit(item.MetadataAuditJson), a => a.Field == "releaseDate" && a.Reason == "existing_value_conflict");
    }

    [Fact]
    public void Embedded_metadata_preserves_album_artists_isrc_and_manual_title()
    {
        var item = Item(); item.Title = "人工标题";
        MetadataMerge.Manual(item, "title", item.Title);
        MetadataMerge.ApplyTags(item, new ParsedAudioMetadata("文件标题", ["艺人"], "专辑", 1, 1, 2003,
            ["Rock"], 100000, null, null, [], ["专辑艺人"], "USABC0300001"));
        Assert.Equal("人工标题", item.Title);
        Assert.Equal("USABC0300001", item.Isrc);
        Assert.Equal(["专辑艺人"], MetadataMerge.List(item.AlbumArtistsJson));
        Assert.Equal(["Rock"], MetadataMerge.List(item.OriginalGenresJson));
        Assert.Equal("embedded", MetadataMerge.Audit(item.MetadataAuditJson).Single(a => a.Field == "isrc").Source);
    }
}
