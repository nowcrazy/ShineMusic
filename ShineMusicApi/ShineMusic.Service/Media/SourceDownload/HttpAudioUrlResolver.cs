using System.Globalization;
using System.Text;
using System.Text.Json;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>有限 HTTP/JSON 音频解析适配器，兼容现有网易端点配置。</summary>
public sealed class HttpAudioUrlResolver(IHttpClientFactory httpClientFactory) : IAudioUrlResolver
{
    /// <summary>独立命名客户端，不继承某音乐平台的 Cookie/Authorization。</summary>
    public const string HttpClientName = "audio-url-resolver";

    /// <summary>来自解析服务的音频链接使用的无平台凭据客户端。</summary>
    public const string DownloadHttpClientName = "audio-url-download";

    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Authorization", "Cookie", "Origin", "Referer", "User-Agent"
    };

    public async Task<SourceDownloadInfo?> ResolveAsync(AudioUrlResolveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Endpoint.Enabled) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 60)));
        try
        {
            var endpoint = request.Endpoint;
            var type = endpoint.Type.ToLowerInvariant();
            if (!string.Equals(request.HttpClientName, HttpClientName, StringComparison.Ordinal))
            {
                Report(request, "configuration", "invalid_client");
                return null;
            }
            var client = httpClientFactory.CreateClient(HttpClientName);
            JsonDocument? document;
            string urlPath;
            if (type == VipFallbackEndpointTypes.NextMusic)
            {
                document = await ResolveNextMusicAsync(client, request, timeout.Token);
                urlPath = "data.url";
            }
            else
            {
                var method = type == VipFallbackEndpointTypes.HttpJson ? endpoint.Method.ToUpperInvariant() : "GET";
                if (method is not ("GET" or "POST"))
                {
                    Report(request, "configuration", "invalid_method");
                    return null;
                }
                urlPath = type switch
                {
                    VipFallbackEndpointTypes.GdStudio => "url",
                    VipFallbackEndpointTypes.HaiTangW => "data.url",
                    VipFallbackEndpointTypes.ZMusic => "data[0].url",
                    VipFallbackEndpointTypes.HttpJson => endpoint.ResponseUrlPath,
                    _ => ""
                };
                if (string.IsNullOrWhiteSpace(urlPath)) return null;
                var body = method == "POST"
                    ? endpoint.Body.ToDictionary(pair => pair.Key, pair => Expand(pair.Value, request, false))
                    : null;
                document = await SendAsync(client, Expand(endpoint.Url, request, true), method, body,
                    endpoint.Headers, request, "fallback_http", timeout.Token);
            }

            using (document)
            {
                if (document is null || !TryReadPath(document.RootElement, urlPath, out var urlValue)
                    || urlValue.ValueKind != JsonValueKind.String || !IsHttpUrl(urlValue.GetString()))
                {
                    Report(request, "resolution", "empty_or_invalid_url");
                    return null;
                }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in endpoint.DownloadHeaders)
                {
                    if (!IsAllowedHeader(key, value) || value.Length > 4096 || key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        Report(request, "configuration", "invalid_header");
                        return null;
                    }
                    headers[key] = value;
                }
                var (bitrate, lossless, container) = InferQuality(request.Level);
                var declaredContainer = ReadString(document.RootElement, endpoint.ResponseContainerPath);
                if (!string.IsNullOrWhiteSpace(declaredContainer) && declaredContainer.Length <= 16
                    && declaredContainer.All(char.IsAsciiLetterOrDigit)) container = declaredContainer.ToLowerInvariant();
                var declaredBitrate = ReadInt(document.RootElement, endpoint.ResponseBitrateKbpsPath);
                if (declaredBitrate is > 0 and <= 100000) bitrate = declaredBitrate.Value;
                Report(request, "resolution", "candidate");
                return new SourceDownloadInfo(urlValue.GetString()!, container, lossless, bitrate,
                    lossless ? SourceQualities.Lossless : bitrate >= 256 ? SourceQualities.High : SourceQualities.Standard)
                {
                    ResolverId = request.ResolverId,
                    ResourceId = $"{request.Provider}:{request.ProviderTrackId}:{request.Level}",
                    ExpiresAtUtc = ReadExpiry(document.RootElement, endpoint.ResponseExpiresAtPath),
                    RequestHeaders = headers,
                    UseIsolatedDownloadClient = true
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            Report(request, "resolution", "timeout");
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or ArgumentException or InvalidOperationException or FormatException)
        {
            Report(request, "resolution", exception is JsonException ? "invalid_json" : "request_failed");
            return null;
        }
    }

    private static async Task<JsonDocument?> ResolveNextMusicAsync(HttpClient client, AudioUrlResolveRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = request.Endpoint.BaseUrl?.TrimEnd('/');
        if (!IsHttpUrl(baseUrl)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Origin"] = "https://wyapi.toubiec.cn", ["Referer"] = "https://wyapi.toubiec.cn/"
        };
        foreach (var (key, value) in request.Endpoint.Headers) headers[key] = value;
        using var ipDocument = await SendAsync(client, $"{baseUrl}/api/ip", "POST",
            new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, headers, request,
            "fallback_ip_http", cancellationToken);
        var ip = ipDocument is null ? null : ReadString(ipDocument.RootElement, "data.ip");
        if (string.IsNullOrWhiteSpace(ip)) return null;
        return await SendAsync(client, $"{baseUrl}/api/getSongUrl", "POST",
            new { id = request.ProviderTrackId, level = request.Level, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ip },
            headers, request, "fallback_song_http", cancellationToken);
    }

    private static async Task<JsonDocument?> SendAsync(HttpClient client, string url, string method, object? body,
        IReadOnlyDictionary<string, string> headers, AudioUrlResolveRequest request, string stage, CancellationToken cancellationToken)
    {
        if (!IsHttpUrl(url))
        {
            Report(request, "configuration", "invalid_url");
            return null;
        }
        using var message = new HttpRequestMessage(method == "POST" ? HttpMethod.Post : HttpMethod.Get, url);
        foreach (var (key, value) in headers)
        {
            if (!IsAllowedHeader(key, value))
            {
                Report(request, "configuration", "invalid_header");
                return null;
            }
            message.Headers.TryAddWithoutValidation(key, value);
        }
        if (body is not null) message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Report(request, stage, response.IsSuccessStatusCode ? "http_ok" : "http_error", (int)response.StatusCode);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            Report(request, stage, "response_too_large");
            return null;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > MaximumResponseBytes)
            {
                Report(request, stage, "response_too_large");
                return null;
            }
            output.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(output.ToArray());
    }

    private static bool IsAllowedHeader(string name, string value) => AllowedHeaders.Contains(name)
        && value is not null && value.Length <= 8192 && !value.Contains('\r') && !value.Contains('\n');

    private static bool IsHttpUrl(string? value) => PublicAudioHttpTransport.IsAllowedUri(value);

    private static string Expand(string? value, AudioUrlResolveRequest request, bool escape)
    {
        string Encode(string item) => escape ? Uri.EscapeDataString(item) : item;
        return (value ?? "").Replace("{id}", Encode(request.ProviderTrackId), StringComparison.Ordinal)
            .Replace("{level}", Encode(request.Level), StringComparison.Ordinal)
            .Replace("{provider}", Encode(request.Provider), StringComparison.Ordinal);
    }

    private static bool TryReadPath(JsonElement root, string? path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 256) return false;
        foreach (var segment in path.Split('.'))
        {
            if (segment.Length == 0) return false;
            var bracket = segment.IndexOf('[');
            var property = bracket < 0 ? segment : segment[..bracket];
            if (property.Length > 0 && (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out value))) return false;
            var position = bracket;
            while (position >= 0)
            {
                var end = segment.IndexOf(']', position + 1);
                if (end < 0 || !int.TryParse(segment.AsSpan(position + 1, end - position - 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index) || value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength()) return false;
                value = value[index];
                position = end + 1;
                if (position == segment.Length) break;
                if (segment[position] != '[') return false;
            }
        }
        return true;
    }

    private static string? ReadString(JsonElement root, string? path) => TryReadPath(root, path, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement root, string? path) => TryReadPath(root, path, out var value)
        && (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            || value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) ? number : null;

    private static DateTimeOffset? ReadExpiry(JsonElement root, string? path)
    {
        if (!TryReadPath(root, path, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
            && seconds is >= 0 and <= 253402300799) return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires) ? expires : null;
    }

    private static (int BitrateKbps, bool IsLossless, string Container) InferQuality(string level) => level.ToLowerInvariant() switch
    {
        "lossless" or "flac" or "999" => (900, true, "flac"),
        "exhigh" or "320" or "740" => (320, false, "mp3"),
        "256" => (256, false, "mp3"),
        "standard" or "192" => (192, false, "mp3"),
        _ => (128, false, "mp3")
    };

    private static void Report(AudioUrlResolveRequest request, string stage, string code, int? status = null)
        => request.Diagnostic?.Invoke(new AudioUrlResolveDiagnostic(stage, code, status));
}
