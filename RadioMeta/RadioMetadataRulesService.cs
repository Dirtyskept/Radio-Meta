using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

[Authenticated(Roles = "Admin")]
public sealed class RadioMetadataRulesService : IService
{
    public object Get(GetRadioTitleCleanupRules request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioTitleCleanupRulesResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        return new RadioTitleCleanupRulesResponse
        {
            Rules = (plugin.Configuration.TitleCleanupRules ?? new List<RadioTitleCleanupRule>())
                .Where(rule => rule is not null && IsValidCleanupRule(rule))
                .Select(CloneTitleCleanupRule)
                .ToList()
        };
    }

    public object Post(SaveRadioTitleCleanupRules request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioTitleCleanupRulesResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        var normalizedRules = NormalizeTitleCleanupRules(request.Rules);
        var configuration = plugin.Configuration;
        var previousRules = configuration.TitleCleanupRules;
        configuration.TitleCleanupRules = normalizedRules;

        try
        {
            plugin.UpdateConfiguration(configuration);
            plugin.MetadataReader.ClearCaches();
            plugin.CurrentTrackService.RevalidateCurrentTracks();
        }
        catch (Exception ex)
        {
            plugin.DiagnosticEvents.Record("Dashboard API", "Configuration", "error", string.Empty, string.Empty, "Save title cleanup rules failed while saving configuration (" + ex.GetType().Name + ").");
            configuration.TitleCleanupRules = previousRules;
            return new RadioTitleCleanupRulesResponse
            {
                Error = "The title cleanup rules could not be saved to the plugin XML."
            };
        }

        return new RadioTitleCleanupRulesResponse
        {
            Rules = normalizedRules.Select(CloneTitleCleanupRule).ToList(),
            Message = "Title cleanup rules saved to the plugin XML and applied to future metadata."
        };
    }

    public object Get(GetRadioTitleCorrections request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioTitleCorrectionsResponse { Error = "The Radio Meta plugin is not initialized." };
        }

        var corrections = (plugin.Configuration.TitleCorrections ?? new List<RadioTitleCorrectionRule>())
            .Where(rule => rule is not null &&
                !string.IsNullOrWhiteSpace(rule.StationName) &&
                !string.IsNullOrWhiteSpace(rule.OriginalTitle) &&
                !string.IsNullOrWhiteSpace(rule.CorrectedTitle))
            .Select(CloneTitleCorrection)
            .OrderBy(rule => rule.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.OriginalTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RadioTitleCorrectionsResponse { Corrections = corrections };
    }

    public object Post(SaveRadioTitleCorrections request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioTitleCorrectionsResponse { Error = "The Radio Meta plugin is not initialized." };
        }

        var next = new List<RadioTitleCorrectionRule>();
        foreach (var incoming in (request.Corrections ?? new List<RadioTitleCorrectionRule>()).Take(1000))
        {
            if (incoming is null) continue;
            var station = (incoming.StationName ?? string.Empty).Trim();
            var original = (incoming.OriginalTitle ?? string.Empty).Trim();
            var corrected = (incoming.CorrectedTitle ?? string.Empty).Trim();
            var existingArtist = (plugin.Configuration.TitleCorrections ?? new List<RadioTitleCorrectionRule>())
                .FirstOrDefault(existing => existing is not null &&
                    string.Equals((existing.StationName ?? string.Empty).Trim(), station, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((existing.OriginalTitle ?? string.Empty).Trim(), original, StringComparison.OrdinalIgnoreCase))?.CorrectedArtist ?? string.Empty;
            var correctedArtist = (incoming.CorrectedArtist ?? string.Empty).Trim();
            if (station.Length == 0 || original.Length == 0 || corrected.Length == 0 ||
                station.Length > 160 || original.Length > 300 || corrected.Length > 300 ||
                (string.Equals(original, corrected, StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(correctedArtist) && string.IsNullOrWhiteSpace(existingArtist)))
            {
                continue;
            }

            var normalized = new RadioTitleCorrectionRule
            {
                StationName = station,
                OriginalTitle = original,
                CorrectedTitle = corrected,
                CorrectedArtist = correctedArtist.Length > 0 ? correctedArtist : existingArtist
            };
            next.RemoveAll(existing =>
                string.Equals(existing.StationName, normalized.StationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.OriginalTitle, normalized.OriginalTitle, StringComparison.OrdinalIgnoreCase));
            if (next.Count < 500) next.Add(normalized);
        }

        var configuration = plugin.Configuration;
        var previous = configuration.TitleCorrections ?? new List<RadioTitleCorrectionRule>();
        configuration.TitleCorrections = next;
        try
        {
            plugin.UpdateConfiguration(configuration);
            plugin.CurrentTrackService.RevalidateCurrentTracks();
        }
        catch (Exception ex)
        {
            plugin.DiagnosticEvents.Record("Dashboard API", "Configuration", "error", string.Empty, string.Empty, "Save exact title corrections failed while saving configuration (" + ex.GetType().Name + ").");
            configuration.TitleCorrections = previous;
            return new RadioTitleCorrectionsResponse { Error = "Exact title corrections could not be saved to the plugin XML." };
        }

        return new RadioTitleCorrectionsResponse
        {
            Corrections = next.Select(CloneTitleCorrection).ToList(),
            Message = "Exact title corrections saved to the plugin XML."
        };
    }

    private static bool IsValidCleanupRule(RadioTitleCleanupRule rule)
    {
        var kind = (rule.RuleType ?? string.Empty).Trim();
        if (string.Equals(kind, "TrailingBracketCode4", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "TrailingSquareBracketAny", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "TrailingParenthesisAny", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(kind, "ExactSuffix", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(rule.Suffix);
    }

    private static List<RadioTitleCleanupRule> NormalizeTitleCleanupRules(
        IEnumerable<RadioTitleCleanupRule>? input)
    {
        const int maximumRules = 100;
        const int maximumStationLength = 160;
        const int maximumSuffixLength = 120;
        const int maximumPreserveLabelsLength = 500;
        var result = new List<RadioTitleCleanupRule>();
        if (input is null)
        {
            return result;
        }

        foreach (var incoming in input.Take(500))
        {
            if (incoming is null)
            {
                continue;
            }

            var station = (incoming.StationName ?? string.Empty).Trim();
            var kindInput = (incoming.RuleType ?? string.Empty).Trim();
            var kind = new[] { "ExactSuffix", "TrailingBracketCode4", "TrailingSquareBracketAny", "TrailingParenthesisAny" }
                .FirstOrDefault(known => string.Equals(kindInput, known, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            var suffix = (incoming.Suffix ?? string.Empty).Trim();
            var preserve = (incoming.PreserveLabels ?? string.Empty).Trim();

            if (station.Length > maximumStationLength) station = station[..maximumStationLength];
            if (suffix.Length > maximumSuffixLength) suffix = suffix[..maximumSuffixLength];
            if (preserve.Length > maximumPreserveLabelsLength) preserve = preserve[..maximumPreserveLabelsLength];
            if (kind.Length == 0 || (kind == "ExactSuffix" && suffix.Length == 0)) continue;

            var normalized = new RadioTitleCleanupRule
            {
                StationName = station,
                RuleType = kind,
                Suffix = kind == "ExactSuffix" ? suffix : string.Empty,
                PreserveLabels = preserve
            };

            if (result.Any(existing =>
                string.Equals(existing.StationName, normalized.StationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.RuleType, normalized.RuleType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Suffix, normalized.Suffix, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.PreserveLabels, normalized.PreserveLabels, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (result.Count < maximumRules) result.Add(normalized);
        }

        return result;
    }

    internal static RadioTitleCleanupRule CloneTitleCleanupRule(RadioTitleCleanupRule rule) => new()
    {
        StationName = rule.StationName ?? string.Empty,
        RuleType = new[] { "ExactSuffix", "TrailingBracketCode4", "TrailingSquareBracketAny", "TrailingParenthesisAny" }
            .FirstOrDefault(known => string.Equals(rule.RuleType, known, StringComparison.OrdinalIgnoreCase)) ?? "TrailingBracketCode4",
        Suffix = rule.Suffix ?? string.Empty,
        PreserveLabels = rule.PreserveLabels ?? string.Empty
    };

    public object Get(GetRadioStationMetadataModes request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioStationMetadataModesResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        return new RadioStationMetadataModesResponse
        {
            Rules = (plugin.Configuration.StationMetadataModes ?? new List<RadioStationMetadataModeRule>())
                .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.StationName))
                .Select(CloneModeRule)
                .ToList()
        };
    }

    public object Post(SaveRadioStationMetadataModes request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioStationMetadataModesResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        var normalizedRules = NormalizeModeRules(request.Rules);
        var configuration = plugin.Configuration;
        var previousRules = configuration.StationMetadataModes;
        configuration.StationMetadataModes = normalizedRules;

        try
        {
            plugin.UpdateConfiguration(configuration);
            plugin.MetadataReader.ClearCaches();
            plugin.CurrentTrackService.ResetPendingMetadataDelays();
            plugin.CurrentTrackService.RevalidateCurrentTracks();
        }
        catch (Exception ex)
        {
            plugin.DiagnosticEvents.Record("Dashboard API", "Configuration", "error", string.Empty, string.Empty, "Save station metadata modes failed while saving configuration (" + ex.GetType().Name + ").");
            configuration.StationMetadataModes = previousRules;
            return new RadioStationMetadataModesResponse
            {
                Error = "The station metadata settings could not be saved."
            };
        }

        return new RadioStationMetadataModesResponse
        {
            Rules = normalizedRules.Select(CloneModeRule).ToList(),
            Message = "Station metadata settings saved and applied."
        };
    }

    private static List<RadioStationMetadataModeRule> NormalizeModeRules(
        IEnumerable<RadioStationMetadataModeRule>? input)
    {
        const int maximumRules = 100;
        const int maximumStationLength = 160;
        var result = new List<RadioStationMetadataModeRule>();
        if (input is null)
        {
            return result;
        }

        foreach (var incoming in input.Take(500))
        {
            if (incoming is null || string.IsNullOrWhiteSpace(incoming.StationName))
            {
                continue;
            }

            var stationName = incoming.StationName.Trim();
            if (stationName.Length > maximumStationLength)
            {
                stationName = stationName[..maximumStationLength];
            }

            var inputMode = (incoming.Mode ?? string.Empty).Trim();
            if (!string.Equals(inputMode, "StationOnly", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(inputMode, "StationDelay", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(inputMode, "Carplay", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(inputMode, "Automatic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var mode = string.Equals(inputMode, "StationOnly", StringComparison.OrdinalIgnoreCase)
                ? "StationOnly"
                : string.Equals(inputMode, "StationDelay", StringComparison.OrdinalIgnoreCase)
                    ? "StationDelay"
                    : string.Equals(inputMode, "Carplay", StringComparison.OrdinalIgnoreCase)
                        ? "Carplay"
                        : "Automatic";

            var delayDeviceName = (incoming.MetadataDelayDeviceName ?? string.Empty).Trim();
            if (delayDeviceName.Length > 120)
            {
                delayDeviceName = delayDeviceName[..120];
            }

            var normalized = new RadioStationMetadataModeRule
            {
                StationName = stationName,
                Mode = mode,
                MetadataDelaySeconds = Math.Clamp(incoming.MetadataDelaySeconds, 0, 30),
                MetadataDelayDeviceName = delayDeviceName
            };

            var existingIndex = result.FindIndex(existing =>
                string.Equals(existing.StationName, stationName, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                result[existingIndex] = normalized;
            }
            else if (result.Count < maximumRules)
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static RadioStationMetadataModeRule CloneModeRule(
        RadioStationMetadataModeRule rule) => new()
    {
        StationName = rule.StationName ?? string.Empty,
        Mode = string.Equals(rule.Mode, "StationOnly", StringComparison.OrdinalIgnoreCase)
            ? "StationOnly"
            : string.Equals(rule.Mode, "StationDelay", StringComparison.OrdinalIgnoreCase)
                ? "StationDelay"
                : string.Equals(rule.Mode, "Carplay", StringComparison.OrdinalIgnoreCase)
                    ? "Carplay"
                    : "Automatic",
        MetadataDelaySeconds = Math.Clamp(rule.MetadataDelaySeconds, 0, 30),
        MetadataDelayDeviceName = (rule.MetadataDelayDeviceName ?? string.Empty).Trim()
    };

    public object Get(GetRadioMetadataIgnorePhrases request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioMetadataIgnorePhrasesResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        return new RadioMetadataIgnorePhrasesResponse
        {
            Rules = (plugin.Configuration.IgnoreMetadataPhrases ?? new List<RadioMetadataIgnorePhraseRule>())
                .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.Phrase))
                .Select(CloneRule)
                .ToList()
        };
    }

    public object Post(SaveRadioMetadataIgnorePhrases request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioMetadataIgnorePhrasesResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        var normalizedRules = NormalizeRules(request.Rules);
        var configuration = plugin.Configuration;
        var previousRules = configuration.IgnoreMetadataPhrases;
        configuration.IgnoreMetadataPhrases = normalizedRules;

        try
        {
            plugin.UpdateConfiguration(configuration);
            plugin.MetadataReader.ClearCaches();
            plugin.CurrentTrackService.RevalidateCurrentTracks();
        }
        catch (Exception ex)
        {
            plugin.DiagnosticEvents.Record("Dashboard API", "Configuration", "error", string.Empty, string.Empty, "Save ignore phrase rules failed while saving configuration (" + ex.GetType().Name + ").");
            configuration.IgnoreMetadataPhrases = previousRules;
            return new RadioMetadataIgnorePhrasesResponse
            {
                Error = "The ignore phrase settings could not be saved."
            };
        }

        return new RadioMetadataIgnorePhrasesResponse
        {
            Rules = normalizedRules.Select(CloneRule).ToList(),
            Message = "Ignore phrase settings saved."
        };
    }

    private static List<RadioMetadataIgnorePhraseRule> NormalizeRules(
        IEnumerable<RadioMetadataIgnorePhraseRule>? input)
    {
        const int maximumRules = 100;
        const int maximumPhraseLength = 200;
        const int maximumStationLength = 160;

        var result = new List<RadioMetadataIgnorePhraseRule>();
        if (input is null)
        {
            return result;
        }

        foreach (var incoming in input.Take(500))
        {
            if (incoming is null || string.IsNullOrWhiteSpace(incoming.Phrase))
            {
                continue;
            }

            var phrase = incoming.Phrase.Trim();
            var stationName = (incoming.StationName ?? string.Empty).Trim();
            if (phrase.Length > maximumPhraseLength)
            {
                phrase = phrase[..maximumPhraseLength];
            }
            if (stationName.Length > maximumStationLength)
            {
                stationName = stationName[..maximumStationLength];
            }

            if (result.Any(existing =>
                string.Equals(existing.Phrase, phrase, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StationName, stationName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new RadioMetadataIgnorePhraseRule
            {
                StationName = stationName,
                Phrase = phrase
            });

            if (result.Count >= maximumRules)
            {
                break;
            }
        }

        return result;
    }

    private static RadioMetadataIgnorePhraseRule CloneRule(
        RadioMetadataIgnorePhraseRule rule) => new()
    {
        StationName = rule.StationName ?? string.Empty,
        Phrase = rule.Phrase ?? string.Empty
    };

    internal static bool TryInferStationCleanupRule(
        string station,
        string originalTitle,
        string correctedTitle,
        string preserveLabels,
        out RadioTitleCleanupRule rule,
        out string description,
        out string error)
    {
        rule = new RadioTitleCleanupRule();
        description = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(station))
        {
            error = "The history entry has no station name, so a station-wide rule cannot be created.";
            return false;
        }

        if (correctedTitle.Length >= originalTitle.Length ||
            !originalTitle.StartsWith(correctedTitle, StringComparison.OrdinalIgnoreCase))
        {
            error = "I couldn't infer a safe station-wide cleanup because the corrected title isn't a clean prefix of the original. Choose This song only or correct a trailing suffix.";
            return false;
        }

        var rawSuffix = originalTitle[correctedTitle.Length..];
        var suffix = rawSuffix.Trim();
        if (suffix.Length == 0 ||
            !(char.IsWhiteSpace(rawSuffix[0]) || "[(-_|/".Contains(rawSuffix[0])))
        {
            error = "I couldn't safely identify a separate suffix in this title. Choose This song only or correct a trailing suffix.";
            return false;
        }

        string ruleType;
        if (suffix.Length >= 3 && suffix[0] == '[' && suffix[^1] == ']' &&
            suffix[1..^1].Length > 0)
        {
            var label = suffix[1..^1].Trim();
            if (IsBuiltInPreservedLabel(label))
            {
                error = "This suffix is a common meaningful label and is preserved by default. Choose This song only if you intentionally want to remove it.";
                return false;
            }
            if (IsCustomPreservedLabel(label, preserveLabels))
            {
                error = "This suffix is in the labels-to-preserve field. Remove it from that field if you intend to clean it.";
                return false;
            }
            ruleType = "TrailingSquareBracketAny";
            description = "remove trailing square-bracket suffixes (for example, [ID123]); common labels are preserved";
        }
        else if (suffix.Length >= 3 && suffix[0] == '(' && suffix[^1] == ')' &&
                 suffix[1..^1].Length > 0)
        {
            var label = suffix[1..^1].Trim();
            if (IsBuiltInPreservedLabel(label))
            {
                error = "This suffix is a common meaningful label and is preserved by default. Choose This song only if you intentionally want to remove it.";
                return false;
            }
            if (IsCustomPreservedLabel(label, preserveLabels))
            {
                error = "This suffix is in the labels-to-preserve field. Remove it from that field if you intend to clean it.";
                return false;
            }
            ruleType = "TrailingParenthesisAny";
            description = "remove trailing parenthetical suffixes (for example, (Station Code)); common labels are preserved";
        }
        else
        {
            ruleType = "ExactSuffix";
            description = "remove this exact trailing suffix: " + suffix;
        }

        rule = new RadioTitleCleanupRule
        {
            StationName = station,
            RuleType = ruleType,
            Suffix = ruleType == "ExactSuffix" ? suffix : string.Empty,
            PreserveLabels = (preserveLabels ?? string.Empty).Trim()
        };
        return true;
    }

    private static bool IsCustomPreservedLabel(string label, string preserveLabels) =>
        (preserveLabels ?? string.Empty)
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim().Trim('[', ']', '(', ')'))
            .Any(value => string.Equals(value, label.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsBuiltInPreservedLabel(string label) =>
        new[] { "live", "edit", "demo", "mono", "full", "remastered", "radio edit", "acoustic", "explicit", "instrumental", "studio" }
            .Contains(label.Trim(), StringComparer.OrdinalIgnoreCase);

    internal static RadioTitleCorrectionRule CloneTitleCorrection(RadioTitleCorrectionRule rule) => new()
    {
        StationName = rule.StationName ?? string.Empty,
        OriginalTitle = rule.OriginalTitle ?? string.Empty,
        CorrectedTitle = rule.CorrectedTitle ?? string.Empty,
        CorrectedArtist = rule.CorrectedArtist ?? string.Empty
    };
}
