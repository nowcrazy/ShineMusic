// 职责：网易云音乐音源 provider（HTTP 直链型）。
// 端点链（公开接口免登录，2026-10 实测）：
//   1. 搜索：music.163.com/api/search/get?s=&type=1&limit= —— 返回 result.songs[]（id/name/artists/album/duration/fee）；
//   2. 详情：music.163.com/api/v3/song/detail?c=[{"id":...}] —— 返回 songs[]（ar/al.name/al.picUrl/dt/fee）；
//   3. 下载地址：music.163.com/api/song/enhance/player/url?ids=[...]&br=320000 —— 返回 data[].url 直链（mp3，320k）。
// 覆盖：主流中文曲库；VIP 曲（fee≠0）搜索可见但下载可能返回空 url → 判定不可下载。
// 注：网易云部分曲目因版权转移（如周杰伦原曲）可能只返回翻唱版本，属平台版权限制非代码缺陷。

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceProviders.Abstractions;

namespace ShineMusic.Service.SourceDownload;

/// <summary>网易云音乐音源提供者。</summary>
public sealed class NeteaseSourceProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SourceDownloadOptions> options,
    ILogger<NeteaseSourceProvider> logger,
    IAudioUrlResolver? audioUrlResolver = null) : IMusicSourceProvider, IQualityLadderProvider, ILazySourceDownloadProvider
{
    /// <summary>命名 HttpClient（Program.cs 注册，统一超时与 Referer/UA 头）。</summary>
    public const string HttpClientName = "netease";

    // Per-async-call diagnostics: fixed stages/codes only, never response text or URLs.
    public sealed record DownloadDiagnostic(string Stage, string Code, int? HttpStatus = null, int? CandidateCount = null);
    private static readonly AsyncLocal<Action<DownloadDiagnostic>?> DiagnosticSink = new();
    private static void Report(string stage, string code, int? status = null, int? count = null)
        => DiagnosticSink.Value?.Invoke(new(stage, code, status, count));

    public async Task<IReadOnlyList<SourceDownloadInfo>> ResolveDownloadCandidatesWithDiagnosticsAsync(
        string id, Action<DownloadDiagnostic> diagnostic, CancellationToken token)
    {
        var previous = DiagnosticSink.Value;
        DiagnosticSink.Value = diagnostic;
        try { return await ResolveDownloadCandidatesAsync(id, token); }
        finally { DiagnosticSink.Value = previous; }
    }

    /// <inheritdoc />
    public string Provider => "netease";

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Netease.Enabled;

    /// <inheritdoc />
    public string? DisabledReason => IsEnabled ? null : "网易云音源未启用（SourceDownload:Netease:Enabled=false）。";

    /// <inheritdoc />
    public async Task<SourceSearchResult[]> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = $"{SearchBase()}?s={Uri.EscapeDataString(query)}&type=1&limit={Math.Clamp(limit, 1, 50)}&offset=0";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (doc is null || !doc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<SourceSearchResult>();
        foreach (var song in songs.EnumerateArray())
        {
            var parsed = ParseTrackFromSearch(song);
            if (parsed is not null) list.Add(parsed);
            if (list.Count >= limit) break;
        }
        return list.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourceSearchResult?> GetTrackAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        var c = JsonSerializer.Serialize(new[] { new { id = long.Parse(providerTrackId) } });
        var url = $"{DetailBase()}?c={Uri.EscapeDataString(c)}";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (doc is null || !doc.RootElement.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0)
            return null;
        return ParseTrackFromDetail(songs[0]);
    }

    /// <inheritdoc />
    public async Task<SourceDownloadInfo> ResolveDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        await foreach (var candidate in ResolveDownloadCandidatesLazilyAsync(
            providerTrackId, [], cancellationToken))
            return candidate;
        throw new SourceUnavailableException("未取得可用下载地址；原因未知。");
    }

    /// <summary>兼容需要物化所有候选的调用方；下载队列使用按需枚举以避免提前取链。</summary>
    public async Task<IReadOnlyList<SourceDownloadInfo>> ResolveDownloadCandidatesAsync(
        string providerTrackId, CancellationToken cancellationToken)
    {
        var candidates = new List<SourceDownloadInfo>();
        await foreach (var candidate in ResolveDownloadCandidatesLazilyAsync(
            providerTrackId, [], cancellationToken))
            candidates.Add(candidate);
        if (candidates.Count == 0)
            throw new SourceUnavailableException("未取得可用下载地址；原因未知。");
        return candidates;
    }

    /// <summary>
    /// 按配置的音质优先级逐档解析，先复用此前降档候选，新请求先官方再按端点配置顺序；
    /// 调用方下载校验成功后停止枚举，
    /// 尚未枚举的端点不会收到请求。官方缺失数据或直链无效不会阻断后续候选。
    /// </summary>
    public async IAsyncEnumerable<SourceDownloadInfo> ResolveDownloadCandidatesLazilyAsync(
        string providerTrackId,
        IReadOnlyList<AudioQualityLevel> qualityChain,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0;
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tiers = qualityChain.Count == 0
                ? DefaultQualityChain()
                : qualityChain.Where(level => level is AudioQualityLevel.High320K or AudioQualityLevel.Lossless or AudioQualityLevel.Below320K)
                    .Distinct().ToArray();
            var deferred = new Dictionary<AudioQualityLevel, List<SourceDownloadInfo>>();
            void DeferCandidate(SourceDownloadInfo candidate, int currentTierIndex)
            {
                var actualTier = CandidateTier(candidate);
                if (Array.IndexOf(tiers, actualTier) <= currentTierIndex) return;
                if (!deferred.TryGetValue(actualTier, out var candidates)) deferred[actualTier] = candidates = [];
                candidates.Add(candidate);
            }
            var fallback = options.Value.Netease.VipFallback;
            for (var tierIndex = 0; tierIndex < tiers.Length; tierIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tier = tiers[tierIndex];
                // 先前请求被降档时保留结果，直到该档成为当前优先级才返回，避免重取及误丢合格资源。
                if (deferred.Remove(tier, out var savedCandidates))
                {
                    foreach (var saved in savedCandidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!seenUrls.Add(saved.DownloadUrl)) continue;
                        count++;
                        yield return saved;
                    }
                }
                var requestedBitrate = RequestedBitrate(tier);
                // 每档内官方源先行，不能越过调用方指定的更高优先级档位。
                var official = await ResolveOfficialAsync(providerTrackId, requestedBitrate, cancellationToken);
                if (official is not null)
                {
                    if (CandidateTier(official) != tier) DeferCandidate(official, tierIndex);
                    else if (seenUrls.Add(official.DownloadUrl))
                    {
                        count++;
                        yield return official;
                    }
                }

                if (!fallback.Enabled || fallback.Endpoints is null) continue;
                for (var index = 0; index < fallback.Endpoints.Count; index++)
                {
                    var endpoint = fallback.Endpoints[index];
                    if (endpoint is null || !endpoint.Enabled) continue;
                    foreach (var level in endpoint.Levels ?? [])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(level) || TierForLevel(level) != tier) continue;
                        var candidate = await ResolveFallbackAsync(providerTrackId, endpoint, index, level,
                            fallback.TimeoutSeconds, cancellationToken);
                        if (candidate is null) continue;
                        if (CandidateTier(candidate) != tier)
                        {
                            DeferCandidate(candidate, tierIndex);
                            continue;
                        }
                        if (!seenUrls.Add(candidate.DownloadUrl)) continue;
                        count++;
                        yield return candidate;
                    }
                }
            }
        }
        finally
        {
            Report("resolution", count == 0 ? "no_candidates_reason_unknown" : "candidates", count: count);
        }
    }

    /// <summary>仅刷新原来源和原音质；配置或响应的资源身份变化时返回 null。</summary>
    public async Task<SourceDownloadInfo?> RefreshDownloadAsync(
        string providerTrackId, SourceDownloadInfo previous, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(previous.ResolverId) || string.IsNullOrWhiteSpace(previous.ResourceId))
            return null;
        SourceDownloadInfo? refreshed = null;
        if (string.Equals(previous.ResolverId, OfficialResolverId, StringComparison.Ordinal))
        {
            if (!TryGetOfficialRequestBitrate(providerTrackId, previous.ResourceId, out var requestedBitrate)) return null;
            refreshed = await ResolveOfficialAsync(providerTrackId, requestedBitrate, cancellationToken);
        }
        else
        {
            var fallback = options.Value.Netease.VipFallback;
            if (!fallback.Enabled || fallback.Endpoints is null) return null;
            for (var index = 0; index < fallback.Endpoints.Count; index++)
            {
                var endpoint = fallback.Endpoints[index];
                if (endpoint is null || !endpoint.Enabled ||
                    !string.Equals(previous.ResolverId, GetResolverId(endpoint, index), StringComparison.Ordinal)) continue;
                foreach (var level in endpoint.Levels ?? [])
                {
                    if (string.IsNullOrWhiteSpace(level) || !string.Equals(previous.ResourceId,
                        GetFallbackResourceId(providerTrackId, endpoint, level), StringComparison.Ordinal)) continue;
                    refreshed = await ResolveFallbackAsync(providerTrackId, endpoint, index, level,
                        fallback.TimeoutSeconds, cancellationToken);
                    break;
                }
                if (refreshed is not null) break;
            }
        }
        return refreshed is not null && SameResource(previous, refreshed) ? refreshed : null;
    }

    private const string OfficialResolverId = "netease:official";

    private async Task<SourceDownloadInfo?> ResolveOfficialAsync(
        string providerTrackId, int requestedBitrate, CancellationToken cancellationToken)
    {
        var ids = JsonSerializer.Serialize(new[] { long.Parse(providerTrackId) });
        var url = $"{PlayerUrlBase()}?ids={Uri.EscapeDataString(ids)}&br={requestedBitrate}";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0
            || data[0].ValueKind != JsonValueKind.Object)
        {
            Report("official", "missing_data", count: 0);
            return null;
        }

        var item = data[0];
        var downloadUrl = GetString(item, "url");
        if (!IsHttpUrl(downloadUrl))
        {
            Report("official", string.IsNullOrWhiteSpace(downloadUrl) ? "empty_url" : "invalid_url", count: 0);
            return null;
        }
        var bitrate = GetInt(item, "br") ?? requestedBitrate;
        var type = (GetString(item, "type") ?? "mp3").ToLowerInvariant();
        var tier = IsLosslessContainer(type) ? AudioQualityLevel.Lossless
            : bitrate >= 320000 ? AudioQualityLevel.High320K : AudioQualityLevel.Below320K;
        var isLossless = tier == AudioQualityLevel.Lossless;
        var expirySeconds = GetInt(item, "expi");
        return new SourceDownloadInfo(downloadUrl!, type, isLossless, bitrate / 1000,
            isLossless ? SourceQualities.Lossless : tier == AudioQualityLevel.High320K ? SourceQualities.High : SourceQualities.Standard)
        {
            ResolverId = OfficialResolverId,
            ResourceId = $"netease:{providerTrackId}:official:{requestedBitrate}:{bitrate}:{type}:{OfficialEndpointFingerprint()}",
            UseIsolatedDownloadClient = true,
            ExpiresAtUtc = expirySeconds is > 0 and <= 604800
                ? DateTimeOffset.UtcNow.AddSeconds(expirySeconds.Value) : null
        };
    }

    private static bool SameResource(SourceDownloadInfo previous, SourceDownloadInfo refreshed)
        => string.Equals(previous.ResolverId, refreshed.ResolverId, StringComparison.Ordinal)
            && string.Equals(previous.ResourceId, refreshed.ResourceId, StringComparison.Ordinal)
            && string.Equals(previous.Container, refreshed.Container, StringComparison.OrdinalIgnoreCase)
            && previous.IsLossless == refreshed.IsLossless
            && previous.BitrateKbps == refreshed.BitrateKbps
            && string.Equals(previous.Quality, refreshed.Quality, StringComparison.Ordinal);

    private AudioQualityLevel[] DefaultQualityChain()
        => new[] { ConfiguredBitrateTier(), AudioQualityLevel.High320K, AudioQualityLevel.Lossless, AudioQualityLevel.Below320K }
            .Distinct().ToArray();

    private AudioQualityLevel ConfiguredBitrateTier()
        => options.Value.Netease.Bitrate >= 999000 ? AudioQualityLevel.Lossless
            : options.Value.Netease.Bitrate >= 320000 ? AudioQualityLevel.High320K : AudioQualityLevel.Below320K;

    private int RequestedBitrate(AudioQualityLevel tier)
    {
        var configuredBitrate = options.Value.Netease.Bitrate;
        if (configuredBitrate is > 0 and <= 10000000 && ConfiguredBitrateTier() == tier) return configuredBitrate;
        return tier switch
        {
            AudioQualityLevel.Lossless => 999000,
            AudioQualityLevel.High320K => 320000,
            AudioQualityLevel.Below320K => 192000,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unsupported quality tier.")
        };
    }

    private static AudioQualityLevel? TierForLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "lossless" or "flac" or "999" => AudioQualityLevel.Lossless,
        "exhigh" or "320" or "740" => AudioQualityLevel.High320K,
        "standard" or "256" or "192" or "128" => AudioQualityLevel.Below320K,
        _ => null
    };

    private static AudioQualityLevel CandidateTier(SourceDownloadInfo candidate)
        => candidate.IsLossless ? AudioQualityLevel.Lossless
            : candidate.BitrateKbps is >= 320 ? AudioQualityLevel.High320K : AudioQualityLevel.Below320K;

    private static bool IsLosslessContainer(string container)
        => container is "flac" or "wav" or "ape" or "alac" or "aiff" or "aif" or "wv";

    private string OfficialEndpointFingerprint()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlayerUrlBase())));

    private bool TryGetOfficialRequestBitrate(string providerTrackId, string? resourceId, out int requestedBitrate)
    {
        requestedBitrate = 0;
        var prefix = $"netease:{providerTrackId}:official:";
        if (resourceId is null || !resourceId.StartsWith(prefix, StringComparison.Ordinal)
            || !resourceId.EndsWith($":{OfficialEndpointFingerprint()}", StringComparison.Ordinal)) return false;
        var suffix = resourceId[prefix.Length..];
        var separator = suffix.IndexOf(':');
        return separator > 0 && int.TryParse(suffix.AsSpan(0, separator), out requestedBitrate)
            && requestedBitrate is > 0 and <= 10000000;
    }

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>
    /// 轻量预判：调 enhance/player/url 检查官方 API 是否返回有效下载直链。
    /// 不做 VIP 回退探测（第三方端点开销大），仅检查官方接口。
    /// fee!=0 的 VIP 曲目官方 API 通常返回空 url → 判定不可下载（除非 VIP 回退开启）。
    /// 超时/异常时返回 true（保守放行）。
    /// </summary>
    public async Task<bool> CanDownloadAsync(string providerTrackId, CancellationToken cancellationToken)
    {
        try
        {
            var ids = JsonSerializer.Serialize(new[] { long.Parse(providerTrackId) });
            var url = $"{PlayerUrlBase()}?ids={Uri.EscapeDataString(ids)}&br={options.Value.Netease.Bitrate}";
            using var doc = await GetJsonAsync(url, cancellationToken);
            if (doc is null || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return true; // 响应异常，保守放行
            var downloadUrl = GetString(data[0], "url");
            // VIP 回退开启时，官方返回空 url 不代表真的不可下载——第三方端点可能解锁，保守放行；
            // 回退关闭时官方空 url 即判定不可下载。
            if (string.IsNullOrWhiteSpace(downloadUrl))
                return options.Value.Netease.VipFallback.Enabled;
            return true;
        }
        catch
        {
            return true; // 超时/异常时保守放行
        }
    }

    /// <summary>平台内只保留候选编排，端点协议由通用解析器执行。</summary>
    private async Task<SourceDownloadInfo?> ResolveFallbackAsync(
        string providerTrackId, NeteaseVipFallbackEndpoint endpoint, int endpointIndex, string level,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var resolver = audioUrlResolver ?? new HttpAudioUrlResolver(httpClientFactory);
        var request = new AudioUrlResolveRequest(Provider, providerTrackId, level,
            GetResolverId(endpoint, endpointIndex), endpoint, timeoutSeconds,
            Diagnostic: diagnostic => Report(diagnostic.Stage, diagnostic.Code, diagnostic.HttpStatus));
        try
        {
            var candidate = await resolver.ResolveAsync(request, cancellationToken);
            Report("fallback", candidate is null ? "empty_or_unavailable" : "candidate", count: candidate is null ? 0 : 1);
            if (candidate is null) return null;
            return candidate with
            {
                ResolverId = request.ResolverId,
                ResourceId = GetFallbackResourceId(providerTrackId, endpoint, level)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Report("fallback", exception is OperationCanceledException ? "timeout" : exception.GetType().Name);
            logger.LogWarning("Netease fallback failed: {ErrorType}", exception.GetType().Name);
            return null;
        }
    }

    private static string GetResolverId(NeteaseVipFallbackEndpoint endpoint, int endpointIndex)
        => string.IsNullOrWhiteSpace(endpoint.Id) ? $"netease:fallback:{endpointIndex}:{endpoint.Type}" : $"netease:fallback:{endpoint.Id}";

    private static string GetFallbackResourceId(string trackId, NeteaseVipFallbackEndpoint endpoint, string level)
    {
        // Include a one-way configuration fingerprint so a same-ID endpoint edit cannot refresh into another resource.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(endpoint))));
        return $"netease:{trackId}:fallback:{Uri.EscapeDataString(level)}:{fingerprint}";
    }

    /// <summary>解析搜索结果条目（artists/album/duration 全字段）。</summary>
    private SourceSearchResult? ParseTrackFromSearch(JsonElement song)
    {
        var id = GetNumberAsString(song, "id");
        var title = GetString(song, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var artists = new List<string>();
        if (song.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistArray.EnumerateArray())
            {
                var name = GetString(artist, "name");
                if (!string.IsNullOrWhiteSpace(name)) artists.Add(name.Trim());
            }
        }

        string? albumName = null;
        string? coverUrl = null;
        if (song.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = GetString(album, "name");
            coverUrl = GetString(album, "picUrl");
        }

        var durationMs = GetLong(song, "duration");
        var fee = GetInt(song, "fee") ?? 0;
        return new SourceSearchResult(
            Provider,
            id,
            title.Trim(),
            artists,
            albumName,
            durationMs,
            Genres: [],
            coverUrl,
            SourcePageUrl: $"https://music.163.com/song?id={id}",
            License: DescribeFee(fee),
            Quality: SourceQualities.High,
            IsLosslessAvailable: false,
            ReleaseYear: SourceReleaseYearParser.FromJson(album, "publishTime", "releaseDate")
                ?? SourceReleaseYearParser.FromJson(song, "publishTime", "releaseDate"));
    }

    /// <summary>解析详情条目（缩写字段 ar/al/dt，封面 picUrl 更完整）。</summary>
    private SourceSearchResult? ParseTrackFromDetail(JsonElement song)
    {
        var id = GetNumberAsString(song, "id");
        var title = GetString(song, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var artists = new List<string>();
        if (song.TryGetProperty("ar", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistArray.EnumerateArray())
            {
                var name = GetString(artist, "name");
                if (!string.IsNullOrWhiteSpace(name)) artists.Add(name.Trim());
            }
        }

        string? albumName = null;
        string? coverUrl = null;
        if (song.TryGetProperty("al", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = GetString(album, "name");
            coverUrl = GetString(album, "picUrl");
        }

        var durationMs = GetLong(song, "dt");
        var fee = GetInt(song, "fee") ?? 0;
        return new SourceSearchResult(
            Provider,
            id,
            title.Trim(),
            artists,
            albumName,
            durationMs,
            Genres: [],
            coverUrl,
            SourcePageUrl: $"https://music.163.com/song?id={id}",
            License: DescribeFee(fee),
            Quality: SourceQualities.High,
            IsLosslessAvailable: false,
            ReleaseYear: SourceReleaseYearParser.FromJson(album, "publishTime", "releaseDate")
                ?? SourceReleaseYearParser.FromJson(song, "publishTime", "releaseDate"));
    }

    /// <summary>GET JSON 并解析；网络/格式异常返回 null（搜索场景容忍失败，由编排层聚合多源结果）。</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, cancellationToken);
            Report("official_http", response.IsSuccessStatusCode ? "http_ok" : "http_error", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Netease request failed: {StatusCode}", (int)response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report("official_http", "cancelled_or_budget_timeout");
            throw;
        }
        catch (Exception exception)
        {
            Report("official_http", exception is OperationCanceledException ? "timeout" : exception.GetType().Name);
            logger.LogWarning("Netease request error: {ErrorType}", exception.GetType().Name);
            return null;
        }
    }

    private string SearchBase() => options.Value.Netease.SearchBaseUrl.TrimEnd('/');
    private string DetailBase() => options.Value.Netease.DetailBaseUrl.TrimEnd('/');
    private string PlayerUrlBase() => options.Value.Netease.PlayerUrlBaseUrl.TrimEnd('/');

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>
    /// 把网易云的 fee 字段翻译成可读的版权说明（仅用于展示与失败文案，不参与「是否下载」的判定）。
    /// 常见取值：0 免费；1 会员；4 数字专辑需购买；8 低音质免费、高音质需会员。
    /// 注意 fee=8 并非 VIP 限制，直接写成「VIP」会误导。
    /// </summary>
    private static string? DescribeFee(int fee) => fee switch
    {
        0 => null,
        1 => "网易云会员专享（fee=1）",
        4 => "网易云数字专辑，需购买（fee=4）",
        8 => "低音质免费、高音质需会员（fee=8）",
        _ => $"网易云版权受限（fee={fee}）",
    };

    /// <summary>数字或字符串形态的 Id 统一转字符串。</summary>
    private static string? GetNumberAsString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }
}
