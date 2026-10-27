// 职责：定义“曲库批量导入（Library Import）”流水线的持久化实体——
// 导入批次（LibraryImportBatch）与批次内的导入条目（LibraryImportItem），
// 以及两者各自的状态机常量。后台服务按“上传→校验→元数据解析→转码→人工审核→发布”的流水线推进条目状态。

using System.Diagnostics.CodeAnalysis;
using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>导入批次状态机常量：描述一次批量导入整体的生命周期。</summary>
public static class LibraryImportStatuses
{
    /// <summary>已入队：批次已创建，等待后台服务开始处理。</summary>
    public const string Queued = "queued";
    /// <summary>处理中：批次内至少有一个条目正在被后台 worker 处理。</summary>
    public const string Processing = "processing";
    /// <summary>终态：全部条目都已走完处理流程且没有失败/拒绝。</summary>
    public const string Completed = "completed";
    /// <summary>终态：处理结束，但存在处理失败或被审核拒绝的条目。</summary>
    public const string CompletedWithErrors = "completed_with_errors";
    /// <summary>终态：批次整体失败。</summary>
    public const string Failed = "failed";
    /// <summary>终态：被管理员取消，剩余条目不再处理。</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>导入条目状态机常量：描述单个音频文件在导入流水线中的阶段。</summary>
public static class LibraryImportItemStatuses
{
    /// <summary>已上传：文件已落入隔离存储（quarantine），等待后台拾取。</summary>
    public const string Uploaded = "uploaded";
    /// <summary>校验中：扩展名白名单、文件大小、隔离路径与文件存在性检查。</summary>
    public const string Validating = "validating";
    /// <summary>元数据解析中：使用 TagLib 读取标题/艺术家/专辑/封面等标签。</summary>
    public const string ParsingMetadata = "parsing_metadata";
    /// <summary>转码中：ffmpeg 统一转码为 AAC/m4a。</summary>
    public const string Transcoding = "transcoding";
    /// <summary>待人工审核：自动处理完成，等待管理员核对元数据（审核流程入口）。</summary>
    public const string PendingReview = "pending_review";
    /// <summary>待审核转公开：用户上传的条目已完成自动私有入库（曲目已创建、仅上传者可见），等待管理员审核决定是否转公开。</summary>
    public const string PendingPublication = "pending_publication";
    /// <summary>可发布：管理员审核通过，等待正式入库发布。</summary>
    public const string ReadyToPublish = "ready_to_publish";
    /// <summary>终态：已发布为正式曲目（Track/MediaAsset）。</summary>
    public const string Published = "published";
    /// <summary>终态：管理员审核拒绝，文件不会入库。</summary>
    public const string Rejected = "rejected";
    /// <summary>终态：校验/解析/转码任一自动环节失败（错误码见 ErrorCode）。</summary>
    public const string ProcessingFailed = "processing_failed";
}

/// <summary>导入批次：一次批量导入操作的容器，与条目构成一对多（级联删除）关系。</summary>
[SugarTable("LibraryImportBatches")]
public sealed class LibraryImportBatch
{
    /// <summary>批次唯一标识，Guid 主键。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>发起导入的管理员用户 Id；(CreatedByUserId, CreatedAtUtc) 复合索引用于“我的导入记录”列表。</summary>
    public Guid CreatedByUserId { get; set; }
    /// <summary>批次整体状态，取值见 <see cref="LibraryImportStatuses"/>，默认 queued。</summary>
    public string Status { get; set; } = LibraryImportStatuses.Queued;
    /// <summary>批次创建时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>批次处理完成时间（UTC）；未完成或重新进入处理中时为空。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "timestamptz")]
    public DateTime? CompletedAtUtc { get; set; }
    /// <summary>批次内的导入条目集合；配置为级联删除（删批次即删条目）。</summary>
    [SugarColumn(IsIgnore = true)]
    public ICollection<LibraryImportItem> Items { get; set; } = [];
}

/// <summary>导入条目：批次中的单个音频文件及其在导入流水线中的全部状态与探测/解析结果。</summary>
[SugarTable("LibraryImportItems")]
public sealed class LibraryImportItem
{
    /// <summary>无参构造：满足 SqlSugar Insertable/Updateable 的 new() 约束（required 成员由调用方填充）。</summary>
    [SetsRequiredMembers]
    public LibraryImportItem() { DisplayName = null!; SourceStorageKey = null!; ContentHash = null!; }

    /// <summary>条目唯一标识，Guid 主键；同时也用于生成隔离存储中的文件名/封面名。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>所属批次 Id 外键（级联删除）。</summary>
    public Guid BatchId { get; set; }
    /// <summary>所属批次导航属性。</summary>
    [SugarColumn(IsIgnore = true)]
    public LibraryImportBatch Batch { get; set; } = null!;
    /// <summary>流水线状态，取值见 <see cref="LibraryImportItemStatuses"/>，默认 uploaded；(Status, CreatedAtUtc) 建有复合索引。</summary>
    public string Status { get; set; } = LibraryImportItemStatuses.Uploaded;
    /// <summary>上传时的原始文件名（用于展示与取扩展名白名单校验），最长 255 字符。</summary>
    public required string DisplayName { get; set; }
    /// <summary>原始上传文件在隔离存储中的存储键（未经信任的文件先隔离，审核通过后才进入正式曲库）。</summary>
    public required string SourceStorageKey { get; set; }
    /// <summary>转码产物（AAC/m4a）的存储键；未完成转码前为空。</summary>
    [SugarColumn(IsNullable = true)]
    public string? TranscodedStorageKey { get; set; }
    /// <summary>从标签中抽取并保存到隔离存储的封面图片存储键。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ArtworkStorageKey { get; set; }
    /// <summary>上传文件内容哈希（SHA256 等），用于去重与完整性校验；该列建有非唯一索引。</summary>
    public required string ContentHash { get; set; }
    /// <summary>文件大小（字节），用于上传上限校验与展示。</summary>
    public long SizeBytes { get; set; }
    /// <summary>ffprobe 探测出的规范化容器格式（如 mp3/flac/m4a）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Container { get; set; }
    /// <summary>ffprobe 探测出的音频编码名称（如 aac、flac）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Codec { get; set; }
    /// <summary>探测出的码率（kbps）。</summary>
    [SugarColumn(IsNullable = true)]
    public int? BitrateKbps { get; set; }
    /// <summary>从音频标签解析出的标题（待人工审核确认）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Title { get; set; }
    /// <summary>艺术家列表，以 JSON 数组字符串存储。</summary>
    [SugarColumn(ColumnDataType = "text")]
    public string ArtistsJson { get; set; } = "[]";
    /// <summary>从标签解析出的专辑标题。</summary>
    [SugarColumn(IsNullable = true)]
    public string? AlbumTitle { get; set; }
    /// <summary>从标签中读取的内嵌歌词（纯文本）；无内嵌歌词时为 null，审核环节可手动补充。
    /// 多行歌词可能超过 varchar(255)，Length = 0 让 SqlSugar 在 Postgres 上映射为 text 类型。</summary>
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? Lyrics { get; set; }
    /// <summary>
    /// 歌词来源：embedded（音频内嵌标签）/ sidecar（同名 .lrc 旁挂文件）/ lrclib（外部歌词源自动匹配）/ manual（人工填写或上传）。
    /// 取值见 <see cref="LyricsSources"/>；无歌词时为 null。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public string? LyricsSource { get; set; }
    /// <summary>歌词匹配置信度（0~1）：内嵌/旁挂为 1，外部源按标题/艺人/时长相似度打分；人工填写为 1。</summary>
    [SugarColumn(IsNullable = true)]
    public double? LyricsConfidence { get; set; }
    /// <summary>外部歌词源返回的记录 ID（如 LRCLIB 的 id），用于回溯匹配结果；非外部来源为 null。</summary>
    [SugarColumn(IsNullable = true)]
    public string? LyricsProviderId { get; set; }
    /// <summary>碟内曲目序号（标签 Track）。</summary>
    [SugarColumn(IsNullable = true)]
    public int? TrackNumber { get; set; }
    /// <summary>碟片号（标签 Disc）。</summary>
    [SugarColumn(IsNullable = true)]
    public int? DiscNumber { get; set; }
    /// <summary>发行年份（标签 Year）。</summary>
    [SugarColumn(IsNullable = true)]
    public int? ReleaseYear { get; set; }
    /// <summary>iTunes 曲目 ID（溯源用）；来自外部元数据源，非必须。</summary>
    [SugarColumn(IsNullable = true)]
    public long? ItunesTrackId { get; set; }
    /// <summary>iTunes 上架国家/地区商店代码（如 US/CN），仅作溯源，不代表艺人国籍。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ItunesCountry { get; set; }
    /// <summary>地区分类（如 华语/欧美/日韩），由流派/艺人名文字推导，缺失时为 null。</summary>
    [SugarColumn(IsNullable = true)]
    public string? Region { get; set; }
    /// <summary>成人内容标记（来自 iTunes trackExplicitness 解析）。</summary>
    [SugarColumn(IsNullable = true)]
    public bool? Explicit { get; set; }
    /// <summary>流派列表，以 JSON 数组字符串存储（JSON 列，应用层负责序列化）。</summary>
    [SugarColumn(ColumnDataType = "text")]
    public string GenresJson { get; set; } = "[]";
    /// <summary>时长（毫秒）；TagLib 解析结果优先，缺失时回退 ffprobe 探测结果。</summary>
    [SugarColumn(IsNullable = true)]
    public long? DurationMs { get; set; }
    /// <summary>转场子状态：queued/processing/succeeded/failed（与主状态解耦，便于后台崩溃恢复时重置）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? TranscodeStatus { get; set; }
    /// <summary>转码目标格式标识（当前固定 m4a）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? TranscodeFormat { get; set; }
    /// <summary>实际转码码率（kbps），取自转码配置。</summary>
    [SugarColumn(IsNullable = true)]
    public int? TranscodeBitrateKbps { get; set; }
    /// <summary>处理过程中的告警代码列表（如 missing_title/missing_artist），JSON 数组字符串存储。</summary>
    [SugarColumn(ColumnDataType = "text")]
    public string WarningsJson { get; set; } = "[]";
    /// <summary>转码/处理失败的自动重试次数（后台定时扫到 processing_failed 且未达上限时自动重置回 uploaded 重排队）。</summary>
    public int ProcessingRetryCount { get; set; }
    /// <summary>失败错误码（机器可读，如 unsupported_media_format/upload_too_large/media_processing_timeout）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ErrorCode { get; set; }
    /// <summary>面向用户的、经过安全脱敏映射的错误信息（不直接回显 ffmpeg/文件系统原始报错）。</summary>
    [SugarColumn(IsNullable = true)]
    public string? ErrorMessage { get; set; }
    /// <summary>关联的点歌记录 Id：若本次导入是为满足某条点歌，则回填以便点歌状态联动。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? SongRequestId { get; set; }
    /// <summary>元数据是否已经管理员人工审核确认（审核流程标记，防止未核对的标签直接发布）。</summary>
    public bool MetadataReviewed { get; set; }
    /// <summary>是否为用户上传（区别于管理员导入）：用户上传的条目处理完成后自动私有入库（立即可听），而非进入人工审核发布队列。</summary>
    public bool IsUserUpload { get; set; }
    /// <summary>外部音源标识（audius/jamendo）：仅音源抓歌条目有值，null 表示手动上传/用户上传；
    /// 导入流水线据此对音源条目启用「评分直通入库」闸门。</summary>
    [SugarColumn(IsNullable = true)]
    public string? SourceProvider { get; set; }
    /// <summary>音源条目的自动评分（0-100）；转码完成后计算，未评分为 null。人工审核页展示辅助决策。</summary>
    [SugarColumn(IsNullable = true)]
    public int? AutoScore { get; set; }
    /// <summary>评分分项明细（JSON），记录音质/元数据/歌词各维度得分与扣分原因。</summary>
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? AutoScoreBreakdownJson { get; set; }
    /// <summary>入库发布时创建的曲目 Id：用户上传私有入库与管理员发布（含音源直通）均回填，供反查曲目/歌词状态与预览回退使用。</summary>
    [SugarColumn(IsNullable = true)]
    public Guid? PublishedTrackId { get; set; }
    /// <summary>条目创建时间（UTC）。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>条目最近一次状态/字段更新时间（UTC），每次状态机推进都会刷新。</summary>
    [SugarColumn(ColumnDataType = "timestamptz")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>软删除标记：管理员点击"删除"后置为 true，列表查询时自动过滤；
    /// 记录本身不物理删除，便于审计与误删恢复。</summary>
    public bool IsDeleted { get; set; }
}
