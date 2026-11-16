using System.Text.Json;
using ShineMusic.Model.Entities;
using ShineMusic.Service.SourceDownload;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;

namespace ShineMusic.Service;

/// <summary>Bounded cross-source lookup for missing album-level fields; existing/manual values remain authoritative.</summary>
public sealed class ProviderMetadataEnricher(ISearchAggregator search, ProviderAccessService access)
{
    public async Task<IReadOnlyList<string>> EnrichAsync(LibraryImportItem item, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(item.Title) || MetadataMerge.List(item.ArtistsJson).Length == 0
            || string.IsNullOrWhiteSpace(item.AlbumTitle) || item.DurationMs is not > 0) return [];
        var needsYear = item.ReleaseYear is null && !MetadataMerge.Locked(item, "releaseYear");
        var needsGenres = MetadataMerge.List(item.OriginalGenresJson).Length == 0 && !MetadataMerge.Locked(item, "originalGenres");
        if (!needsYear && !needsGenres) return [];
        var names = search.Providers.Where(p => p.ConfigEnabled && p.SearchEnabled && p.MetadataEnabled
            && p.Declares(ProviderCapability.Search) && p.Declares(ProviderCapability.Metadata))
            .OrderByDescending(p => p.ProviderName == "netease").ThenByDescending(p => p.Priority)
            .Take(3).Select(p => p.ProviderName).ToArray();
        if (names.Length == 0) return [];
        var diagnostics = new List<object>();
        var filled = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var results = await search.SearchAsync($"{item.Title} {MetadataMerge.List(item.ArtistsJson)[0]}", names, 5, deadline.Token);
            foreach (var outcome in results.Outcomes)
                diagnostics.Add(new { source = outcome.ProviderName, reason = outcome.ErrorCode ?? "search_completed", count = outcome.Results.Count });
            foreach (var candidate in results.AllResults.Where(c => Matches(item, c))
                .OrderBy(c => Array.IndexOf(names, c.Provider)).Take(3))
            {
                var detail = await access.GetMetadataAsync(candidate.Provider, candidate.ProviderTrackId, deadline.Token);
                if (detail is null || !Matches(item, detail))
                {
                    diagnostics.Add(new { source = candidate.Provider, id = candidate.ProviderTrackId, reason = "detail_unconfirmed" });
                    continue;
                }
                diagnostics.Add(new { source = candidate.Provider, id = candidate.ProviderTrackId, reason = "identity_album_confirmed" });
                filled.AddRange(Apply(item, detail, needsYear, needsGenres));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var warnings = MetadataMerge.List(item.WarningsJson).Append("provider_metadata_timeout").Distinct();
            item.WarningsJson = JsonSerializer.Serialize(warnings);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            diagnostics.Add(new { reason = "provider_metadata_unavailable" });
        }
        if (diagnostics.Count > 0)
        {
            var audit = MetadataMerge.Audit(item.MetadataAuditJson);
            audit.RemoveAll(row => row.Field == "providerMetadataDiagnostics");
            audit.Add(new("providerMetadataDiagnostics", JsonSerializer.Serialize(diagnostics), "provider-metadata", null, DateTime.UtcNow, false));
            item.MetadataAuditJson = JsonSerializer.Serialize(audit);
        }
        token.ThrowIfCancellationRequested();
        return filled.Distinct().ToArray();
    }

    internal static bool Matches(LibraryImportItem item, SourceSearchResult candidate)
    {
        static string Album(string? text) => string.Concat((text ?? "").Normalize(System.Text.NormalizationForm.FormKC)
            .Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        return !string.IsNullOrWhiteSpace(item.AlbumTitle) && Album(item.AlbumTitle) == Album(candidate.AlbumTitle)
            && MetadataMerge.List(item.ArtistsJson).Length > 0 && candidate.Artists.Count > 0
            && item.DurationMs is > 0 && TrackIdentityMatcher.Evaluate(
                new(item.Title ?? "", MetadataMerge.List(item.ArtistsJson), item.DurationMs),
                new(candidate.Title, candidate.Artists, candidate.DurationMs)).Verdict == IdentityVerdict.Matched;
    }

    internal static IReadOnlyList<string> Apply(LibraryImportItem item, SourceSearchResult candidate, bool year = true, bool genres = true)
    {
        if (!Matches(item, candidate)) return [];
        var fields = new List<string>();
        if (year && !MetadataMerge.Locked(item, "releaseYear") && candidate.ReleaseYear is > 0
            && (item.ReleaseDate is null || item.ReleaseDate.StartsWith(candidate.ReleaseYear.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)))
            if (MetadataMerge.Fill(item, "releaseYear", item.ReleaseYear, candidate.ReleaseYear, item.ReleaseYear is null,
                value => item.ReleaseYear = value, candidate.Provider, candidate.ProviderTrackId)) fields.Add("releaseYear");
        if (genres && !MetadataMerge.Locked(item, "originalGenres"))
            if (MetadataMerge.Fill(item, "originalGenres", MetadataMerge.List(item.OriginalGenresJson), candidate.Genres.ToArray(),
                MetadataMerge.List(item.OriginalGenresJson).Length == 0, value => item.OriginalGenresJson = JsonSerializer.Serialize(value),
                candidate.Provider, candidate.ProviderTrackId)) fields.Add("originalGenres");
        return fields;
    }
}
