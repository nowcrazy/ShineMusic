using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Controllers.Admin;
using ShineMusic.Api.Controllers.App;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using ShineMusic.Tests.Acceptance;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

/// <summary>Real authorization, file responses, SQLite selection and original byte readback.</summary>
public sealed class OriginalAudioExportTests
{
    [Theory]
    [InlineData("https://export-client.example", true)]
    [InlineData("https://unconfigured-client.example", false)]
    public async Task Configured_browser_can_read_the_attachment_name_and_range_metadata(string origin, bool allowed)
    {
        using var host = new OriginalAudioExportTestHost();
        var track = host.Track("原始歌曲");
        host.Asset(track.Id);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.Url(track.Id));
        request.Headers.Add("Origin", origin);
        request.Headers.Range = new RangeHeaderValue(4, 15);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(OriginalAudioExportTestHost.OriginalBytes[4..16], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Expose-Headers"));
        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"))
                .Split(',', StringSplitOptions.TrimEntries);
            foreach (var name in new[] { "Content-Disposition", "ETag", "Accept-Ranges", "Content-Range" })
                Assert.Contains(name, exposed, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("原始歌曲.flac", response.Content.Headers.ContentDisposition?.FileNameStar);
        }
    }

    [Fact]
    public async Task Administrator_receives_original_attachment_instead_of_primary_compatibility_copy()
    {
        using var host = new OriginalAudioExportTestHost();
        var track = host.Track("原始歌曲");
        var original = host.Asset(track.Id, "original/source.flac", isPrimary: false);
        host.Asset(track.Id, "playback/compatibility.m4a", isOriginal: false, container: "m4a", bytes: [9, 8, 7]);

        using var response = await host.Client.GetAsync(host.Url(track.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("原始歌曲.flac", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(OriginalAudioExportTestHost.OriginalBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(original.ContentHash, Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(host.LibraryRoot, original.StorageKey)))).ToLowerInvariant());
        Assert.Equal(2, host.Db.Queryable<MediaAsset>().Count());
        AssertPrivate(response);
    }

    [Fact]
    public async Task Range_and_conditional_requests_reuse_native_original_file_semantics()
    {
        using var host = new OriginalAudioExportTestHost();
        var track = host.Track();
        host.Asset(track.Id);
        using var full = await host.Client.GetAsync(host.Url(track.Id));
        var etag = Assert.IsType<EntityTagHeaderValue>(full.Headers.ETag);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.Url(track.Id));
        request.Headers.Range = new RangeHeaderValue(4, 15);
        request.Headers.IfRange = new RangeConditionHeaderValue(etag);

        using var partial = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(4, 15, OriginalAudioExportTestHost.OriginalBytes.Length), partial.Content.Headers.ContentRange);
        Assert.Equal(OriginalAudioExportTestHost.OriginalBytes[4..16], await partial.Content.ReadAsByteArrayAsync());
        AssertPrivate(partial);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, host.Url(track.Id));
        conditional.Headers.IfNoneMatch.Add(etag);
        using var unchanged = await host.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        AssertPrivate(unchanged);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("user", HttpStatusCode.Forbidden)]
    public async Task Authentication_and_administrator_role_are_required(string? role, HttpStatusCode expected)
    {
        using var host = new OriginalAudioExportTestHost();
        var track = host.Track();
        host.Asset(track.Id);
        host.Client.DefaultRequestHeaders.Remove("X-Export-Test-Role");
        if (role is not null) host.Client.DefaultRequestHeaders.Add("X-Export-Test-Role", role);

        using var response = await host.Client.GetAsync(host.Url(track.Id));

        Assert.Equal(expected, response.StatusCode);
        Assert.NotEqual(OriginalAudioExportTestHost.OriginalBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Private_or_recycled_tracks_are_hidden_even_from_administrators(bool recycled)
    {
        using var host = new OriginalAudioExportTestHost();
        var track = host.Track();
        track.OwnerUserId = Guid.NewGuid();
        track.IsRecycled = recycled;
        if (!recycled) track.PublishedAt = null;
        host.Db.Updateable(track).ExecuteCommand();
        host.Asset(track.Id);

        using var response = await host.Client.GetAsync(host.Url(track.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertPrivate(response);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("retired")]
    [InlineData("missing")]
    [InlineData("traversal")]
    [InlineData("unknown-track")]
    public async Task Unverified_unpublished_missing_or_unsafe_assets_are_not_exported(string fault)
    {
        using var host = new OriginalAudioExportTestHost();
        var track = host.Track();
        host.Asset(track.Id, fault == "traversal" ? "../outside.flac" : "original/source.flac",
            isOriginal: fault != "legacy", isPublished: fault != "retired", writeFile: fault is not ("missing" or "traversal"));
        // A real file outside the library must remain inaccessible through a stored key.
        File.WriteAllBytes(Path.Combine(host.Root, "outside.flac"), OriginalAudioExportTestHost.OriginalBytes);

        using var response = await host.Client.GetAsync(host.Url(fault == "unknown-track" ? Guid.NewGuid() : track.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertPrivate(response);
        Assert.DoesNotContain(host.Root, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../bad\\name:\"\r\n.flac", "_bad_name____.flac.flac")]
    [InlineData("CON", "_CON.flac")]
    [InlineData("  ...  ", null)]
    public async Task Attachment_filename_removes_paths_header_controls_and_reserved_device_names(string title, string? expected)
    {
        using var host = new OriginalAudioExportTestHost();
        var track = host.Track(title);
        host.Asset(track.Id);

        using var response = await host.Client.GetAsync(host.Url(track.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar;
        Assert.Equal(expected ?? $"track-{track.Id:N}.flac", name);
        Assert.DoesNotContain('/', name!);
        Assert.DoesNotContain('\\', name!);
        Assert.DoesNotContain('\r', name!);
        Assert.DoesNotContain('\n', name!);
    }

    [Fact]
    public async Task Unknown_container_uses_a_non_executable_attachment_extension()
    {
        using var host = new OriginalAudioExportTestHost();
        var track = host.Track("song");
        host.Asset(track.Id, container: "exe");

        using var response = await host.Client.GetAsync(host.Url(track.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("song.audio", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_publication_marks_only_a_retained_source_as_original(bool originalAvailable)
    {
        using var host = new OriginalAudioExportTestHost();
        host.Db.CodeFirst.InitTables(typeof(Artist), typeof(TrackArtist), typeof(Album), typeof(AlbumArtist),
            typeof(Genre), typeof(TrackGenre), typeof(Lyric), typeof(LibraryImportBatch), typeof(LibraryImportItem),
            typeof(AdminAuditEntry), typeof(ExternalDownloadTask));
        var quarantine = Path.Combine(host.Root, "quarantine");
        Directory.CreateDirectory(quarantine);
        if (originalAvailable) File.WriteAllBytes(Path.Combine(quarantine, "source.flac"), OriginalAudioExportTestHost.OriginalBytes);
        var compatible = new byte[4096];
        Encoding.ASCII.GetBytes("ftyp").CopyTo(compatible, 4);
        File.WriteAllBytes(Path.Combine(quarantine, "compatible.m4a"), compatible);
        var batch = new LibraryImportBatch { CreatedByUserId = Guid.NewGuid() };
        host.Db.Insertable(batch).ExecuteCommand();
        var item = new LibraryImportItem
        {
            BatchId = batch.Id, Title = "Publication", ArtistsJson = "[\"Artist\"]", DurationMs = 200000,
            DisplayName = "source.flac", SourceStorageKey = "source.flac", Container = "flac", Codec = "flac",
            ContentHash = Convert.ToHexString(SHA256.HashData(OriginalAudioExportTestHost.OriginalBytes)).ToLowerInvariant(),
            BitrateKbps = 900, TranscodeBitrateKbps = 192, TranscodedStorageKey = "compatible.m4a",
            TranscodeStatus = "succeeded", IsMetadataReviewed = true, Status = LibraryImportItemStatuses.ReadyToPublish
        };
        host.Db.Insertable(item).ExecuteCommand();
        var publisher = new ImportPublishService(host.Db, Options.Create(new SongRequestFulfillmentOptions()),
            new QuarantineStoragePaths(Options.Create(new StorageOptions { QuarantineRoot = quarantine }),
                new TestEnvironment { ContentRootPath = host.Root }), host.Paths, NullLogger<ImportPublishService>.Instance,
            new FfmpegMediaService(Options.Create(new FfmpegOptions()), NullLogger<FfmpegMediaService>.Instance),
            new ConfigurationBuilder().Build());

        var published = await publisher.PublishAsync(item, new ImportPublishContext(Guid.Empty, "export-test", false), false, null, default);

        Assert.True(published.Succeeded, published.Error?.Message);
        var assets = host.Db.Queryable<MediaAsset>().ToList();
        Assert.Equal(originalAvailable ? 1 : 0, assets.Count(asset => asset.IsOriginal));
        using var response = await host.Client.GetAsync(host.Url(published.TrackId!.Value));
        Assert.Equal(originalAvailable ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        if (originalAvailable) Assert.Equal(OriginalAudioExportTestHost.OriginalBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Upgraded_asset_is_marked_original_and_exports_the_unmodified_upgrade()
    {
        using var host = new OriginalAudioExportTestHost();
        host.Db.CodeFirst.InitTables<LibraryImportItem>();
        var track = host.Track();
        host.Asset(track.Id, container: "mp3");
        var source = Path.Combine(host.Root, "upgrade.mp3");
        var ffmpeg = TestFfmpeg.Options();
        var start = new ProcessStartInfo(ffmpeg.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "libmp3lame", "-b:a", "320k", source })
            start.ArgumentList.Add(argument);
        using (var process = Process.Start(start)!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        var media = new FfmpegMediaService(Options.Create(ffmpeg), NullLogger<FfmpegMediaService>.Instance);
        var probe = await media.ProbeAsync(source, default);
        var bytes = await File.ReadAllBytesAsync(source);
        var item = new LibraryImportItem
        {
            DisplayName = "upgrade.mp3", SourceStorageKey = "upgrade.mp3", Container = probe.Container,
            Codec = probe.Codec, BitrateKbps = probe.BitrateKbps,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        };
        host.Db.Insertable(item).ExecuteCommand();

        var result = await new AssetReplacementService(host.Db, host.Paths, media, TimeSpan.FromHours(24))
            .ReplaceAsync(track, item, source, default);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.True(host.Db.Queryable<MediaAsset>().Single(asset => asset.IsPublished && asset.IsPrimary).IsOriginal);
        using var response = await host.Client.GetAsync(host.Url(track.Id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    private static void AssertPrivate(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}

internal sealed class OriginalAudioExportTestHost : IDisposable
{
    internal static readonly byte[] OriginalBytes = Encoding.ASCII.GetBytes("fLaC").Concat(
        Enumerable.Range(0, 4092).Select(index => (byte)(index % 251))).ToArray();
    private readonly WebApplication application;
    internal string Root { get; } = Directory.CreateTempSubdirectory("shine-original-export-").FullName;
    internal string LibraryRoot => Path.Combine(Root, "library");
    internal SqlSugarClient Db { get; }
    internal LibraryStoragePaths Paths { get; }
    internal HttpClient Client { get; }
    internal Guid UserId { get; } = Guid.NewGuid();

    internal OriginalAudioExportTestHost()
    {
        Directory.CreateDirectory(LibraryRoot);
        Db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={Path.Combine(Root, "export.db")};Pooling=False", DbType = DbType.Sqlite,
            IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.DbTableName = JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                EntityService = (property, column) => column.DbColumnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
            }
        });
        Db.CodeFirst.InitTables<Track, MediaAsset>();
        Paths = new LibraryStoragePaths(Options.Create(new StorageOptions { LibraryRoot = LibraryRoot }),
            new TestEnvironment { ContentRootPath = Root });
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing", ContentRootPath = Root,
            ApplicationName = typeof(OriginalAudioExportController).Assembly.FullName
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ISqlSugarClient>(Db);
        builder.Services.AddSingleton(Paths);
        builder.Services.AddShineMusicCors(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "https://export-client.example" }).Build(), builder.Environment);
        builder.Services.AddControllers().AddApplicationPart(typeof(OriginalAudioExportController).Assembly)
            .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new ExportControllerFeatureProvider()));
        builder.Services.AddAuthentication("OriginalExportTest").AddScheme<AuthenticationSchemeOptions, OriginalExportTestAuthenticationHandler>(
            "OriginalExportTest", _ => { });
        builder.Services.AddAuthorization();
        application = builder.Build();
        application.UseRouting();
        application.UseCors("Client");
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapControllers();
        application.StartAsync().GetAwaiter().GetResult();
        Client = application.GetTestClient();
        Client.DefaultRequestHeaders.Add("X-Export-Test-Role", "admin");
        Client.DefaultRequestHeaders.Add("X-Export-Test-User", UserId.ToString());
    }

    internal Track Track(string title = "song")
    {
        var track = new Track { Title = title, PublishedAt = DateTimeOffset.UtcNow };
        Db.Insertable(track).ExecuteCommand();
        return track;
    }

    internal MediaAsset Asset(Guid trackId, string key = "original/source.flac", bool isOriginal = true,
        bool isPrimary = true, bool isPublished = true, string container = "flac", bool writeFile = true, byte[]? bytes = null)
    {
        bytes ??= OriginalBytes;
        var asset = new MediaAsset
        {
            TrackId = trackId, StorageKey = key, Container = container, Codec = container, BitrateKbps = 192,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), IsOriginal = isOriginal,
            IsPrimary = isPrimary, IsPublished = isPublished
        };
        if (writeFile)
        {
            var path = Path.Combine(LibraryRoot, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        Db.Insertable(asset).ExecuteCommand();
        return asset;
    }

    internal Uri Url(Guid trackId) => new(Client.BaseAddress!, $"/api/v1/admin/tracks/{trackId}/original");
    internal Uri MyUrl(Guid trackId) => new(Client.BaseAddress!, $"/api/v1/me/library/{trackId}/original");
    public void Dispose()
    {
        Client.Dispose();
        application.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Db.Dispose();
        Directory.Delete(Root, recursive: true);
    }

    private sealed class ExportControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            for (var index = feature.Controllers.Count - 1; index >= 0; index--)
                if (feature.Controllers[index].AsType() != typeof(OriginalAudioExportController)
                    && feature.Controllers[index].AsType() != typeof(UserOriginalAudioExportController))
                    feature.Controllers.RemoveAt(index);
        }
    }
}

internal sealed class OriginalExportTestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Export-Test-Role"].ToString();
        if (role is not ("admin" or "user")) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (Guid.TryParse(Request.Headers["X-Export-Test-User"].ToString(), out var userId))
            claims.Add(new("sub", userId.ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
