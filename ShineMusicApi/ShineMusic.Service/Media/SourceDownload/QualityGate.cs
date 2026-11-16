// 职责：Phase 3 质量闸门——统一管理下载降级策略，消费已就位的 QualityPolicy 配置。
//
// 背景：appsettings.json 的 ProviderFramework.QualityPolicy 已声明：
//   PreferredQuality=Lossless / MinimumQuality=320K / AllowLossyFallback=false /
//   AllowPreview=false / RequireFullTrack=true / RequireStudioVersion=true
// 但 Phase 1/2 期间「只声明不强制」——SourceDownloadJob 的音质候选遍历由 V1 的
// IQualityLadderProvider 驱动（SQ→HQ→PQ），没有统一闸门。
//
// 本类消费该配置，提供：
//   1) 按优先级排序的允许质量等级链（无损 → 320K → 拒绝，绝不降到 128K）；
//   2) 对 AudioValidator 验证结果的准入判定；
//   3) 对候选音质的过滤（低于最低质量的候选直接跳过，不浪费下载带宽）。

using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceDownload;

/// <summary>
/// Phase 3 质量闸门：消费 QualityPolicy 配置，统一管理下载降级策略。
/// 核心规则：无损 FLAC → 320K → 拒绝，绝不自动降到 128K 入库。
/// </summary>
public sealed class QualityGate
{
    private readonly SourceDownloadOptions _options;

    /// <summary>构造质量闸门。</summary>
    /// <param name="options">SourceDownload 配置（含 QualityPolicy）。</param>
    public QualityGate(IOptions<SourceDownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>
    /// 返回按优先级排序的允许质量等级列表。
    /// 默认策略下返回 [Lossless, High320K]，Below320K 不在列表中（绝不自动降到 128K）。
    /// 若 AllowLossyFallback=true（非默认），则追加 Below320K。
    /// </summary>
    public IReadOnlyList<AudioQualityLevel> GetAllowedQualityChain()
    {
        var policy = _options.ProviderFramework.QualityPolicy;
        var chain = new List<AudioQualityLevel>();

        // 首选质量
        var preferred = ParseQualityLevel(policy.PreferredQuality);
        if (preferred != AudioQualityLevel.Unknown && !chain.Contains(preferred))
            chain.Add(preferred);

        // 最低质量（作为链的最后一档，但 Below320K 受 AllowLossyFallback 控制）
        var minimum = ParseQualityLevel(policy.MinimumQuality);
        if (minimum != AudioQualityLevel.Unknown && !chain.Contains(minimum))
            chain.Add(minimum);

        // 如果首选是 Lossless 且最低是 320K，确保 320K 在链中
        if (preferred == AudioQualityLevel.Lossless && !chain.Contains(AudioQualityLevel.High320K))
            chain.Add(AudioQualityLevel.High320K);

        // 有损兜底（默认关闭）
        if (policy.AllowLossyFallback && !chain.Contains(AudioQualityLevel.Below320K))
            chain.Add(AudioQualityLevel.Below320K);

        // 去空 + 降序排列（无损优先）
        return chain
            .Where(level => level != AudioQualityLevel.Unknown)
            .OrderByDescending(level => (int)level)
            .ToArray();
    }

    /// <summary>
    /// 判断给定的声明质量等级是否在允许的降级链中。
    /// 不在链中的候选直接跳过，不浪费下载带宽。
    /// </summary>
    /// <param name="declaredLevel">候选的声明质量等级。</param>
    /// <returns>是否允许下载该候选。</returns>
    public bool IsQualityAllowed(AudioQualityLevel declaredLevel)
    {
        if (declaredLevel == AudioQualityLevel.Unknown)
            return true; // 未知质量不拦截，由 AudioValidator 下载后实测判定

        var chain = GetAllowedQualityChain();
        return chain.Contains(declaredLevel);
    }

    /// <summary>
    /// 判断 AudioValidator 的实测结果是否达到入库门槛。
    /// </summary>
    /// <param name="probedLevel">ffprobe 实测的质量等级。</param>
    /// <returns>是否允许入库。</returns>
    public bool IsAcceptableForLibrary(AudioQualityLevel probedLevel)
    {
        var chain = GetAllowedQualityChain();
        // 实测等级在允许链中，或高于最低允许等级
        var minAllowed = chain.Count > 0 ? chain.Min(level => (int)level) : (int)AudioQualityLevel.High320K;
        return (int)probedLevel >= minAllowed;
    }

    /// <summary>
    /// 判断试听片段是否允许入库（默认禁止）。
    /// </summary>
    public bool IsPreviewAllowed =>
        _options.ProviderFramework.QualityPolicy.AllowPreview;

    /// <summary>解析质量等级字符串配置值为枚举。</summary>
    private static AudioQualityLevel ParseQualityLevel(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "lossless" => AudioQualityLevel.Lossless,
            "320k" or "high" or "high320k" => AudioQualityLevel.High320K,
            "below320k" or "low" or "128k" => AudioQualityLevel.Below320K,
            _ => AudioQualityLevel.Unknown,
        };
}
