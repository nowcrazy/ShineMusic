using ShineMusic.Api.Data;

namespace ShineMusic.Api.Tests.Domain;

/// <summary>
/// 为「匹配校验器」构造点歌需求记录的测试夹具。
/// </summary>
/// <remarks>
/// <see cref="SongRequestRecord"/> 是 EF 实体类而非 record，不支持 <c>with</c> 表达式，
/// 因此这里用显式复制的方式派生变体。
/// </remarks>
internal static class RequestFixture
{
    public static SongRequestRecord Create(string title, string artistName) =>
        new()
        {
            Title = title,
            ArtistName = artistName,
            // 归一化字段不参与 Validate 的比较逻辑，填占位值满足 required 约束。
            NormalizedTitle = title.ToUpperInvariant(),
            NormalizedArtistName = artistName.ToUpperInvariant(),
        };

    /// <summary>复制一份需求，并覆盖标题与艺人（其余字段保持原值）。</summary>
    public static SongRequestRecord With(this SongRequestRecord source, string? title = null, string? artistName = null) =>
        new()
        {
            Id = source.Id,
            Title = title ?? source.Title,
            ArtistName = artistName ?? source.ArtistName,
            NormalizedTitle = (title ?? source.Title).ToUpperInvariant(),
            NormalizedArtistName = (artistName ?? source.ArtistName).ToUpperInvariant(),
            AlbumTitle = source.AlbumTitle,
            Status = source.Status,
            CreatedByUserId = source.CreatedByUserId,
        };
}
