// 职责：QQ音乐音源 provider（HTTP 直链型）。
// 端点链（2026-11 实测匿名态可用）：
//   1. 搜索：c.y.qq.com/soso/fcgi-bin/client_search_cp —— 返回 data.song.list[]（songmid/歌名/艺人/专辑/interval）；
//   2. 详情：c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg —— 按 songmid 取单曲元数据；
//   3. 下载地址：u.y.qq.com/cgi-bin/musicu.fcg（POST vkey.GetVkeyServer/CgiGetVkey）—— 返回 purl，
//      拼 http://ws.stream.qqmusic.qq.com/{purl} 即直链；匿名态仅 128k m4a（C400 前缀），
//      登录 Cookie 可至 320k（M500/M800）与无损 flac（F000）。
// 局限：
//   - VIP 曲目匿名态 purl 为空 → 判定不可下载（抛 SourceUnavailableException，提示配置 Cookie 或换源）；
//   - 2026-11 起匿名 vkey 对多数曲目统一返回 result=104003（空 purl，即使搜索标 pay=0），
//     即匿名态 vkey 探测作为「负向预判」不可靠 —— 匿名态 CanDownloadAsync 不做过滤（解析失败由换源兜底），
//     改为搜索时按 pay.payplay==1 这一平台确定性标记过滤 VIP 曲目；配置登录 Cookie 后恢复 vkey 探测过滤。

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>QQ音乐音源提供者。</summary>
public sealed class QqSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options) : IMusicSourceProvider
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称。</summary>
    public const string HttpClientName = "qq";

    private const string SearchBase = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";
    private const string DetailBase = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg";
    private const string VkeyBase = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string StreamBase = "http://ws.stream.qqmusic.qq.com/";

    /// <inheritdoc />
    public string Provider => "qq";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Qq.Enabled;

    /// <inheritdoc />
    public string? DisabledReason => IsEnabled ? null : "QQ音乐音源未启用（SourceDownload:Qq:Enabled=false）。";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{SearchBase}?w={Uri.EscapeDataString(query)}&format=json&n={Math.Clamp(limit, 1, 50)}&p=1";
        var raw = await client.GetStringAsync(url, cancellationToken);

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("song", out var song)
            || !song.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];

        // 匿名态（未配置 Cookie）按平台确定性标记 pay.payplay==1 预过滤 VIP 曲目（匿名 vkey 必空 purl）；
        // 配置 Cookie 后 VIP 曲目可能可取，不过滤，交由 CanDownloadAsync 探测。
        var filterVip = string.IsNullOrWhiteSpace(options.Value.Qq.Cookie);
        var results = new List<SourceSearchResult>();
        foreach (var entry in list.EnumerateArray())
        {
            var songMid = GetString(entry, "songmid");
            var title = GetString(entry, "songname");
            if (string.IsNullOrWhiteSpace(songMid) || string.IsNullOrWhiteSpace(title))
                continue;
            var artists = ParseSingers(entry);
            var album = GetString(entry, "albumname");
            var albumMid = GetString(entry, "albummid");
            long? durationMs = null;
            if (entry.TryGetProperty("interval", out var interval) && interval.ValueKind == JsonValueKind.Number
                && interval.TryGetInt64(out var seconds) && seconds > 0)
                durationMs = seconds * 1000;
            // pay.payplay==1 标记 VIP 曲目（匿名态不可下载，登录 Cookie 后可取）。
            var isVip = entry.TryGetProperty("pay", out var pay)
                && pay.TryGetProperty("payplay", out var payPlay)
                && payPlay.ValueKind == JsonValueKind.Number && payPlay.GetInt32() == 1;
            if (isVip && filterVip)
                continue;
            results.Add(new SourceSearchResult(
                Provider,
                songMid,
                title,
                artists,
                string.IsNullOrWhiteSpace(album) ? null : album,
                durationMs,
                [],
                string.IsNullOrWhiteSpace(albumMid) ? null : $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg",
                $"https://y.qq.com/n/ryqq/songDetail/{songMid}",
                License: null,
                isVip ? SourceQualities.High : SourceQualities.Standard,
                IsLosslessAvailable: isVip,
                ReleaseYear: SourceReleaseYearParser.FromJson(entry, "pubtime", "year")));
        }
        return results.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        string json;
        try
        {
            json = await client.GetStringAsync(
                $"{DetailBase}?songmid={Uri.EscapeDataString(providerTrackId)}&format=json", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return null;
            var track = data[0];
            var title = GetString(track, "songname");
            if (string.IsNullOrWhiteSpace(title))
                return null;
            var album = GetString(track, "albumname");
            var albumMid = GetString(track, "albummid");
            long? durationMs = null;
            if (track.TryGetProperty("interval", out var interval) && interval.ValueKind == JsonValueKind.Number
                && interval.TryGetInt64(out var seconds) && seconds > 0)
                durationMs = seconds * 1000;
            return new SourceSearchResult(
                Provider,
                providerTrackId,
                title,
                ParseSingers(track),
                string.IsNullOrWhiteSpace(album) ? null : album,
                durationMs,
                [],
                string.IsNullOrWhiteSpace(albumMid) ? null : $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg",
                $"https://y.qq.com/n/ryqq/songDetail/{providerTrackId}",
                null,
                SourceQualities.Standard,
                IsLosslessAvailable: false,
                ReleaseYear: SourceReleaseYearParser.FromJson(track, "pubtime", "year"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>匿名态 VIP 曲目 purl 为空 → 抛出，由换源机制兜底；配置登录 Cookie 后可取 320k/无损。</remarks>
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var vkey = await ResolveVkeyAsync(providerTrackId, cancellationToken);
        if (vkey is null || string.IsNullOrWhiteSpace(vkey.Value.Purl))
            throw new SourceUnavailableException("QQ音乐匿名态仅 VIP 曲目试听，需配置 Cookie 或换源。");
        return vkey.Value.ToDownloadInfo(StreamBase + vkey.Value.Purl);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 轻量预判：匿名态下 vkey 接口对多数曲目统一返回 104003（空 purl），负向预判不可靠，
    /// 故匿名态不过滤（VIP 已在搜索时按 pay.payplay 预过滤；解析失败由换源兜底）；
    /// 配置登录 Cookie 后探测结果可信，purl 为空判定不可下载。超时/异常时保守放行。
    /// </remarks>
    public async Task<bool> CanDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Qq.Cookie))
            return true;
        try
        {
            var vkey = await ResolveVkeyAsync(providerTrackId, cancellationToken);
            return vkey is not null && !string.IsNullOrWhiteSpace(vkey.Value.Purl);
        }
        catch
        {
            return true; // 超时/异常时保守放行
        }
    }

    /// <summary>
    /// 调 musicu.fcg vkey 接口取播放地址：POST JSON body，匿名 uin=0。
    /// 返回 purl 与 filename（C400=128k m4a / M500=320k mp3 / M800=320k m4a / F000=flac）；接口失败返回 null。
    /// </summary>
    private async Task<VkeyResult?> ResolveVkeyAsync(string songMid, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var body = JsonSerializer.Serialize(new
        {
            req_0 = new
            {
                module = "vkey.GetVkeyServer",
                method = "CgiGetVkey",
                param = new
                {
                    guid = "1145141919",
                    songmid = new[] { songMid },
                    songtype = new[] { 0 },
                    uin = "0",
                    platform = "20"
                }
            },
            comm = new { uin = "0", format = "json", ct = 24, cv = 0 }
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(VkeyBase, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("req_0", out var req)
                || !req.TryGetProperty("data", out var data)
                || !data.TryGetProperty("midurlinfo", out var infos)
                || infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0)
                return null;
            var info = infos[0];
            return new VkeyResult(GetString(info, "purl"), GetString(info, "filename"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析 singer 数组为艺人名列表。</summary>
    private static IReadOnlyList<string> ParseSingers(JsonElement entry)
    {
        if (!entry.TryGetProperty("singer", out var singers) || singers.ValueKind != JsonValueKind.Array)
            return [];
        var artists = new List<string>();
        foreach (var singer in singers.EnumerateArray())
        {
            var name = GetString(singer, "name");
            if (!string.IsNullOrWhiteSpace(name))
                artists.Add(name.Trim());
        }
        return artists;
    }

    /// <summary>从 JsonElement 上安全取字符串字段。</summary>
    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>vkey 接口返回的播放地址信息（purl 路径 + 文件名）。</summary>
    private readonly record struct VkeyResult(string? Purl, string? FileName)
    {
        /// <summary>
        /// 按文件名前缀/扩展名判定容器与音质：
        /// F000/.flac=无损，M500/M800=320k 高音质，C400=128k 标准（匿名态封顶）。
        /// </summary>
        public SourceDownloadInfo ToDownloadInfo(string url)
        {
            var name = FileName ?? string.Empty;
            if (name.StartsWith("F000", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                return new SourceDownloadInfo(url, "flac", IsLossless: true, BitrateKbps: null, SourceQualities.Lossless);
            if (name.StartsWith("M500", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("M800", StringComparison.OrdinalIgnoreCase))
            {
                var container = name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? "m4a" : "mp3";
                return new SourceDownloadInfo(url, container, IsLossless: false, BitrateKbps: 320, SourceQualities.High);
            }
            return new SourceDownloadInfo(url, "m4a", IsLossless: false, BitrateKbps: 128, SourceQualities.Standard);
        }
    }
}
