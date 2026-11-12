using System.Text;
using ShineMusic.Model.Contracts;

namespace ShineMusic.Service.SourceDownload;

public enum SourceSearchMode
{
    Title,
    Artist,
    ArtistTrack,
    ArtistAlbum,
    Album
}

public sealed record SourceSearchCriteria(
    SourceSearchMode Mode,
    string ProviderQuery,
    string Term,
    string? Artist);

public static class SourceSearchMatcher
{
    public static bool TryParse(
        string query,
        string? mode,
        out SourceSearchCriteria? criteria,
        out string error)
    {
        criteria = null;
        error = string.Empty;
        var term = query.Trim();
        if (term.Length == 0)
        {
            error = "A search query is required.";
            return false;
        }

        var parsedMode = mode?.Trim().ToLowerInvariant() switch
        {
            "title" => SourceSearchMode.Title,
            "artist" => SourceSearchMode.Artist,
            "artist-track" => SourceSearchMode.ArtistTrack,
            "artist-album" => SourceSearchMode.ArtistAlbum,
            "album" => SourceSearchMode.Album,
            _ => (SourceSearchMode?)null
        };
        if (parsedMode is null)
        {
            error = "Search mode must be 'title', 'artist', 'artist-track', 'artist-album', or 'album'.";
            return false;
        }

        if (parsedMode is SourceSearchMode.ArtistTrack or SourceSearchMode.ArtistAlbum)
        {
            var separatorIndex = term.IndexOfAny(['-', '－', '–', '—']);
            if (separatorIndex <= 0 || separatorIndex == term.Length - 1)
            {
                error = parsedMode == SourceSearchMode.ArtistTrack
                    ? "Enter the artist and song as 'artist - song'."
                    : "Enter the artist and album as 'artist - album'.";
                return false;
            }

            var artist = term[..separatorIndex].Trim();
            var artistTerm = term[(separatorIndex + 1)..].Trim();
            if (artist.Length == 0 || artistTerm.Length == 0)
            {
                error = "Both artist and search term are required.";
                return false;
            }

            criteria = new SourceSearchCriteria(parsedMode.Value, $"{artist} {artistTerm}", artistTerm, artist);
            return true;
        }

        criteria = new SourceSearchCriteria(parsedMode.Value, term, term, null);
        return true;
    }

    public static IReadOnlyList<SourceSearchItemDto> Filter(
        IEnumerable<SourceSearchItemDto> results,
        SourceSearchCriteria criteria)
    {
        var normalizedTerm = Normalize(criteria.Term);
        var normalizedArtist = Normalize(criteria.Artist);
        return results.Where(item => criteria.Mode switch
        {
            SourceSearchMode.Title => Normalize(item.Title) == normalizedTerm,
            SourceSearchMode.Artist => item.Artists.Any(artist => Normalize(artist) == normalizedTerm),
            SourceSearchMode.ArtistTrack =>
                Normalize(item.Title) == normalizedTerm
                && item.Artists.Any(artist => Normalize(artist) == normalizedArtist),
            SourceSearchMode.ArtistAlbum =>
                Normalize(item.AlbumTitle) == normalizedTerm
                && item.Artists.Any(artist => Normalize(artist) == normalizedArtist),
            SourceSearchMode.Album => Normalize(item.AlbumTitle) == normalizedTerm,
            _ => false
        }).ToArray();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(normalized.Length);
        var pendingWhitespace = false;
        foreach (var character in normalized)
        {
            var normalizedCharacter = character switch
            {
                >= '\uFF01' and <= '\uFF5E' => (char)(character - 0xFEE0),
                '\u3000' => ' ',
                _ => character
            };

            if (char.IsWhiteSpace(normalizedCharacter))
            {
                pendingWhitespace = builder.Length > 0;
                continue;
            }

            if (char.IsPunctuation(normalizedCharacter) || char.IsSymbol(normalizedCharacter))
                continue;

            if (pendingWhitespace)
                builder.Append(' ');
            builder.Append(char.ToUpperInvariant(normalizedCharacter));
            pendingWhitespace = false;
        }
        return builder.ToString();
    }
}
