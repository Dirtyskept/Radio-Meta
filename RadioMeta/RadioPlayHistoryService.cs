using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

[Authenticated(Roles = "Admin")]
public sealed class RadioPlayHistoryService : IService
{
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
        var correctedArtist = (request.CorrectedArtist ?? string.Empty).Trim();
        var preserveLabels = (request.PreserveLabels ?? string.Empty).Trim();
        if (preserveLabels.Length > 500)
        {
            return new RadioHistoryTitleEditResponse
            {
                Error = "The labels-to-preserve field is limited to 500 characters."
            };
        }

        if (request.Id == Guid.Empty || correctedTitle.Length == 0 || correctedTitle.Length > 300 || correctedArtist.Length > 300)
        {
            return new RadioHistoryTitleEditResponse
            {
                Error = "Choose a history entry and enter a corrected title (1–300 characters); the artist is limited to 300 characters."
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
        if (scope == "Station" && correctedArtist.Length > 0 &&
            !string.Equals(correctedArtist, (entry.Artist ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            return new RadioHistoryTitleEditResponse
            {
                Error = "Artist corrections apply to This song only. Switch the correction scope to This song only to change the artist.",
                Scope = "Station"
            };
        }

        if (scope == "SongOnly" &&
            string.Equals(displayedTitle, correctedTitle, StringComparison.Ordinal) &&
            (correctedArtist.Length == 0 || string.Equals((entry.Artist ?? string.Empty).Trim(), correctedArtist, StringComparison.Ordinal)))
        {
            return new RadioHistoryTitleEditResponse
            {
                Scope = "SongOnly",
                Message = "The corrected artist and title match the current values; nothing needed to change."
            };
        }
        var configuration = plugin.Configuration;
        var oldCorrections = configuration.TitleCorrections ?? new List<RadioTitleCorrectionRule>();
        var oldCleanupRules = configuration.TitleCleanupRules ?? new List<RadioTitleCleanupRule>();
        var nextCorrections = oldCorrections.Select(RadioMetadataRulesService.CloneTitleCorrection).ToList();
        var nextCleanupRules = oldCleanupRules.Select(RadioMetadataRulesService.CloneTitleCleanupRule).ToList();
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

            var existingCorrection = oldCorrections.FirstOrDefault(rule => rule is not null &&
                string.Equals((rule.StationName ?? string.Empty).Trim(), station, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((rule.OriginalTitle ?? string.Empty).Trim(), originalTitle, StringComparison.OrdinalIgnoreCase));
            var artistToSave = correctedArtist;
            if (string.Equals(correctedArtist, (entry.Artist ?? string.Empty).Trim(), StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(existingCorrection?.CorrectedArtist))
            {
                // When editing the title of an already-corrected history item, retain its existing artist override.
                artistToSave = existingCorrection.CorrectedArtist.Trim();
            }
            else if (string.Equals(correctedArtist, (entry.Artist ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                // An unchanged artist is not an override; let future incoming artist values pass through.
                artistToSave = string.Empty;
            }

            nextCorrections.Add(new RadioTitleCorrectionRule
            {
                StationName = station,
                OriginalTitle = originalTitle,
                CorrectedTitle = correctedTitle,
                CorrectedArtist = artistToSave
            });
        }
        else
        {
            if (!RadioMetadataRulesService.TryInferStationCleanupRule(station, originalTitle, correctedTitle, preserveLabels, out var inferredRule, out ruleDescription, out var inferError))
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
        catch (Exception ex)
        {
            plugin.DiagnosticEvents.Record("Dashboard API", "Configuration", "error", string.Empty, string.Empty, "Save history title correction failed while saving configuration (" + ex.GetType().Name + ").");
            configuration.TitleCorrections = oldCorrections;
            configuration.TitleCleanupRules = oldCleanupRules;
            return new RadioHistoryTitleEditResponse
            {
                Error = "The correction could not be saved to the plugin XML configuration."
            };
        }

        var artistForHistory = scope == "Station"
            ? (entry.Artist ?? string.Empty).Trim()
            : (correctedArtist.Length > 0 ? correctedArtist : (entry.Artist ?? string.Empty).Trim());
        if (!history.UpdateSongMetadata(request.Id, artistForHistory, correctedTitle, out _, out _, out var historyError))
        {
            configuration.TitleCorrections = oldCorrections;
            configuration.TitleCleanupRules = oldCleanupRules;
            try { plugin.UpdateConfiguration(configuration); }
            catch (Exception rollbackError)
            {
                plugin.DiagnosticEvents.Record("Dashboard API", "History", "error", string.Empty, string.Empty, "Rollback of history title configuration failed (" + rollbackError.GetType().Name + ").");
            }
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
                ? "History artist/title corrected. An exact correction for this station and incoming title was saved to XML and will be used for future matches."
                : "History title corrected. A reusable cleanup rule for this station was saved to XML: " + ruleDescription
        };
    }

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
