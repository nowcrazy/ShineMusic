// 职责：Phase 6 验收测试——QualityGate 降级策略。
// 验收标准：
//   - SQ 失效可尝试 HQ（无损不可用时降级到 320K）
//   - HQ 失效不自动降级到 128K（Below320K 不在允许链中）
//   - AllowLossyFallback=false 时 Below320K 被拒绝
//   - AllowLossyFallback=true 时 Below320K 进入允许链

using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using Xunit;

namespace ShineMusic.Tests.Rules;

public class QualityGateTests
{
    private static IOptions<SourceDownloadOptions> Wrap(SourceDownloadOptions options) =>
        Microsoft.Extensions.Options.Options.Create(options);

    private static SourceDownloadOptions DefaultOptions(Action<SourceDownloadOptions>? configure = null)
    {
        var options = new SourceDownloadOptions();
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void Default_chain_is_lossless_then_320k()
    {
        // 验收：默认降级链为 [Lossless, High320K]
        var gate = new QualityGate(Wrap(DefaultOptions()));
        var chain = gate.GetAllowedQualityChain();

        Assert.Equal(2, chain.Count);
        Assert.Equal(AudioQualityLevel.Lossless, chain[0]);
        Assert.Equal(AudioQualityLevel.High320K, chain[1]);
    }

    [Fact]
    public void Below320k_is_never_in_default_chain()
    {
        // 验收：HQ 失效不自动降级到 128K
        var gate = new QualityGate(Wrap(DefaultOptions()));
        var chain = gate.GetAllowedQualityChain();

        Assert.DoesNotContain(AudioQualityLevel.Below320K, chain);
    }

    [Fact]
    public void Lossless_is_allowed_by_default()
    {
        var gate = new QualityGate(Wrap(DefaultOptions()));
        Assert.True(gate.IsQualityAllowed(AudioQualityLevel.Lossless));
    }

    [Fact]
    public void High320k_is_allowed_by_default()
    {
        var gate = new QualityGate(Wrap(DefaultOptions()));
        Assert.True(gate.IsQualityAllowed(AudioQualityLevel.High320K));
    }

    [Fact]
    public void Below320k_is_rejected_by_default()
    {
        // 验收：128K 不在允许链中，IsQualityAllowed 返回 false
        var gate = new QualityGate(Wrap(DefaultOptions()));
        Assert.False(gate.IsQualityAllowed(AudioQualityLevel.Below320K));
    }

    [Fact]
    public void Unknown_quality_is_not_blocked_by_gate()
    {
        // 未知质量不拦截，由 AudioValidator 下载后实测判定
        var gate = new QualityGate(Wrap(DefaultOptions()));
        Assert.True(gate.IsQualityAllowed(AudioQualityLevel.Unknown));
    }

    [Fact]
    public void AllowLossyFallback_adds_below320k_to_chain()
    {
        // 验收：AllowLossyFallback=true 时 Below320K 进入允许链
        var gate = new QualityGate(Wrap(DefaultOptions(options =>
            options.ProviderFramework.QualityPolicy.AllowLossyFallback = true)));
        var chain = gate.GetAllowedQualityChain();

        Assert.Contains(AudioQualityLevel.Below320K, chain);
        Assert.True(gate.IsQualityAllowed(AudioQualityLevel.Below320K));
    }

    [Fact]
    public void Lossless_passes_acceptable_threshold()
    {
        var gate = new QualityGate(Wrap(DefaultOptions()));
        Assert.True(gate.IsAcceptableForLibrary(AudioQualityLevel.Lossless));
    }

    [Fact]
    public void High320k_passes_acceptable_threshold()
    {
        var gate = new QualityGate(Wrap(DefaultOptions()));
        Assert.True(gate.IsAcceptableForLibrary(AudioQualityLevel.High320K));
    }

    [Fact]
    public void Below320k_fails_acceptable_threshold_by_default()
    {
        // 验收：128K 不达到入库门槛
        var gate = new QualityGate(Wrap(DefaultOptions()));
        Assert.False(gate.IsAcceptableForLibrary(AudioQualityLevel.Below320K));
    }

    [Fact]
    public void Preview_is_not_allowed_by_default()
    {
        var gate = new QualityGate(Wrap(DefaultOptions()));
        Assert.False(gate.IsPreviewAllowed);
    }

    [Fact]
    public void Preview_can_be_allowed_via_config()
    {
        var gate = new QualityGate(Wrap(DefaultOptions(options =>
            options.ProviderFramework.QualityPolicy.AllowPreview = true)));
        Assert.True(gate.IsPreviewAllowed);
    }
}
