using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ShineMusic.Model.Entities;
using Xunit;

namespace ShineMusic.Tests.Integration;

public sealed class UserOriginalAudioExportTests
{
    [Fact]
    public async Task Owner_downloads_an_unmodified_original_attachment_instead_of_the_compatibility_copy()
    {
        using var host = new OriginalAudioExportTestHost();
        SignIn(host, host.UserId);
        var track = PrivateTrack(host, host.UserId, "本人/原曲");
        var original = host.Asset(track.Id, isPrimary: false);
        host.Asset(track.Id, "playback/compatible.m4a", isOriginal: false, container: "m4a", bytes: [7, 8, 9]);

        using var response = await host.Client.GetAsync(host.MyUrl(track.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("本人_原曲.flac", response.Content.Headers.ContentDisposition?.FileNameStar);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(OriginalAudioExportTestHost.OriginalBytes, bytes);
        Assert.Equal(original.ContentHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(OriginalAudioExportTestHost.OriginalBytes,
            await File.ReadAllBytesAsync(Path.Combine(host.LibraryRoot, original.StorageKey)));
        Assert.Equal(2, host.Db.Queryable<MediaAsset>().Count());
        AssertPrivate(response);
    }

    [Fact]
    public async Task Owner_range_and_conditional_downloads_use_the_same_private_original()
    {
        using var host = new OriginalAudioExportTestHost();
        SignIn(host, host.UserId);
        var track = PrivateTrack(host, host.UserId);
        host.Asset(track.Id);
        using var full = await host.Client.GetAsync(host.MyUrl(track.Id));
        var etag = Assert.IsType<EntityTagHeaderValue>(full.Headers.ETag);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.MyUrl(track.Id));
        request.Headers.Range = new RangeHeaderValue(4, 15);
        request.Headers.IfRange = new RangeConditionHeaderValue(etag);

        using var partial = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(4, 15, OriginalAudioExportTestHost.OriginalBytes.Length), partial.Content.Headers.ContentRange);
        Assert.Equal(OriginalAudioExportTestHost.OriginalBytes[4..16], await partial.Content.ReadAsByteArrayAsync());
        AssertPrivate(partial);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, host.MyUrl(track.Id));
        conditional.Headers.IfNoneMatch.Add(etag);
        using var unchanged = await host.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        AssertPrivate(unchanged);
    }

    [Theory]
    [InlineData("user", true, HttpStatusCode.OK)]
    [InlineData("admin", true, HttpStatusCode.OK)]
    [InlineData("user", false, HttpStatusCode.NotFound)]
    [InlineData("admin", false, HttpStatusCode.NotFound)]
    public async Task Private_original_access_requires_ownership_and_has_no_administrator_override(
        string role, bool ownsTrack, HttpStatusCode expected)
    {
        using var host = new OriginalAudioExportTestHost();
        var track = PrivateTrack(host, host.UserId);
        host.Asset(track.Id);
        SignIn(host, ownsTrack ? host.UserId : Guid.NewGuid(), role);

        using var response = await host.Client.GetAsync(host.MyUrl(track.Id));

        Assert.Equal(expected, response.StatusCode);
        AssertPrivate(response);
        if (!ownsTrack) Assert.NotEqual(OriginalAudioExportTestHost.OriginalBytes, await response.Content.ReadAsByteArrayAsync());
        if (role == "admin")
        {
            using var administratorRoute = await host.Client.GetAsync(host.Url(track.Id));
            Assert.Equal(HttpStatusCode.NotFound, administratorRoute.StatusCode);
        }
    }

    [Fact]
    public async Task Anonymous_requests_are_unauthorized()
    {
        using var host = new OriginalAudioExportTestHost();
        var track = PrivateTrack(host, host.UserId);
        host.Asset(track.Id);
        host.Client.DefaultRequestHeaders.Remove("X-Export-Test-Role");
        host.Client.DefaultRequestHeaders.Remove("X-Export-Test-User");

        using var response = await host.Client.GetAsync(host.MyUrl(track.Id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(OriginalAudioExportTestHost.OriginalBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_authenticated_principal_without_a_valid_user_id_is_unauthorized()
    {
        using var host = new OriginalAudioExportTestHost();
        var track = PrivateTrack(host, host.UserId);
        host.Asset(track.Id);
        host.Client.DefaultRequestHeaders.Remove("X-Export-Test-User");

        using var response = await host.Client.GetAsync(host.MyUrl(track.Id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertPrivate(response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_library_references_and_retained_uploader_ids_do_not_allow_private_export(bool retainsUploader)
    {
        using var host = new OriginalAudioExportTestHost();
        SignIn(host, host.UserId);
        var track = host.Track();
        if (retainsUploader)
        {
            track.OwnerUserId = host.UserId;
            host.Db.Updateable(track).ExecuteCommand();
        }
        host.Db.CodeFirst.InitTables<UserLibraryItem>();
        host.Db.Insertable(new UserLibraryItem { UserId = host.UserId, TrackId = track.Id }).ExecuteCommand();
        host.Asset(track.Id);

        using var response = await host.Client.GetAsync(host.MyUrl(track.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertPrivate(response);
        SignIn(host, host.UserId, "admin");
        using var administratorRoute = await host.Client.GetAsync(host.Url(track.Id));
        Assert.Equal(HttpStatusCode.OK, administratorRoute.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("legacy")]
    [InlineData("unpublished")]
    [InlineData("traversal")]
    [InlineData("no-asset")]
    public async Task The_owner_cannot_export_missing_unverified_unpublished_or_unsafe_originals(string fault)
    {
        using var host = new OriginalAudioExportTestHost();
        SignIn(host, host.UserId);
        var track = PrivateTrack(host, host.UserId);
        if (fault != "no-asset")
            host.Asset(track.Id, fault == "traversal" ? "../outside.flac" : "original/source.flac",
                isOriginal: fault != "legacy", isPublished: fault != "unpublished", writeFile: fault is not ("missing" or "traversal"));
        File.WriteAllBytes(Path.Combine(host.Root, "outside.flac"), OriginalAudioExportTestHost.OriginalBytes);

        using var response = await host.Client.GetAsync(host.MyUrl(track.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertPrivate(response);
        Assert.DoesNotContain(host.Root, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("recycled")]
    [InlineData("deleted-track")]
    [InlineData("deleted-asset")]
    [InlineData("unpublished-asset")]
    [InlineData("now-public")]
    public async Task Visibility_and_asset_state_are_rechecked_before_conditional_or_range_downloads(string change)
    {
        using var host = new OriginalAudioExportTestHost();
        SignIn(host, host.UserId);
        var track = PrivateTrack(host, host.UserId);
        var asset = host.Asset(track.Id);
        using var first = await host.Client.GetAsync(host.MyUrl(track.Id));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = Assert.IsType<EntityTagHeaderValue>(first.Headers.ETag);
        switch (change)
        {
            case "recycled":
                track.IsRecycled = true;
                host.Db.Updateable(track).ExecuteCommand();
                break;
            case "deleted-track":
                host.Db.Deleteable<Track>().Where(candidate => candidate.Id == track.Id).ExecuteCommand();
                break;
            case "deleted-asset":
                host.Db.Deleteable<MediaAsset>().Where(candidate => candidate.Id == asset.Id).ExecuteCommand();
                break;
            case "unpublished-asset":
                asset.IsPublished = false;
                host.Db.Updateable(asset).ExecuteCommand();
                break;
            case "now-public":
                track.PublishedAt = DateTimeOffset.UtcNow;
                host.Db.Updateable(track).ExecuteCommand();
                break;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, host.MyUrl(track.Id));
        request.Headers.IfNoneMatch.Add(etag);
        request.Headers.Range = new RangeHeaderValue(0, 15);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertPrivate(response);
    }

    [Fact]
    public async Task An_unknown_track_is_not_found()
    {
        using var host = new OriginalAudioExportTestHost();
        SignIn(host, host.UserId);

        using var response = await host.Client.GetAsync(host.MyUrl(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertPrivate(response);
    }

    private static Track PrivateTrack(OriginalAudioExportTestHost host, Guid owner, string title = "private song")
    {
        var track = host.Track(title);
        track.PublishedAt = null;
        track.OwnerUserId = owner;
        host.Db.Updateable(track).ExecuteCommand();
        return track;
    }

    private static void SignIn(OriginalAudioExportTestHost host, Guid userId, string role = "user")
    {
        host.Client.DefaultRequestHeaders.Remove("X-Export-Test-Role");
        host.Client.DefaultRequestHeaders.Remove("X-Export-Test-User");
        host.Client.DefaultRequestHeaders.Add("X-Export-Test-Role", role);
        host.Client.DefaultRequestHeaders.Add("X-Export-Test-User", userId.ToString());
    }

    private static void AssertPrivate(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
