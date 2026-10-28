// 文件存储路径配置选项：绑定 "Storage" 配置节，定义正式媒体库与待审核隔离区的根目录。
namespace ShineMusic.Model.Options;

/// <summary>
/// 媒体文件磁盘存储配置，对应配置节 "Storage"，两个根目录均在启动时校验非空。
/// </summary>
public sealed class StorageOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// 正式媒体库根目录：人工审核通过后的音频文件才会落在此目录并对用户可见。
    /// 相对路径相对于应用内容根目录（ContentRootPath）解析。
    /// </summary>
    public string LibraryRoot { get; set; } = string.Empty;

    /// <summary>
    /// 隔离区根目录：新导入、尚未通过人工审核的文件存放于此，与正式媒体库物理隔离，
    /// 防止未审核内容被直接播放或下载。相对路径同样按内容根目录解析。
    /// </summary>
    public string QuarantineRoot { get; set; } = string.Empty;
}
