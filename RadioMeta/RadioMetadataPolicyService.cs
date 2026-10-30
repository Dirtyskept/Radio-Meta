using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RadioMeta;

/// <summary>Sanitized explanation of the metadata lookup mode resolved for a playback identity.</summary>
internal sealed class RadioStationMetadataModeDiagnostic
{
    public string Mode { get; init; } = "Automatic";
    public bool LookupDisabled { get; init; }
    public string MatchedRuleStationName { get; init; } = string.Empty;
    public string MatchType { get; init; } = "no matching rule";
    public IReadOnlyList<string> ConfiguredStationOnlyRuleNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Applies runtime metadata configuration: station/device modes, ignored phrases,
/// stability-delay values, exact title corrections, and title-cleanup rules.
/// CurrentTrackService remains responsible for accepted-track state and publication.
/// </summary>
internal sealed class RadioMetadataPolicyService
{
    private readonly RadioMetaPlugin _plugin;

    internal RadioMetadataPolicyService(RadioMetaPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    }

    /// <summary>
    /// Applies the configured ignore phrase rules. A blank station scope means
    /// the rule applies to all stations.
    /// </summary>
    public bool IsMetadataPhraseIgnored(string? stationName, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var rules = _plugin.Configuration.IgnoreMetadataPhrases;
        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        var station = (stationName ?? string.Empty).Trim();
        var value = candidate.Trim();
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Phrase))
            {
                continue;
            }

            var configuredStation = (rule.StationName ?? string.Empty).Trim();
            if (configuredStation.Length > 0 &&
                !string.Equals(configuredStation, station, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value.Contains(rule.Phrase.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a station-specific rule first, then a device-wide rule whose
    /// StationName field matches the current Emby device name. Older rules may
    /// still use MetadataDelayDeviceName as a station-specific restriction.
    /// </summary>
    private RadioStationMetadataModeRule? FindApplicableStationMetadataRule(
        string? stationName,
        string? deviceName)
    {
        var station = (stationName ?? string.Empty).Trim();
        if (station.Length == 0)
        {
            return null;
        }

        var rules = _plugin.Configuration.StationMetadataModes;
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        var stationRule = rules.FirstOrDefault(candidate =>
            candidate is not null &&
            string.Equals((candidate.StationName ?? string.Empty).Trim(), station,
                StringComparison.OrdinalIgnoreCase));

        if (stationRule is not null)
        {
            var legacyTargetDevice = (stationRule.MetadataDelayDeviceName ?? string.Empty).Trim();
            if (legacyTargetDevice.Length == 0 ||
                string.Equals(legacyTargetDevice, (deviceName ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return stationRule;
            }
        }

        var device = (deviceName ?? string.Empty).Trim();
        if (device.Length == 0)
        {
            return null;
        }

        return rules.FirstOrDefault(candidate =>
            candidate is not null &&
            string.Equals((candidate.StationName ?? string.Empty).Trim(), device,
                StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(candidate.MetadataDelayDeviceName));
    }

    /// <summary>
    /// Returns whether track lookups are disabled for the station/device scope.
    /// A station-specific rule takes priority over a device-wide rule.
    /// </summary>
    public bool IsStationMetadataLookupDisabled(string? stationName, string? deviceName = null) =>
        GetStationMetadataModeDiagnostic(new[] { stationName }, deviceName).LookupDisabled;

    /// <summary>
    /// Resolves a playback's mode against multiple safe station identity candidates. Emby can
    /// briefly expose a .strm filename, item Name, and RadioMetaStation ProviderId that differ
    /// during playback handoff. An explicit StationOnly match on any of those identities wins,
    /// because that setting specifically means "do not look for metadata".
    /// </summary>
    public RadioStationMetadataModeDiagnostic GetStationMetadataModeDiagnostic(
        IEnumerable<string?> stationNames,
        string? deviceName = null)
    {
        var aliases = (stationNames ?? Array.Empty<string?>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        var rules = _plugin.Configuration.StationMetadataModes ?? new List<RadioStationMetadataModeRule>();
        var stationOnlyNames = rules
            .Where(rule => rule is not null &&
                string.Equals((rule.Mode ?? string.Empty).Trim(), "StationOnly", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(rule.StationName))
            .Select(rule => rule.StationName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

        // StationOnly must win over other modes for the same station identity, but still
        // respect an older rule's explicit device restriction. A blank target means all devices.
        // Exact matches are preferred. A narrowly-scoped fallback also ignores a recognized
        // trailing stream-quality suffix (for example, "OZFM - 96k" vs "OZFM"). This is
        // intentional for StationOnly because stations are often detected by their short name,
        // while the saved rule was created from a bitrate-labelled .strm filename. We do not
        // strip arbitrary suffixes or apply this relaxed match to Automatic/Carplay rules.
        foreach (var alias in aliases)
        {
            var stationRule = rules.FirstOrDefault(candidate => candidate is not null &&
                string.Equals((candidate.StationName ?? string.Empty).Trim(), alias, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals((stationRule?.Mode ?? string.Empty).Trim(), "StationOnly", StringComparison.OrdinalIgnoreCase))
                continue;

            var legacyTargetDevice = (stationRule!.MetadataDelayDeviceName ?? string.Empty).Trim();
            if (legacyTargetDevice.Length > 0 &&
                !string.Equals(legacyTargetDevice, (deviceName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            return new RadioStationMetadataModeDiagnostic
            {
                Mode = "StationOnly",
                LookupDisabled = true,
                MatchedRuleStationName = stationRule.StationName.Trim(),
                MatchType = "exact station identity match",
                ConfiguredStationOnlyRuleNames = stationOnlyNames
            };
        }

        // A stream title such as "OZFM" may omit the bitrate suffix included in the saved
        // station rule name ("OZFM - 96k"). Consider only StationOnly rules here so normal
        // Automatic or Carplay behavior keeps its existing exact-match semantics. An explicit
        // StationOnly rule still takes precedence even when another alias resolves differently.
        foreach (var alias in aliases)
        {
            var normalizedAlias = NormalizeStationOnlyIdentity(alias);
            if (normalizedAlias.Length == 0)
                continue;

            foreach (var stationRule in rules.Where(candidate => candidate is not null &&
                string.Equals((candidate.Mode ?? string.Empty).Trim(), "StationOnly", StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.Equals(NormalizeStationOnlyIdentity(stationRule!.StationName), normalizedAlias,
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                var legacyTargetDevice = (stationRule.MetadataDelayDeviceName ?? string.Empty).Trim();
                if (legacyTargetDevice.Length > 0 &&
                    !string.Equals(legacyTargetDevice, (deviceName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                return new RadioStationMetadataModeDiagnostic
                {
                    Mode = "StationOnly",
                    LookupDisabled = true,
                    MatchedRuleStationName = stationRule.StationName.Trim(),
                    MatchType = "station identity match after ignoring bitrate suffix",
                    ConfiguredStationOnlyRuleNames = stationOnlyNames
                };
            }
        }

        // Exact station rules take priority over device-wide rules. A station-specific rule with
        // a legacy device restriction is applicable only to that exact device, matching the UI.
        foreach (var alias in aliases)
        {
            var stationRule = rules.FirstOrDefault(candidate => candidate is not null &&
                string.Equals((candidate.StationName ?? string.Empty).Trim(), alias, StringComparison.OrdinalIgnoreCase));
            if (stationRule is null)
                continue;

            var legacyTargetDevice = (stationRule.MetadataDelayDeviceName ?? string.Empty).Trim();
            if (legacyTargetDevice.Length > 0 &&
                !string.Equals(legacyTargetDevice, (deviceName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            var mode = NormalizeMode(stationRule.Mode);
            return new RadioStationMetadataModeDiagnostic
            {
                Mode = mode,
                LookupDisabled = false,
                MatchedRuleStationName = stationRule.StationName.Trim(),
                MatchType = "exact station identity match",
                ConfiguredStationOnlyRuleNames = stationOnlyNames
            };
        }

        var device = (deviceName ?? string.Empty).Trim();
        if (device.Length > 0)
        {
            var deviceRule = rules.FirstOrDefault(candidate => candidate is not null &&
                string.Equals((candidate.StationName ?? string.Empty).Trim(), device, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(candidate.MetadataDelayDeviceName));
            if (deviceRule is not null)
            {
                var mode = NormalizeMode(deviceRule.Mode);
                return new RadioStationMetadataModeDiagnostic
                {
                    Mode = mode,
                    LookupDisabled = string.Equals(mode, "StationOnly", StringComparison.OrdinalIgnoreCase),
                    MatchedRuleStationName = deviceRule.StationName.Trim(),
                    MatchType = "exact device-name match",
                    ConfiguredStationOnlyRuleNames = stationOnlyNames
                };
            }
        }

        return new RadioStationMetadataModeDiagnostic
        {
            Mode = "Automatic",
            LookupDisabled = false,
            MatchedRuleStationName = string.Empty,
            MatchType = aliases.Count > 0 ? "no matching station rule" : "station identity unavailable",
            ConfiguredStationOnlyRuleNames = stationOnlyNames
        };
    }

    private static readonly Regex TrailingStationBitrateSuffixRegex = new(
        @"\s*(?:[-‐‑‒–—−]\s*|\(\s*|\s+)\d{2,4}\s*(?:k(?:bps|bit/s|bit)?|kb/s|bps|bit/s)(?:\s*\))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Removes only a recognized trailing bitrate marker used in some .strm names.
    /// It deliberately does not simplify arbitrary station names or punctuation.
    /// </summary>
    private static string NormalizeStationOnlyIdentity(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return string.Empty;

        var match = TrailingStationBitrateSuffixRegex.Match(normalized);
        if (match.Success)
            normalized = normalized[..match.Index].TrimEnd();

        return normalized;
    }

    private static string NormalizeMode(string? value) =>
        string.Equals((value ?? string.Empty).Trim(), "Carplay", StringComparison.OrdinalIgnoreCase)
            ? "Carplay"
            : string.Equals((value ?? string.Empty).Trim(), "StationOnly", StringComparison.OrdinalIgnoreCase)
                ? "StationOnly"
                : string.Equals((value ?? string.Empty).Trim(), "StationDelay", StringComparison.OrdinalIgnoreCase)
                    ? "StationDelay"
                    : "Automatic";

    /// <summary>
    /// Returns true when the applicable station/device rule uses Carplay mode.
    /// Carplay transition-clearing behavior is intentionally distinct from StationDelay.
    /// </summary>
    public bool IsStationCarplayMode(string? stationName, string? deviceName = null)
    {
        var rule = FindApplicableStationMetadataRule(stationName, deviceName);
        return string.Equals(
            (rule?.Mode ?? string.Empty).Trim(),
            "Carplay",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true for the station-scoped delay mode. It uses normal metadata
    /// lookup but accepts the first valid track immediately after entering the
    /// station; the configured stability delay applies to later track changes.
    /// </summary>
    public bool IsStationDelayMode(string? stationName, string? deviceName = null)
    {
        var rule = FindApplicableStationMetadataRule(stationName, deviceName);
        return string.Equals(
            (rule?.Mode ?? string.Empty).Trim(),
            "StationDelay",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the applicable station/device metadata stability delay. A station
    /// rule overrides a device-wide rule; older XML entries remain supported.
    /// </summary>
    public int GetStationMetadataDelaySeconds(string? stationName, string? deviceName = null)
    {
        var rule = FindApplicableStationMetadataRule(stationName, deviceName);
        return rule is null ? 0 : Math.Clamp(rule.MetadataDelaySeconds, 0, 30);
    }

    private static readonly Regex TrailingBracketCode4Regex = new(
        @"\s+\[([A-Za-z0-9]{4})\]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TrailingSquareBracketAnyRegex = new(
        @"\s*\[([^\[\]]+)\]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TrailingParenthesisAnyRegex = new(
        @"\s*\(([^()]*)\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> BuiltInPreservedSuffixLabels = new(
        new[] { "live", "edit", "demo", "mono", "full", "remastered", "radio edit", "acoustic", "explicit", "instrumental", "studio" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies an exact station/incoming-title artist override saved from Play History.</summary>
    public string CleanSongArtist(string? stationName, string? songTitle, string? artist)
    {
        var incoming = RadioMetaValueHelpers.NormalizeHtmlText(songTitle);
        var normalizedArtist = RadioMetaValueHelpers.NormalizeHtmlText(artist);
        if (incoming.Length == 0)
        {
            return normalizedArtist;
        }

        var station = (stationName ?? string.Empty).Trim();
        var corrections = _plugin.Configuration.TitleCorrections;
        if (corrections is null || corrections.Count == 0)
        {
            return normalizedArtist;
        }

        var correction = corrections.FirstOrDefault(rule =>
        {
            if (rule is null ||
                !string.Equals((rule.StationName ?? string.Empty).Trim(), station, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(rule.CorrectedArtist))
            {
                return false;
            }

            var originalTitle = (rule.OriginalTitle ?? string.Empty).Trim();
            if (string.Equals(originalTitle, incoming, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return originalTitle.IndexOf('&') >= 0 &&
                string.Equals(RadioMetaValueHelpers.NormalizeHtmlText(originalTitle), incoming, StringComparison.OrdinalIgnoreCase);
        });

        return correction is null
            ? normalizedArtist
            : RadioMetaValueHelpers.NormalizeHtmlText(correction.CorrectedArtist);
    }

    /// <summary>Applies exact station/title corrections first, followed by configured suffix rules. The raw value remains available for diagnostics.</summary>
    public string CleanSongTitle(string? stationName, string? songTitle)
    {
        var incoming = RadioMetaValueHelpers.NormalizeHtmlText(songTitle);
        if (incoming.Length == 0)
        {
            return incoming;
        }

        var station = (stationName ?? string.Empty).Trim();
        var cleaned = incoming;
        var corrections = _plugin.Configuration.TitleCorrections;
        if (corrections is not null)
        {
            var correction = corrections.FirstOrDefault(rule =>
            {
                if (rule is null ||
                    !string.Equals((rule.StationName ?? string.Empty).Trim(), station, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(rule.CorrectedTitle))
                {
                    return false;
                }

                var originalTitle = (rule.OriginalTitle ?? string.Empty).Trim();
                if (string.Equals(originalTitle, incoming, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Existing saved corrections may contain encoded punctuation. Decode
                // only those values that can contain an entity marker.
                return originalTitle.IndexOf('&') >= 0 &&
                    string.Equals(RadioMetaValueHelpers.NormalizeHtmlText(originalTitle), incoming, StringComparison.OrdinalIgnoreCase);
            });
            if (correction is not null)
            {
                cleaned = RadioMetaValueHelpers.NormalizeHtmlText(correction.CorrectedTitle);
            }
        }

        var rules = _plugin.Configuration.TitleCleanupRules;
        if (rules is null || rules.Count == 0 || cleaned.Length == 0)
        {
            return cleaned.Trim();
        }

        var applicableRules = rules.Where(rule =>
        {
            if (rule is null)
            {
                return false;
            }

            var configuredStation = (rule.StationName ?? string.Empty).Trim();
            return configuredStation.Length == 0 ||
                string.Equals(configuredStation, station, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        // Literal suffix rules remain intentionally simple and safe.
        foreach (var rule in applicableRules.Where(rule =>
            string.Equals(rule.RuleType, "ExactSuffix", StringComparison.OrdinalIgnoreCase)))
        {
            var suffix = (rule.Suffix ?? string.Empty).Trim();
            if (suffix.Length > 0 && cleaned.Length > suffix.Length &&
                cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^suffix.Length].TrimEnd();
            }
        }

        var bracketRules = applicableRules.Where(rule =>
            string.Equals(rule.RuleType, "TrailingBracketCode4", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule.RuleType, "TrailingSquareBracketAny", StringComparison.OrdinalIgnoreCase)).ToList();
        var parenthesisRules = applicableRules.Where(rule =>
            string.Equals(rule.RuleType, "TrailingParenthesisAny", StringComparison.OrdinalIgnoreCase)).ToList();

        var preserveSquare = new HashSet<string>(BuiltInPreservedSuffixLabels, StringComparer.OrdinalIgnoreCase);
        var preserveRound = new HashSet<string>(BuiltInPreservedSuffixLabels, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in bracketRules.Concat(parenthesisRules))
        {
            foreach (var label in (rule.PreserveLabels ?? string.Empty).Split(
                new[] { ',', ';', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalizedLabel = label.Trim().Trim('[', ']', '(', ')');
                if (normalizedLabel.Length > 0)
                {
                    preserveSquare.Add(normalizedLabel);
                    preserveRound.Add(normalizedLabel);
                }
            }
        }

        // Generic bracket/parenthesis rules remove only trailing groups, repeat
        // as necessary, preserve common meaningful labels, and never empty a title.
        while (true)
        {
            var useAny = bracketRules.Any(rule =>
                string.Equals(rule.RuleType, "TrailingSquareBracketAny", StringComparison.OrdinalIgnoreCase));
            var match = useAny ? TrailingSquareBracketAnyRegex.Match(cleaned) : TrailingBracketCode4Regex.Match(cleaned);
            if (!match.Success || preserveSquare.Contains(match.Groups[1].Value.Trim()))
            {
                break;
            }

            var shortened = cleaned[..match.Index].TrimEnd();
            if (shortened.Length == 0)
            {
                break;
            }
            cleaned = shortened;
        }

        if (parenthesisRules.Count > 0)
        {
            while (true)
            {
                var match = TrailingParenthesisAnyRegex.Match(cleaned);
                if (!match.Success || preserveRound.Contains(match.Groups[1].Value.Trim()))
                {
                    break;
                }

                var shortened = cleaned[..match.Index].TrimEnd();
                if (shortened.Length == 0)
                {
                    break;
                }
                cleaned = shortened;
            }
        }

        return cleaned.Trim();
    }

}
