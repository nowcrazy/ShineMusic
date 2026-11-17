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
    [Theory]
    [InlineData("cover", false)]
    [InlineData("releaseYear", false)]
    [InlineData("releaseDate", false)]
    [InlineData("title", true)]
    [InlineData("artists", true)]
    [InlineData("album", true)]
    [InlineData("isrc", true)]
    public void Only_identity_conflicts_block_automatic_publish(string field, bool blocking)
    {
        var item = Item();
        MetadataMerge.Fill(item, field, "current", "candidate", false, _ => { }, "provider");
        Assert.True(MetadataMerge.HasConflicts(item));
        Assert.Equal(blocking, MetadataMerge.HasBlockingConflicts(item));
    }

    [Fact]
    public void Corrupt_embedded_text_cannot_fill_or_conflict_with_provider_metadata()
    {
        var item = Item();
        item.Title = "身骑白马";
        MetadataMerge.ApplyTags(item, new ParsedAudioMetadata("\uFFFD标题", ["\uFFFD艺人"], "\uFFFD专辑", null, null, null,
            ["\uFFFD流派"], 100000, null, null, []));
        Assert.Equal("身骑白马", item.Title);
        Assert.Empty(MetadataMerge.List(item.ArtistsJson));
        Assert.Empty(MetadataMerge.List(item.GenresJson));
        Assert.False(MetadataMerge.HasConflicts(item));
    }

    [Theory]
    [InlineData(2.0, 5)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NaN, 0)]
    public void Lyrics_confidence_cannot_exceed_dimension_maximum(double confidence, int expected)
    {
        var item = Item();
        item.LyricsConfidence = confidence;
        var score = ShineMusic.Service.SourceDownload.SourceScoreCalculator.Calculate(item);
        Assert.Equal(expected, score.Lyrics.Score);
        Assert.InRange(score.Total, 0, 100);
    }

    [Fact]
    public void Corrupt_genres_do_not_earn_metadata_points()
    {
        var item = Item();
        item.GenresJson = "[\"\\uFFFDRock\"]";
        Assert.Equal(0, ShineMusic.Service.SourceDownload.SourceScoreCalculator.Calculate(item).Metadata.Score);
    }
    [Fact]
    public void Historical_corrupt_candidate_is_retained_but_does_not_block()
    {
        var item = Item();
        item.MetadataAuditJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new ShineMusic.Model.Contracts.MetadataFieldAuditDto("title", "\"身骑白马\"", "migu", null, DateTime.UtcNow, false,
                CandidateJson: System.Text.Json.JsonSerializer.Serialize("\uFFFD标题"), CandidateSource: "embedded")
        });
        Assert.True(MetadataMerge.HasConflicts(item));
        Assert.False(MetadataMerge.HasBlockingConflicts(item));
    }


}
