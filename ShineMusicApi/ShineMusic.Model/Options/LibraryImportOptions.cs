// 曲库导入选项：管理员手动导入的自动发布策略，对应配置节 "LibraryImport"。
namespace ShineMusic.Model.Options;

/// <summary>
/// 管理员手动导入（非用户上传、非外部音源抓歌）的自动发布配置。
/// 转码完成后按 SourceScoreCalculator 评分（满分 100）：
/// 达到 <see cref="AutoPublishThreshold"/> 且 <see cref="AutoPublishEnabled"/> 开启时直通发布，
/// 否则进入人工审核队列。
/// </summary>
public sealed class LibraryImportOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "LibraryImport";

    /// <summary>是否允许管理员导入条目评分达标后直通发布；默认 true。</summary>
    public bool AutoPublishEnabled { get; set; } = true;

    /// <summary>直通发布的最低评分（0-100，默认 70）。</summary>
    public int AutoPublishThreshold { get; set; } = 70;
}
