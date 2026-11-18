using System;

namespace RadioMeta;

/// <summary>
/// Single source for the plugin version at runtime. The value comes from the
/// assembly version set in RadioMeta.csproj, so log messages and HTTP
/// User-Agent strings cannot go stale between releases.
/// </summary>
/// <remarks>
/// The inline bootstrap script in <see cref="RadioMetaPlayerIntegration"/> has no
/// version number on purpose, so plugin updates do not rewrite Emby's index.html.
/// </remarks>
internal static class RadioMetaBuildInfo
{
    /// <summary>Three-part plugin version, for example "1.3.152".</summary>
    internal static readonly string Version =
        typeof(RadioMetaBuildInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>Builds a User-Agent value such as "RadioMeta/1.3.152 artwork-fetch".</summary>
    internal static string UserAgent(string? purpose = null) =>
        string.IsNullOrWhiteSpace(purpose)
            ? "RadioMeta/" + Version
            : "RadioMeta/" + Version + " " + purpose.Trim();
}
