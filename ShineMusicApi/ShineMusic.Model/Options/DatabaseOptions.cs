// 数据库连接配置选项：绑定 appsettings 的 "Database" 配置节，提供 PostgreSQL 连接字符串。
namespace ShineMusic.Model.Options;

/// <summary>
/// 数据库连接配置，对应配置节 "Database"。
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// PostgreSQL 连接字符串（Npgsql 格式，如 Host=...;Database=shinemusic;...）；为必填项，启动时校验非空。
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}