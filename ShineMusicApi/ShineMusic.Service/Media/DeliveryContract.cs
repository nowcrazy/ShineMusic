// 职责：音源质量稳定性策略 §8 第 1 步的「交付契约」——发布时记下承诺，供发布后回读比对。
//
// 背景（docs/音源质量稳定性策略.md §5 / §12）：系统此前从未把「用户实际播放资产码率」与
// 「曲目身份」当作被测量量，成功定义 = 文件落盘 + 元数据齐 + 转码成功，于是下载 100% 成功、
// 单测全绿与 192kbps + 翻唱可以并存。契约让「交付了什么」成为显式、可断言、可聚合的事实。
//
// 零 DDL：契约只在发布流程中内存构造，验证结果复用 LibraryImportItem.WarningsJson /
// AutoScoreBreakdownJson / ContentHash / SizeBytes，不新增表、不新增列。
//
// 本期边界（第 1 步 1a）：
//   - 离线验证主资产的容器 / 魔数 / 码率 / 文件大小，并在发布事务前阻断不达标条目；
//   - **真实 HTTP 回读（L3a，§5.3）不在本文件内**：它需要 WebApplicationFactory 与 K5 的
//     启动引导可替换接缝，见 docs 文档 §12.4。契约里的 ExpectedSizeBytes / ExpectedContentType
//     正是留给 L3a 用 Range: bytes=0-15 比对的那一组事实；
//   - ExpectedIdentity / ResolvedIdentity 当前**同源**（都来自 LibraryImportItem 元数据快照），
//     身份闸门真正生效要等第 2 步把「歌单原曲身份」与「实际下载候选身份」分开记录。
//     此处先固定契约形状与 ReasonCode，避免第 2 步再改序列化格式。

using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Service;

/// <summary>交付契约验证结果写入 WarningsJson 的机器可读代码。</summary>
public static class DeliveryContractCodes
{
    public const string Ok = "delivery_contract_ok";
    public const string Mismatch = "delivery_contract_mismatch";
    public const string IdentityAmbiguous = "identity_ambiguous";
    public const string IdentityRejected = "identity_rejected";
    public const string IdentityDeferredToScoring = "identity_deferred_to_scoring";
    public const string OriginalUnavailable = "original_unavailable";
    public const string BitrateUnknown = "bitrate_unknown";
    /// <summary>Phase 3：容器声称无损但 ffprobe 检测实际编码为有损（有损转无损伪装）。</summary>
    public const string EncodingMismatch = "encoding_mismatch";
    /// <summary>Phase 3：下载文件时长与元数据不符（试听片段冒充完整歌曲）。</summary>
    public const string PreviewFragment = "preview_fragment";
}

/// <summary>
/// 发布时对「用户将实际拿到什么」的一次承诺。
/// </summary>
/// <param name="ImportItemId">导入条目 Id，供审核页与补偿任务反查。</param>
/// <param name="TrackId">本次发布创建的曲目 Id。</param>
/// <param name="ExpectedIdentity">歌单原曲身份（第 2 步前与 ResolvedIdentity 同源）。</param>
/// <param name="ResolvedIdentity">实际解析/下载到的候选身份。</param>
/// <param name="IdentityVerdict">身份闸门结论（第 1 步由唯一决策点给出）。</param>
/// <param name="IdentityReasonCode">身份判定原因码，写入契约明细便于复盘。</param>
/// <param name="MinimumBitrateKbps">自动发布最低码率门槛（256）；原文件缺失时按转码码率降档。</param>
/// <param name="PrimaryStorageKey">播放主资产的库内存储键（IsPrimary=true 的那一份）。</param>
/// <param name="ExpectedContentType">主资产应有的 Content-Type，供 L3a 比对响应头。</param>
/// <param name="ProbedBitrateKbps">主资产声明码率（下载时 ffprobe 回填）。</param>
/// <param name="ExpectedSizeBytes">主资产字节数，供 L3a 用 Content-Range 总长比对。</param>
/// <param name="ContentHash">复用条目的内容哈希。</param>
/// <param name="OriginalFileAvailable">原始下载文件是否仍存在；false 时主资产是 192k 转码副本。</param>
public sealed record DeliveryContract(
    Guid ImportItemId,
    Guid TrackId,
    TrackIdentity ExpectedIdentity,
    TrackIdentity ResolvedIdentity,
    IdentityVerdict IdentityVerdict,
    string? IdentityReasonCode,
    int MinimumBitrateKbps,
    string PrimaryStorageKey,
    string? ExpectedContentType,
    int? ProbedBitrateKbps,
    long ExpectedSizeBytes,
    string ContentHash,
    bool OriginalFileAvailable);

/// <summary>契约验证结果。<see cref="Failures"/> 面向审核页与日志，逐条可追溯。</summary>
public sealed record DeliveryContractVerification(
    bool IsAccepted,
    string ReasonCode,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings,
    int EffectiveBitrateFloorKbps,
    bool OriginalUnavailable);

/// <summary>契约离线验证器：无 DB、无网络、无宿主依赖，纯文件 + 元数据比对。</summary>
public static class DeliveryContractVerifier
{
    /// <summary>自动发布的绝对音质门槛（与 SourceScoreCalculator 的 256k 门槛同向）。</summary>
    public const int AutomaticPublishMinimumBitrateKbps = 256;

    private const int DefaultTranscodeBitrateKbps = 192;

    /// <summary>
    /// 在发布事务提交前验证主资产。任何失败都会让发布返回冲突，而不是静默入库。
    /// </summary>
    /// <param name="contract">发布时构造的契约。</param>
    /// <param name="primaryFilePath">主资产对应的隔离区源文件路径（复制进曲库前的同一份内容）。</param>
    /// <param name="transcodeBitrateKbps">条目记录的转码码率；原文件缺失时用它作为降档后的门槛。</param>
    public static DeliveryContractVerification Verify(
        DeliveryContract contract,
        string primaryFilePath,
        int? transcodeBitrateKbps)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var failures = new List<string>();
        var warnings = new List<string>();

        // —— 身份（V2 Phase 2 裁决「评分制取代硬闸门」）——
        // 三态仍完整记录在契约里供追溯，但**不再作为交付契约的硬失败**。
        // 发布决策统一交给 SourceScoreCalculator 的身份 40 分维度 + 90 分阈值：
        // Rejected=0 分、Ambiguous=部分分，数学上达不到自动发布阈值，会转人工审核；
        // 与「契约失败可强制放行并留审计」的既有拍板一致，只是路径由「删文件阻断」改为「低分待审」。
        var identityCode = contract.IdentityVerdict switch
        {
            IdentityVerdict.Rejected => DeliveryContractCodes.IdentityRejected,
            IdentityVerdict.Ambiguous => DeliveryContractCodes.IdentityAmbiguous,
            _ => null,
        };
        if (identityCode is not null)
            warnings.Add($"{identityCode}: 身份判定 {contract.IdentityVerdict}（{contract.IdentityReasonCode ?? "unknown"}），" +
                         "交付契约不阻断，交评分闸门决定是否自动发布。");

        // —— 容器 / Content-Type ——
        if (string.IsNullOrWhiteSpace(contract.ExpectedContentType))
            failures.Add("unsupported_container: 主资产容器不在可流式白名单内，播放端会返回 415。");

        // —— 主资产文件存在性、大小、魔数 ——
        if (string.IsNullOrWhiteSpace(primaryFilePath) || !System.IO.File.Exists(primaryFilePath))
        {
            failures.Add("primary_asset_missing: 主资产文件不存在。");
        }
        else
        {
            var info = new FileInfo(primaryFilePath);
            if (info.Length <= 0)
                failures.Add("primary_asset_empty: 主资产文件为空。");
            if (info.Length != contract.ExpectedSizeBytes)
                failures.Add($"size_mismatch: 主资产实际 {info.Length} 字节，契约记录 {contract.ExpectedSizeBytes} 字节。");
            if (info.Length >= 16 && !MagicMatches(primaryFilePath, contract.ExpectedContentType))
                failures.Add($"magic_mismatch: 主资产首字节与 {contract.ExpectedContentType ?? "未知容器"} 不符（可能是 404 页面或占位音频）。");
        }

        // —— 码率：原文件缺失时按转码副本降档，避免把合法的兼容交付误判为失败 ——
        var floor = contract.MinimumBitrateKbps;
        var originalUnavailable = !contract.OriginalFileAvailable;
        if (originalUnavailable)
        {
            floor = transcodeBitrateKbps is > 0
                ? transcodeBitrateKbps.Value
                : DefaultTranscodeBitrateKbps;
            warnings.Add(DeliveryContractCodes.OriginalUnavailable);
        }

        if (contract.ProbedBitrateKbps is not > 0)
        {
            // 码率未知记为「未验证」而非失败：不许静默放行，但也不因缺元数据把合法上传卡死。
            warnings.Add(DeliveryContractCodes.BitrateUnknown);
        }
        else if (contract.ProbedBitrateKbps.Value < floor)
        {
            failures.Add($"bitrate_below_floor: 主资产 {contract.ProbedBitrateKbps.Value}kbps < 门槛 {floor}kbps" +
                         (originalUnavailable ? "（原文件缺失，已按转码副本降档）" : string.Empty) + "。");
        }

        var reason = failures.Count > 0
            ? DeliveryContractCodes.Mismatch
            : (identityCode is not null
                ? DeliveryContractCodes.IdentityDeferredToScoring
                : DeliveryContractCodes.Ok);

        return new DeliveryContractVerification(
            failures.Count == 0,
            reason,
            failures,
            warnings,
            floor,
            originalUnavailable);
    }

    /// <summary>
    /// 按 Content-Type 校验首 16 字节魔数。只做「是不是这个容器」的最小断言；
    /// 中段被替换且长度不变的情况抓不到，属残余事项 K1（需抽样全量哈希）。
    /// </summary>
    private static bool MagicMatches(string path, string? contentType)
    {
        Span<byte> head = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = stream.Read(head);
        if (read < 4)
            return false;

        return contentType switch
        {
            "audio/flac" => Ascii(head, read, "fLaC"),
            "audio/ogg" => Ascii(head, read, "OggS"),
            "audio/wav" => Ascii(head, read, "RIFF"),
            "audio/aiff" => Ascii(head, read, "FORM"),
            // M4A/MP4：ftyp 位于第 4~8 字节。
            "audio/mp4" => read >= 8 && Ascii(head[4..], read - 4, "ftyp"),
            // MP3：ID3 标签，或 MPEG 帧同步 0xFF Ex。
            "audio/mpeg" => Ascii(head, read, "ID3") || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),
            // 其余容器暂无稳定魔数（wma/ape/dsf 等），不做断言。
            _ => true,
        };
    }

    private static bool Ascii(ReadOnlySpan<byte> head, int length, string prefix)
    {
        if (length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
            if (head[i] != (byte)prefix[i])
                return false;
        return true;
    }
}
