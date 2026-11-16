using System.Diagnostics;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>其余音源的薄适配：复用既有解析或自下载实现，不实现平台协议。</summary>
public abstract class AdditionalLegacyProviderAdapter : LegacySourceProviderAdapter
{
    private readonly bool selfDownload;
    private readonly IReadOnlyList<ProviderCapabilityDescriptor> capabilities;

    /// <summary>绑定平台并声明能力；声明音质不代表媒体探测结果。</summary>
    protected AdditionalLegacyProviderAdapter(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options,
        string providerName, bool selfDownload, bool highQuality, bool lossless) : base(legacy, options)
    {
        if (!string.Equals(legacy.Provider, providerName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"需要 provider={providerName}，实际为 {legacy.Provider}。", nameof(legacy));
        this.selfDownload = selfDownload;
        var declarations = new List<ProviderCapabilityDescriptor>
        {
            new(ProviderCapability.Search, ProviderStatus.Degraded, "复用既有搜索；依赖平台可用性及配置。"),
            new(ProviderCapability.Metadata, ProviderStatus.Degraded, "复用既有详情；可用字段见 ProviderMetadataCatalog。"),
            new(ProviderCapability.Resolve, ProviderStatus.Degraded,
                selfDownload ? "由既有自下载器内部解析资源，不提供独立直链。" : "复用既有直链解析。"),
        };
        if (highQuality)
            declarations.Add(new(ProviderCapability.Download320, ProviderStatus.Degraded,
                "部分曲目声明高码率；实际音质须媒体探测。"));
        if (lossless)
            declarations.Add(new(ProviderCapability.DownloadLossless, ProviderStatus.Degraded,
                "部分曲目提供原文件/FLAC；声明值不是实测无损结论。"));
        capabilities = declarations.AsReadOnly();
    }

    /// <inheritdoc />
    public override IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities => capabilities;

    /// <inheritdoc />
    public override async Task<DownloadResult> DownloadAsync(DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (selfDownload)
            {
                if (Legacy is not ISourceFileDownloader downloader)
                    return DownloadResult.Failed(ProviderName, request.ProviderTrackId,
                        $"{ProviderName}_downloader_missing", "既有音源未实现 ISourceFileDownloader。", stopwatch.ElapsedMilliseconds);
                var file = await downloader.DownloadToFileAsync(request.ProviderTrackId, request.OutputDirectory,
                    request.FileBaseName, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(file.FilePath) || !File.Exists(file.FilePath))
                    return DownloadResult.Failed(ProviderName, request.ProviderTrackId,
                        $"{ProviderName}_resource_unavailable", "自下载器未交付音频文件。", stopwatch.ElapsedMilliseconds);
                return new DownloadResult
                {
                    Stage = DownloadStage.Downloaded,
                    ProviderName = ProviderName,
                    ProviderTrackId = request.ProviderTrackId,
                    FilePath = file.FilePath,
                    Format = MapFormat(file.Container),
                    Quality = MapQuality(file.Quality),
                    FileSize = new FileInfo(file.FilePath).Length,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                };
            }

            var info = await Legacy.ResolveDownloadAsync(request.ProviderTrackId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(info.DownloadUrl))
                return DownloadResult.Failed(ProviderName, request.ProviderTrackId,
                    $"{ProviderName}_resource_unavailable", "未解析出可用下载直链。", stopwatch.ElapsedMilliseconds);
            return new DownloadResult
            {
                Stage = DownloadStage.Resolved,
                ProviderName = ProviderName,
                ProviderTrackId = request.ProviderTrackId,
                DownloadUrl = info.DownloadUrl,
                Format = MapFormat(info.Container),
                Quality = MapQuality(info),
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (SourceUnavailableException exception)
        {
            return DownloadResult.Failed(ProviderName, request.ProviderTrackId,
                $"{ProviderName}_resource_unavailable", exception.Message, stopwatch.ElapsedMilliseconds);
        }
    }
}

/// <summary>Audius V2 适配器，复用原文件/流地址解析。</summary>
public sealed class AudiusProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
    : AdditionalLegacyProviderAdapter(legacy, options, "audius", false, true, true);

/// <summary>Jamendo V2 适配器，复用 FLAC/MP3 地址解析。</summary>
public sealed class JamendoProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
    : AdditionalLegacyProviderAdapter(legacy, options, "jamendo", false, true, true);

/// <summary>Bilibili V2 适配器；不声明 320K 或无损下载能力。</summary>
public sealed class BilibiliProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
    : AdditionalLegacyProviderAdapter(legacy, options, "bilibili", true, false, false);

/// <summary>YouTube Music V2 适配器；不声明 320K 或无损下载能力。</summary>
public sealed class YouTubeMusicProvider(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
    : AdditionalLegacyProviderAdapter(legacy, options, "ytmusic", true, false, false);
