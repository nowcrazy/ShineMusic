// Redis 连接配置选项：绑定 appsettings 的 "Redis" 配置节。
// 留空表示不启用 Redis（开发应急回退），此时 Program.cs 注册进程内分布式缓存替代。
namespace ShineMusic.Model.Options;

/// <summary>
/// Redis 连接配置，对应配置节 "Redis"。
/// </summary>
public sealed class RedisOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "Redis";

    /// <summary>
    /// Redis 连接字符串（如 "localhost:6379"）；为空时回退到进程内内存缓存，便于无 Docker 环境临时开发。
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>分布式缓存键前缀：避免与同一 Redis 实例上的其他应用互相污染。</summary>
    public string InstanceName { get; set; } = "shinemusic:";
}
