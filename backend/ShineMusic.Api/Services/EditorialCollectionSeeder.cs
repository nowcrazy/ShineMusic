using Microsoft.EntityFrameworkCore;
using ShineMusic.Api.Data;

namespace ShineMusic.Api.Services;

/// <summary>
/// 首次启动时按曲库现状生成真实的「编辑精选合集」。
/// 仅当 EditorialCollections 为空且存在已发布曲目时执行，天然幂等；
/// 生成后可由管理端继续编辑，不会被重复覆盖。
/// </summary>
public static class EditorialCollectionSeeder
{
    /// <summary>
    /// 若尚无任何编辑合集，则按「全部精选 + 各风格精选」自动建合集。
    /// 合集封面取该合集第一首有封面的曲目，避免前台显示空白占位。
    /// </summary>
    /// <returns>本次创建的合集数量；已有合集或无曲目时返回 0。</returns>
    public static async Task<int> SeedIfEmptyAsync(MusicDbContext db, CancellationToken cancellationToken = default)
    {
        // 已有任何合集（无论是否发布）就不再干预，避免覆盖运营手工编排的内容
        if (await db.EditorialCollections.AnyAsync(cancellationToken)) return 0;

        var tracks = await db.Tracks
            .AsNoTracking()
            .Where(t => t.PublishedAt != null)
            .Include(t => t.TrackGenres)
                .ThenInclude(tg => tg.Genre)
            .ToListAsync(cancellationToken);

        if (tracks.Count == 0) return 0;

        // SQLite 不支持在 ORDER BY 中使用 DateTimeOffset，排序放到内存里做
        tracks = tracks.OrderBy(t => t.AddedAt).ToList();

        var collections = new List<EditorialCollection>();

        // ① 全部精选：曲库里最值得先听的一批
        collections.Add(new EditorialCollection
        {
            Title = "编辑精选",
            Description = "曲库里最值得先听的一批",
            ArtworkId = tracks.FirstOrDefault(t => t.ArtworkId != null)?.ArtworkId,
            SortOrder = 0,
            IsPublished = true,
            CollectionTracks = tracks
                .Select((t, i) => new CollectionTrack { TrackId = t.Id, SortOrder = i })
                .ToList(),
        });

        // ② 按风格聚合：每个有曲目的风格建一个合集（曲目多的排前面）
        var byGenre = new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            foreach (var genreName in track.TrackGenres
                         .Select(tg => tg.Genre.Name)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!byGenre.TryGetValue(genreName, out var list))
                    byGenre[genreName] = list = [];
                list.Add(track);
            }
        }

        var sortOrder = 1;
        foreach (var (genreName, list) in byGenre
                     .OrderByDescending(kv => kv.Value.Count)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            collections.Add(new EditorialCollection
            {
                Title = $"{genreName}精选",
                Description = $"{genreName} 风格，按曲库自动聚合",
                ArtworkId = list.FirstOrDefault(t => t.ArtworkId != null)?.ArtworkId,
                SortOrder = sortOrder++,
                IsPublished = true,
                CollectionTracks = list
                    .Select((t, i) => new CollectionTrack { TrackId = t.Id, SortOrder = i })
                    .ToList(),
            });
        }

        db.EditorialCollections.AddRange(collections);
        await db.SaveChangesAsync(cancellationToken);
        return collections.Count;
    }
}
