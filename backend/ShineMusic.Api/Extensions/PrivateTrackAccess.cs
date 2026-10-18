// 私有曲目（用户上传、尚未转公开）的访问判断。
// 私有曲目在前台查询（列表/搜索/推荐）中天然不可见（全部按 PublishedAt 过滤），
// 仅在“详情 / 播放流 / 歌词”三个按 Id 直取的端点需要判断访问权：
// 上传者本人与管理员可见，其他人按 404 处理（不暴露存在性）。

using System.Security.Claims;

namespace ShineMusic.Api.Extensions;

/// <summary>私有曲目访问判断扩展。</summary>
public static class PrivateTrackAccess
{
    /// <summary>
    /// 判断当前用户是否可访问该曲目。
    /// 已公开曲目任何人可访问；未公开曲目仅上传者本人与管理员可访问。
    /// </summary>
    /// <param name="publishedAt">曲目发布时间；为空表示私有。</param>
    /// <param name="ownerUserId">上传者用户 Id；官方/管理员导入的曲目为空。</param>
    /// <param name="user">当前请求的用户主体。</param>
    public static bool CanAccess(DateTimeOffset? publishedAt, Guid? ownerUserId, ClaimsPrincipal user)
    {
        if (publishedAt is not null)
            return true;
        if (user.IsInRole("admin"))
            return true;
        return ownerUserId is not null && user.TryGetUserId(out var userId) && ownerUserId.Value == userId;
    }
}
