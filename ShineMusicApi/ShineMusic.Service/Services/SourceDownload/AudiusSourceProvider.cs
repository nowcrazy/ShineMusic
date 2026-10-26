// 职责：Audius 音源平台实现。
// Audius 是创作者主动分享的开放平台，公共 API 免密钥（仅需 app_name 标识）：
// - 搜索：GET /v1/tracks/search?query=&app_name=
// - 详情：GET /v1/tracks/{id}?app_name=
// - 无损原文件：GET /v1/tracks/{id}/download?app_name=（仅作者在 Stems & Downloads 开放 Public 下载时可用，302 到内容节点）
// - 兜底流：GET /v1/tracks/{id}/stream?app_name=（320k MP3，302 到内容节点）

using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>Audius 音源提供者。</summary>
public sealed class AudiusSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options,
    ILogger<AudiusSourceProvider> logger) : IMusicSourceProvider
{
    /// <summary>命名 HttpClient（Program.cs 注册，统一超时控制）。</summary>
    public const string HttpClientName = "audius";

    public string Provider => "audius";
    public bool IsEnabled => options.Value.Audius.Enabled;
    public string? DisabledReason => IsEnabled ? null : "Audius source is disabled by configuration.";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = $"{Base()}/v1/tracks/search?query={Uri.EscapeDataString(query)}&limit={limit}&app_name={AppName()}";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SourceSearchResult>();
        foreach (var item in data.EnumerateArray())
        {
            var parsed = ParseTrack(item);
            if (parsed is not null) results.Add(parsed);
            if (results.Count >= limit) break;
        }
        return results.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var url = $"{Base()}/v1/tracks/{Uri.EscapeDataString(providerTrackId)}?app_name={AppName()}";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (doc is null) return null;
        // 详情端点 data 可能是对象或单元素数组，兼容两种形态。
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return null;
        var element = data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
            ? data[0]
            : data.ValueKind == JsonValueKind.Object ? data : (JsonElement?)null;
        return element is null ? null : ParseTrack(element.Value);
    }

    /// <inheritdoc />
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var track = await GetTrackAsync(providerTrackId, cancellationToken)
            ?? throw new SourceUnavailableException("该曲目在 Audius 上不存在或已被删除。");

        // 作者开放了 Public 全曲下载 → 尝试原文件（通常无损）；
        // Audius 的 is_downloadable 标记并不可靠（部分曲目标记可下载但 /download 返回 403），
        // 因此用一次轻量探测校验端点可用性，失败即回退到 320k 流地址。
        if (track.IsLosslessAvailable)
        {
            var downloadUrl = $"{Base()}/v1/tracks/{Uri.EscapeDataString(providerTrackId)}/download?app_name={AppName()}";
            if (await IsEndpointAvailableAsync(downloadUrl, cancellationToken))
            {
                return new SourceDownloadInfo(
                    downloadUrl,
                    string.Empty, // 原文件格式不定（flac/wav/mp3 均可能），交给下载层按响应头探测
                    IsLossless: true,
                    BitrateKbps: null,
                    Quality: SourceQualities.Lossless);
            }
            logger.LogWarning("Audius download endpoint returned non-success for track {TrackId}; falling back to stream.", providerTrackId);
        }

        // 兜底：320k 流式转码。内容节点可能因版权把 CID 拉黑（403）或曲目被删（404），
        // 探测失败时抛出明确原因，避免下载层只报笼统的 "could not be downloaded"。
        var streamUrl = $"{Base()}/v1/tracks/{Uri.EscapeDataString(providerTrackId)}/stream?app_name={AppName()}";
        if (!await IsEndpointAvailableAsync(streamUrl, cancellationToken))
            throw new SourceUnavailableException("该曲目在 Audius 已下架或被内容节点屏蔽，无法下载。");
        return new SourceDownloadInfo(
            streamUrl,
            "mp3",
            IsLossless: false,
            BitrateKbps: 320,
            Quality: SourceQualities.High);
    }

    /// <summary>
    /// 轻量校验下载/流端点可用性（Audius 部分曲目标记可下载但实际 403，内容节点也可能拉黑 CID）。
    /// 用 GET + ResponseHeadersRead（而非 HEAD）：部分内容节点对 HEAD 支持不佳，GET 仅读响应头最贴近真实下载行为。
    /// </summary>
    private async Task<bool> IsEndpointAvailableAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // 3xx 重定向视为可用（内容节点会 302），4xx/5xx 视为不可用。
            return response.IsSuccessStatusCode || ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析 Audius 曲目 JSON 为统一模型；关键字段缺失或曲目已删除/不可流播时返回 null 跳过。</summary>
    private SourceSearchResult? ParseTrack(JsonElement item)
    {
        var id = GetString(item, "id");
        var title = GetString(item, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        // Audius 搜索会混入已删除/不可用的盗版上传曲目（is_delete / is_available=false / is_streamable=false），
        // 它们的 stream/download 端点必然 404，必须在搜索阶段就过滤掉，否则用户派发的下载任务注定失败。
        if (GetBool(item, "is_delete") || HasFalseFlag(item, "is_available") || HasFalseFlag(item, "is_streamable"))
            return null;

        // 艺人：Audius 曲目挂单一 user 对象。
        var artists = new List<string>();
        if (item.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(user, "name");
            if (!string.IsNullOrWhiteSpace(name)) artists.Add(name.Trim());
        }

        // 封面：优先 480x480，回退 150x150 / 1000x1000。
        string? coverUrl = null;
        if (item.TryGetProperty("artwork", out var artwork) && artwork.ValueKind == JsonValueKind.Object)
            coverUrl = GetString(artwork, "480x480") ?? GetString(artwork, "1000x1000") ?? GetString(artwork, "150x150");

        // 下载可用性：新版 download 区块优先，回退旧字段 is_downloadable。
        var downloadable = false;
        if (item.TryGetProperty("download", out var download) && download.ValueKind == JsonValueKind.Object)
            downloadable = GetBool(download, "is_downloadable") && !GetBool(download, "requires_follow");
        if (!downloadable)
            downloadable = GetBool(item, "is_downloadable");

        var durationSeconds = GetLong(item, "duration");
        var genre = GetString(item, "genre");
        return new SourceSearchResult(
            Provider,
            id,
            title.Trim(),
            artists,
            AlbumTitle: GetString(item, "album_name") ?? GetString(item, "playlist_name"),
            DurationMs: durationSeconds * 1000,
            Genres: string.IsNullOrWhiteSpace(genre) ? [] : [genre.Trim()],
            CoverUrl: coverUrl,
            SourcePageUrl: AbsolutePermalink(GetString(item, "permalink")),
            License: GetString(item, "license"),
            Quality: downloadable ? SourceQualities.Lossless : SourceQualities.High,
            IsLosslessAvailable: downloadable);
    }

    /// <summary>GET JSON 并解析；网络/格式异常返回 null（搜索场景容忍失败，由编排层聚合多源结果）。</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Audius request failed: {StatusCode} {Url}", (int)response.StatusCode, url);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Audius request error: {Url}", url);
            return null;
        }
    }

    private string Base() => options.Value.Audius.BaseUrl.TrimEnd('/');
    private string AppName() => Uri.EscapeDataString(options.Value.Audius.AppName);

    /// <summary>Audius 的 permalink 是相对路径（如 /byone/xxx），补全为 audius.co 绝对地址，供前端直接打开。</summary>
    private static string? AbsolutePermalink(string? permalink)
    {
        if (string.IsNullOrWhiteSpace(permalink)) return null;
        if (permalink.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return permalink;
        return $"https://audius.co{(permalink.StartsWith('/') ? permalink : $"/{permalink}")}";
    }

    /// <summary>字段存在且显式为 false 时返回 true（字段缺失视为未知，不误伤）。</summary>
    private static bool HasFalseFlag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
