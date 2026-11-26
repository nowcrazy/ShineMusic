using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Api.Controllers;
using ShineMusic.Model.Entities;
using ShineMusic.Tests.Acceptance;
using Xunit;

namespace ShineMusic.Tests.Integration;

[Collection("Source download database acceptance")]
public sealed class PlaylistDeletionLockOrderTests
{
    private static UserCollectionController Controller(Guid userId) => new()
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"))
            }
        }
    };

    [Fact]
    public async Task Playlist_deletion_locks_parent_before_children_in_the_settlers_lock_order()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        fixture.Db.CodeFirst.InitTables<UserPlaylist, UserPlaylistTrack>();
        var owner = Guid.NewGuid();
        var playlist = new UserPlaylist { UserId = owner, Name = "delete during settlement" };
        fixture.Db.Insertable(playlist).ExecuteCommand();
        fixture.Db.Insertable(new UserPlaylistTrack { PlaylistId = playlist.Id, TrackId = Guid.NewGuid() }).ExecuteCommand();
        var writes = new List<string>();
        fixture.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            var statement = sql.TrimStart();
            if (!statement.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && !statement.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) return;
            Assert.NotNull(fixture.Db.Ado.Transaction);
            writes.Add(statement);
        };

        var response = await Controller(owner).DeletePlaylist(playlist.Id, fixture.Db, default);
        fixture.Db.Aop.OnLogExecuting = null;

        Assert.Equal(204, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
        Assert.Equal(3, writes.Count);
        Assert.StartsWith("UPDATE", writes[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user_playlists", writes[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user_id", writes[0], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("DELETE", writes[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user_playlist_tracks", writes[1], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("DELETE", writes[2], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user_playlists", writes[2], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user_id", writes[2], StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Db.Queryable<UserPlaylist>().ToList());
        Assert.Empty(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
    }

    [Fact]
    public async Task A_non_owner_cannot_lock_or_delete_another_users_playlist()
    {
        using var fixture = new SourceDownloadAcceptanceFixture();
        fixture.Db.CodeFirst.InitTables<UserPlaylist, UserPlaylistTrack>();
        var playlist = new UserPlaylist { UserId = Guid.NewGuid(), Name = "retained" };
        fixture.Db.Insertable(playlist).ExecuteCommand();
        fixture.Db.Insertable(new UserPlaylistTrack { PlaylistId = playlist.Id, TrackId = Guid.NewGuid() }).ExecuteCommand();
        var writes = 0;
        fixture.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) writes++;
        };

        var response = await Controller(Guid.NewGuid()).DeletePlaylist(playlist.Id, fixture.Db, default);
        fixture.Db.Aop.OnLogExecuting = null;

        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
        Assert.Equal(0, writes);
        Assert.Single(fixture.Db.Queryable<UserPlaylist>().ToList());
        Assert.Single(fixture.Db.Queryable<UserPlaylistTrack>().ToList());
    }
}
