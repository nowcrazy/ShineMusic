using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Entities;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

public sealed class ChartsTests : IDisposable
{
    private readonly SqlSugarClient db = new(new ConnectionConfig
    {
        ConnectionString = "Data Source=:memory:",
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = false,
        InitKeyType = InitKeyType.Attribute,
        ConfigureExternalServices = new ConfigureExternalServices
        {
            EntityNameService = (_, entity) => entity.DbTableName = JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
            EntityService = (property, column) => column.DbColumnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
        }
    });

    public ChartsTests()
    {
        db.CodeFirst.InitTables(typeof(Track), typeof(ListeningEvent), typeof(Favorite),
            typeof(TrackArtist), typeof(TrackGenre), typeof(Genre), typeof(MediaAsset));
    }

    [Fact]
    public async Task Empty_database_returns_four_empty_charts()
    {
        var charts = await Charts();
        Assert.Equal(new[] { "weekly-plays", "favorites", "all-time-plays", "new-arrivals" }, charts.Keys);
        Assert.All(charts.Values, items => Assert.Empty(items));
    }

    [Fact]
    public async Task Counts_only_plays_in_window_and_current_favorites_of_published_tracks()
    {
        var first = Track(1);
        var second = Track(2);
        var old = Track(3);
        var hidden = Track(4, false);
        var zero = Track(5);
        db.Insertable(new[] { first, second, old, hidden, zero }).ExecuteCommand();
        Play(first, -1); Play(first, -2);
        Play(second, -1); Play(second, -2);
        Play(old, -8); Play(old, -9); Play(old, -10);
        for (var i = 0; i < 10; i++) Play(hidden, -1);
        Play(zero, -1, ListeningEventTypes.Complete);
        Play(zero, -1, ListeningEventTypes.Skip);
        Play(zero, 1); // Future timestamps must not inflate either play chart.
        Play(second, -1, ListeningEventTypes.Complete); // Must not break the tie.
        Favorite(first); Favorite(second); Favorite(second); Favorite(hidden);

        var charts = await Charts();
        Assert.Equal(new[] { first.Id, second.Id }, charts["weekly-plays"]);
        Assert.Equal(new[] { second.Id, first.Id }, charts["favorites"]);
        Assert.Equal(new[] { old.Id, first.Id, second.Id }, charts["all-time-plays"]);
        db.Deleteable<Favorite>().Where(row => row.TrackId == second.Id).ExecuteCommand();
        Assert.Equal(new[] { first.Id }, (await Charts())["favorites"]);
    }

    [Fact]
    public async Task Each_chart_is_limited_to_twenty_with_stable_id_ties()
    {
        var tracks = Enumerable.Range(1, 25).Select(id => Track(id)).ToArray();
        db.Insertable(tracks).ExecuteCommand();
        foreach (var track in tracks.Reverse())
        {
            Play(track, -1);
            Favorite(track);
        }
        var expected = tracks.Take(20).Select(track => track.Id).ToArray();
        Assert.All((await Charts()).Values, items => Assert.Equal(expected, items));
        Assert.All((await Charts()).Values, items => Assert.Equal(expected, items));
    }

    [Fact]
    public async Task Genre_filters_before_top_twenty_without_multiplying_counts()
    {
        var rock = new Genre { Name = "摇滚" };
        var jazz = new Genre { Name = "Jazz" };
        db.Insertable(new[] { rock, jazz }).ExecuteCommand();
        var tracks = Enumerable.Range(1, 25).Select(id => Track(id)).ToArray();
        var hidden = Track(30, false);
        db.Insertable(tracks.Append(hidden).ToArray()).ExecuteCommand();
        foreach (var track in tracks.Append(hidden))
        {
            Play(track, -1); Favorite(track);
            db.Insertable(new TrackGenre { TrackId = track.Id, GenreId = jazz.Id }).ExecuteCommand();
        }
        foreach (var track in tracks.Skip(22).Append(hidden))
            db.Insertable(new TrackGenre { TrackId = track.Id, GenreId = rock.Id }).ExecuteCommand();
        Play(tracks[24], -1); Favorite(tracks[24]);
        var charts = await Charts("  摇滚  ");
        var ranked = new[] { tracks[24].Id, tracks[22].Id, tracks[23].Id };
        Assert.Equal(ranked, charts["weekly-plays"]);
        Assert.Equal(ranked, charts["all-time-plays"]);
        Assert.Equal(ranked, charts["favorites"]);
        Assert.Equal(tracks.Skip(22).Select(track => track.Id), charts["new-arrivals"]);
        Assert.All((await Charts("unknown")).Values, items => Assert.Empty(items));
        var unfiltered = await Charts();
        var blank = await Charts("  ");
        foreach (var id in unfiltered.Keys) Assert.Equal(unfiltered[id], blank[id]);
    }

    [Fact]
    public async Task New_arrivals_uses_publication_time_without_requiring_activity()
    {
        var first = Track(1);
        var newer = Track(2);
        newer.PublishedAt = first.PublishedAt!.Value.AddDays(1);
        var hidden = Track(3, false);
        db.Insertable(new[] { first, newer, hidden }).ExecuteCommand();
        var charts = await Charts();
        Assert.Equal(new[] { newer.Id, first.Id }, charts["new-arrivals"]);
        Assert.Empty(charts["weekly-plays"]);
        Assert.Empty(charts["favorites"]);
        Assert.Empty(charts["all-time-plays"]);
    }

    private static Track Track(int id, bool published = true) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"),
        Title = $"Track {id}",
        PublishedAt = published ? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) : null
    };

    private void Play(Track track, int days, string kind = ListeningEventTypes.Play) =>
        db.Insertable(new ListeningEvent
        {
            TrackId = track.Id, UserId = Guid.NewGuid(), ListeningEventType = kind,
            OccurredAtUtc = DateTime.UtcNow.AddDays(days)
        }).ExecuteCommand();

    private void Favorite(Track track) => db.Insertable(new Favorite
    {
        TrackId = track.Id, UserId = Guid.NewGuid()
    }).ExecuteCommand();

    private async Task<Dictionary<string, Guid[]>> Charts(string? genre = null)
    {
        var result = await new ChartsController(db).GetCharts(CancellationToken.None, genre);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        var json = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        return json.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("id").GetString()!,
            item => item.GetProperty("tracks").EnumerateArray().Select(track => track.GetProperty("id").GetGuid()).ToArray());
    }

    public void Dispose() => db.Dispose();
}
