using System;

namespace RadioMeta;

/// <summary>
/// Pure extraction of the StreamTitle value from an ICY metadata field.
/// Kept free of networking so the live reader and offline regression harness
/// execute the same code.
/// </summary>
internal static class RadioIcyStreamTitleParser
{
    internal static string ExtractStreamTitleValue(string rawValue)
    {
        var value = (rawValue ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value[0] == '\'' || value[0] == '"')
        {
            var quote = value[0];
            var terminatorIndex = FindQuotedFieldTerminator(value, quote);
            if (terminatorIndex > 0)
            {
                // Do not stop at apostrophes inside titles such as "I'll" or
                // "Let's". A quoted ICY field ends at its quote-semicolon pair.
                return value[1..terminatorIndex];
            }

            // Tolerate a quoted title with no semicolon only when its final
            // non-whitespace character is the matching quote. If the delimiter
            // is malformed, preserve interior apostrophes rather than truncating.
            var trimmed = value.TrimEnd();
            if (trimmed.Length > 1 && trimmed[^1] == quote)
            {
                return trimmed[1..^1];
            }

            return value[1..].Trim().TrimEnd(';').Trim();
        }

        var end = value.IndexOf(';');
        return end >= 0 ? value[..end].Trim() : value.Trim();
    }

    private static int FindQuotedFieldTerminator(string value, char quote)
    {
        var searchFrom = 1;
        while (searchFrom < value.Length)
        {
            var quoteIndex = value.IndexOf(quote, searchFrom);
            if (quoteIndex < 0)
            {
                return -1;
            }

            var delimiterIndex = quoteIndex + 1;
            while (delimiterIndex < value.Length && char.IsWhiteSpace(value[delimiterIndex]))
            {
                delimiterIndex++;
            }

            if (delimiterIndex < value.Length && value[delimiterIndex] == ';')
            {
                return quoteIndex;
            }

            searchFrom = quoteIndex + 1;
        }

        return -1;
    }
}
