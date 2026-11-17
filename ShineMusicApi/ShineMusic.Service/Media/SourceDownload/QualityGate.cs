using Microsoft.Extensions.Options;
using ShineMusic.Model.Domain;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceDownload;

/// <summary>候选选择与实测准入分离；显示等级不能证明 192kbps 下限。</summary>
public sealed class QualityGate
{
    private readonly SourceQualityPolicyOptions _policy;

    /// <summary>构造质量闸门。</summary>
    public QualityGate(IOptions<SourceDownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _policy = options.Value.ProviderFramework.QualityPolicy;
    }

    private int MinimumBitrate => _policy.MinimumQuality.Trim().ToLowerInvariant() switch
    {
        "192k" => 192,
        "256k" => 256,
        _ => 320, // 未识别的旧配置不放宽下限，包括 128K/Below320K。
    };

    /// <summary>常用目标优先，其次可用无损与合格有损；不改变显示枚举。</summary>
    public IReadOnlyList<AudioQualityLevel> GetAllowedQualityChain()
    {
        var chain = new List<AudioQualityLevel>();
        if (string.Equals(_policy.PreferredQuality, "Lossless", StringComparison.OrdinalIgnoreCase))
            chain.Add(AudioQualityLevel.Lossless);
        if (!string.Equals(_policy.MinimumQuality, "Lossless", StringComparison.OrdinalIgnoreCase))
            chain.Add(AudioQualityLevel.High320K);
        if (!chain.Contains(AudioQualityLevel.Lossless)) chain.Add(AudioQualityLevel.Lossless);
        if (_policy.AllowLossyFallback && MinimumBitrate < 320)
            chain.Add(AudioQualityLevel.Below320K);
        return chain;
    }

    /// <summary>仅用于下载前的粗筛；未知声明需下载实测。</summary>
    public bool IsQualityAllowed(AudioQualityLevel declaredLevel) =>
        declaredLevel == AudioQualityLevel.Unknown || GetAllowedQualityChain().Contains(declaredLevel);

    /// <summary>声明码率可提前排除 128K，但不能作为入库证据。</summary>
    public bool IsCandidateAllowed(bool isLossless, int? bitrateKbps) =>
        isLossless || (IsQualityAllowed(bitrateKbps is >= 320 ? AudioQualityLevel.High320K
            : bitrateKbps is > 0 ? AudioQualityLevel.Below320K : AudioQualityLevel.Unknown)
            && (bitrateKbps is null or <= 0 || bitrateKbps >= MinimumBitrate));

    /// <summary>兼容旧调用：单独的等级缺少编码/码率证据，不授予入库资格。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "保留旧实例 API 签名；等级本身不再授予准入资格。")]
    public bool IsAcceptableForLibrary(AudioQualityLevel probedLevel) => false;

    /// <summary>入库必须使用原始文件的真实编码、容器与码率。</summary>
    public bool IsAcceptableForLibrary(string? codec, string? container, int? bitrateKbps)
    {
        if (!AudioAdmissionPolicy.IsAcceptable(codec, container, bitrateKbps)) return false;
        if (AudioAdmissionPolicy.IsLosslessCodec(codec)) return true;
        return !string.Equals(_policy.MinimumQuality, "Lossless", StringComparison.OrdinalIgnoreCase)
            && bitrateKbps >= MinimumBitrate
            && (bitrateKbps >= 320 || _policy.AllowLossyFallback);
    }

    /// <summary>保留现有配置契约；完整性验证始终拒绝已识别片段。</summary>
    public bool IsPreviewAllowed => _policy.AllowPreview;
}
