// 职责：用户自定义主题壁纸文件的磁盘操作助手（不涉及数据库）。
// 壁纸存放于曲库根目录下的 themes/{userId}.{ext}，仅允许 jpg/png/webp 三种格式。
// 设计取舍（与 UserAvatarFiles 一致）：
// - 壁纸不属于曲库媒体资产（无 MediaAsset/Artwork 行引用），存储治理对账时排除 themes/ 目录；
// - 文件名固定为 {userId}.{扩展名}，上传即整体替换（先删旧文件再写新文件）；
// - 壁纸读取 URL 带文件最后写入时间戳版本参数（?v=ticks），换图后浏览器缓存自动失效；
// - 壁纸含用户私人图片，读取端点要求登录（前端经鉴权请求取回二进制后以 objectURL 注入样式）。
// 路径一律经 LibraryStoragePaths.TryResolve 校验，杜绝路径穿越。

using ShineMusic.Api.Data;

namespace ShineMusic.Api.Services;

/// <summary>
/// 用户主题壁纸文件助手：负责壁纸的查找、写入、删除与 URL 构造。
/// 所有操作以 LibraryStoragePaths 为信任根，userId 仅用于拼接受控目录内的文件名。
/// </summary>
public static class UserThemeFiles
{
    /// <summary>壁纸目录的存储键前缀（相对曲库根目录）。</summary>
    public const string DirectoryKey = "themes/";

    /// <summary>壁纸文件体积上限（8MB）：壁纸比头像大得多，需容纳整屏照片，超出时拒绝上传。</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 查找指定用户当前的壁纸文件路径；不存在时返回 null。
    /// 目录内出现多个匹配文件（并发上传残留）时取最近写入的一个。
    /// </summary>
    /// <param name="paths">曲库路径解析器。</param>
    /// <param name="userId">用户 ID（Guid 的 D 格式字符串，含横线）。</param>
    public static string? FindFilePath(LibraryStoragePaths paths, Guid userId)
    {
        if (!paths.TryResolve(DirectoryKey, out var directory) || !Directory.Exists(directory))
            return null;
        var matches = Directory.GetFiles(directory, $"{userId}.*");
        if (matches.Length == 0)
            return null;
        return matches.Length == 1 ? matches[0] : matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    /// <summary>
    /// 删除指定用户的全部壁纸文件（忽略不存在的文件与并发删除冲突）。
    /// </summary>
    /// <returns>是否至少删除成功一个文件。</returns>
    public static bool DeleteAll(LibraryStoragePaths paths, Guid userId)
    {
        if (!paths.TryResolve(DirectoryKey, out var directory) || !Directory.Exists(directory))
            return false;
        var deleted = false;
        foreach (var file in Directory.GetFiles(directory, $"{userId}.*"))
        {
            try
            {
                File.Delete(file);
                deleted = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 文件被占用等情况忽略：壁纸可被后续上传覆盖，删除失败不阻断请求。
            }
        }
        return deleted;
    }

    /// <summary>
    /// 构造壁纸对外读取 URL；用户无壁纸时返回 null。
    /// URL 附带文件最后写入时间戳版本参数，浏览器在换图后能拿到新内容。
    /// </summary>
    public static string? BuildUrl(LibraryStoragePaths paths, Guid userId)
    {
        var file = FindFilePath(paths, userId);
        if (file is null)
            return null;
        var version = File.GetLastWriteTimeUtc(file).Ticks;
        return $"/api/v1/me/theme/wallpaper?v={version}";
    }
}
