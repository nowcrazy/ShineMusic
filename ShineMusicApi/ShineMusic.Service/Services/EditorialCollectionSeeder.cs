using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service;

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
    public static async Task<int> SeedIfEmptyAsync(ISqlSugarClient db, CancellationToken cancellationToken = default)
    {
        // 已有任何合集（无论是否发布）就不再干预，避免覆盖运营手工编排的内容
        if (await db.Queryable<EditorialCollection>().AnyAsync(cancellationToken)) return 0;

        var tracks = await db.Queryable<Track>()
            .Where(t => t.PublishedAt.HasValue)
            .ToListAsync(cancellationToken);

        if (tracks.Count == 0) return 0;

        // 只需流派维度用于聚合：手工填充 TrackGenres（不调用完整水合，避免无谓的艺人/专辑/封面查询）。
        var trackIds = tracks.Select(t => t.Id).ToList();
        var trackGenres = await db.Queryable<TrackGenre>()
            .Where(link => trackIds.Contains(link.TrackId))
            .ToListAsync(cancellationToken);
        var genreIds = trackGenres.Select(link => link.GenreId).Distinct().ToList();
        var genres = genreIds.Count == 0
            ? []
            : await db.Queryable<Genre>().Where(genre => genreIds.Contains(genre.Id)).ToListAsync(cancellationToken);
        var genreMap = genres.ToDictionary(genre => genre.Id);
        foreach (var link in trackGenres)
            if (genreMap.TryGetValue(link.GenreId, out var genre))
                link.Genre = genre;
        var trackGenreLookup = trackGenres.ToLookup(link => link.TrackId);
        foreach (var track in tracks)
            track.TrackGenres = trackGenreLookup[track.Id].ToList();

        // 排序放内存里做：排序键为 AddedAt，集合规模有限，避免依赖库端排序差异。
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

        // 合集本体 + 曲目关联整体一个事务（保证级联插入的原子性）。
        await db.Ado.BeginTranAsync();
        try
        {
            foreach (var collection in collections)
            {
                await db.Insertable(collection).ExecuteCommandAsync(cancellationToken);
                foreach (var link in collection.CollectionTracks)
                {
                    link.CollectionId = collection.Id;
                    await db.Insertable(link).ExecuteCommandAsync(cancellationToken);
                }
            }
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        return collections.Count;
    }
}
