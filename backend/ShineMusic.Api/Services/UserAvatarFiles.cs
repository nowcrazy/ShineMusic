// 职责：用户头像文件的磁盘操作助手（不涉及数据库）。
// 头像存放于曲库根目录下的 avatars/{userId}.{ext}，仅允许 jpg/png/webp 三种格式。
// 设计取舍：
// - 头像不属于曲库媒体资产（无 MediaAsset/Artwork 行引用），因此存储审计
//   （StorageGovernanceService）在对账枚举时显式排除 avatars/ 目录，避免被误判为孤儿文件；
// - 文件名固定为 {userId}.{扩展名}，上传即整体替换（先删旧文件再写新文件）；
// - 头像读取 URL 带文件最后写入时间戳版本参数（?v=ticks），浏览器换头像后无需手动清缓存。
// 路径一律经 LibraryStoragePaths.TryResolve 校验，杜绝路径穿越。

using ShineMusic.Api.Data;

namespace ShineMusic.Api.Services;

/// <summary>
/// 用户头像文件助手：负责头像的查找、写入、删除与 URL 构造。
/// 所有操作以 LibraryStoragePaths 为信任根，userId 仅用于拼接受控目录内的文件名。
/// </summary>
public static class UserAvatarFiles
{
    /// <summary>头像目录的存储键前缀（相对曲库根目录）。</summary>
    public const string DirectoryKey = "avatars/";

    /// <summary>头像文件体积上限（2MB），超出时拒绝上传。</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>受支持的图片格式（魔术字节校验通过后使用）。</summary>
    private static readonly (string ContentType, string Extension)[] SupportedFormats =
    [
        ("image/jpeg", ".jpg"),
        ("image/png", ".png"),
        ("image/webp", ".webp"),
    ];

    /// <summary>
    /// 查找指定用户当前的头像文件路径；不存在时返回 null。
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
    /// 删除指定用户的全部头像文件（忽略不存在的文件与并发删除冲突）。
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
                // 文件被占用等情况忽略：头像可被后续上传覆盖，删除失败不阻断请求。
            }
        }
        return deleted;
    }

    /// <summary>
    /// 通过图片文件头魔术字节识别受支持的图片格式。
    /// 不信任客户端声明的 Content-Type，仅按二进制特征判定，防止伪造类型落地。
    /// </summary>
    /// <param name="header">文件前若干字节（建议至少 12 字节）。</param>
    /// <returns>识别成功返回（MIME, 扩展名）；无法识别返回 null。</returns>
    public static (string ContentType, string Extension)? DetectFormat(ReadOnlySpan<byte> header)
    {
        // JPEG：FF D8 FF
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return SupportedFormats[0];
        // PNG：89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return SupportedFormats[1];
        // WEBP：RIFF .... WEBP（第 8-11 字节为 WEBP）
        if (header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
            return SupportedFormats[2];
        return null;
    }

    /// <summary>
    /// 构造头像对外读取 URL；用户无头像时返回 null。
    /// URL 附带文件最后写入时间戳版本参数，浏览器在头像更换后能拿到新内容。
    /// </summary>
    public static string? BuildUrl(LibraryStoragePaths paths, Guid userId)
    {
        var file = FindFilePath(paths, userId);
        if (file is null)
            return null;
        var version = File.GetLastWriteTimeUtc(file).Ticks;
        return $"/api/v1/me/avatar?v={version}";
    }
}
