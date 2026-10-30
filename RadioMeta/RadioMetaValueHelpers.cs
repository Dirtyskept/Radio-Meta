using System.Net;

namespace RadioMeta;

/// <summary>Shared value helpers used by station capture and display code.</summary>
internal static class RadioMetaValueHelpers
{
    /// <summary>
    /// Decodes HTML entities found in station metadata and trims surrounding
    /// whitespace. This is a local string operation; it does not perform I/O.
    /// </summary>
    internal static string NormalizeHtmlText(string? value) =>
        (WebUtility.HtmlDecode(value ?? string.Empty) ?? string.Empty).Trim();

    internal static string FirstNonEmpty(string preferred, string fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

    internal static string LimitText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();

        if (value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 3)
        {
            return value[..maxLength];
        }

        return value[..(maxLength - 3)] + "...";
    }
}
