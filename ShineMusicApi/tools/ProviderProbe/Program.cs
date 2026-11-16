using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service.SourceDownload;

var repo = Directory.GetCurrentDirectory();
var apiRoot = Path.Combine(repo, "ShineMusicApi", "ShineMusic.Api");
var output = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Specify a scratch output directory.");
Directory.CreateDirectory(output);
var filter = args.Length > 1 ? args[1].Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = apiRoot });
builder.Configuration.AddJsonFile(Path.Combine(apiRoot, "appsettings.json"), false)
    .AddJsonFile(Path.Combine(apiRoot, "appsettings.Development.json"), true)
    .AddUserSecrets(typeof(ModuleRegistrations).Assembly, optional: true).AddEnvironmentVariables();
builder.Logging.ClearProviders();
var options = builder.Configuration.GetSection("SourceDownload").Get<SourceDownloadOptions>() ?? new();
var originalEnabled = new Dictionary<string, bool>();
foreach (var property in typeof(SourceDownloadOptions).GetProperties())
{
    var section = property.GetValue(options);
    var enabled = section?.GetType().GetProperty("Enabled");
    if (enabled?.PropertyType == typeof(bool) && section is not null)
    {
        originalEnabled[property.Name] = (bool)enabled.GetValue(section)!;
        enabled.SetValue(section, true);
    }
}
options.YtDlp.ExecutablePath = Path.Combine(repo, "ShineMusicApi", "tools", "yt-dlp.exe");
options.YtDlp.CookiesFile = Path.Combine(repo, "ShineMusicApi", "tools", "youtube-cookies.txt");
options.YtDlp.SearchTimeoutSeconds = 35;
options.YtDlp.MetadataTimeoutSeconds = 30;
builder.Services.AddSingleton<IOptions<SourceDownloadOptions>>(Options.Create(options));
builder.Services.AddShineMusicMetadataModule();
builder.Services.AddShineMusicMediaModule();
using var host = builder.Build(); // Deliberately never Start: no DB, MQ or hosted workers.
var sources = host.Services.GetServices<IMusicSourceProvider>().Where(p => filter is null || filter.Contains(p.Provider)).ToArray();
var report = new ProbeReport(DateTimeOffset.UtcNow, new Dictionary<string, bool>
{
    ["neteaseCookie"] = !string.IsNullOrWhiteSpace(options.Netease.Cookie),
    ["miguCookie"] = !string.IsNullOrWhiteSpace(options.Migu.Cookie),
    ["qqCookie"] = !string.IsNullOrWhiteSpace(options.Qq.Cookie),
    ["kuwoCookie"] = !string.IsNullOrWhiteSpace(options.Kuwo.Cookie),
    ["kugouCookie"] = !string.IsNullOrWhiteSpace(options.Kugou.Cookie),
    ["qishuiCookie"] = !string.IsNullOrWhiteSpace(options.Qishui.Cookie),
    ["jamendoClientId"] = !string.IsNullOrWhiteSpace(options.Jamendo.ClientId),
    ["ytCookiesFile"] = File.Exists(options.YtDlp.CookiesFile),
    ["ytProxyConfigured"] = !string.IsNullOrWhiteSpace(options.YtDlp.Proxy)
}, []);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (source, token) =>
{
    var result = new SourceProbe(source.Provider, []);
    Console.WriteLine($"START {source.Provider}");
    var samples = new[] { new Sample("晴天", "周杰伦", 269000), new Sample("富士山下", "陈奕迅", null), new Sample("月亮代表我的心", "邓丽君", null) };
    if (source.Provider is "audius" or "jamendo")
        samples = [new Sample("晴天", "周杰伦", 269000), new Sample("love", "", null), new Sample("piano", "", null)];
    foreach (var sample in samples)
    {
        var attempt = new SampleProbe(sample.Title + " " + sample.Artist);
        result.Samples.Add(attempt);
        var timer = Stopwatch.StartNew();
        SourceSearchResult[] found = [];
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            found = await source.SearchAsync(attempt.Query, 10, deadline.Token).WaitAsync(deadline.Token);
            attempt.SearchStatus = found.Length > 0 ? "results" : "empty";
            attempt.SearchCount = found.Length;
            attempt.SearchExamples = found.Take(3).Select(Snapshot).ToArray();
        }
        catch (Exception ex) { attempt.SearchStatus = Error(ex); }
        attempt.SearchMs = timer.ElapsedMilliseconds;
        var expected = new TrackIdentity(sample.Title, string.IsNullOrEmpty(sample.Artist) ? [] : [sample.Artist], sample.Duration);
        var selected = found.FirstOrDefault(item => TrackIdentityMatcher.Evaluate(expected,
            new TrackIdentity(item.Title, item.Artists, item.DurationMs)).Verdict == IdentityVerdict.Matched);
        attempt.Matched = selected is not null;
        if (selected is null && source.Provider == "netease" && sample.Title is "晴天" or "富士山下")
            selected = new SourceSearchResult(source.Provider, sample.Title == "晴天" ? "186016" : "65766",
                sample.Title, [sample.Artist], null, sample.Duration, [], null, null, null, SourceQualities.High, false);
        selected ??= found.FirstOrDefault(); // 不匹配的候选只计传输能力，不计目标歌曲命中。
        if (selected is null)
        {
            // Independently test known IDs when search is blocked/empty, so metadata is not inferred from search.
            var known = source.Provider switch
            {
                "netease" when sample.Title == "晴天" => "186016",
                "netease" when sample.Title == "富士山下" => "65766",
                "qq" when sample.Title == "晴天" => "0039MnYb0qxYhV",
                "bilibili" when sample.Title == "晴天" => "BV1xx411c7mD",
                "ytmusic" when sample.Title == "晴天" => "DYptgVvkVLQ",
                _ => null
            };
            if (known is not null)
                selected = new SourceSearchResult(source.Provider, known, sample.Title, [sample.Artist], null, sample.Duration, [], null, null, null, SourceQualities.Standard, false);
            else continue;
        }
        attempt.Selected = Snapshot(selected);
        SourceSearchResult? detail = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            detail = await source.GetTrackAsync(selected.ProviderTrackId, deadline.Token).WaitAsync(deadline.Token);
            attempt.MetadataStatus = detail is null ? "empty" : "returned";
            attempt.Metadata = detail is null ? null : Snapshot(detail);
            attempt.DetailMatches = detail is not null && TrackIdentityMatcher.Evaluate(expected,
                new TrackIdentity(detail.Title, detail.Artists, detail.DurationMs)).Verdict == IdentityVerdict.Matched;
        }
        catch (Exception ex) { attempt.MetadataStatus = Error(ex); }
        // All matched test songs; only one unrelated sample per provider to bound traffic.
        // 每个查询只下载一个候选；未匹配歌曲的结果仅用于验证传输能力，不算目标命中。
        var media = new DownloadProbe();
        attempt.Download = media;
        var dir = Path.Combine(output, source.Provider, result.Samples.Count.ToString());
        Directory.CreateDirectory(dir);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            var paths = new List<string>();
            if (source is ISourceFileDownloader self)
            {
                var file = await self.DownloadToFileAsync(selected.ProviderTrackId, dir, "audio", deadline.Token).WaitAsync(deadline.Token);
                paths.Add(file.FilePath);
                media.DeclaredQuality = file.Quality;
            }
            else
            {
                var candidates = source is IQualityLadderProvider ladder
                    ? await ladder.ResolveDownloadCandidatesAsync(selected.ProviderTrackId, deadline.Token).WaitAsync(deadline.Token)
                    : [await source.ResolveDownloadAsync(selected.ProviderTrackId, deadline.Token).WaitAsync(deadline.Token)];
                media.CandidateCount = candidates.Count;
                foreach (var candidate in candidates.Take(3))
                {
                    try
                    {
                        var path = Path.Combine(dir, "audio-" + paths.Count + ".bin");
                        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(source.Provider);
                        using var response = await client.GetAsync(candidate.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > 262144000) throw new IOException("file_size_limit");
                        await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
                        await using (var file = File.Create(path))
                        {
                            var buffer = new byte[81920];
                            long total = 0;
                            int read;
                            while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
                            {
                                total += read;
                                if (total > 262144000) throw new IOException("file_size_limit");
                                await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                            }
                        }
                        paths.Add(path);
                        media.DeclaredQuality = candidate.Quality;
                        var probe = await Probe(path, deadline.Token);
                        media.Attempts.Add(probe);
                        if (probe.ValidAudio && probe.DurationSeconds >= 30 &&
                            (detail?.DurationMs is not > 0 || probe.DurationSeconds >= detail.DurationMs.Value / 1000d * .75)) break;
                    }
                    catch (Exception ex) when (!deadline.IsCancellationRequested) { media.Errors.Add(Error(ex)); }
                }
            }
            if (source is ISourceFileDownloader)
                foreach (var path in paths) media.Attempts.Add(await Probe(path, deadline.Token));
            media.Status = media.Attempts.Any(p => p.ValidAudio) ? "audio_received" : "no_valid_audio";
        }
        catch (Exception ex) { media.Status = Error(ex); }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(dir))
                try { File.Delete(path); } catch (IOException) { }
        }
        Console.WriteLine($"SAMPLE {source.Provider} {attempt.Query}: search={attempt.SearchStatus}/{attempt.SearchCount} matched={attempt.Matched} metadata={attempt.MetadataStatus} download={media.Status}");
        await Task.Delay(400, token);
    }
    var pathOut = Path.Combine(output, source.Provider + ".json");
    await File.WriteAllTextAsync(pathOut, JsonSerializer.Serialize(result, jsonOptions), token);
    lock (report.Sources) report.Sources.Add(result);
    Console.WriteLine($"DONE {source.Provider}");
});
await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, jsonOptions));
Console.WriteLine("REPORT " + Path.Combine(output, "report.json"));

static object Snapshot(SourceSearchResult item) => new
{
    item.ProviderTrackId, item.Title, item.Artists, item.AlbumTitle, item.DurationMs, item.Genres,
    HasCover = !string.IsNullOrWhiteSpace(item.CoverUrl), HasSourcePage = !string.IsNullOrWhiteSpace(item.SourcePageUrl),
    item.License, item.Quality, item.IsLosslessAvailable, item.ReleaseYear
};
static string Error(Exception ex)
{
    if (ex is OperationCanceledException or TimeoutException) return "timeout";
    var text = Regex.Replace(ex.Message, @"https?://\S+", "[url]");
    return ex.GetType().Name + ": " + text[..Math.Min(text.Length, 240)];
}
static async Task<MediaProbeResult> Probe(string path, CancellationToken token)
{
    var start = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new IOException("ffprobe_start_failed");
    var stdout = process.StandardOutput.ReadToEndAsync(token);
    var stderr = process.StandardError.ReadToEndAsync(token);
    try { await process.WaitForExitAsync(token); }
    catch { if (!process.HasExited) process.Kill(true); throw; }
    var raw = await stdout;
    await stderr;
    if (process.ExitCode != 0) return new(false, null, 0, 0, new FileInfo(path).Length, null, null);
    using var doc = JsonDocument.Parse(raw);
    var audio = doc.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");
    if (audio.ValueKind != JsonValueKind.Object) return new(false, null, 0, 0, new FileInfo(path).Length, null, null);
    var format = doc.RootElement.GetProperty("format");
    double Number(JsonElement el, string key) => el.TryGetProperty(key, out var value) && double.TryParse(value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
    return new(true, audio.GetProperty("codec_name").GetString(), Number(format, "duration"),
        (Number(audio, "bit_rate") is > 0 and var rate ? rate : Number(format, "bit_rate")) / 1000,
        new FileInfo(path).Length, (int)Number(audio, "sample_rate"), (int)Number(audio, "bits_per_raw_sample"));
}
internal sealed record Sample(string Title, string Artist, long? Duration);
internal sealed record ProbeReport(DateTimeOffset StartedAt, Dictionary<string, bool> CredentialsPresent, List<SourceProbe> Sources);
internal sealed record SourceProbe(string Provider, List<SampleProbe> Samples);
internal sealed class SampleProbe(string query)
{
    public string Query { get; } = query;
    public string SearchStatus { get; set; } = "not_tested";
    public int SearchCount { get; set; }
    public long SearchMs { get; set; }
    public object[] SearchExamples { get; set; } = [];
    public bool Matched { get; set; }
    public object? Selected { get; set; }
    public string MetadataStatus { get; set; } = "not_tested";
    public bool DetailMatches { get; set; }
    public object? Metadata { get; set; }
    public DownloadProbe? Download { get; set; }
}
internal sealed class DownloadProbe
{
    public string Status { get; set; } = "not_tested";
    public int CandidateCount { get; set; }
    public string? DeclaredQuality { get; set; }
    public List<MediaProbeResult> Attempts { get; } = [];
    public List<string> Errors { get; } = [];
}
internal sealed record MediaProbeResult(bool ValidAudio, string? Codec, double DurationSeconds, double BitrateKbps,
    long SizeBytes, int? SampleRateHz, int? BitsPerSample);
