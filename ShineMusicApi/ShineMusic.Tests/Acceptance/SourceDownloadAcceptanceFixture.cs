using System.Net;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Model.Ports;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using ShineMusic.Service.Messaging;
using ShineMusic.Service.Media;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;
using ShineMusic.Tests.SourceProviders;
using SqlSugar;

namespace ShineMusic.Tests.Acceptance;

internal sealed class SourceDownloadAcceptanceFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "shine-acceptance-" + Guid.NewGuid().ToString("N"));
    public SqlSugarClient Db { get; }
    public SourceDownloadOptions Options { get; } = new();
    public RecordingBus Bus { get; } = new();
    public ScriptedMedia Media { get; } = new();
    public ScriptedHttp Http { get; } = new();
    public QuarantineStoragePaths Storage { get; }
    public LadderProvider Primary { get; } = new("qq");
    public List<IMusicSourceProvider> Legacy { get; } = [];
    public List<FakeV2Provider> SearchProviders { get; } = [];
    private readonly ServiceProvider _leaseServices;

    public SourceDownloadAcceptanceFixture()
    {
        Directory.CreateDirectory(Root);
        Db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={Path.Combine(Root, "acceptance.db")};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.DbTableName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                EntityService = (property, column) => column.DbColumnName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)
            }
        });
        Db.CodeFirst.InitTables<ExternalDownloadTask, LibraryImportBatch, LibraryImportItem, MediaAsset, MessageOutboxEntry>();
        Db.CodeFirst.InitTables<MessageDeadLetter>();
        Db.CodeFirst.InitTables<LibraryWorkflowReview>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<MediaWorkerIdentity>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new MediaLeaseOptions()));
        services.AddScoped<ISqlSugarClient>(_ => new SqlSugarClient(Db.CurrentConnectionConfig));
        services.AddScoped<MediaTaskLeaseService>();
        _leaseServices = services.BuildServiceProvider();
        Storage = new QuarantineStoragePaths(Microsoft.Extensions.Options.Options.Create(
            new StorageOptions { QuarantineRoot = Root }), new TestEnvironment { ContentRootPath = Root });
        Legacy.Add(Primary);
        SearchProviders.Add(new FakeV2Provider("qq"));
    }

    public ISearchAggregator Aggregator()
    {
        var (registry, _, orchestrator) = FrameworkFixtures.Build(Options, SearchProviders.ToArray());
        return new SearchAggregator(orchestrator, registry, Legacy);
    }

    public ProviderAccessService Access()
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(Options);
        var health = new ProviderHealthTracker();
        var adapters = Legacy.Select(provider => new AcceptanceAdapter(provider, wrapped)).ToArray();
        var registry = new ProviderRegistry(adapters, wrapped, health);
        var orchestrator = new ProviderOrchestrator(registry, health, wrapped, NullLogger<ProviderOrchestrator>.Instance);
        return new ProviderAccessService(registry, orchestrator, health, Legacy, NullLogger<ProviderAccessService>.Instance);
    }

    public SourceDownloadJob Job() => new(Db, Aggregator(), Bus, Http,
        Microsoft.Extensions.Options.Options.Create(Options), Storage, Media, NullLogger<SourceDownloadJob>.Instance, Access(), Lease());

    public MediaTaskLeaseService Lease() => new(Db, _leaseServices.GetRequiredService<IServiceScopeFactory>(),
        _leaseServices.GetRequiredService<MediaWorkerIdentity>(),
        Microsoft.Extensions.Options.Options.Create(new MediaLeaseOptions()), NullLogger<MediaTaskLeaseService>.Instance);

    public SourceDownloadFallbackService Fallback() => new(Db, Aggregator(),
        new SourceDownloadService(Db, Legacy, Bus, Access()), NullLogger<SourceDownloadFallbackService>.Instance);

    public ExternalDownloadTask Seed(string status = ExternalDownloadTaskStatuses.Queued, long duration = 269000)
    {
        var task = new ExternalDownloadTask
        {
            SourceProvider = "qq", ProviderTrackId = "track-1", Title = "晴天",
            ArtistsJson = "[\"周杰伦\"]", DurationMs = duration, Status = status,
            CreatedByUserId = Guid.NewGuid()
        };
        Db.Insertable(task).ExecuteCommand();
        Primary.DetailDuration = duration;
        return task;
    }

    public ExternalDownloadTask Reload(Guid id) => Db.Queryable<ExternalDownloadTask>().Single(t => t.Id == id);
    public void Dispose()
    {
        Http.Dispose();
        _leaseServices.Dispose();
        Db.Dispose();
        Directory.Delete(Root, true);
    }
}

internal sealed class AcceptanceAdapter(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
    : ShineMusic.Service.SourceProviders.Adapters.LegacySourceProviderAdapter(legacy, options)
{
    public override bool UsesVerifiedCapabilities => false;
    public override IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities => FrameworkFixtures.AllCapabilities();
    public override Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("下载任务应继续使用生产文件流水线。");
}

internal sealed class RecordingBus : IRabbitMessageBus
{
    public List<(string Queue, Guid Id)> Messages { get; } = [];
    public bool FailImports { get; set; }
    public Task PublishAsync(string queue, Guid id, CancellationToken cancellationToken = default)
    {
        if (FailImports && queue == QueueNames.Imports)
            throw new InvalidOperationException("simulated broker failure");
        Messages.Add((queue, id));
        return Task.CompletedTask;
    }
}

internal sealed class ScriptedMedia : IMediaTranscoder
{
    public Func<string, MediaProbe> Probe { get; set; } = _ => Flac();
    public static MediaProbe Flac(long duration = 269000) => new("flac", "flac", 935, duration, "flac", 44100, 16, 2);
    public static MediaProbe Mp3(int bitrate = 320, long duration = 269000) => new("mp3", "mp3", bitrate, duration, "mp3", 44100, null, 2);
    public Task<MediaProbe> ProbeAsync(string sourcePath, CancellationToken cancellationToken) => Task.FromResult(Probe(sourcePath));
    public Task TranscodeToAacAsync(string sourcePath, string destinationPath, int bitrateKbps, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("下载验收不应转码");
}

internal sealed class ScriptedHttp : HttpMessageHandler, IHttpClientFactory
{
    public List<string> Requests { get; } = [];
    public Func<string, HttpResponseMessage> Respond { get; set; } = _ =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) };
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(path);
        return Task.FromResult(Respond(path));
    }
}

internal sealed class LadderProvider(string provider) : IMusicSourceProvider, IQualityLadderProvider
{
    public string Provider => provider;
    public bool IsEnabled { get; set; } = true;
    public string? DisabledReason => IsEnabled ? null : "disabled";
    public long DetailDuration { get; set; } = 269000;
    public IReadOnlyList<SourceDownloadInfo> Candidates { get; set; } = [Candidate("sq", true, 935)];
    public static SourceDownloadInfo Candidate(string name, bool lossless, int bitrate) =>
        new($"https://example.test/{name}.{(lossless ? "flac" : "mp3")}", lossless ? "flac" : "mp3", lossless, bitrate,
            lossless ? SourceQualities.Lossless : bitrate >= 320 ? SourceQualities.High : SourceQualities.Standard);
    public Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(Array.Empty<SourceSearchResult>());
    public Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken) =>
        Task.FromResult<SourceSearchResult?>(FrameworkFixtures.Result(Provider) with { DurationMs = DetailDuration, CoverUrl = null });
    public Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken) => Task.FromResult(Candidates[0]);
    public Task<IReadOnlyList<SourceDownloadInfo>> ResolveDownloadCandidatesAsync(string providerTrackId, CancellationToken cancellationToken) => Task.FromResult(Candidates);
}

internal sealed class TestEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "ShineMusic.Tests";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
