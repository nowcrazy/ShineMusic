using System.Globalization;
using System.Text.Json;

namespace ShineMusic.Service.SourceDownload;

internal static class SourceReleaseYearParser
{
    private const int MinimumYear = 1800;
    private const int MaximumYear = 2200;

    public static int? FromJson(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in properties)
        {
            if (element.TryGetProperty(property, out var value)
                && TryParse(value, out var year))
                return year;
        }

        return null;
    }

    private static bool TryParse(JsonElement value, out int year)
    {
        year = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var number):
                return TryParseNumber(number, out year);
            case JsonValueKind.String:
                return TryParseString(value.GetString(), out year);
            default:
                return false;
        }
    }

    private static bool TryParseString(string? value, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.Length == 4 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedYear))
            return SetYear(parsedYear, out year);

        if (DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compactDate))
            return SetYear(compactDate.Year, out year);

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return SetYear(date.Year, out year);

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && TryParseNumber(number, out year);
    }

    private static bool TryParseNumber(long value, out int year)
    {
        if (value is >= MinimumYear and <= MaximumYear)
            return SetYear((int)value, out year);

        if (value is >= 10_000_000 and <= 99_999_999
            && DateTime.TryParseExact(value.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compactDate))
            return SetYear(compactDate.Year, out year);

        try
        {
            var date = value >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : value >= 1_000_000_000
                    ? DateTimeOffset.FromUnixTimeSeconds(value)
                    : default;
            if (date == default)
            {
                year = 0;
                return false;
            }

            return SetYear(date.Year, out year);
        }
        catch (ArgumentOutOfRangeException)
        {
            year = 0;
            return false;
        }
    }

    private static bool SetYear(int candidate, out int year)
    {
        year = candidate;
        return candidate is >= MinimumYear and <= MaximumYear;
    }
}
