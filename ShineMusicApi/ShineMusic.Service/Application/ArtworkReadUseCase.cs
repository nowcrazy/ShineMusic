// 职责：封面读取用例（Application 层）。
// 把原先写在 Controller 里的三件事收口到这里：可见性查询（经端口）、MIME 白名单、
// storageKey 路径穿越校验与物理文件存在性。Controller 只负责把结果映射成 HTTP 文件流。

using ShineMusic.Model.Ports;
using ShineMusic.Repository.Data;

namespace ShineMusic.Service.Application;

/// <summary>公开封面的读取用例：返回可直接流式输出的物理路径与 Content-Type。</summary>
public sealed class ArtworkReadUseCase(IArtworkVisibilityStore store, LibraryStoragePaths storagePaths)
{
    /// <summary>用例结果。</summary>
    /// <param name="FilePath">通过根目录前缀校验且确实存在的物理文件路径。</param>
    /// <param name="ContentType">已通过白名单校验的图片 MIME 类型。</param>
    public sealed record Result(string FilePath, string ContentType);

    // 允许输出的图片 MIME 白名单；数据库中的 ContentType 不在此列时按 404 处理，避免伪造类型。
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    /// <summary>封面不存在、未被公开内容引用、类型非法、路径越界或文件缺失时统一返回 null（不暴露具体原因）。</summary>
    public async Task<Result?> ResolveAsync(Guid artworkId, CancellationToken cancellationToken)
    {
        var artwork = await store.GetPublicAsync(artworkId, cancellationToken);
        if (artwork is null || !AllowedContentTypes.Contains(artwork.ContentType))
            return null;
        if (!storagePaths.TryResolve(artwork.StorageKey, out var filePath) || !File.Exists(filePath))
            return null;
        return new Result(filePath, artwork.ContentType);
    }
}
