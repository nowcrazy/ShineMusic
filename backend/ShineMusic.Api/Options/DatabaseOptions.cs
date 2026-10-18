// 数据库连接配置选项：绑定 appsettings 的 "Database" 配置节，提供 SQLite 连接字符串。
namespace ShineMusic.Api.Options;

/// <summary>
/// 数据库连接配置，对应配置节 "Database"。
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// SQLite 数据库连接字符串；为必填项，启动时校验非空。
    /// 其中 DataSource 若为相对路径，会在 Program.cs 中按内容根目录解析为绝对路径。
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}