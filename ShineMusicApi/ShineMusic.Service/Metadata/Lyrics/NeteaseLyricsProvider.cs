using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;

namespace ShineMusic.Service.Lyrics;

/// <summary>Anonymous original-platform lookup; trusted platform + ID must come from the import source.</summary>
public sealed class NeteaseLyricsProvider(IHttpClientFactory httpClientFactory, IOptions<LyricsOptions> options) : IExternalLyricsProvider
{
    public const string SourceName = "netease";
    public string Source => SourceName;

    public static bool CanFetch(LyricsQuery query) =>
        string.Equals(query.SourceProvider, SourceName, StringComparison.OrdinalIgnoreCase) &&
        long.TryParse(query.SourceTrackId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;

    public async Task<LyricsMatch?> FetchAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanFetch(query)) return null;
        var id = query.SourceTrackId!;
        var client = httpClientFactory.CreateClient(LyricsResolver.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://music.163.com/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1");
        request.Headers.TryAddWithoutValidation("User-Agent", options.Value.UserAgent);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LyricsValidation.Reject(query, Source, id, $"source_unavailable_http_{(int)response.StatusCode}");
            return null;
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var codeValue) || codeValue != 200)
        {
            LyricsValidation.Reject(query, Source, id, "source_unavailable_api");
            return null;
        }
        foreach (var flag in new[] { "nolyric", "uncollected" })
            if (root.TryGetProperty(flag, out var value) && value.ValueKind == JsonValueKind.True)
            {
                LyricsValidation.Reject(query, Source, id, flag == "nolyric" ? "instrumental" : "no_lyrics");
                return null;
            }
        var content = root.TryGetProperty("lrc", out var lrc) && lrc.ValueKind == JsonValueKind.Object &&
            lrc.TryGetProperty("lyric", out var lyric) && lyric.ValueKind == JsonValueKind.String
            ? LyricsContent.Normalize(lyric.GetString()) : null;
        if (!LyricsValidation.ValidateContent(query, Source, id, content)) return null;
        return new LyricsMatch(content!, Source, 1, id);
    }
}
