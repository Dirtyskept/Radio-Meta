using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

public sealed class RadioPlayHistoryResponse
{
    public string Error { get; set; } = string.Empty;
    public int Limit { get; set; }
    public List<RadioPlayHistoryEntryDto> Items { get; set; } = new();
}

public sealed class RadioPlayHistoryEntryDto
{
    public Guid Id { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string OriginalSongTitle { get; set; } = string.Empty;
    public DateTimeOffset PlayedAt { get; set; }
}

[Route("/RadioMeta/History", "GET")]
public sealed class GetRadioPlayHistory : IReturn<RadioPlayHistoryResponse>
{
}

public sealed class RadioPlayHistoryMutationResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int RemovedCount { get; set; }
}

[Route("/RadioMeta/History/Delete", "POST")]
public sealed class DeleteRadioPlayHistoryEntry : IReturn<RadioPlayHistoryMutationResponse>
{
    public Guid Id { get; set; }
}

[Route("/RadioMeta/History/Clear", "POST")]
public sealed class ClearRadioPlayHistory : IReturn<RadioPlayHistoryMutationResponse>
{
}

public sealed class RadioHistoryTitleEditResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RuleDescription { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
}

[Route("/RadioMeta/History/Edit", "POST")]
public sealed class EditRadioPlayHistoryTitle : IReturn<RadioHistoryTitleEditResponse>
{
    public Guid Id { get; set; }
    public string CorrectedTitle { get; set; } = string.Empty;
    // SongOnly stores an exact station/title correction. Station infers a reusable suffix rule.
    public string Scope { get; set; } = "SongOnly";
    // Additional comma/semicolon-separated labels to preserve for a reusable station rule.
    public string PreserveLabels { get; set; } = string.Empty;
}

public sealed class RadioMetadataIgnorePhrasesResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioMetadataIgnorePhraseRule> Rules { get; set; } = new();
}

[Route("/RadioMeta/IgnorePhrases", "GET")]
public sealed class GetRadioMetadataIgnorePhrases : IReturn<RadioMetadataIgnorePhrasesResponse>
{
}

[Route("/RadioMeta/IgnorePhrases", "POST")]
public sealed class SaveRadioMetadataIgnorePhrases : IReturn<RadioMetadataIgnorePhrasesResponse>
{
    public List<RadioMetadataIgnorePhraseRule> Rules { get; set; } = new();
}

public sealed class RadioStationMetadataModesResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioStationMetadataModeRule> Rules { get; set; } = new();
}

[Route("/RadioMeta/StationModes", "GET")]
public sealed class GetRadioStationMetadataModes : IReturn<RadioStationMetadataModesResponse>
{
}

[Route("/RadioMeta/StationModes", "POST")]
public sealed class SaveRadioStationMetadataModes : IReturn<RadioStationMetadataModesResponse>
{
    public List<RadioStationMetadataModeRule> Rules { get; set; } = new();
}

public sealed class RadioTitleCleanupRulesResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioTitleCleanupRule> Rules { get; set; } = new();
}

[Route("/RadioMeta/TitleCleanup", "GET")]
public sealed class GetRadioTitleCleanupRules : IReturn<RadioTitleCleanupRulesResponse>
{
}

[Route("/RadioMeta/TitleCleanup", "POST")]
public sealed class SaveRadioTitleCleanupRules : IReturn<RadioTitleCleanupRulesResponse>
{
    public List<RadioTitleCleanupRule> Rules { get; set; } = new();
}

public sealed class RadioTitleCorrectionsResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioTitleCorrectionRule> Corrections { get; set; } = new();
}

[Route("/RadioMeta/TitleCorrections", "GET")]
public sealed class GetRadioTitleCorrections : IReturn<RadioTitleCorrectionsResponse>
{
}

[Route("/RadioMeta/TitleCorrections", "POST")]
public sealed class SaveRadioTitleCorrections : IReturn<RadioTitleCorrectionsResponse>
{
    public List<RadioTitleCorrectionRule> Corrections { get; set; } = new();
}


public sealed class RadioPathSettingsResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string StationFolder { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
    public string ExpandedStationFolder { get; set; } = string.Empty;
    public bool AutomaticStationDetection { get; set; }
    public bool StationFolderAvailable { get; set; }
    public string ResolvedFfprobePath { get; set; } = string.Empty;
    public bool FfprobeAvailable { get; set; }
}

[Route("/RadioMeta/PathSettings", "GET")]
public sealed class GetRadioPathSettings : IReturn<RadioPathSettingsResponse>
{
}

[Route("/RadioMeta/PathSettings", "POST")]
public sealed class SaveRadioPathSettings : IReturn<RadioPathSettingsResponse>
{
    public string StationFolder { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
}

public sealed class RadioCurrentTrackDto
{
    public string SessionId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string RawSongTitle { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RadioCurrentTracksResponse
{
    public string Error { get; set; } = string.Empty;
    public List<RadioCurrentTrackDto> Items { get; set; } = new();
}

[Route("/RadioMeta/CurrentTracks", "GET")]
public sealed class GetRadioCurrentTracks : IReturn<RadioCurrentTracksResponse>
{
}

[Authenticated]
public sealed class RadioPlayHistoryService : IService
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
        catch
        {
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
            if (station.Length == 0 || original.Length == 0 || corrected.Length == 0 ||
                station.Length > 160 || original.Length > 300 || corrected.Length > 300 ||
                string.Equals(original, corrected, StringComparison.Ordinal))
            {
                continue;
            }

            var normalized = new RadioTitleCorrectionRule
            {
                StationName = station,
                OriginalTitle = original,
                CorrectedTitle = corrected
            };
            next.RemoveAll(existing =>
                string.Equals(existing.StationName, normalized.StationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.OriginalTitle, normalized.OriginalTitle, StringComparison.OrdinalIgnoreCase));
            if (next.Count < 500) next.Add(normalized);
        }

        var configuration = plugin.Configuration;
        var previous = configuration.TitleCorrections;
        configuration.TitleCorrections = next;
        try
        {
            plugin.UpdateConfiguration(configuration);
            plugin.CurrentTrackService.RevalidateCurrentTracks();
        }
        catch
        {
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

    private static RadioTitleCleanupRule CloneTitleCleanupRule(RadioTitleCleanupRule rule) => new()
    {
        StationName = rule.StationName ?? string.Empty,
        RuleType = new[] { "ExactSuffix", "TrailingBracketCode4", "TrailingSquareBracketAny", "TrailingParenthesisAny" }
            .FirstOrDefault(known => string.Equals(rule.RuleType, known, StringComparison.OrdinalIgnoreCase)) ?? "TrailingBracketCode4",
        Suffix = rule.Suffix ?? string.Empty,
        PreserveLabels = rule.PreserveLabels ?? string.Empty
    };

    public object Get(GetRadioPathSettings request)
    {
        var plugin = RadioMetaPlugin.Instance;
        return plugin is null
            ? new RadioPathSettingsResponse { Error = "The Radio Meta plugin is not initialized." }
            : CreatePathSettingsResponse(plugin);
    }

    public object Post(SaveRadioPathSettings request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioPathSettingsResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        var stationFolder = (request.StationFolder ?? string.Empty).Trim();
        var ffprobePath = (request.FfprobePath ?? string.Empty).Trim();
        if (stationFolder.Length > 1024 || ffprobePath.Length > 1024)
        {
            return new RadioPathSettingsResponse
            {
                Error = "Paths must be 1024 characters or fewer."
            };
        }

        var configuration = plugin.Configuration;
        var previousStationFolder = configuration.StationFolder;
        var previousFfprobePath = configuration.FfprobePath;
        configuration.StationFolder = stationFolder;
        configuration.FfprobePath = ffprobePath;

        try
        {
            plugin.UpdateConfiguration(configuration);
            plugin.MetadataReader.ClearCaches();
            plugin.CurrentTrackService.RevalidateCurrentTracks();
        }
        catch
        {
            configuration.StationFolder = previousStationFolder;
            configuration.FfprobePath = previousFfprobePath;
            return new RadioPathSettingsResponse
            {
                Error = "The path settings could not be saved to the plugin XML configuration."
            };
        }

        var response = CreatePathSettingsResponse(plugin);
        response.Message = response.AutomaticStationDetection
            ? "Path settings saved. Station folder is blank, so automatic active audio .strm detection is enabled."
            : response.StationFolderAvailable
                ? "Path settings saved. The configured Station folder is available."
                : "Path settings saved, but the Station folder could not be found under the Emby process account. Check the path or clear it to enable automatic detection.";
        if (!string.IsNullOrWhiteSpace(response.FfprobePath) && !response.FfprobeAvailable)
        {
            response.Message += " The configured FFprobe path did not resolve to an existing executable; a bare command name may still resolve through PATH.";
        }

        return response;
    }

    private static RadioPathSettingsResponse CreatePathSettingsResponse(RadioMetaPlugin plugin)
    {
        var configuredFolder = plugin.GetConfiguredStationFolder();
        var expandedFolder = plugin.GetStationFolder();
        var resolvedFfprobe = plugin.GetFfprobePath();
        var automatic = configuredFolder.Length == 0;
        var folderAvailable = !automatic && !string.IsNullOrWhiteSpace(expandedFolder) && Directory.Exists(expandedFolder);
        return new RadioPathSettingsResponse
        {
            StationFolder = configuredFolder,
            FfprobePath = (plugin.Configuration.FfprobePath ?? string.Empty).Trim(),
            ExpandedStationFolder = expandedFolder,
            AutomaticStationDetection = automatic,
            StationFolderAvailable = folderAvailable,
            ResolvedFfprobePath = resolvedFfprobe,
            FfprobeAvailable = !string.IsNullOrWhiteSpace(resolvedFfprobe) && File.Exists(resolvedFfprobe)
        };
    }
    public object Get(GetRadioCurrentTracks request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioCurrentTracksResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        return new RadioCurrentTracksResponse
        {
            Items = plugin.CurrentTrackService.GetCurrentTracks()
                .Select(track => new RadioCurrentTrackDto
                {
                    SessionId = track.SessionId,
                    StationName = track.StationName,
                    Artist = track.Artist,
                    SongTitle = track.SongTitle,
                    RawSongTitle = track.RawSongTitle,
                    Source = track.Source,
                    UpdatedAt = track.UpdatedAt
                })
                .ToList()
        };
    }

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
        catch
        {
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
                !string.Equals(inputMode, "Carplay", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(inputMode, "Automatic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var mode = string.Equals(inputMode, "StationOnly", StringComparison.OrdinalIgnoreCase)
                ? "StationOnly"
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
        catch
        {
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

    public object Post(DeleteRadioPlayHistoryEntry request)
    {
        var history = RadioMetaPlugin.Instance?.HistoryStore;
        if (history is null)
        {
            return new RadioPlayHistoryMutationResponse
            {
                Error = "The Radio Meta history service is not initialized."
            };
        }

        if (!history.Remove(request.Id, out var error))
        {
            return new RadioPlayHistoryMutationResponse { Error = error };
        }

        return new RadioPlayHistoryMutationResponse
        {
            Message = "History entry deleted.",
            RemovedCount = 1
        };
    }

    public object Post(ClearRadioPlayHistory request)
    {
        var history = RadioMetaPlugin.Instance?.HistoryStore;
        if (history is null)
        {
            return new RadioPlayHistoryMutationResponse
            {
                Error = "The Radio Meta history service is not initialized."
            };
        }

        if (!history.Clear(out var removedCount, out var error))
        {
            return new RadioPlayHistoryMutationResponse { Error = error };
        }

        return new RadioPlayHistoryMutationResponse
        {
            Message = removedCount == 0
                ? "Radio play history was already empty."
                : "All radio play history was cleared.",
            RemovedCount = removedCount
        };
    }

    public object Post(EditRadioPlayHistoryTitle request)
    {
        var plugin = RadioMetaPlugin.Instance;
        var history = plugin?.HistoryStore;
        if (plugin is null || history is null)
        {
            return new RadioHistoryTitleEditResponse
            {
                Error = "The Radio Meta history service is not initialized."
            };
        }

        var correctedTitle = (request.CorrectedTitle ?? string.Empty).Trim();
        var preserveLabels = (request.PreserveLabels ?? string.Empty).Trim();
        if (preserveLabels.Length > 500)
        {
            return new RadioHistoryTitleEditResponse
            {
                Error = "The labels-to-preserve field is limited to 500 characters."
            };
        }

        if (request.Id == Guid.Empty || correctedTitle.Length == 0 || correctedTitle.Length > 300)
        {
            return new RadioHistoryTitleEditResponse
            {
                Error = "Choose a history entry and enter a corrected title (1–300 characters)."
            };
        }

        var entry = history.GetById(request.Id);
        if (entry is null)
        {
            return new RadioHistoryTitleEditResponse
            {
                Error = "That history entry no longer exists. Refresh the page and try again."
            };
        }

        var displayedTitle = (entry.SongTitle ?? string.Empty).Trim();
        var originalTitle = (string.IsNullOrWhiteSpace(entry.OriginalSongTitle)
            ? displayedTitle
            : entry.OriginalSongTitle.Trim());
        var station = (entry.StationName ?? string.Empty).Trim();
        var scope = string.Equals((request.Scope ?? string.Empty).Trim(), "Station", StringComparison.OrdinalIgnoreCase)
            ? "Station"
            : "SongOnly";
        if (scope == "SongOnly" && string.Equals(displayedTitle, correctedTitle, StringComparison.Ordinal))
        {
            return new RadioHistoryTitleEditResponse
            {
                Scope = "SongOnly",
                Message = "The corrected title matches the current title; nothing needed to change."
            };
        }
        var configuration = plugin.Configuration;
        var oldCorrections = configuration.TitleCorrections ?? new List<RadioTitleCorrectionRule>();
        var oldCleanupRules = configuration.TitleCleanupRules ?? new List<RadioTitleCleanupRule>();
        var nextCorrections = oldCorrections.Select(CloneTitleCorrection).ToList();
        var nextCleanupRules = oldCleanupRules.Select(CloneTitleCleanupRule).ToList();
        var ruleDescription = string.Empty;

        if (scope == "SongOnly")
        {
            nextCorrections.RemoveAll(rule =>
                string.Equals((rule.StationName ?? string.Empty).Trim(), station, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((rule.OriginalTitle ?? string.Empty).Trim(), originalTitle, StringComparison.OrdinalIgnoreCase));

            if (nextCorrections.Count >= 500)
            {
                return new RadioHistoryTitleEditResponse
                {
                    Error = "The limit of 500 exact title corrections has been reached. Remove an existing correction before adding another."
                };
            }

            nextCorrections.Add(new RadioTitleCorrectionRule
            {
                StationName = station,
                OriginalTitle = originalTitle,
                CorrectedTitle = correctedTitle
            });
        }
        else
        {
            if (!TryInferStationCleanupRule(station, originalTitle, correctedTitle, preserveLabels, out var inferredRule, out ruleDescription, out var inferError))
            {
                return new RadioHistoryTitleEditResponse
                {
                    Error = inferError,
                    Scope = "Station"
                };
            }

            var existingRuleIndex = nextCleanupRules.FindIndex(rule =>
                string.Equals((rule.StationName ?? string.Empty).Trim(), inferredRule.StationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((rule.RuleType ?? string.Empty).Trim(), inferredRule.RuleType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((rule.Suffix ?? string.Empty).Trim(), inferredRule.Suffix, StringComparison.OrdinalIgnoreCase));
            if (existingRuleIndex >= 0)
            {
                // Updating a rule through the inline editor also updates its preserve-label list.
                nextCleanupRules[existingRuleIndex] = inferredRule;
            }
            else
            {
                if (nextCleanupRules.Count >= 100)
                {
                    return new RadioHistoryTitleEditResponse
                    {
                        Error = "The limit of 100 reusable title cleanup rules has been reached. Remove an existing rule before adding another."
                    };
                }
                nextCleanupRules.Add(inferredRule);
            }
        }

        configuration.TitleCorrections = nextCorrections;
        configuration.TitleCleanupRules = nextCleanupRules;
        try
        {
            plugin.UpdateConfiguration(configuration);
        }
        catch
        {
            configuration.TitleCorrections = oldCorrections;
            configuration.TitleCleanupRules = oldCleanupRules;
            return new RadioHistoryTitleEditResponse
            {
                Error = "The correction could not be saved to the plugin XML configuration."
            };
        }

        if (!history.UpdateSongTitle(request.Id, correctedTitle, out _, out var historyError))
        {
            configuration.TitleCorrections = oldCorrections;
            configuration.TitleCleanupRules = oldCleanupRules;
            try { plugin.UpdateConfiguration(configuration); } catch { /* Preserve the primary history-save error. */ }
            return new RadioHistoryTitleEditResponse
            {
                Error = historyError
            };
        }

        plugin.MetadataReader.ClearCaches();
        plugin.CurrentTrackService.RevalidateCurrentTracks();

        return new RadioHistoryTitleEditResponse
        {
            Scope = scope,
            RuleDescription = ruleDescription,
            Message = scope == "SongOnly"
                ? "History title corrected. An exact correction for this station and incoming title was saved to XML and will be used for future matches."
                : "History title corrected. A reusable cleanup rule for this station was saved to XML: " + ruleDescription
        };
    }

    private static bool TryInferStationCleanupRule(
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

    private static RadioTitleCorrectionRule CloneTitleCorrection(RadioTitleCorrectionRule rule) => new()
    {
        StationName = rule.StationName ?? string.Empty,
        OriginalTitle = rule.OriginalTitle ?? string.Empty,
        CorrectedTitle = rule.CorrectedTitle ?? string.Empty
    };

    public object Get(GetRadioPlayHistory request)
    {
        var plugin = RadioMetaPlugin.Instance;
        var history = plugin?.HistoryStore;

        if (plugin is null || history is null)
        {
            return new RadioPlayHistoryResponse
            {
                Error = "The Radio Meta history service is not initialized."
            };
        }

        var limit = plugin.GetRadioHistoryLimit();
        var entries = history.GetRecent(limit)
            .Select(item => new RadioPlayHistoryEntryDto
            {
                Id = item.Id,
                StationName = item.StationName,
                Artist = item.Artist,
                SongTitle = item.SongTitle,
                OriginalSongTitle = string.IsNullOrWhiteSpace(item.OriginalSongTitle) ? item.SongTitle : item.OriginalSongTitle,
                PlayedAt = item.PlayedAt
            })
            .ToList();

        return new RadioPlayHistoryResponse
        {
            Limit = limit,
            Items = entries
        };
    }
}
