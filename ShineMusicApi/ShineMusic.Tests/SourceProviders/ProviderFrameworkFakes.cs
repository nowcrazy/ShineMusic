// 职责：V2.0 Provider 框架测试的公共替身与装配工具（不连 DB / MQ / 网络）。

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;

namespace ShineMusic.Tests.SourceProviders;

internal static class FrameworkFixtures
{
    /// <summary>默认配置：把预算压到秒级，测试不等 25 秒。</summary>
    internal static SourceDownloadOptions Options(Action<SourceDownloadOptions>? configure = null)
    {
        var options = new SourceDownloadOptions();
        options.ProviderFramework.Search.TotalBudgetSeconds = 3;
        options.ProviderFramework.Search.ProviderTimeoutSeconds = 1;
        options.ProviderFramework.Search.MaxProviderTimeoutSeconds = 12;
        options.ProviderFramework.Search.MaxConcurrentProviders = 5;
        options.ProviderFramework.Search.MaxResultsPerProvider = 20;
        configure?.Invoke(options);
        return options;
    }

    internal static (ProviderRegistry Registry, ProviderHealthTracker Health, ProviderOrchestrator Orchestrator) Build(
        SourceDownloadOptions options,
        params IMusicProvider[] providers)
    {
        var health = new ProviderHealthTracker();
        var optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var registry = new ProviderRegistry(providers, optionsWrapper, health);
        var orchestrator = new ProviderOrchestrator(
            registry, health, optionsWrapper, NullLogger<ProviderOrchestrator>.Instance);
        return (registry, health, orchestrator);
    }

    internal static MusicTrack Track(string provider, string title = "晴天") =>
        new(provider, "track-1",
            new TrackIdentity(title, ["周杰伦"], 269000, "叶惠美"),
            SourceQualities.High, false, "https://example.test/song", null, null);

    internal static SourceSearchResult Result(string provider) =>
        new(provider, "track-1", "晴天", ["周杰伦"], "叶惠美", 269000,
            [], "https://example.test/cover", "https://example.test/song", null,
            SourceQualities.High, false, 2003);

    internal static SourceDownloadInfo Info(
        string url = "https://example.test/audio.flac",
        bool lossless = true,
        int? bitrateKbps = 935,
        string container = "flac") =>
        new(url, container, lossless, bitrateKbps,
            lossless ? SourceQualities.Lossless : SourceQualities.High);

    internal static IReadOnlyList<ProviderCapabilityDescriptor> AllCapabilities(
        ProviderStatus status = ProviderStatus.Available) =>
        Enum.GetValues<ProviderCapability>()
            .Where(capability => capability != ProviderCapability.None)
            .Select(capability => new ProviderCapabilityDescriptor(capability, status))
            .ToArray();

    internal static IReadOnlyList<ProviderCapabilityDescriptor> Capabilities(
        params (ProviderCapability Capability, ProviderStatus Status)[] states) =>
        states.Select(item => new ProviderCapabilityDescriptor(item.Capability, item.Status)).ToArray();
}

/// <summary>可编排行为的 V2 Provider 替身。</summary>
internal sealed class FakeV2Provider : IMusicSearchProvider, IMusicMetadataProvider, IMusicDownloadProvider
{
    public FakeV2Provider(
        string name,
        bool configEnabled = true,
        IReadOnlyList<ProviderCapabilityDescriptor>? capabilities = null)
    {
        ProviderName = name;
        IsConfigEnabled = configEnabled;
        DeclaredCapabilities = capabilities ?? FrameworkFixtures.AllCapabilities();
    }

    public string ProviderName { get; }

    public bool IsConfigEnabled { get; set; }

    public string? ConfigDisabledReason => IsConfigEnabled ? null : $"{ProviderName} 配置禁用";

    public IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities { get; set; }

    public Func<CancellationToken, Task<IReadOnlyList<MusicTrack>>>? OnSearch { get; set; }

    public Func<CancellationToken, Task<DownloadResult>>? OnDownload { get; set; }

    public Task<IReadOnlyList<MusicTrack>> SearchAsync(string keyword, CancellationToken cancellationToken = default) =>
        OnSearch is null
            ? Task.FromResult<IReadOnlyList<MusicTrack>>([FrameworkFixtures.Track(ProviderName)])
            : OnSearch(cancellationToken);

    public Task<MusicTrack?> GetTrackAsync(string trackId, CancellationToken cancellationToken = default) =>
        Task.FromResult<MusicTrack?>(FrameworkFixtures.Track(ProviderName));

    public Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) =>
        OnDownload is null
            ? Task.FromResult(new DownloadResult
            {
                Stage = DownloadStage.Resolved,
                ProviderName = ProviderName,
                ProviderTrackId = request.ProviderTrackId,
                DownloadUrl = "https://example.test/audio.flac",
            })
            : OnDownload(cancellationToken);
}

/// <summary>既有 IMusicSourceProvider 替身，用于验证薄适配层是否真的委托老实现。</summary>
internal sealed class FakeLegacyProvider : IMusicSourceProvider, IQualityLadderProvider, ISourceFileDownloader
{
    public FakeLegacyProvider(string provider, bool enabled = true)
    {
        Provider = provider;
        IsEnabled = enabled;
    }

    public string Provider { get; }

    public bool IsEnabled { get; set; }

    public string? DisabledReason => IsEnabled ? null : $"{Provider} 未启用";

    public IReadOnlyList<SourceSearchResult> SearchResults { get; set; } = [FrameworkFixtures.Result("netease")];

    public SourceSearchResult? DetailResult { get; set; }

    public IReadOnlyList<SourceDownloadInfo> Candidates { get; set; } = [FrameworkFixtures.Info()];

    public SourceDownloadInfo? SingleDownloadInfo { get; set; }

    public string? DownloadedFilePath { get; set; }

    public Exception? ResolveException { get; set; }

    public int LastSearchLimit { get; private set; }

    public int SearchCallCount { get; private set; }

    public Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        LastSearchLimit = limit;
        SearchCallCount++;
        return Task.FromResult(SearchResults.ToArray());
    }

    public Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken) =>
        Task.FromResult(DetailResult);

    public Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken) =>
        ResolveException is not null
            ? Task.FromException<SourceDownloadInfo>(ResolveException)
            : Task.FromResult(SingleDownloadInfo ?? FrameworkFixtures.Info());

    public Task<IReadOnlyList<SourceDownloadInfo>> ResolveDownloadCandidatesAsync(
        string providerTrackId, CancellationToken cancellationToken) =>
        ResolveException is not null
            ? Task.FromException<IReadOnlyList<SourceDownloadInfo>>(ResolveException)
            : Task.FromResult(Candidates);

    public Task<SourceFileDownload> DownloadToFileAsync(
        string providerTrackId, string outputDirectory, string fileBaseName, CancellationToken cancellationToken)
    {
        if (ResolveException is not null)
            return Task.FromException<SourceFileDownload>(ResolveException);
        return Task.FromResult(new SourceFileDownload(
            DownloadedFilePath ?? string.Empty, "flac", SourceQualities.Lossless));
    }
}

/// <summary>只实现 IMusicSourceProvider、没有自下载能力的替身（验证装配错误被结构化上报）。</summary>
internal sealed class FakePlainLegacyProvider : IMusicSourceProvider
{
    public string Provider => "migu";

    public bool IsEnabled => true;

    public string? DisabledReason => null;

    public Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(Array.Empty<SourceSearchResult>());

    public Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken) =>
        Task.FromResult<SourceSearchResult?>(null);

    public Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken) =>
        Task.FromResult(FrameworkFixtures.Info());
}

/// <summary>DI 注册顺序测试用；必须是 public 且有无参构造，才能被 ActivatorUtilities 创建。</summary>
public sealed class FakeDiProviderA : IMusicProvider
{
    public string ProviderName => "a";

    public bool IsConfigEnabled => true;

    public string? ConfigDisabledReason => null;

    public IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities =>
        FrameworkFixtures.AllCapabilities();
}

/// <summary>DI 注册顺序测试用（第二个实现）。</summary>
public sealed class FakeDiProviderB : IMusicProvider
{
    public string ProviderName => "b";

    public bool IsConfigEnabled => true;

    public string? ConfigDisabledReason => null;

    public IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities =>
        FrameworkFixtures.AllCapabilities();
}
