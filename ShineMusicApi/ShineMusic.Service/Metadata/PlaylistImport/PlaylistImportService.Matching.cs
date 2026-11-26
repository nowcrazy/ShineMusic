using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service.PlaylistImport;

public sealed partial class PlaylistImportService
{
    // Bound each IN list for SQLite as well as PostgreSQL. Change this one value
    // if a supported provider has a smaller parameter limit.
    private const int LibraryMatchQueryBatchSize = 400;

    private sealed class LibraryMatchTrackRow
    {
        public LibraryMatchTrackRow() { }
        public Guid Id { get; set; }
        public long? DurationMs { get; set; }
        public Guid? AlbumId { get; set; }
        public string NormalizedTitle { get; set; } = string.Empty;
    }

    /// <summary>
    /// Load the same published candidates as the single-song query in bounded
    /// batches, then apply its artist, album and duration rules in input order.
    /// </summary>
    private async Task<IReadOnlyList<Guid?>> MatchLibraryBatchAsync(
        IReadOnlyList<ParsedPlaylistTrack> tracks, CancellationToken cancellationToken)
    {
        var matches = new Guid?[tracks.Count];
        var requests = tracks.Select(track => new
        {
            Title = Normalize(track.Title),
            Artists = track.Artists.Select(NormalizeLoose)
                .Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal),
            Album = NormalizeLoose(track.AlbumTitle),
            track.DurationMs
        }).ToArray();
        var requestedTitles = requests
            .Where(request => request.Title.Length > 0 && request.Artists.Count > 0)
            .Select(request => request.Title)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedTitles.Length == 0)
            return matches;

        var candidateTracks = new List<LibraryMatchTrackRow>();
        foreach (var titleBatch in requestedTitles.Chunk(LibraryMatchQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateTracks.AddRange(await db.Queryable<Track>()
                .Where(track => track.PublishedAt != null && titleBatch.Contains(track.Title.ToLower()))
                .Select(track => new LibraryMatchTrackRow
                {
                    Id = track.Id,
                    DurationMs = track.DurationMs,
                    AlbumId = track.AlbumId,
                    NormalizedTitle = track.Title.ToLower()
                })
                .ToListAsync(cancellationToken));
        }
        if (candidateTracks.Count == 0)
            return matches;

        var albumTitles = new Dictionary<Guid, string>();
        var albumIds = candidateTracks.Where(candidate => candidate.AlbumId.HasValue)
            .Select(candidate => candidate.AlbumId!.Value).Distinct().ToArray();
        foreach (var albumBatch in albumIds.Chunk(LibraryMatchQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var albums = await db.Queryable<Album>()
                .Where(album => albumBatch.Contains(album.Id))
                .Select(album => new { album.Id, album.Title })
                .ToListAsync(cancellationToken);
            foreach (var album in albums)
                albumTitles[album.Id] = NormalizeLoose(album.Title);
        }

        var artistsByTrack = new Dictionary<Guid, HashSet<string>>();
        var candidateIds = candidateTracks.Select(candidate => candidate.Id).Distinct().ToArray();
        foreach (var candidateBatch in candidateIds.Chunk(LibraryMatchQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artists = await db.Queryable<TrackArtist, Artist>((link, artist) => link.ArtistId == artist.Id)
                .Where((link, artist) => candidateBatch.Contains(link.TrackId))
                .Select((link, artist) => new { link.TrackId, artist.Name })
                .ToListAsync(cancellationToken);
            foreach (var artist in artists)
            {
                if (!artistsByTrack.TryGetValue(artist.TrackId, out var names))
                    artistsByTrack[artist.TrackId] = names = new HashSet<string>(StringComparer.Ordinal);
                names.Add(NormalizeLoose(artist.Name));
            }
        }

        // The grouping key is LOWER(title) returned by the database. Do not
        // lower the original title again in .NET: PostgreSQL locale rules can
        // differ from .NET's casing rules, and filtering must stay SQL-owned.
        var candidatesByTitle = candidateTracks.ToLookup(candidate => candidate.NormalizedTitle, StringComparer.Ordinal);
        for (var index = 0; index < requests.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = requests[index];
            if (request.Title.Length == 0 || request.Artists.Count == 0)
                continue;
            var songAndArtistMatches = candidatesByTitle[request.Title]
                .Where(candidate => artistsByTrack.TryGetValue(candidate.Id, out var names)
                    && names.Overlaps(request.Artists))
                .ToList();
            var albumMatches = request.Album.Length == 0
                ? []
                : songAndArtistMatches.Where(candidate => candidate.AlbumId is { } albumId
                    && albumTitles.TryGetValue(albumId, out var title) && title == request.Album).ToList();
            var eligible = albumMatches.Count > 0 ? albumMatches : songAndArtistMatches;
            if (request.DurationMs is null)
            {
                matches[index] = eligible.Count == 1 ? eligible[0].Id : null;
                continue;
            }

            // Keep the existing strict five-second window and nearest-duration
            // selection. No new tie-break or ambiguity policy is introduced.
            matches[index] = eligible
                .Where(candidate => candidate.DurationMs.HasValue)
                .Select(candidate => (candidate.Id, Diff: Math.Abs(candidate.DurationMs!.Value - request.DurationMs.Value)))
                .Where(candidate => candidate.Diff < 5000)
                .OrderBy(candidate => candidate.Diff)
                .Select(candidate => (Guid?)candidate.Id)
                .FirstOrDefault();
        }
        return matches;
    }
}
