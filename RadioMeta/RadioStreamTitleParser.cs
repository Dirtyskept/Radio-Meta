using System;
using System.Text.Json;
using System.Text.RegularExpressions;

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

        // Ads, promos and break markers are never treated as a song.
        if (IsNonSongMarker(normalizedStreamTitle.Trim()))
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

        // Stream-title formats, tried in order. Each recognizer handles one
        // format and returns false when the title is not in that format. To
        // support a new format, add one recognizer here, with tests.
        if (TryIHeartTextFormat(normalizedTitle, out var parsed) ||
            TryTitleArtistKeyValueFormat(normalizedTitle, out parsed) ||
            TryTildeFormat(normalizedTitle, out parsed) ||
            TryDoublePipeFormat(normalizedTitle, out parsed) ||
            TryDashFormat(normalizedTitle, out parsed))
        {
            return parsed;
        }

        // No recognizable format: show the whole value as the song title.
        return (string.Empty, normalizedTitle.Trim());
    }

    // ---- Stream-title format recognizers ------------------------------------

    // iHeartRadio / WideOrbit cart record: the title is in text="...", and the
    // artist comes before " - text=" or " | text=":
    //   Ed Sheeran - text="Bad Habits" song_spot="M" MediaBaseId="2720462" ...
    private static readonly Regex IHeartTextRegex = new(
        @"^(?<artist>.*?)\s*(?:-|\|)\s*text=""(?<title>[^""]*)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // iHeartRadio key-value variant:
    //   title="SOMETHING JUST LIKE THIS",artist="Chainsmokers / Coldplay",url="..."
    private static readonly Regex TitleArtistKeyValueRegex = new(
        @"^\s*title=""(?<title>[^""]*)""\s*,\s*artist=""(?<artist>[^""]*)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // iHeartRadio spot type: song_spot="T" marks traffic (ads and promos).
    private static readonly Regex IHeartTrafficSpotRegex = new(
        @"\bsong_spot\s*=\s*""T""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Broadcast-automation fields need at least this many '~' fields, so an
    // ordinary title that merely contains a '~' is left alone.
    private const int MinimumTildeFields = 4;

    /// <summary>
    /// Ads, promos and break markers, never shown as a song:
    /// "*" / "#" (ad break start/end); "##..." (Triton TildeATS events);
    /// iHeartRadio traffic spots (song_spot="T"); and iHeartRadio records with
    /// no artist, such as " - text="Spot Block End"".
    /// </summary>
    private static bool IsNonSongMarker(string value)
    {
        if (value == "*" || value == "#" || value.StartsWith("##", StringComparison.Ordinal))
        {
            return true;
        }

        if (IHeartTrafficSpotRegex.IsMatch(value))
        {
            return true;
        }

        var iHeart = IHeartTextRegex.Match(value);
        if (iHeart.Success && iHeart.Groups["artist"].Value.Trim().Length == 0)
        {
            return true;
        }

        var keyValue = TitleArtistKeyValueRegex.Match(value);
        return keyValue.Success && keyValue.Groups["artist"].Value.Trim().Length == 0;
    }

    private static bool TryIHeartTextFormat(string value, out (string Artist, string SongTitle) parsed)
    {
        parsed = default;
        var match = IHeartTextRegex.Match(value);
        if (!match.Success) return false;
        return TryUse(match.Groups["artist"].Value, match.Groups["title"].Value, out parsed);
    }

    private static bool TryTitleArtistKeyValueFormat(string value, out (string Artist, string SongTitle) parsed)
    {
        parsed = default;
        var match = TitleArtistKeyValueRegex.Match(value);
        if (!match.Success) return false;
        return TryUse(match.Groups["artist"].Value, match.Groups["title"].Value, out parsed);
    }

    // Broadcast-automation '~' fields, artist first and title second:
    //   R101:            DAFT PUNK~ONE MORE TIME~DISCOVERY~2000~~231~...~R101
    //   Triton TildeATS: Billie Eilish ~ Bad Guy ~ 246 ~ 0 ^
    private static bool TryTildeFormat(string value, out (string Artist, string SongTitle) parsed)
    {
        parsed = default;
        var fields = value.TrimEnd().TrimEnd('^').Split('~');
        if (fields.Length < MinimumTildeFields) return false;
        return TryUse(fields[0], fields[1], out parsed);
    }

    // Double-pipe fields, artist first and title second:
    //   MADONNA || Beautiful Stranger || S
    private static bool TryDoublePipeFormat(string value, out (string Artist, string SongTitle) parsed)
    {
        parsed = default;
        var fields = value.Split("||", StringSplitOptions.None);
        if (fields.Length < 2) return false;
        return TryUse(fields[0], fields[1], out parsed);
    }

    // The standard "Artist - Title" (also en/em dashes). The earliest separator
    // is used, so later dashes stay in the title. Extra text after " | " is
    // dropped from the title: "Artist - Title | Visit our website".
    private static bool TryDashFormat(string value, out (string Artist, string SongTitle) parsed)
    {
        parsed = default;

        // Split the normalized value, not the original encoded input. Keep the
        // rest of the title intact, including any later hyphens or dashes.
        var separatorIndex = -1;
        var separatorLength = 0;
        foreach (var candidate in ArtistTitleSeparators)
        {
            var candidateIndex = value.IndexOf(
                candidate, StringComparison.Ordinal);
            if (candidateIndex <= 0 ||
                candidateIndex + candidate.Length >= value.Length)
            {
                continue;
            }

            if (separatorIndex < 0 || candidateIndex < separatorIndex)
            {
                separatorIndex = candidateIndex;
                separatorLength = candidate.Length;
            }
        }

        if (separatorIndex < 0) return false;

        var artist = value[..separatorIndex].Trim();
        var title = value[(separatorIndex + separatorLength)..].Trim();
        var pipeIndex = title.IndexOf(" | ", StringComparison.Ordinal);
        if (pipeIndex > 0)
        {
            title = title[..pipeIndex].Trim();
        }

        parsed = (artist, title);
        return true;
    }

    // Accepts a recognized artist/title only when both are present.
    private static bool TryUse(string artist, string title, out (string Artist, string SongTitle) parsed)
    {
        artist = (artist ?? string.Empty).Trim();
        title = (title ?? string.Empty).Trim();
        parsed = (artist, title);
        return artist.Length > 0 && title.Length > 0;
    }
}
