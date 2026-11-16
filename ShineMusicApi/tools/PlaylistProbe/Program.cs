using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service.PlaylistImport;

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var services = new ServiceCollection();
services.AddLogging(b => b.ClearProviders());
services.AddSingleton<IOptions<SourceDownloadOptions>>(Options.Create(new SourceDownloadOptions()));
services.AddShineMusicMetadataModule();
foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IPlaylistParser)
    && d.ImplementationType != typeof(NeteasePlaylistParser) && d.ImplementationType != typeof(QQMusicPlaylistParser)).ToArray())
    services.Remove(descriptor);
var evidence = new List<object>();
foreach (var name in new[] { "netease", "qqmusic" })
    services.AddHttpClient(name).AddHttpMessageHandler(() => new EvidenceHandler(evidence));
using var provider = services.BuildServiceProvider();
var parsers = provider.GetServices<IPlaylistParser>().Where(p => p.Platform is "netease" or "qqmusic").ToArray();
var urls = args.Length > 1 ? args.Skip(1).ToList()
    : new List<string> { "https://music.163.com/playlist?id=19723756", "https://music.163.com/playlist?id=3778678" };
if (args.Length == 1)
{
    using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("qqmusic");
    foreach (var discovery in new[] { "https://c.y.qq.com/splcloud/fcgi-bin/fcg_get_diss_by_tag.fcg?format=json&categoryId=10000000&sortId=5&sin=0&ein=1", "https://y.qq.com/", "https://c.y.qq.com/musichall/fcgi-bin/fcg_yqqhomepagerecommend.fcg?format=json" })
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var text = await client.GetStringAsync(discovery, timeout.Token);
            var match = Regex.Match(text, "(?:playlist/|\"dissid\"\\s*:\\s*\"?)([0-9]{5,})");
            if (match.Success) { urls.Add("https://y.qq.com/n/ryqq/playlist/" + match.Groups[1].Value); break; }
        }
        catch (Exception ex) { evidence.Add(new { discovery, error = SafeError(ex) }); }
    }
}
var results = new List<object>();
foreach (var url in urls)
{
    var parser = parsers.First(p => p.CanHandle(url));
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var playlist = await parser.ParseAsync(url, timeout.Token);
        results.Add(new { url, parser.Platform, status = "complete", playlist.ExpectedTrackCount, parsedCount = playlist.Tracks.Count,
            missingTitle = playlist.Tracks.Count(t => string.IsNullOrWhiteSpace(t.Title)),
            missingArtist = playlist.Tracks.Count(t => t.Artists.Count == 0),
            missingSourceId = playlist.Tracks.Count(t => string.IsNullOrWhiteSpace(t.SourceTrackId)),
            duplicates = playlist.Tracks.GroupBy(t => t.SourceTrackId).Sum(g => g.Count() - 1),
            orderedTracks = playlist.Tracks });
    }
    catch (Exception ex) { results.Add(new { url, parser.Platform, status = "failed", error = SafeError(ex) }); }
    Console.WriteLine(JsonSerializer.Serialize(results.Last()));
}
if (args.Length == 1 && !urls.Any(u => u.Contains("y.qq.com"))) results.Add(new { platform = "qqmusic", status = "discovery_failed", error = "No public playlist ID found; parser not exercised." });
await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, results, evidence }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(Path.Combine(output, "report.json"));
static string SafeError(Exception ex) => ex is PlaylistParseException ? ex.Message : ex is OperationCanceledException ? "timeout_or_cancelled" : ex.GetType().Name;

sealed class EvidenceHandler(List<object> evidence) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var response = await base.SendAsync(request, token);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        object? counts = null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            if (root.TryGetProperty("playlist", out var playlist))
                counts = new { expected = playlist.TryGetProperty("trackCount", out var n) ? n.GetInt32() : (int?)null,
                    orderedIds = playlist.TryGetProperty("trackIds", out var ids) ? ids.Clone() : (JsonElement?)null };
            else if (root.TryGetProperty("cdlist", out var lists) && lists.GetArrayLength() > 0)
            {
                var list = lists[0];
                counts = new { expected = list.TryGetProperty("songnum", out var qqCount) ? qqCount.Clone() : (JsonElement?)null,
                    current = list.TryGetProperty("cur_song_num", out var c) ? c.Clone() : (JsonElement?)null,
                    orderedIds = list.TryGetProperty("songlist", out var songs) ? songs.EnumerateArray().Select(s =>
                        s.TryGetProperty("songmid", out var mid) || s.TryGetProperty("mid", out mid) ? mid.GetString() : null).ToArray() : null };
            }
        }
        catch (JsonException) { }
        evidence.Add(new { endpoint = request.RequestUri!.GetLeftPart(UriPartial.Path), requestLength = request.RequestUri.AbsoluteUri.Length,
            httpStatus = (int)response.StatusCode, bytes = bytes.Length, counts });
        return response;
    }
}
