// 职责：把既有 11 个 IMusicSourceProvider「薄适配」成 V2 的三组能力接口。
//
// 提案 §12 明确要求「保留你现有的 Provider 代码和下载逻辑，逐步迁移」：
// 本类不重写任何平台协议、不改搜索/解析实现、不碰 IEnumerable<IMusicSourceProvider> 的注册顺序，
// 只做 DTO 转换 + 能力声明。将来某个平台要独立拆分搜索与下载实现时，
// 直接换掉对应适配类即可，Registry / Orchestrator 无需改动。
//
// 「搜索与下载分离」在这里是接口层面的分离，不是把网易云的 HTTP 代码物理拆成两个类——
// 物理拆分留到真的出现「搜索可用但下载不可用」的实现差异时再做，避免为目录好看过度拆类。

using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceProviders.Adapters;

/// <summary>既有 provider → V2 能力接口的适配基类。</summary>
public abstract class LegacySourceProviderAdapter :
    IMusicSearchProvider,
    IMusicMetadataProvider,
    IMusicDownloadProvider
{
    /// <summary>被适配的既有 provider（DI 中与老链路共享同一个单例）。</summary>
    protected IMusicSourceProvider Legacy { get; }

    /// <summary>现有配置（读取 V2 搜索上限等参数，不改老配置语义）。</summary>
    protected SourceDownloadOptions Options { get; }

    /// <summary>构造适配层。</summary>
    /// <param name="legacy">既有 provider 实例。</param>
    /// <param name="options">SourceDownload 配置。</param>
    protected LegacySourceProviderAdapter(IMusicSourceProvider legacy, IOptions<SourceDownloadOptions> options)
    {
        Legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        ArgumentNullException.ThrowIfNull(options);
        Options = options.Value;
    }

    /// <inheritdoc />
    public string ProviderName => Legacy.Provider;

    /// <inheritdoc />
    public virtual bool UsesVerifiedCapabilities => true;

    /// <inheritdoc />
    public bool IsConfigEnabled => Legacy.IsEnabled;

    /// <inheritdoc />
    public string? ConfigDisabledReason => Legacy.DisabledReason;

    /// <inheritdoc />
    public abstract IReadOnlyList<ProviderCapabilityDescriptor> DeclaredCapabilities { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(Options.ProviderFramework.Search.MaxResultsPerProvider, 1, 50);
        var results = await Legacy.SearchAsync(keyword, limit, cancellationToken).ConfigureAwait(false);
        return results.Select(MusicTrack.From).ToArray();
    }

    /// <inheritdoc />
    public async Task<MusicTrack?> GetTrackAsync(string trackId, CancellationToken cancellationToken = default)
    {
        var result = await Legacy.GetTrackAsync(trackId, cancellationToken).ConfigureAwait(false);
        return result is null ? null : MusicTrack.From(result);
    }

    /// <inheritdoc />
    public abstract Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default);

    /// <summary>容器名 → 音频格式枚举（仅映射声明值，不代表已用 ffprobe 验证真实编码）。</summary>
    /// <param name="container">容器名。</param>
    protected static AudioFormat MapFormat(string? container) =>
        container?.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "flac" => AudioFormat.Flac,
            "mp3" => AudioFormat.Mp3,
            "m4a" or "aac" or "mp4" => AudioFormat.M4a,
            "ogg" or "oga" => AudioFormat.Ogg,
            "wav" => AudioFormat.Wav,
            "alac" or "m4a_alac" => AudioFormat.Alac,
            _ => AudioFormat.Unknown,
        };

    /// <summary>既有 SourceQualities 等级 → V2 质量等级。</summary>
    /// <param name="quality">既有音质等级字符串。</param>
    protected static AudioQualityLevel MapQuality(string? quality) =>
        quality switch
        {
            SourceQualities.Lossless => AudioQualityLevel.Lossless,
            SourceQualities.High => AudioQualityLevel.High320K,
            SourceQualities.Standard => AudioQualityLevel.Below320K,
            _ => AudioQualityLevel.Unknown,
        };

    /// <summary>按声明码率/无损标记推断质量等级（仅用于解析结果，非实测）。</summary>
    /// <param name="info">解析出的下载信息。</param>
    protected static AudioQualityLevel MapQuality(SourceDownloadInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.IsLossless)
            return AudioQualityLevel.Lossless;
        if (info.BitrateKbps is >= 320)
            return AudioQualityLevel.High320K;
        if (info.BitrateKbps is > 0)
            return AudioQualityLevel.Below320K;
        return MapQuality(info.Quality);
    }
}
