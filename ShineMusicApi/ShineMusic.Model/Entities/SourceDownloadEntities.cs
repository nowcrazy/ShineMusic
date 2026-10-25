// 职责：定义「外部音源抓歌」领域的持久化实体与状态机常量。
// ExternalDownloadTask 记录一次「从开放版权平台下载一首曲目」的任务全生命周期：
// 排队 → 下载中 → 已移交导入流水线 → 完成（直通入库/转人工审核）或失败。

using System.Diagnostics.CodeAnalysis;
using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>外部下载任务状态机常量。</summary>
public static class ExternalDownloadTaskStatuses
{
    /// <summary>已入队：等待后台下载服务拾取。</summary>
    public const string Queued = "queued";
    /// <summary>下载中：正在从音源平台拉取音频文件。</summary>
    public const string Downloading = "downloading";
    /// <summary>已移交导入流水线：文件已落隔离区并创建 LibraryImportItem（转码/歌词进行中）。</summary>
    public const string ImportQueued = "import_queued";
    /// <summary>终态：导入流水线处理完成（AutoPublished 区分直通入库还是转人工审核）。</summary>
    public const string Completed = "completed";
    /// <summary>终态：下载或移交失败（错误码见 ErrorCode，可重试）。</summary>
    public const string Failed = "failed";
}

/// <summary>
/// 外部音源下载任务：保存音源平台快照元数据（供展示与入库预填）、下载/评分结果，
/// 以及移交后的导入条目 Id（ImportItemId）供管理端跳转审核。
/// </summary>
[SugarTable("ExternalDownloadTasks")]
public sealed class ExternalDownloadTask
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public ExternalDownloadTask() { Provider = null!; ProviderTrackId = null!; }

    /// <summary>任务唯一标识，Guid 主键（同时用作隔离区下载文件名主干）。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>音源平台标识：audius / jamendo。与 ProviderTrackId 构成唯一索引防重复抓取。</summary>
    public required string Provider { get; set; }

    /// <summary>曲目在音源平台上的 Id。</summary>
    public required string ProviderTrackId { get; set; }

    /// <summary>平台返回的曲目标题（创建任务时的快照，入库时预填给导入条目）。</summary>
    public string? Title { get; set; }

    /// <summary>艺人列表快照，JSON 数组字符串。</summary>
    public string ArtistsJson { get; set; } = "[]";

    /// <summary>专辑标题快照。</summary>
    public string? AlbumTitle { get; set; }

    /// <summary>流派列表快照，JSON 数组字符串（入库预填时经 GenreMoodMapper 映射为中文 mood）。</summary>
    public string GenresJson { get; set; } = "[]";

    /// <summary>时长（毫秒）快照。</summary>
    public long? DurationMs { get; set; }

    /// <summary>音质等级快照：lossless（有无损原文件）/ high（≥256k）/ standard。</summary>
    public string? Quality { get; set; }

    /// <summary>曲目许可信息（如 CC-BY、CC0 等，来自平台）。</summary>
    public string? License { get; set; }

    /// <summary>曲目在音源平台的页面链接（溯源/试听用）。</summary>
    public string? SourcePageUrl { get; set; }

    /// <summary>封面图 URL（平台 CDN 地址，下载阶段拉取落隔离区）。</summary>
    public string? CoverUrl { get; set; }

    /// <summary>任务状态，取值见 <see cref="ExternalDownloadTaskStatuses"/>。</summary>
    public string Status { get; set; } = ExternalDownloadTaskStatuses.Queued;

    /// <summary>导入流水线完成后的自动评分（0-100）；未评分为 null。</summary>
    public int? AutoScore { get; set; }

    /// <summary>是否因评分达标被自动直通入库（false 表示转人工审核或未处理完）。</summary>
    public bool AutoPublished { get; set; }

    /// <summary>失败错误码（机器可读）。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>面向用户的脱敏错误信息。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>自动重试已发生的次数（失败时按 30s/2min/10min 指数退避重排队，达 3 次才转 failed 终态）。</summary>
    public int AutoRetryCount { get; set; }

    /// <summary>下次自动重试的最早时间（UTC）；null 表示立即可被拾取。仅 queued 状态下有意义。</summary>
    public DateTime? NextRetryAtUtc { get; set; }

    /// <summary>移交导入流水线后关联的导入条目 Id（跳转审核页用）。</summary>
    public Guid? ImportItemId { get; set; }

    /// <summary>发起任务的管理员用户 Id。</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>任务创建时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>最近状态/字段更新时间（UTC）。</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
