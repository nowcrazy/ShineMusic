// 职责：外部歌单解析结果的短期缓存（单例）。
//
// 为什么需要：网易云 200+ 首的歌单解析需要抓歌单页 + 分批拉 song/detail，
// 实测 20~60 秒。此前「预览」与「导入」各自解析一次，导入请求里光解析就吃掉大半超时预算，
// 叠加逐曲音源搜索必然被前端 120s 超时掐断。缓存后导入直接复用预览结果，解析成本只付一次。

using System.Collections.Concurrent;

namespace ShineMusic.Service.PlaylistImport;

/// <summary>
/// 解析结果短期缓存：键为 (用户 Id, 歌单链接)，条目 15 分钟过期。
/// 每次写入顺带清理过期项，不额外引入后台定时器。
/// </summary>
public sealed class PlaylistParseCache
{
    /// <summary>条目存活时长：足够覆盖「预览 → 点导入」的人工间隔。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<(Guid UserId, string Url), Entry> _entries = new();

    /// <summary>缓存一条解析结果（同一用户同一链接后写覆盖前写）。</summary>
    public void Set(Guid userId, string url, string platform, ParsedPlaylist playlist)
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAtUtc <= now)
                _entries.TryRemove(pair.Key, out _);
        }
        _entries[(userId, Normalize(url))] = new Entry(playlist, platform, now.Add(Ttl));
    }

    /// <summary>尝试取回未过期的解析结果。</summary>
    public bool TryGet(Guid userId, string url, out ParsedPlaylist playlist, out string platform)
    {
        if (_entries.TryGetValue((userId, Normalize(url)), out var entry) && entry.ExpiresAtUtc > DateTime.UtcNow)
        {
            playlist = entry.Playlist;
            platform = entry.Platform;
            return true;
        }
        playlist = null!;
        platform = string.Empty;
        return false;
    }

    private static string Normalize(string url) => url.Trim();

    private sealed record Entry(ParsedPlaylist Playlist, string Platform, DateTime ExpiresAtUtc);
}
