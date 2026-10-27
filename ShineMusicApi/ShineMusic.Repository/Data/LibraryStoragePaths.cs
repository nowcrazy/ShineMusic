// 职责：提供曲库存储目录的路径解析与安全校验。
// LibraryStoragePaths 面向正式曲库目录，QuarantineStoragePaths 面向上传隔离目录；
// 两者都把外部传入的“存储键（storageKey）”拼接到受信任的根目录下，
// 并通过前缀校验阻止路径穿越（path traversal，如 “../”）逃逸出根目录。

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;

namespace ShineMusic.Repository.Data;

/// <summary>
/// 正式曲库存储根目录解析器：将存储键安全映射为根目录内的绝对文件路径。
/// </summary>
public sealed class LibraryStoragePaths
{
    private readonly string _libraryRoot;

    /// <summary>
    /// 从存储配置解析曲库根目录：相对路径基于应用 ContentRootPath 转绝对路径，并去除结尾分隔符以便后续前缀判断。
    /// </summary>
    public LibraryStoragePaths(IOptions<StorageOptions> storageOptions, IWebHostEnvironment environment)
    {
        _libraryRoot = Path.GetFullPath(storageOptions.Value.LibraryRoot, environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>曲库根目录绝对路径（供存储治理等需要枚举磁盘文件的场景使用，不对外暴露可写语义）。</summary>
    public string Root => _libraryRoot;

    /// <summary>
    /// 尝试将存储键解析为根目录内的安全绝对路径。
    /// </summary>
    /// <param name="storageKey">相对存储键（来自数据库/上传流程，不可直接信任）。</param>
    /// <param name="filePath">解析成功时输出绝对路径；失败时为 <see cref="string.Empty"/>。</param>
    /// <returns>路径合法且位于曲库根目录内返回 true；否则返回 false（拒绝访问）。</returns>
    public bool TryResolve(string storageKey, out string filePath)
    {
        filePath = string.Empty;
        try
        {
            // GetFullPath 会规范化 “..”、“.” 与符号链接式写法，得到真实目标绝对路径。
            var resolvedPath = Path.GetFullPath(Path.Combine(_libraryRoot, storageKey));
            // 合法路径必须以“根目录 + 分隔符”为前缀，确保文件严格位于根目录之内（根目录本身不算命中）。
            var rootPrefix = _libraryRoot + Path.DirectorySeparatorChar;
            // Windows 文件系统不区分大小写，POSIX 区分大小写。
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            // 前缀不匹配说明 storageKey 试图穿越到根目录之外，拒绝该路径。
            if (!resolvedPath.StartsWith(rootPrefix, comparison))
                return false;

            filePath = resolvedPath;
            return true;
        }
        // 非法字符/格式或 IO 不支持等异常一律视为解析失败（fail-closed），不向上抛出。
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// 上传隔离存储根目录解析器：未经审核的上传文件先存放在隔离区，逻辑与正式曲库解析器相同。
/// </summary>
public sealed class QuarantineStoragePaths
{
    private readonly string _quarantineRoot;

    /// <summary>
    /// 从存储配置解析隔离区根目录（相对路径基于 ContentRootPath 绝对化，并去除结尾分隔符）。
    /// </summary>
    public QuarantineStoragePaths(IOptions<StorageOptions> storageOptions, IWebHostEnvironment environment)
    {
        _quarantineRoot = Path.GetFullPath(storageOptions.Value.QuarantineRoot, environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>隔离区根目录绝对路径（供需要直接在根下创建子目录的场景使用）。</summary>
    public string Root => _quarantineRoot;

    /// <summary>
    /// 尝试将存储键解析为隔离区内的安全绝对路径；含路径穿越或非法格式时返回 false。
    /// </summary>
    /// <param name="storageKey">相对存储键（不可信输入）。</param>
    /// <param name="filePath">解析成功时输出绝对路径；失败时为 <see cref="string.Empty"/>。</param>
    /// <returns>路径位于隔离根目录内返回 true；否则返回 false。</returns>
    public bool TryResolve(string storageKey, out string filePath)
    {
        filePath = string.Empty;
        try
        {
            // 规范化拼接后的绝对路径，暴露 “../” 等穿越企图。
            var resolvedPath = Path.GetFullPath(Path.Combine(_quarantineRoot, storageKey));
            // 强制以“隔离根 + 分隔符”为前缀，保证不会写到隔离区之外。
            var rootPrefix = _quarantineRoot + Path.DirectorySeparatorChar;
            // Windows 大小写不敏感、POSIX 大小写敏感。
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!resolvedPath.StartsWith(rootPrefix, comparison))
                return false;

            filePath = resolvedPath;
            return true;
        }
        // 任何路径解析异常均按拒绝处理，避免不可信输入导致崩溃或越权访问。
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
