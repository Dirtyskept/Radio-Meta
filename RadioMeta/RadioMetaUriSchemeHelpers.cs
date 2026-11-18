using System;

namespace RadioMeta;

/// <summary>Shared scheme and credential predicates for URLs consumed by Radio Meta.</summary>
/// <remarks>
/// These helpers classify schemes and detect embedded credentials only; they do not
/// check whether a host is public. Network-level public-address enforcement lives in
/// RadioMetadataTestUrlSafety.
/// </remarks>
internal static class RadioMetaUriSchemeHelpers
{
    // Query-string names that carry Emby (or generic) access tokens. Any URL that
    // contains one of these must never be stored, displayed, or reused.
    // This is the union of the lists previously duplicated across six call sites.
    private static readonly string[] CredentialQueryNames =
    {
        "api_key",
        "apikey",
        "access_token",
        "emby_token",
        "x-emby-token",
        "authorization"
    };

    internal static bool IsHttpOrHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    internal static bool IsHttpHttpsOrIcy(Uri uri) =>
        IsHttpOrHttps(uri) || uri.Scheme == "icy";

    /// <summary>
    /// Returns true when the URL carries user-info (user:password@) or a known
    /// access-token query parameter. Matching is a case-insensitive substring
    /// test on the percent-decoded query, which is deliberately conservative:
    /// it also catches encoded names such as api%5Fkey. A query that cannot be
    /// decoded is treated as carrying credentials.
    /// </summary>
    internal static bool HasEmbeddedCredentials(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo)) return true;
        var query = uri.Query;
        if (string.IsNullOrEmpty(query)) return false;
        try { query = Uri.UnescapeDataString(query); }
        catch { return true; }
        foreach (var name in CredentialQueryNames)
        {
            if (query.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    /// <summary>Returns true when the URL path starts with any of the given prefixes (case-insensitive).</summary>
    internal static bool PathStartsWithAny(Uri uri, params string[] prefixes)
    {
        var path = uri.AbsolutePath;
        foreach (var prefix in prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
