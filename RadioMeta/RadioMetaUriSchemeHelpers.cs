using System;

namespace RadioMeta;

/// <summary>Shared scheme predicates for URLs consumed by Radio Meta.</summary>
/// <remarks>These helpers classify schemes only; callers retain their own URL-safety policies.</remarks>
internal static class RadioMetaUriSchemeHelpers
{
    internal static bool IsHttpOrHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    internal static bool IsHttpHttpsOrIcy(Uri uri) =>
        IsHttpOrHttps(uri) || uri.Scheme == "icy";
}
