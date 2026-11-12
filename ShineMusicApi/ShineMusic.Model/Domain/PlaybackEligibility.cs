// 职责：播放资格的领域规则（纯函数，不引用 ASP.NET Core 类型）。
// 接口层负责从 HttpContext 取出 ClaimsPrincipal 并转成这里需要的简单参数，
// 「谁能听什么」的判定只在这里定义一份。

namespace ShineMusic.Model.Domain;

/// <summary>音频流端点的播放资格判定。</summary>
public static class PlaybackEligibility
{
    /// <summary>
    /// 匿名播放开关关闭且当前请求未认证时，需要返回 401。
    /// 注意：匿名可听是本产品的既定原则，开关默认开启。
    /// </summary>
    public static bool IsAuthenticationRequired(bool anonymousPlaybackEnabled, bool isAuthenticated) =>
        !anonymousPlaybackEnabled && !isAuthenticated;

    /// <summary>
    /// 已公开曲目任何人可播；未公开（私有）曲目仅管理员或上传者本人可播，
    /// 其他人按 404 处理以不暴露资源存在性。
    /// </summary>
    public static bool CanAccess(
        DateTimeOffset? publishedAt,
        Guid? ownerUserId,
        bool isAdmin,
        Guid? currentUserId)
    {
        if (publishedAt is not null) return true;
        if (isAdmin) return true;
        return ownerUserId is not null && currentUserId is not null && ownerUserId.Value == currentUserId.Value;
    }
}
