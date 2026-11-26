using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Extensions;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;

return await ProbeRunner.RunAsync(args);

internal static class ProbeRunner
{
    private const long MaximumBytes = 64 * 1024 * 1024;
    private const long MaximumTotalBytes = 192 * 1024 * 1024;
    private static readonly AsyncLocal<CaseResult?> CurrentCase = new();
    private static readonly SemaphoreSlim NetworkGate = new(2);
    private static int downloadAttempts;
    private static long downloadedBytes;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--resolve-only")
        {
            Console.Error.WriteLine("Usage: ResolverProbe <outputDir> <ffmpegBinDirectory> [--resolve-only]");
            return 2;
        }
        var resolveOnly = args.Length == 3;
        var output = Path.GetFullPath(args[0]);
        var bin = Path.GetFullPath(args[1]);
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var ffmpeg = Path.Combine(bin, "ffmpeg" + suffix);
        var ffprobe = Path.Combine(bin, "ffprobe" + suffix);
        if (!File.Exists(ffprobe) || !File.Exists(ffmpeg))
        {
            Console.Error.WriteLine("ffmpeg and ffprobe are required in the supplied directory.");
            return 2;
        }
        var repository = FindRepository();
        Directory.CreateDirectory(output);
        var temporary = Directory.CreateTempSubdirectory("shine-resolver-probe-").FullName;
        var report = new ProbeReport(DateTimeOffset.UtcNow) { ResolveOnly = resolveOnly };
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancel.Cancel(); };
        try
        {
            using var high = CreateServices(repository, "high-first", ffmpeg, ffprobe);
            using var lossless = CreateServices(repository, "lossless-first", ffmpeg, ffprobe);
            var samples = new List<Sample>
            {
                new("restricted-reference", "186016", "晴天", ["周杰伦"], 269000),
                new("vip-candidate", "65766", "富士山下", ["陈奕迅"], 258902),
                new("unavailable", "0", "invalid-id", [], null, true)
            };
            var shortSample = resolveOnly ? null : await FindShortSampleAsync(high.GetRequiredService<NeteaseSourceProvider>(), report, cancel.Token);
            if (shortSample is not null) samples.Add(shortSample);
            foreach (var sample in samples)
            {
                var cases = await Task.WhenAll(
                    RunCaseAsync(high, "high-first", sample, temporary, resolveOnly, cancel.Token),
                    RunCaseAsync(lossless, "lossless-first", sample, temporary, resolveOnly, cancel.Token));
                report.Cases.AddRange(cases);
                foreach (var item in cases)
                    Console.WriteLine($"{item.Profile} {item.SampleKind}/{item.TrackId}: {item.Status}; candidates={item.Candidates.Count}, downloads={item.DownloadRequests}");
            }
        }
        catch (Exception exception) { report.FatalError = ErrorCode(exception); }
        finally
        {
            try { Directory.Delete(temporary, recursive: true); report.TemporaryAudioDeleted = true; }
            catch (IOException) { report.TemporaryAudioDeleted = false; }
            catch (UnauthorizedAccessException) { report.TemporaryAudioDeleted = false; }
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            report.DownloadAttempts = Math.Min(Volatile.Read(ref downloadAttempts), 6);
            report.DownloadedBytes = Volatile.Read(ref downloadedBytes);
            report.Cancelled = cancel.IsCancellationRequested;
            report.AcceptancePassed = resolveOnly ? null : !report.Cancelled && report.FatalError is null
                && report.TemporaryAudioDeleted && report.ShortDiscovery == "selected_search_and_detail_match"
                && report.Cases.Count > 0 && report.Cases.All(item => item.Status is "accepted" or "expected_unavailable");
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
        }
        Console.WriteLine($"REPORT {Path.Combine(output, "report.json")}; accepted={report.Cases.Count(item => item.Status == "accepted")}; audioDeleted={report.TemporaryAudioDeleted}");
        if (report.Cancelled) return 130;
        if (report.FatalError is not null || !report.TemporaryAudioDeleted) return 1;
        return resolveOnly || report.AcceptancePassed == true ? 0 : 3;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ShineMusicApi", "ShineMusic.Api", "appsettings.json"))) return directory.FullName;
        throw new InvalidOperationException("Run from the ShineMusic repository or a child directory.");
    }

    private static ServiceProvider CreateServices(string repository, string profile, string ffmpeg, string ffprobe)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(repository, "ShineMusicApi", "ShineMusic.Api", "appsettings.json"), false).Build();
        var options = configuration.GetSection("SourceDownload").Get<SourceDownloadOptions>() ?? new();
        SourceDownloadOptionsBinding.ReplaceConfiguredNeteaseEndpoints(configuration, options);
        options.Netease.Cookie = "";
        options.ProviderFramework.QualityPolicy.PreferredQuality = profile == "lossless-first" ? "Lossless" : "320K";
        options.ProviderFramework.QualityPolicy.MinimumQuality = "320K";
        options.ProviderFramework.QualityPolicy.AllowLossyFallback = false;
        foreach (var endpoint in options.Netease.VipFallback.Endpoints)
        {
            endpoint.Headers.Remove("Cookie");
            endpoint.Headers.Remove("Authorization");
        }
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton<IOptions<SourceDownloadOptions>>(Options.Create(options));
        services.AddSingleton<IOptions<FfmpegOptions>>(Options.Create(new FfmpegOptions { ExecutablePath = ffmpeg, ProbeExecutablePath = ffprobe, TimeoutMinutes = 1 }));
        services.AddShineMusicMetadataModule();
        services.AddShineMusicMediaModule();
        var officialPath = new Uri(options.Netease.PlayerUrlBaseUrl).AbsolutePath;
        foreach (var name in new[] { NeteaseSourceProvider.HttpClientName, HttpAudioUrlResolver.HttpClientName, HttpAudioUrlResolver.DownloadHttpClientName })
            services.AddHttpClient(name).AddHttpMessageHandler(() => new TrafficHandler(name, officialPath));
        services.AddSingleton<IAudioUrlResolver>(provider => new CountingResolver(new HttpAudioUrlResolver(provider.GetRequiredService<IHttpClientFactory>())));
        services.AddSingleton<NeteaseSourceProvider>();
        services.AddSingleton<QualityGate>();
        services.AddSingleton<FfmpegMediaService>();
        return services.BuildServiceProvider();
    }

    private static async Task<Sample?> FindShortSampleAsync(NeteaseSourceProvider provider, ProbeReport report, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var found = await provider.SearchAsync("Intro", 20, budget.Token);
            foreach (var candidate in found.Where(item => item.DurationMs is > 12000 and <= 90000).Take(4))
            {
                var detail = await provider.GetTrackAsync(candidate.ProviderTrackId, budget.Token);
                if (detail is null || !Matches(candidate, detail)) continue;
                report.ShortDiscovery = "selected_search_and_detail_match";
                report.ShortSample = Snapshot(detail);
                return new("short", detail.ProviderTrackId, detail.Title, detail.Artists, detail.DurationMs);
            }
            report.ShortDiscovery = "no_matching_short_track";
        }
        catch (Exception exception) { report.ShortDiscovery = ErrorCode(exception); }
        return null;
    }

    private static async Task<CaseResult> RunCaseAsync(ServiceProvider services, string profile, Sample sample, string temporary,
        bool resolveOnly, CancellationToken cancellationToken)
    {
        var result = new CaseResult(profile, sample.Kind, sample.Id, new { sample.Title, sample.Artists, sample.DurationMs });
        CurrentCase.Value = result;
        var timer = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var provider = services.GetRequiredService<NeteaseSourceProvider>();
            var detail = await provider.GetTrackAsync(sample.Id, budget.Token);
            result.Metadata = detail is null ? null : Snapshot(detail);
            if (sample.Unavailable)
            {
                result.Status = detail is null ? "expected_unavailable" : "unexpected_metadata_for_invalid_id";
                return result;
            }
            if (detail is null) { result.Status = "metadata_unavailable_no_download"; return result; }
            var identity = TrackIdentityMatcher.Evaluate(new(sample.Title, sample.Artists, sample.DurationMs), Identity(detail));
            result.IdentityVerdict = identity.Verdict.ToString();
            result.IdentityReason = identity.ReasonCode;
            if (identity.Verdict != IdentityVerdict.Matched) { result.Status = "metadata_mismatch_no_download"; return result; }
            var gate = services.GetRequiredService<QualityGate>();
            var options = services.GetRequiredService<IOptions<SourceDownloadOptions>>().Value;
            var chain = gate.GetAllowedQualityChain();
            result.PotentialResolutionSteps = chain.Count + options.Netease.VipFallback.Endpoints
                .Where(endpoint => options.Netease.VipFallback.Enabled && endpoint.Enabled)
                .Sum(endpoint => endpoint.Levels.Count(level => Tier(level) is { } tier && chain.Contains(tier)));
            await using var iterator = provider.ResolveDownloadCandidatesLazilyAsync(sample.Id, chain, budget.Token).GetAsyncEnumerator(budget.Token);
            while (result.Candidates.Count < 4 && await iterator.MoveNextAsync())
            {
                var candidate = iterator.Current;
                var attempt = new CandidateResult(SafeId(candidate.ResolverId), candidate.Quality, candidate.Container, candidate.BitrateKbps);
                result.Candidates.Add(attempt);
                if (!gate.IsCandidateAllowed(candidate.IsLossless, candidate.BitrateKbps)) { attempt.Status = "declaration_below_policy"; continue; }
                if (resolveOnly)
                {
                    attempt.Status = "not_downloaded_resolve_only";
                    result.Status = "candidate_available_not_downloaded";
                    break;
                }
                var path = Path.Combine(temporary, Guid.NewGuid().ToString("N") + ".audio");
                try
                {
                    var transfer = await TransferAsync(services.GetRequiredService<IHttpClientFactory>(), candidate, path, budget.Token);
                    attempt.HttpStatus = 200;
                    attempt.SizeBytes = transfer.Size;
                    attempt.Sha256 = transfer.Hash;
                    var media = services.GetRequiredService<FfmpegMediaService>();
                    attempt.Probe = await media.ProbeAsync(path, budget.Token);
                    attempt.DurationMatches = detail.DurationMs is > 0 && attempt.Probe.DurationMs is > 0
                        && Math.Abs(attempt.Probe.DurationMs.Value - detail.DurationMs.Value) <= Math.Max(3000, detail.DurationMs.Value * 0.05);
                    await media.ValidateCompleteAsync(path, budget.Token);
                    attempt.DecodeIntegrity = "passed";
                    attempt.QualityAccepted = gate.IsAcceptableForLibrary(attempt.Probe.Codec, attempt.Probe.Container, attempt.Probe.BitrateKbps);
                    if (!attempt.DurationMatches || !attempt.QualityAccepted) { attempt.Status = "duration_or_quality_rejected"; continue; }
                    attempt.Status = "accepted";
                    result.Status = "accepted";
                    result.EnumerationStoppedOnAccepted = true;
                    break;
                }
                catch (Exception exception)
                {
                    attempt.Status = ErrorCode(exception);
                    if (exception is HttpRequestException http) attempt.HttpStatus = (int?)http.StatusCode;
                    if (attempt.Status is "download_budget_exhausted" or "total_byte_budget_exhausted" || budget.IsCancellationRequested) break;
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
            if (result.Status == "not_started") result.Status = result.Candidates.Count >= 4 ? "candidate_limit" : "no_accepted_candidate";
            if (result.Candidates.Count == 0)
                result.AvailabilitySummary = result.PlatformPrivilege?.State is < 0
                    ? "platform_restricted_and_no_resolver_candidate"
                    : "no_resolver_candidate_observed";
        }
        catch (Exception exception) { result.Status = ErrorCode(exception); }
        finally
        {
            result.ElapsedMs = timer.ElapsedMilliseconds;
            result.UnrequestedPotentialSteps = Math.Max(0, result.PotentialResolutionSteps - result.ResolutionSteps.Count);
            CurrentCase.Value = null;
        }
        return result;
    }

    private static async Task<(long Size, string Hash)> TransferAsync(IHttpClientFactory factory, SourceDownloadInfo candidate, string path, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref downloadAttempts) > 6) throw new ProbeLimitException("download_budget_exhausted");
        if (Volatile.Read(ref downloadedBytes) >= MaximumTotalBytes) throw new ProbeLimitException("total_byte_budget_exhausted");
        if (!PublicAudioHttpTransport.IsAllowedUri(candidate.DownloadUrl)) throw new ProbeLimitException("non_public_audio_url");
        using var client = factory.CreateClient(candidate.UseIsolatedDownloadClient ? HttpAudioUrlResolver.DownloadHttpClientName : NeteaseSourceProvider.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, candidate.DownloadUrl);
        foreach (var (key, value) in candidate.RequestHeaders ?? new Dictionary<string, string>())
        {
            if (!(key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || key.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                || key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) || key.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)) || value.Length > 4096 || value.Contains('\r') || value.Contains('\n'))
                throw new ProbeLimitException("unsupported_download_header");
            request.Headers.TryAddWithoutValidation(key, value);
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("complete_response_required", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new ProbeLimitException("size_limit");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaximumBytes) throw new ProbeLimitException("size_limit");
            if (Interlocked.Add(ref downloadedBytes, read) > MaximumTotalBytes) throw new ProbeLimitException("total_byte_budget_exhausted");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (response.Content.Headers.ContentLength is { } length && length != total) throw new ProbeLimitException("length_mismatch");
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static bool Matches(SourceSearchResult expected, SourceSearchResult actual) =>
        TrackIdentityMatcher.Evaluate(Identity(expected), Identity(actual)).Verdict == IdentityVerdict.Matched
        && TrackIdentityMatcher.Evaluate(Identity(actual), Identity(expected)).Verdict == IdentityVerdict.Matched;
    private static TrackIdentity Identity(SourceSearchResult item) => new(item.Title, item.Artists, item.DurationMs, item.AlbumTitle);
    private static object Snapshot(SourceSearchResult item) => new { item.ProviderTrackId, item.Title, item.Artists, item.AlbumTitle, item.DurationMs, item.License };
    private static string SafeId(string? value) => new((value ?? "unknown").Where(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_').Take(100).ToArray());
    private static string ErrorCode(Exception exception) => exception switch
    {
        ProbeLimitException limit => limit.Code,
        OperationCanceledException or TimeoutException => "timeout_or_cancelled",
        HttpRequestException request => "http_error_" + ((int?)request.StatusCode)?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MediaProcessingException media => "media_" + SafeId(media.Code),
        SourceUnavailableException => "source_unavailable",
        IOException => "io_error",
        _ => exception.GetType().Name
    };
    private static AudioQualityLevel? Tier(string level) => level.ToLowerInvariant() switch
    {
        "999" or "lossless" or "flac" => AudioQualityLevel.Lossless,
        "320" or "740" or "exhigh" => AudioQualityLevel.High320K,
        "128" or "192" or "256" or "standard" => AudioQualityLevel.Below320K,
        _ => null
    };

    private sealed class CountingResolver(IAudioUrlResolver inner) : IAudioUrlResolver
    {
        public Task<SourceDownloadInfo?> ResolveAsync(AudioUrlResolveRequest request, CancellationToken cancellationToken)
        {
            CurrentCase.Value?.ResolutionSteps.Add(SafeId(request.ResolverId) + ":" + SafeId(request.Level));
            return inner.ResolveAsync(request with
            {
                Diagnostic = diagnostic =>
                {
                    CurrentCase.Value?.ResolutionDiagnostics.Add(new(
                        SafeId(request.ResolverId), SafeId(request.Level), diagnostic.Stage, diagnostic.Code, diagnostic.HttpStatus));
                    request.Diagnostic?.Invoke(diagnostic);
                }
            }, cancellationToken);
        }
    }
    private sealed class TrafficHandler(string clientName, string officialPath) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (CurrentCase.Value is { } result)
            {
                if (clientName == HttpAudioUrlResolver.DownloadHttpClientName) result.DownloadRequests++;
                else if (clientName == HttpAudioUrlResolver.HttpClientName) result.ResolverHttpRequests++;
                else
                {
                    result.PlatformHttpRequests++;
                    if (request.RequestUri?.AbsolutePath == officialPath)
                    {
                        var bitrate = request.RequestUri.Query.TrimStart('?').Split('&').FirstOrDefault(part => part.StartsWith("br=", StringComparison.Ordinal)) ?? "br=unknown";
                        result.ResolutionSteps.Add("official:" + SafeId(bitrate));
                    }
                }
            }
            await NetworkGate.WaitAsync(cancellationToken);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (clientName == NeteaseSourceProvider.HttpClientName && CurrentCase.Value is { } current)
                {
                    try { await CapturePlatformStatusAsync(request, response, current, cancellationToken); }
                    catch (JsonException) { }
                    catch { response.Dispose(); throw; }
                }
                return response;
            }
            finally { NetworkGate.Release(); }
        }

        private async Task CapturePlatformStatusAsync(HttpRequestMessage request, HttpResponseMessage response,
            CaseResult result, CancellationToken cancellationToken)
        {
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 1048576) return;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            static int? Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number) ? number : null;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("privileges", out var privileges) && privileges.ValueKind == JsonValueKind.Array)
                foreach (var item in privileges.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.Number && id.GetRawText() == result.TrackId)
                        result.PlatformPrivilege = new(Number(item, "st"), Number(item, "pl"), Number(item, "dl"), Number(item, "cp"));
            if (request.RequestUri?.AbsolutePath == officialPath
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
                && data[0].ValueKind == JsonValueKind.Object)
            {
                var item = data[0];
                var hasUrl = item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString());
                result.OfficialResponses.Add(new(Number(item, "code"), hasUrl, Number(item, "br"),
                    item.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)));
            }
        }
    }
}

internal sealed class ProbeLimitException(string code) : Exception { public string Code { get; } = code; }
internal sealed record Sample(string Kind, string Id, string Title, IReadOnlyList<string> Artists, long? DurationMs, bool Unavailable = false);
internal sealed class ProbeReport(DateTimeOffset startedAtUtc)
{
    public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
    public bool ResolveOnly { get; init; }
    public bool Cancelled { get; set; }
    public bool? AcceptancePassed { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string ConfigurationScope { get; } = "api appsettings.json only; anonymous; no .env, Development, UserSecrets or environment providers";
    public string ShortDiscovery { get; set; } = "not_attempted";
    public object? ShortSample { get; set; }
    public int DownloadAttempts { get; set; }
    public long DownloadedBytes { get; set; }
    public bool TemporaryAudioDeleted { get; set; }
    public string? FatalError { get; set; }
    public List<CaseResult> Cases { get; } = [];
    public string[] Limitations { get; } = ["No library/database writes; bounded live smoke test, not coverage statistics.", "HTTP 200, a .flac name and a lossless codec do not prove a lossless recording source.", "Metadata match precedes transfer; duration and decoding cannot alone prove audio recording identity.", "At most six transfer attempts and 192 MiB globally, 64 MiB per file, four yielded candidates and 60 seconds per case/profile. Byte budget may overshoot by at most two 80 KiB read buffers before cancellation.", "Budget-limited cases are unverified, not proof that the source lacks the song.", "Short-track discovery is a separate 30-second budget; selected ID and search/detail agreement are reported.", "No link-refresh or cross-process resume test is performed by this probe."];
}
internal sealed class CaseResult(string profile, string sampleKind, string trackId, object expected)
{
    public string Profile { get; } = profile;
    public string SampleKind { get; } = sampleKind;
    public string TrackId { get; } = trackId;
    public object Expected { get; } = expected;
    public object? Metadata { get; set; }
    public PlatformPrivilegeResult? PlatformPrivilege { get; set; }
    public string? AvailabilitySummary { get; set; }
    public List<OfficialResponseResult> OfficialResponses { get; } = [];
    public List<ResolutionDiagnosticResult> ResolutionDiagnostics { get; } = [];
    public string? IdentityVerdict { get; set; }
    public string? IdentityReason { get; set; }
    public string Status { get; set; } = "not_started";
    public long ElapsedMs { get; set; }
    public int PlatformHttpRequests { get; set; }
    public int ResolverHttpRequests { get; set; }
    public int DownloadRequests { get; set; }
    public int PotentialResolutionSteps { get; set; }
    public HashSet<string> ResolutionSteps { get; } = [];
    public int UnrequestedPotentialSteps { get; set; }
    public bool EnumerationStoppedOnAccepted { get; set; }
    public List<CandidateResult> Candidates { get; } = [];
}
internal sealed record PlatformPrivilegeResult(int? State, int? PlayBitrate, int? DownloadBitrate, int? CopyrightStatus);
internal sealed record OfficialResponseResult(int? Code, bool HasUrl, int? Bitrate, bool HasTrialInfo);
internal sealed record ResolutionDiagnosticResult(string ResolverId, string Level, string Stage, string Code, int? HttpStatus);
internal sealed class CandidateResult(string resolverId, string declaredQuality, string declaredContainer, int? declaredBitrateKbps)
{
    public string ResolverId { get; } = resolverId;
    public string DeclaredQuality { get; } = declaredQuality;
    public string DeclaredContainer { get; } = declaredContainer;
    public int? DeclaredBitrateKbps { get; } = declaredBitrateKbps;
    public string Status { get; set; } = "not_started";
    public int? HttpStatus { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public ProbedMedia? Probe { get; set; }
    public bool DurationMatches { get; set; }
    public bool QualityAccepted { get; set; }
    public string DecodeIntegrity { get; set; } = "not_checked";
}
