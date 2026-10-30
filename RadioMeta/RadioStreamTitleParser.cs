using System;
using System.Text.Json;

namespace RadioMeta;

/// <summary>Pure parsing and validation for station-provided stream-title values.</summary>
/// <remarks>Kept free of network, cache, and playback state so the rules can be changed and tested independently.</remarks>
internal static class RadioStreamTitleParser
{
    // Radio stations use several dash characters between artist and title.
    // Keep this list ordered by use, while SplitStreamTitle selects the earliest
    // separator in the received title so later title punctuation is preserved.
    private static readonly string[] ArtistTitleSeparators =
    {
        " - ",  // ASCII hyphen-minus
        " – ", // en dash
        " — "  // em dash
    };

    internal static string NormalizeStreamTitle(string? streamTitle)
    {
        var value = RadioMetaValueHelpers.NormalizeHtmlText(streamTitle);
        if (value.IndexOf('\u00B4') < 0)
        {
            return value;
        }

        // Some station metadata uses the spacing acute accent between letters
        // where an apostrophe is intended, e.g. "Gravity´s". Normalize only
        // that word-internal punctuation pattern; leave other accent marks alone.
        var chars = value.ToCharArray();
        for (var i = 1; i < chars.Length - 1; i++)
        {
            if (chars[i] == '\u00B4' &&
                char.IsLetterOrDigit(chars[i - 1]) &&
                char.IsLetterOrDigit(chars[i + 1]))
            {
                chars[i] = '\'';
            }
        }

        return new string(chars);
    }

    internal static bool IsUsableStreamTitle(string? streamTitle) =>
        IsUsableNormalizedStreamTitle(NormalizeStreamTitle(streamTitle));

    internal static bool IsUsableNormalizedStreamTitle(string? normalizedStreamTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedStreamTitle))
        {
            return false;
        }

        var value = normalizedStreamTitle.Trim().Trim('\0', '\'', '"');
        if (value.Length == 0)
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            RadioMetaUriSchemeHelpers.IsHttpOrHttps(uri))
        {
            return false;
        }

        var lower = value.ToLowerInvariant();
        var containsUrl = lower.Contains("http://", StringComparison.Ordinal) ||
                          lower.Contains("https://", StringComparison.Ordinal) ||
                          lower.Contains("www.", StringComparison.Ordinal);
        if (containsUrl &&
            (lower.Contains("tracklist:", StringComparison.Ordinal) ||
             lower.Contains("track list:", StringComparison.Ordinal) ||
             lower.Contains("playlist:", StringComparison.Ordinal) ||
             lower.Contains("listen live:", StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    internal static string FindStreamTitle(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format) ||
            !format.TryGetProperty("tags", out var tags))
        {
            return string.Empty;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (string.Equals(
                tag.Name, "StreamTitle",
                StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeStreamTitle(tag.Value.GetString());
            }
        }

        return string.Empty;
    }

    internal static (string Artist, string SongTitle) SplitStreamTitle(
        string streamTitle) =>
        SplitNormalizedStreamTitle(NormalizeStreamTitle(streamTitle));

    internal static (string Artist, string SongTitle) SplitNormalizedStreamTitle(
        string normalizedTitle)
    {
        if (!IsUsableNormalizedStreamTitle(normalizedTitle))
        {
            return (string.Empty, string.Empty);
        }

        // Split the normalized value, not the original encoded input. Keep the
        // rest of the title intact, including any later hyphens or dashes.
        var separatorIndex = -1;
        var separatorLength = 0;
        foreach (var candidate in ArtistTitleSeparators)
        {
            var candidateIndex = normalizedTitle.IndexOf(
                candidate, StringComparison.Ordinal);
            if (candidateIndex <= 0 ||
                candidateIndex + candidate.Length >= normalizedTitle.Length)
            {
                continue;
            }

            if (separatorIndex < 0 || candidateIndex < separatorIndex)
            {
                separatorIndex = candidateIndex;
                separatorLength = candidate.Length;
            }
        }

        if (separatorIndex >= 0)
        {
            return (
                normalizedTitle[..separatorIndex].Trim(),
                normalizedTitle[(separatorIndex + separatorLength)..].Trim());
        }

        // If the station supplies a title without a recognizable
        // separator, show it as the song title.
        return (string.Empty, normalizedTitle.Trim());
    }
}
