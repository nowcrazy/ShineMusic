using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.Lyrics;

if (args.Length < 2) throw new ArgumentException("Usage: input.json output-directory [--downloads-only]");
var downloadsOnly = args.Contains("--downloads-only");
var skipDownloads = args.Contains("--skip-downloads");
if (downloadsOnly && skipDownloads) throw new ArgumentException("Conflicting modes.");
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using var document = JsonDocument.Parse(await File.ReadAllTextAsync(input));
var tracks = document.RootElement.GetProperty("results")[0].GetProperty("orderedTracks").Deserialize<ParsedPlaylistTrack[]>()!;
var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "ShineMusicApi/ShineMusic.Api/appsettings.json")).Build();
var services = new ServiceCollection();
services.AddLogging(b => b.ClearProviders());
services.AddSingleton<IOptions<SourceDownloadOptions>>(Options.Create(config.GetSection("SourceDownload").Get<SourceDownloadOptions>() ?? new()));
services.AddSingleton<IOptions<LyricsOptions>>(Options.Create(config.GetSection("Lyrics").Get<LyricsOptions>() ?? new()));
services.AddSingleton<IMusicSourceProvider, NeteaseSourceProvider>();
services.AddShineMusicMetadataModule();
services.AddSingleton<ArtworkFetcher>();
using var provider = services.BuildServiceProvider();
var source = provider.GetServices<IMusicSourceProvider>().Single(p => p.Provider == "netease");
var lyrics = provider.GetRequiredService<ILyricsResolver>();
var artwork = provider.GetRequiredService<ArtworkFetcher>();
var json = new JsonSerializerOptions { WriteIndented = true };
var results = new ConcurrentDictionary<int, object>();
var started = DateTimeOffset.UtcNow;
if (!downloadsOnly)
{
await Parallel.ForEachAsync(Enumerable.Range(0, tracks.Length), new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (index, token) =>
{
    var track = tracks[index];
    var timer = Stopwatch.StartNew();
    object metadata;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var detail = await source.GetTrackAsync(track.SourceTrackId!, timeout.Token);
        var matched = detail is not null && TrackIdentityMatcher.Evaluate(new(track.Title, track.Artists, track.DurationMs, track.AlbumTitle), new(detail.Title, detail.Artists, detail.DurationMs, detail.AlbumTitle)).Verdict == IdentityVerdict.Matched;
        metadata = new { status = detail is null ? "empty" : matched ? "matched" : "mismatch", detail = detail is null ? null : new { detail.Title, detail.Artists, detail.AlbumTitle, detail.DurationMs, detail.ReleaseYear, detail.Genres, hasCover = !string.IsNullOrWhiteSpace(detail.CoverUrl) } };
    }
    catch (Exception ex) { metadata = new { status = Error(ex) }; }
    object lyric;
    var query = new LyricsQuery(track.Title, track.Artists, track.AlbumTitle, track.DurationMs, null, null, "netease", track.SourceTrackId);
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var match = await lyrics.ResolveAsync(query, timeout.Token);
        var stamps = match is null ? [] : Regex.Matches(match.Content, @"\[(\d{1,3}):(\d{2}(?:\.\d+)?)\]").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).ToArray();
        var timelineValid = match is not null && LyricsValidation.ValidateContent(query, match.Source, match.ProviderId, match.Content);
        lyric = new { status = match is null ? "unresolved" : "validated_not_listening_verified", match?.Source, match?.Confidence, match?.ProviderId, characters = match?.Content.Length ?? 0, timestampCount = stamps.Length, lastTimestampSeconds = stamps.Length == 0 ? (double?)null : stamps.Max(), timelineValid, diagnostics = query.Diagnostics.Distinct().ToArray() };
    }
    catch (Exception ex) { lyric = new { status = Error(ex), diagnostics = query.Diagnostics.Distinct().ToArray() }; }
    var row = new { index = index + 1, track, metadata, lyric, elapsedMs = timer.ElapsedMilliseconds };
    results[index] = row;
    await File.WriteAllTextAsync(Path.Combine(output, $"track-{index + 1:D3}.json"), JsonSerializer.Serialize(row, json), token);
    Console.WriteLine($"TRACK {index + 1}/{tracks.Length} done");
});
await File.WriteAllTextAsync(Path.Combine(output, "all-tracks.json"), JsonSerializer.Serialize(new { started, completed = DateTimeOffset.UtcNow, results = results.OrderBy(p => p.Key).Select(p => p.Value) }, json));
}
var samples = Enumerable.Range(0, Math.Min(12, tracks.Length)).Select(i => (int)Math.Round(i * (tracks.Length - 1d) / Math.Max(1, Math.Min(12, tracks.Length) - 1))).Distinct().ToArray();
var enrichments = new List<object>();
if (!downloadsOnly)
{
foreach (var index in samples)
{
    var track = tracks[index];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    ItunesMetadataResult result;
    try { result = await artwork.FetchMatchedAsync(track.Title, track.Artists, track.AlbumTitle, track.DurationMs, "covers", Path.Combine(output, "covers"), timeout.Token); }
    catch (Exception ex) { result = new(false, Error: Error(ex)); }
    enrichments.Add(new { index = index + 1, track.SourceTrackId, result });
    Console.WriteLine($"ENRICH {index + 1}: {result.Success} {result.Error}");
    await Task.Delay(3100);
}
await File.WriteAllTextAsync(Path.Combine(output, "enrichment.json"), JsonSerializer.Serialize(enrichments, json));
}
if (!skipDownloads)
{
foreach (var concurrency in new[] { 1, 3 })
    await DownloadBatch(samples, concurrency, $"downloads-c{concurrency}.json");
var extra = Array.FindIndex(tracks, t => t.Title == "九张机");
if (extra >= 0 && !samples.Contains(extra)) await DownloadBatch([extra], 1, "downloads-九张机.json");
}
Console.WriteLine("COMPLETE");

async Task DownloadBatch(int[] indices, int concurrency, string reportName)
{
var downloads = new ConcurrentBag<object>();
var active = 0;
var peak = 0;
var downloadTimer = Stopwatch.StartNew();
await Parallel.ForEachAsync(indices, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (index, token) =>
{
    var track = tracks[index];
    var taskStart = DateTimeOffset.UtcNow;
    var diagnostics = new List<object>();
    var stage = "resolve";
    var timer = Stopwatch.StartNew();
    var attempts = new List<object>();
    var status = "no_valid_audio";
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    try
    {
        var candidates = await ((NeteaseSourceProvider)source).ResolveDownloadCandidatesWithDiagnosticsAsync(track.SourceTrackId!, d => diagnostics.Add(new { at = DateTimeOffset.UtcNow, diagnostic = d }), timeout.Token);
        foreach (var candidate in candidates.Take(3))
        {
            var path = Path.Combine(output, $"audio-{Guid.NewGuid():N}.bin");
            DateTimeOffset? transferStart = null, transferEnd = null;
            int? httpStatus = null;
            try
            {
                stage = "http_headers";
                using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("netease");
                using var response = await client.GetAsync(candidate.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                httpStatus = (int)response.StatusCode;
                response.EnsureSuccessStatusCode();
                await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var file = File.Create(path))
                {
                    stage = "transfer";
                    lock (downloads) { transferStart = DateTimeOffset.UtcNow; peak = Math.Max(peak, ++active); }
                    try
                    {
                    var buffer = new byte[81920]; long total = 0; int read;
                    while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        total += read;
                        if (total > 100 * 1024 * 1024) throw new IOException("size_limit");
                        await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    }
                    }
                    finally { lock (downloads) { transferEnd = DateTimeOffset.UtcNow; --active; } }
                }
                stage = "ffprobe";
                var start = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
                var raw = await stdout; await stderr;
                if (process.ExitCode != 0) throw new IOException("invalid_audio");
                using var probe = JsonDocument.Parse(raw);
                var audio = probe.RootElement.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "audio");
                var format = probe.RootElement.GetProperty("format");
                double Number(JsonElement el, string key) => el.TryGetProperty(key, out var value) && double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;
                var seconds = Number(format, "duration");
                var delta = track.DurationMs is > 0 ? Math.Abs(seconds - track.DurationMs.Value / 1000d) : (double?)null;
                var codec = audio.GetProperty("codec_name").GetString();
                var bitrateKbps = Number(format, "bit_rate") / 1000;
                var sampleRate = Number(audio, "sample_rate");
                stage = "full_decode";
                await Decode(path, timeout.Token);
                var valid = seconds > 0 && delta is <= 5 && bitrateKbps > 0 && sampleRate > 0 && !string.IsNullOrWhiteSpace(codec);
                attempts.Add(new { status = valid ? "validated" : "probe_checks_failed", candidate.Quality, codec, seconds, delta, bitrateKbps, sampleRate, bytes = new FileInfo(path).Length, fullDecode = true, transferStart, transferEnd, httpStatus });
                if (valid) { status = "validated_identity_not_fingerprinted"; break; }
            }
            catch (Exception ex) { attempts.Add(new { status = Error(ex), failureStage = stage, transferStart, transferEnd, httpStatus }); }
            finally { if (File.Exists(path)) File.Delete(path); }
            if (timeout.IsCancellationRequested) break;
        }
    }
    catch (Exception ex) { status = Error(ex); diagnostics.Add(new { failureStage = stage, code = status }); }
    downloads.Add(new { index = index + 1, track.SourceTrackId, track.Title, status, taskStart, taskEnd = DateTimeOffset.UtcNow, elapsedMs = timer.ElapsedMilliseconds, diagnostics, attempts });
    Console.WriteLine($"DOWNLOAD {index + 1}: {status}");
});
await File.WriteAllTextAsync(Path.Combine(output, reportName), JsonSerializer.Serialize(new { concurrencyLimit = concurrency, peakActiveTransfers = peak, transferDefinition = "body stream read/write interval; excludes headers, resolution and decode", elapsedMs = downloadTimer.ElapsedMilliseconds, sampleIndices = indices.Select(i => i + 1), results = downloads }, json));
}
static async Task Decode(string path, CancellationToken token)
{
    var start = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in new[] { "-nostdin", "-v", "error", "-xerror", "-i", path, "-map", "0:a:0", "-f", "null", "-" }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync(token);
    var stderr = process.StandardError.ReadToEndAsync(token);
    try { await process.WaitForExitAsync(token); } catch { if (!process.HasExited) process.Kill(true); throw; }
    await stdout;
    var errors = await stderr;
    if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(errors)) throw new IOException("full_decode_failed");
}
static string Error(Exception ex) => ex is OperationCanceledException ? "timeout_or_cancelled" : ex.GetType().Name;
