using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

public sealed class SavedCollectionsTests : IDisposable
{
    private readonly SqlSugarClient db = new(new ConnectionConfig
    {
        ConnectionString = "Data Source=:memory:", DbType = DbType.Sqlite,
        IsAutoCloseConnection = false, InitKeyType = InitKeyType.Attribute,
        ConfigureExternalServices = new ConfigureExternalServices
        {
            EntityNameService = (_, entity) => entity.DbTableName = JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
            EntityService = (property, column) => column.DbColumnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
        }
    });
    private readonly Guid userId = Guid.NewGuid();

    public SavedCollectionsTests() => db.CodeFirst.InitTables(typeof(SavedCollection), typeof(EditorialCollection),
        typeof(CollectionTrack), typeof(Track), typeof(UserPlaylist));

    [Fact]
    public async Task Put_and_delete_are_idempotent_and_isolated_by_user()
    {
        var collection = Collection();
        var owner = Controller(userId);
        var otherId = Guid.NewGuid();
        var other = Controller(otherId);
        Status(204, await owner.SaveCollection(collection.Id, default));
        var created = db.Queryable<SavedCollection>().Single().CreatedAtUtc;
        Status(204, await owner.SaveCollection(collection.Id, default));
        Assert.Equal(created, db.Queryable<SavedCollection>().Single().CreatedAtUtc);
        Assert.Empty((await List(other)).Items);
        Status(204, await other.SaveCollection(collection.Id, default));
        Assert.Equal(2, db.Queryable<SavedCollection>().Count());
        Assert.ThrowsAny<Exception>(() => db.Insertable(new SavedCollection
            { UserId = userId, CollectionId = collection.Id }).ExecuteCommand());
        Status(204, await owner.UnsaveCollection(collection.Id, default));
        Status(204, await owner.UnsaveCollection(collection.Id, default));
        Assert.Empty((await List(owner)).Items);
        Assert.Single((await List(other)).Items);
        Status(204, await owner.UnsaveCollection(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Private_empty_missing_and_user_playlists_cannot_be_saved_or_leaked()
    {
        var controller = Controller(userId);
        var hidden = Collection(published: false);
        var empty = Collection(withTrack: false);
        var unpublishedTracks = Collection(trackPublished: false);
        var playlist = new UserPlaylist { UserId = Guid.NewGuid(), Name = "Private playlist" };
        db.Insertable(playlist).ExecuteCommand();
        foreach (var id in new[] { hidden.Id, empty.Id, unpublishedTracks.Id, playlist.Id, Guid.NewGuid() })
        {
            Status(404, await controller.SaveCollection(id, default));
            Assert.Equal(0, db.Queryable<SavedCollection>().Count());
            // Stale/orphan joins must also be filtered from both results and total.
            db.Insertable(new SavedCollection { UserId = userId, CollectionId = id }).ExecuteCommand();
            Assert.Equal(0, (await List(controller)).Total);
            Status(204, await controller.UnsaveCollection(id, default));
        }
        var visible = Collection();
        Status(204, await controller.SaveCollection(visible.Id, default));
        db.Updateable<EditorialCollection>().SetColumns(item => item.IsPublished == false)
            .Where(item => item.Id == visible.Id).ExecuteCommand();
        Assert.Empty((await List(controller)).Items);
        Status(404, await controller.SaveCollection(visible.Id, default));
        Status(204, await controller.UnsaveCollection(visible.Id, default));
    }

    [Fact]
    public async Task Paging_uses_saved_time_then_id_and_counts_only_published_tracks_in_three_queries()
    {
        var first = Collection();
        var second = Collection();
        var third = Collection();
        var hiddenTrack = new Track { Title = "Unpublished" };
        db.Insertable(hiddenTrack).ExecuteCommand();
        db.Insertable(new CollectionTrack { CollectionId = first.Id, TrackId = hiddenTrack.Id }).ExecuteCommand();
        var savedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Insertable(new[]
        {
            new SavedCollection { UserId = userId, CollectionId = first.Id, CreatedAtUtc = savedAt },
            new SavedCollection { UserId = userId, CollectionId = second.Id, CreatedAtUtc = savedAt },
            new SavedCollection { UserId = userId, CollectionId = third.Id, CreatedAtUtc = savedAt.AddDays(1) }
        }).ExecuteCommand();
        var queryCount = 0;
        db.Aop.OnLogExecuting = (_, _) => queryCount++;
        var result = await List(Controller(userId), 1, 2);
        Assert.Equal(3, queryCount);
        Assert.Equal(3, result.Total);
        Assert.Equal(1, result.Page);
        Assert.Equal(2, result.PageSize);
        var ties = new[] { first.Id, second.Id }.Order().ToArray();
        Assert.Equal(new[] { third.Id, ties[0] }, result.Items.Select(item => item.Id));
        Assert.All(result.Items, item => Assert.Equal(1, item.TrackCount));
        Assert.Equal($"/api/v1/artwork/{third.ArtworkId}", result.Items[0].ArtworkUrl);
        Assert.Equal(new[] { ties[1] }, (await List(Controller(userId), 2, 2)).Items.Select(item => item.Id));
        Assert.Empty((await List(Controller(userId), 3, 2)).Items);
        var defaults = await List(Controller(userId));
        Assert.Equal(1, defaults.Page);
        Assert.Equal(20, defaults.PageSize);
    }

    [Fact]
    public async Task Authentication_paging_and_editorial_feature_gate_are_enforced()
    {
        var anonymous = Controller(null);
        Status(401, await anonymous.GetSavedCollections(null, null, default));
        Status(401, await anonymous.SaveCollection(Guid.NewGuid(), default));
        Status(401, await anonymous.UnsaveCollection(Guid.NewGuid(), default));
        var controller = Controller(userId);
        foreach (var (page, size) in new[] { (0, 20), (1, 0), (1, 1001), (int.MaxValue, 1000) })
            Status(400, await controller.GetSavedCollections(page, size, default));
        Assert.NotNull(typeof(SavedCollectionsController).GetCustomAttribute<AuthorizeAttribute>());
        var gate = typeof(SavedCollectionsController).GetCustomAttribute<FeatureGateAttribute>()!;
        foreach (var enabled in new[] { false, true })
        {
            using var services = new ServiceCollection().AddSingleton<IOptions<FeatureOptions>>(
                Options.Create(new FeatureOptions { EditorialCollections = enabled })).BuildServiceProvider();
            var filter = (IAsyncActionFilter)gate.CreateInstance(services);
            var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
            var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), controller);
            var invoked = false;
            await filter.OnActionExecutionAsync(context, () =>
            {
                invoked = true;
                return Task.FromResult(new ActionExecutedContext(action, [], controller));
            });
            Assert.Equal(enabled, invoked);
            if (!enabled) Assert.IsType<NotFoundObjectResult>(context.Result);
        }
    }

    private EditorialCollection Collection(bool published = true, bool withTrack = true, bool trackPublished = true)
    {
        var collection = new EditorialCollection { Title = "Editorial", Description = "Description",
            IsPublished = published, ArtworkId = Guid.NewGuid() };
        db.Insertable(collection).ExecuteCommand();
        if (withTrack)
        {
            var track = new Track { Title = "Track", PublishedAt = trackPublished ? DateTimeOffset.UtcNow : null };
            db.Insertable(track).ExecuteCommand();
            db.Insertable(new CollectionTrack { CollectionId = collection.Id, TrackId = track.Id }).ExecuteCommand();
        }
        return collection;
    }

    private SavedCollectionsController Controller(Guid? id) => new(db)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = id.HasValue ? new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", id.Value.ToString())], "test"))
                : new ClaimsPrincipal(new ClaimsIdentity())
        } }
    };

    private static async Task<PageResult<CollectionSummaryDto>> List(SavedCollectionsController controller, int? page = null, int? size = null) =>
        Assert.IsType<PageResult<CollectionSummaryDto>>(Assert.IsAssignableFrom<IValueHttpResult>(
            await controller.GetSavedCollections(page, size, default)).Value);

    private static void Status(int expected, IResult result) =>
        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

    public void Dispose() => db.Dispose();
}
