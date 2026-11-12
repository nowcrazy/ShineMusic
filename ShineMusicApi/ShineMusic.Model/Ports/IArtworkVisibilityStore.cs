// 职责：封面读取用例所需的曲库读端口。
// 与 ITrackPlaybackStore 同一模式：接口层只表达「要一张公开可见的封面」，
// 「是否被已发布内容引用」这类存储细节由基础设施实现。

namespace ShineMusic.Model.Ports;

/// <summary>公开可见封面的存储位置与 MIME 类型。</summary>
public sealed record PublicArtworkAsset(string StorageKey, string ContentType);

/// <summary>封面可见性读模型。</summary>
public interface IArtworkVisibilityStore
{
    /// <summary>读取被已发布曲目 / 专辑 / 精选集引用的封面；不可见或不存在返回 null。</summary>
    Task<PublicArtworkAsset?> GetPublicAsync(Guid artworkId, CancellationToken cancellationToken);
}
