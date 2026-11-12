// 职责：导入条目状态机的领域规则（纯函数）。
// 原先以散落的字面量比较分布在 LibraryImportController 的 7 个端点里，
// 这里集中定义后，端点只负责把结论映射成 HTTP 状态码。

using ShineMusic.Model.Entities;

namespace ShineMusic.Model.Domain;

/// <summary>导入条目状态流转与可变性判定。</summary>
public static class LibraryImportStateMachine
{
    /// <summary>已发布/已拒绝为不可变状态：PATCH 与 DELETE 均返回 409。</summary>
    public static bool IsImmutable(string status) =>
        status is LibraryImportItemStatuses.Published or LibraryImportItemStatuses.Rejected;

    /// <summary>终态：已发布、已拒绝、处理失败。只有终态条目可从列表移除（dismiss）。</summary>
    public static bool IsTerminal(string status) =>
        status is LibraryImportItemStatuses.Published
            or LibraryImportItemStatuses.Rejected
            or LibraryImportItemStatuses.ProcessingFailed;

    /// <summary>处理中间态：这些状态下不允许丢弃条目（应先等流水线跑完或拒绝）。</summary>
    public static bool IsIntermediate(string status) =>
        status is LibraryImportItemStatuses.Uploaded
            or LibraryImportItemStatuses.Validating
            or LibraryImportItemStatuses.ParsingMetadata
            or LibraryImportItemStatuses.Transcoding;

    /// <summary>
    /// 是否允许拒绝/丢弃条目：已发布与处理中间态不允许（与改造前端点判定逐项一致）。
    /// 注意已拒绝条目允许再次丢弃（端点会幂等地把状态写回 rejected）。
    /// </summary>
    public static bool CanDiscard(string status) =>
        status != LibraryImportItemStatuses.Published && !IsIntermediate(status);

    /// <summary>是否允许从列表移除：仅终态条目。</summary>
    public static bool CanDismiss(string status) => IsTerminal(status);

    /// <summary>元数据是否达到「已审核」门槛：标题与艺人均非空。</summary>
    public static bool IsMetadataComplete(string? title, int artistCount) =>
        !string.IsNullOrWhiteSpace(title) && artistCount > 0;

    /// <summary>
    /// 保存元数据后的状态流转：pending_review 且元数据齐全时进位到 ready_to_publish；
    /// 其余状态（含已退回人工审核的）保持不变。
    /// </summary>
    public static string StatusAfterReview(string currentStatus, bool metadataReviewed) =>
        currentStatus == LibraryImportItemStatuses.PendingReview && metadataReviewed
            ? LibraryImportItemStatuses.ReadyToPublish
            : currentStatus;
}
