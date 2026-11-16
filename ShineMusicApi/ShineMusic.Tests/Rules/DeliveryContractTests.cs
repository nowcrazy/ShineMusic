// 职责：锁定 §8 第 1 步「交付契约」的离线验证闸门。
// 用户反馈的原问题是「文件很小 + 很多歌曲是翻唱」——系统把「文件落盘 + 元数据齐 + 转码成功」
// 当成功，从未验证用户实际拿到的主资产。这些用例保证不达标条目在发布事务前被挡下。

using System.Text;
using System.Text.Json;
using ShineMusic.Service;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class DeliveryContractTests
{
    private static readonly TrackIdentity SampleIdentity = new("晴天", ["周杰伦"], 269_000);

    private static DeliveryContract Contract(
        string? contentType = "audio/flac",
        int? bitrateKbps = 935,
        long expectedSizeBytes = 4096,
        bool originalFileAvailable = true,
        IdentityVerdict verdict = IdentityVerdict.Matched,
        string? identityReason = TrackIdentityMatcher.ReasonMatched,
        int minimumBitrateKbps = DeliveryContractVerifier.AutomaticPublishMinimumBitrateKbps)
        => new(
            Guid.NewGuid(), Guid.NewGuid(),
            SampleIdentity, SampleIdentity,
            verdict, identityReason,
            minimumBitrateKbps,
            "original/track/asset.flac",
            contentType,
            bitrateKbps,
            expectedSizeBytes,
            "sha256-placeholder",
            originalFileAvailable);

    [Fact]
    public void Valid_lossless_flac_is_accepted()
    {
        using var file = new TempAudioFile("fLaC", 4096);
        var result = DeliveryContractVerifier.Verify(Contract(), file.Path, 192);

        Assert.True(result.IsAccepted);
        Assert.Equal(DeliveryContractCodes.Ok, result.ReasonCode);
        Assert.Empty(result.Failures);
        Assert.Equal(256, result.EffectiveBitrateFloorKbps);
    }

    [Fact]
    public void Missing_original_file_downgrades_floor_to_transcode_bitrate()
    {
        // §5.3 补充场景：原文件缺失时主资产就是 192k 转码副本，按 256 判会合法失败。
        using var file = new TempAudioFile("ftyp", 4096, magicOffset: 4);
        var contract = Contract(contentType: "audio/mp4", bitrateKbps: 192,
            expectedSizeBytes: 4096, originalFileAvailable: false);
        var result = DeliveryContractVerifier.Verify(contract, file.Path, 192);

        Assert.True(result.IsAccepted);
        Assert.Equal(192, result.EffectiveBitrateFloorKbps);
        Assert.True(result.OriginalUnavailable);
        Assert.Contains(DeliveryContractCodes.OriginalUnavailable, result.Warnings);
    }

    [Fact]
    public void Bitrate_below_floor_is_rejected()
    {
        using var file = new TempAudioFile("fLaC", 4096);
        var result = DeliveryContractVerifier.Verify(Contract(bitrateKbps: 128), file.Path, 192);

        Assert.False(result.IsAccepted);
        Assert.Equal(DeliveryContractCodes.Mismatch, result.ReasonCode);
        Assert.Contains(result.Failures, f => f.Contains("bitrate_below_floor", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_magic_is_rejected()
    {
        // 咪咕实测踩过：SQ 档声明 FLAC 但 URL 404，错误页被当成 .flac 落盘。
        using var file = new TempAudioFile("<!DOCTYPE html>", 4096);
        var result = DeliveryContractVerifier.Verify(Contract(expectedSizeBytes: 4096), file.Path, 192);

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Failures, f => f.Contains("magic_mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Unstreamable_container_is_rejected()
    {
        using var file = new TempAudioFile("fLaC", 4096);
        var result = DeliveryContractVerifier.Verify(Contract(contentType: null), file.Path, 192);

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Failures, f => f.Contains("unsupported_container", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejected_identity_is_deferred_to_scoring_not_blocked_by_contract()
    {
        // V2 Phase 2 裁决「评分制取代硬闸门」：契约仍完整记录身份结论，但不再阻断交付。
        // 阻断责任交给 SourceScoreCalculator 的身份 40 分维度 + 90 分阈值（见评分测试）。
        using var file = new TempAudioFile("fLaC", 4096);
        var contract = Contract(verdict: IdentityVerdict.Rejected,
            identityReason: TrackIdentityMatcher.ReasonTitleMismatch);
        var result = DeliveryContractVerifier.Verify(contract, file.Path, 192);

        Assert.True(result.IsAccepted);
        Assert.Empty(result.Failures);
        Assert.Equal(DeliveryContractCodes.IdentityDeferredToScoring, result.ReasonCode);
        var warning = result.Warnings.Single(w => w.Contains("identity_rejected", StringComparison.Ordinal));
        Assert.Contains(TrackIdentityMatcher.ReasonTitleMismatch, warning);
    }

    [Fact]
    public void Ambiguous_identity_is_deferred_to_scoring_not_folded_into_pass()
    {
        using var file = new TempAudioFile("fLaC", 4096);
        var contract = Contract(verdict: IdentityVerdict.Ambiguous, identityReason: "decoration_unknown");
        var result = DeliveryContractVerifier.Verify(contract, file.Path, 192);

        Assert.True(result.IsAccepted);
        Assert.Empty(result.Failures);
        Assert.Equal(DeliveryContractCodes.IdentityDeferredToScoring, result.ReasonCode);
        Assert.Contains(result.Warnings, w => w.Contains("identity_ambiguous", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("identity_rejected", StringComparison.Ordinal));
    }

    [Fact]
    public void Fault_injection_primary_pointed_back_at_192k_transcode_is_rejected()
    {
        // 评审 R2：只跑正常路径证明不了「系统不会退化」。
        // 模拟历史 bug——原文件明明还在，却把 IsPrimary 指回 192k 转码副本。
        // 此时不触发 original_unavailable 降档，192 < 256 必须被挡下。
        using var file = new TempAudioFile("ftyp", 4096, magicOffset: 4);
        var contract = Contract(contentType: "audio/mp4", bitrateKbps: 192,
            expectedSizeBytes: 4096, originalFileAvailable: true);
        var result = DeliveryContractVerifier.Verify(contract, file.Path, 192);

        Assert.False(result.IsAccepted);
        Assert.False(result.OriginalUnavailable);
        Assert.Contains(result.Failures, f => f.Contains("bitrate_below_floor", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_bitrate_is_visible_but_not_blocking()
    {
        // 缺元数据不该把合法上传卡死，但必须计入「未验证占比」（§2 过程指标）。
        using var file = new TempAudioFile("fLaC", 4096);
        var result = DeliveryContractVerifier.Verify(Contract(bitrateKbps: null), file.Path, 192);

        Assert.True(result.IsAccepted);
        Assert.Contains(DeliveryContractCodes.BitrateUnknown, result.Warnings);
    }

    [Fact]
    public void Missing_or_mismatched_primary_asset_is_rejected()
    {
        var missing = DeliveryContractVerifier.Verify(Contract(), "/definitely/not/a/file.flac", 192);
        Assert.False(missing.IsAccepted);
        Assert.Contains(missing.Failures, f => f.Contains("primary_asset_missing", StringComparison.Ordinal));

        using var file = new TempAudioFile("fLaC", 4096);
        var sizeMismatch = DeliveryContractVerifier.Verify(Contract(expectedSizeBytes: 9999), file.Path, 192);
        Assert.False(sizeMismatch.IsAccepted);
        Assert.Contains(sizeMismatch.Failures, f => f.Contains("size_mismatch", StringComparison.Ordinal));
    }

    // ====== 零 DDL 落库格式（WarningsJson / AutoScoreBreakdownJson） ======

    [Fact]
    public void AppendWarning_is_idempotent_and_tolerates_bad_json()
    {
        var once = ImportPublishService.AppendWarning("[]", DeliveryContractCodes.Ok);
        var twice = ImportPublishService.AppendWarning(once, DeliveryContractCodes.Ok);
        Assert.Equal(once, twice);
        Assert.Equal($"[\"{DeliveryContractCodes.Ok}\"]", once);

        var recovered = ImportPublishService.AppendWarning("not-json", DeliveryContractCodes.Mismatch);
        Assert.Equal($"[\"{DeliveryContractCodes.Mismatch}\"]", recovered);
    }

    [Fact]
    public void WithVerification_keeps_existing_breakdown_and_adds_node()
    {
        var merged = ImportPublishService.WithVerification("{\"quality\":40}", new { reason = "delivery_contract_ok", accepted = true });
        using var document = JsonDocument.Parse(merged);

        Assert.Equal(40, document.RootElement.GetProperty("quality").GetInt32());
        Assert.Equal("delivery_contract_ok", document.RootElement.GetProperty("verification").GetProperty("reason").GetString());

        var fromScratch = ImportPublishService.WithVerification(null, new { reason = "delivery_contract_ok" });
        using var scratchDocument = JsonDocument.Parse(fromScratch);
        Assert.True(scratchDocument.RootElement.TryGetProperty("verification", out _));
    }

    /// <summary>生成带指定头部魔数的临时音频文件，用完即删。</summary>
    private sealed class TempAudioFile : IDisposable
    {
        public string Path { get; }

        public TempAudioFile(string magic, int totalSize, int magicOffset = 0)
        {
            Path = System.IO.Path.GetTempFileName();
            using var stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None);
            var head = Encoding.ASCII.GetBytes(magic);
            stream.SetLength(Math.Max(totalSize, magicOffset + head.Length));
            stream.Position = magicOffset;
            stream.Write(head);
        }

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch (IOException) { }
        }
    }
}
