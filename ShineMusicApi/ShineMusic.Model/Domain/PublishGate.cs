// 职责：导入条目「发布闸门」的领域规则（纯函数，无 IO、无框架依赖）。
// 原先内联在 ImportPublishService.PublishAsync 中；抽到这里后由单元测试锁定，
// 人工发布与后台自动发布两条路径共用同一份判定，避免闸门被某一路径绕过。

using System.Text.Json;
using ShineMusic.Model.Entities;

namespace ShineMusic.Model.Domain;

/// <summary>
/// 发布入库的前置条件。forceFulfill 只能影响「点歌需求联动」，不能绕过本闸门。
/// </summary>
public static class PublishGate
{
    /// <summary>转码成功状态的数据库字面量。</summary>
    public const string TranscodeSucceeded = "succeeded";

    /// <summary>
    /// 是否满足发布前置条件：状态 ready_to_publish + 转码成功 + 元数据已审核 + 转码产物键非空。
    /// 四项缺一即返回 409，属于设计行为而非缺陷。
    /// </summary>
    public static bool IsEligible(LibraryImportItem item) =>
        item.Status == LibraryImportItemStatuses.ReadyToPublish
        && string.Equals(item.TranscodeStatus, TranscodeSucceeded, StringComparison.Ordinal)
        && item.IsMetadataReviewed
        && !string.IsNullOrWhiteSpace(item.TranscodedStorageKey);

    /// <summary>
    /// 发布前完整性校验：标题、艺人、时长缺一不可。歌词是可选内容，
    /// 缺失时播放器显示「暂无歌词」，不影响上架。
    /// </summary>
    public static IReadOnlyList<string> MissingRequiredFields(LibraryImportItem item)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(item.Title)) missing.Add("title");
        if (DeserializeList(item.ArtistsJson).Count == 0) missing.Add("artists");
        if (item.DurationMs is null or <= 0) missing.Add("duration");
        return missing;
    }

    /// <summary>解析以 JSON 数组存储的艺人/流派列表；非法 JSON 一律按空列表处理（fail-closed）。</summary>
    public static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
