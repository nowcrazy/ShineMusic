using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Services;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.Messaging;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

/// <summary>Real multipart, media processing, private library and HTTP Range delivery; isolated SQLite/files only.</summary>
[Collection(L3aCollection.Name)]
public sealed class UserUploadChainTests
{
    public static IEnumerable<object?[]> Sources()
    {
        yield return [null, false];
        yield return [null, true];
        var directory = Environment.GetEnvironmentVariable("SHINEMUSIC_TEST_USER_AUDIO_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.flac").Order())
                yield return [Path.GetFullPath(path), false];
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task Upload_process_private_library_stream_and_reupload(string? sourcePath, bool appendTrailingData)
    {
        using var factory = new UserUploadChainFactory();
        var userId = Guid.NewGuid();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        var source = sourcePath ?? Path.Combine(factory.Root, "generated.flac");
        if (sourcePath is null)
        {
            await GenerateAsync(source);
            if (appendTrailingData)
            {
                await using var tail = new FileStream(source, FileMode.Append);
                await tail.WriteAsync(Encoding.ASCII.GetBytes("non-audio trailing data after complete FLAC frames"));
            }
        }
        var originalHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source));

        using var uploaded = await UploadAsync(client, source);
        Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode);
        using var upload = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var batchId = upload.RootElement.GetProperty("batchId").GetGuid();
        var itemId = Assert.Single(upload.RootElement.GetProperty("queued").EnumerateArray()).GetProperty("itemId").GetGuid();
        Assert.Contains(factory.Bus.Messages, message => message.Queue == QueueNames.Imports && message.Id == itemId);
        Assert.Equal(0, factory.Db.Queryable<Track>().Count());
        var staged = factory.Db.Queryable<LibraryImportItem>().Single(item => item.Id == itemId);
        var stagedPath = Path.Combine(factory.QuarantineRoot, staged.SourceStorageKey);
        Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(stagedPath)));

        using (var queuedResponse = await client.GetAsync($"/api/v1/me/uploads/{batchId}"))
        {
            Assert.Equal(HttpStatusCode.OK, queuedResponse.StatusCode);
            using var queued = JsonDocument.Parse(await queuedResponse.Content.ReadAsStringAsync());
            Assert.Equal("uploaded", Assert.Single(queued.RootElement.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
        }
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<LibraryImportJob>().ProcessAsync(itemId, CancellationToken.None);

        var processed = factory.Db.Queryable<LibraryImportItem>().Single(item => item.Id == itemId);
        Assert.True(processed.Status == LibraryImportItemStatuses.Published,
            $"Unexpected status {processed.Status}: {processed.ErrorCode} {processed.ErrorMessage}");
        Assert.Null(processed.ErrorCode);
        var trackId = Assert.IsType<Guid>(processed.PublishedTrackId);
        var track = factory.Db.Queryable<Track>().Single(row => row.Id == trackId);
        Assert.Equal(userId, track.OwnerUserId);
        Assert.Null(track.PublishedAt);

        using (var statusResponse = await client.GetAsync($"/api/v1/me/uploads/{batchId}"))
        {
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            var item = Assert.Single(status.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("published", item.GetProperty("status").GetString());
            Assert.Equal(trackId, item.GetProperty("trackId").GetGuid());
        }
        using (var libraryResponse = await client.GetAsync("/api/v1/me/library"))
        {
            Assert.Equal(HttpStatusCode.OK, libraryResponse.StatusCode);
            using var library = JsonDocument.Parse(await libraryResponse.Content.ReadAsStringAsync());
            var item = Assert.Single(library.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(trackId, item.GetProperty("trackId").GetGuid());
            Assert.False(item.GetProperty("isPublic").GetBoolean());
        }

        var asset = factory.Db.Queryable<MediaAsset>().Single(row => row.TrackId == trackId && row.IsPrimary);
        Assert.True(asset.IsOriginal);
        var mediaPath = Path.Combine(factory.LibraryRoot, asset.StorageKey);
        Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(mediaPath)));
        await factory.Services.GetRequiredService<FfmpegMediaService>().ValidateCompleteAsync(mediaPath, CancellationToken.None);
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tracks/{trackId}/stream"))
        {
            request.Headers.Range = new RangeHeaderValue(0, 31);
            using var response = await client.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(new FileInfo(mediaPath).Length, response.Content.Headers.ContentRange?.Length);
            Assert.Equal(32, bytes.Length);
            Assert.Equal("fLaC", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Contains("private", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tracks/{trackId}/stream?format=aac"))
        {
            request.Headers.Range = new RangeHeaderValue(0, 31);
            using var response = await client.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("audio/mp4", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.EnumerateFiles(factory.TemporaryPlaybackRoot, "*.m4a").Any());
            // A rendition is transient; the retained original and its quality record are unchanged.
            Assert.Equal(1, factory.Db.Queryable<MediaAsset>().Count());
            Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(mediaPath)));
        }

        using (var repeated = await UploadAsync(client, source))
        {
            Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
            using var repeat = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
            Assert.Equal(trackId, Assert.Single(repeat.RootElement.GetProperty("referenced").EnumerateArray()).GetProperty("trackId").GetGuid());
            Assert.Empty(repeat.RootElement.GetProperty("queued").EnumerateArray());
        }
        Assert.Equal(1, factory.Db.Queryable<Track>().Count());
        Assert.Equal(1, factory.Db.Queryable<MediaAsset>().Count());
        Assert.Equal(1, factory.Db.Queryable<UserLibraryItem>().Count());

        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        using var otherBatch = await other.GetAsync($"/api/v1/me/uploads/{batchId}");
        using var otherStream = await other.GetAsync($"/api/v1/tracks/{trackId}/stream");
        Assert.Equal(HttpStatusCode.NotFound, otherBatch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherStream.StatusCode);
        Assert.Contains("private", otherStream.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", otherStream.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string source)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(source)), "files", Path.GetFileName(source));
        return await client.PostAsync("/api/v1/me/uploads", form);
    }

    private static async Task GenerateAsync(string path)
    {
        var start = new ProcessStartInfo(TestFfmpeg.Options().ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "flac", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}

public sealed class UserUploadChainFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _previousEnvironment = [];
    private readonly string _previousWorkingDirectory = Environment.CurrentDirectory;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "shine-user-upload-chain-" + Guid.NewGuid().ToString("N"));
    public string LibraryRoot => Path.Combine(Root, "library");
    public string QuarantineRoot => Path.Combine(Root, "quarantine");
    public string TemporaryPlaybackRoot => Path.Combine(Root, "temporary-playback");
    public SqlSugarClient Db { get; }
    public UploadChainBus Bus { get; } = new();

    public UserUploadChainFactory()
    {
        foreach (var (key, value) in new[] {
            ("Auth__SigningKey", "Upload-Chain-Integration-Test-Key-0123456789-abcdef"),
            ("Worker__Mode", "none"), ("Features__UserUploads", "true") })
        {
            _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        Environment.CurrentDirectory = ApiDirectory();
        Directory.CreateDirectory(Root);
        Db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={Path.Combine(Root, "chain.db")};Pooling=False",
            DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.DbTableName = JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                EntityService = (property, column) => column.DbColumnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
            }
        });
        Db.CodeFirst.InitTables(typeof(Track), typeof(Artist), typeof(Album), typeof(Genre), typeof(Artwork),
            typeof(TrackArtist), typeof(AlbumArtist), typeof(TrackGenre), typeof(Lyric), typeof(MediaAsset),
            typeof(UserLibraryItem), typeof(LibraryImportBatch), typeof(LibraryImportItem), typeof(ExternalDownloadTask),
            typeof(LibraryWorkflowReview), typeof(UserNotification), typeof(MessageOutboxEntry));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(ApiDirectory());
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStartupBootstrap>();
            services.AddSingleton<IStartupBootstrap, NoopStartupBootstrap>();
            services.RemoveAll<IHostedService>();
            services.RemoveAll<ISqlSugarClient>();
            services.AddSingleton<ISqlSugarClient>(Db);
            services.RemoveAll<IRabbitMessageBus>();
            services.AddSingleton<IRabbitMessageBus>(Bus);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory, OfflineUploadHttpFactory>();
            services.PostConfigure<StorageOptions>(options => { options.LibraryRoot = LibraryRoot; options.QuarantineRoot = QuarantineRoot; });
            services.PostConfigure<TemporaryPlaybackOptions>(options => options.CacheDirectory = TemporaryPlaybackRoot);
            services.PostConfigure<LyricsOptions>(options => options.AutoFetchEnabled = false);
            services.PostConfigure<FfmpegOptions>(options =>
            {
                var ffmpeg = TestFfmpeg.Options();
                options.ExecutablePath = ffmpeg.ExecutablePath;
                options.ProbeExecutablePath = ffmpeg.ProbeExecutablePath;
            });
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "UploadTest";
                options.DefaultChallengeScheme = "UploadTest";
            }).AddScheme<AuthenticationSchemeOptions, UploadTestAuthenticationHandler>("UploadTest", _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Db.Dispose();
        Environment.CurrentDirectory = _previousWorkingDirectory;
        foreach (var (key, value) in _previousEnvironment) Environment.SetEnvironmentVariable(key, value);
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    private static string ApiDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var file = Path.Combine(directory.FullName, "ShineMusic.Api", "appsettings.json");
            if (File.Exists(file)) return Path.GetDirectoryName(file)!;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Cannot locate the API project configuration.");
    }
}

public sealed class UploadChainBus : IRabbitMessageBus
{
    public List<(string Queue, Guid Id)> Messages { get; } = [];
    public Task PublishAsync(string queue, Guid id, CancellationToken cancellationToken = default)
    {
        Messages.Add((queue, id));
        return Task.CompletedTask;
    }
}

public sealed class OfflineUploadHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new OfflineUploadHttpHandler());
    private sealed class OfflineUploadHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}

public sealed class UploadTestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Test-User"].ToString(), out var userId))
            return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId.ToString())], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
