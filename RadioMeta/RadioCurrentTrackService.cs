using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RadioMeta;

/// <summary>
/// Canonical, accepted metadata for a radio playback session. This is the
/// plugin-side source of truth consumed by play history and published to the
/// active Emby session for browser integrations.
/// </summary>
public sealed class RadioCurrentTrack
{
    public string SessionId { get; init; } = string.Empty;
    public string StationName { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string SongTitle { get; init; } = string.Empty;
    // Exact parsed title before cleanup, retained for diagnostics and future rules.
    public string RawSongTitle { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Centralizes final metadata validation, current-track state, session handoff,
/// and history recording. Metadata readers remain source adapters: they fetch
/// candidates, but this service decides what becomes the accepted current track.
/// </summary>
public sealed class RadioCurrentTrackService
{
    private const string StationKey = "RadioMetaStation";
    private const string ArtistKey = "RadioMetaArtist";
    private const string SongKey = "RadioMetaSong";
    private const string SourceKey = "RadioMetaSource";
    private const string UpdatedAtKey = "RadioMetaUpdatedAt";

    // Remove these legacy session-only values when publishing/clearing so a stale value
    // from the previous RadioNowPlaying helper cannot override current RadioMeta metadata.
    private static readonly string[] LegacyProviderKeys =
    {
        "RadioNowPlayingStation",
        "RadioNowPlayingArtist",
        "RadioNowPlayingSong",
        "RadioNowPlayingSource",
        "RadioNowPlayingUpdatedAt"
    };

    private readonly RadioMetaPlugin _plugin;
    private readonly ConcurrentDictionary<string, RadioCurrentTrack> _currentBySession =
        new(StringComparer.OrdinalIgnoreCase);
    // Tracks a radio station session even while metadata is unavailable. This
    // lets other plugin endpoints avoid a second, conflicting live metadata read.
    private readonly ConcurrentDictionary<string, string> _activeStationsBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeStationPathsBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeDeviceNamesBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastHistoryKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingTrackCandidate> _pendingTrackCandidates =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record PendingTrackCandidate(string Key, DateTimeOffset FirstSeen);

    public RadioCurrentTrackService(RadioMetaPlugin plugin)
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
    public bool IsStationMetadataLookupDisabled(string? stationName, string? deviceName = null)
    {
        var station = (stationName ?? string.Empty).Trim();
        if (station.Length == 0)
        {
            return false;
        }

        // Historically, StationOnly was scoped to the station and did not use
        // MetadataDelayDeviceName. Preserve that behavior for older XML rules.
        var stationRule = _plugin.Configuration.StationMetadataModes?.FirstOrDefault(candidate =>
            candidate is not null &&
            string.Equals((candidate.StationName ?? string.Empty).Trim(), station,
                StringComparison.OrdinalIgnoreCase));
        if (string.Equals((stationRule?.Mode ?? string.Empty).Trim(), "StationOnly",
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rule = FindApplicableStationMetadataRule(station, deviceName);
        return string.Equals(
            (rule?.Mode ?? string.Empty).Trim(),
            "StationOnly",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when the applicable station/device rule uses Carplay mode.
    /// Carplay clears injected song values on station changes so the new station
    /// title/artwork can show while the configured metadata delay runs.
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
    /// Gets the applicable station/device metadata stability delay. A station
    /// rule overrides a device-wide rule; older XML entries remain supported.
    /// </summary>
    public int GetStationMetadataDelaySeconds(string? stationName, string? deviceName = null)
    {
        var rule = FindApplicableStationMetadataRule(stationName, deviceName);
        return rule is null ? 0 : Math.Clamp(rule.MetadataDelaySeconds, 0, 30);
    }

    /// <summary>Restarts any in-progress stability windows after station settings change.</summary>
    public void ResetPendingMetadataDelays() => _pendingTrackCandidates.Clear();


    private bool HasPendingMetadataDelay(string sessionId, string stationName, string? deviceName) =>
        GetStationMetadataDelaySeconds(stationName, deviceName) > 0 &&
        _pendingTrackCandidates.ContainsKey(sessionId);

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

    /// <summary>Applies exact station/title corrections first, followed by configured
    /// suffix rules. The raw value remains available to the caller and is retained
    /// on RadioCurrentTrack for diagnostics.</summary>
    public string CleanSongTitle(string? stationName, string? songTitle)
    {
        var incoming = (songTitle ?? string.Empty).Trim();
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
                rule is not null &&
                string.Equals((rule.StationName ?? string.Empty).Trim(), station, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((rule.OriginalTitle ?? string.Empty).Trim(), incoming, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(rule.CorrectedTitle));
            if (correction is not null)
            {
                cleaned = correction.CorrectedTitle.Trim();
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

    /// <summary>Marks which station a tracked Emby playback session is playing,
    /// even if the station has no valid track metadata yet.</summary>
    public void MarkStationActive(
        string? sessionId,
        string? stationName,
        string? stationPath = null,
        string? deviceName = null)
    {
        var session = (sessionId ?? string.Empty).Trim();
        var station = (stationName ?? string.Empty).Trim();
        if (session.Length > 0 && station.Length > 0)
        {
            _activeStationsBySession[session] = station;
            if (!string.IsNullOrWhiteSpace(stationPath))
            {
                _activeStationPathsBySession[session] = stationPath.Trim();
            }
            else
            {
                _activeStationPathsBySession.TryRemove(session, out _);
            }

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                _activeDeviceNamesBySession[session] = deviceName.Trim();
            }
            else
            {
                _activeDeviceNamesBySession.TryRemove(session, out _);
            }
        }
    }

    /// <summary>
    /// Gets currently active station paths. This is used when StationFolder is
    /// blank, because automatic mode can enumerate active .strm items but does
    /// not scan an arbitrary library for every possible station.
    /// </summary>
    public IReadOnlyList<(string StationName, string Path)> GetActiveStationPaths()
    {
        var result = new List<(string StationName, string Path)>();
        foreach (var active in _activeStationsBySession)
        {
            if (_activeStationPathsBySession.TryGetValue(active.Key, out var path) &&
                !string.IsNullOrWhiteSpace(path))
            {
                result.Add((active.Value, path));
            }
        }

        return result
            .GroupBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsStationActive(string? stationName)
    {
        var station = (stationName ?? string.Empty).Trim();
        return station.Length > 0 && _activeStationsBySession.Values.Any(active =>
            string.Equals(active, station, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the latest accepted track for a station when one or more
    /// Emby sessions are playing that station.</summary>
    public RadioCurrentTrack? GetCurrentTrackForStation(string? stationName)
    {
        var station = (stationName ?? string.Empty).Trim();
        if (station.Length == 0)
        {
            return null;
        }

        return _currentBySession.Values
            .Where(track => string.Equals(track.StationName, station, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(track => track.UpdatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Validates a reader's candidate and publishes the canonical in-memory
    /// current-track record. History is recorded separately, outside the Emby
    /// session lock, after the caller has verified that the session still plays
    /// the station. Rejected candidates never become current tracks.
    /// </summary>
    public bool TryAcceptTrack(
        string sessionId,
        string stationName,
        string? artist,
        string? songTitle,
        string? source,
        string? metadataError,
        string? streamTitle,
        string? deviceName,
        out RadioCurrentTrack? track,
        out string rejectionReason,
        out bool shouldRecordHistory,
        out bool isWaitingForStabilityDelay)
    {
        track = null;
        rejectionReason = string.Empty;
        shouldRecordHistory = false;
        isWaitingForStabilityDelay = false;

        var normalizedSession = (sessionId ?? string.Empty).Trim();
        var normalizedStation = (stationName ?? string.Empty).Trim();
        var normalizedArtist = (artist ?? string.Empty).Trim();
        var rawSongTitle = (songTitle ?? string.Empty).Trim();
        var normalizedSong = CleanSongTitle(normalizedStation, rawSongTitle);
        var normalizedSource = (source ?? string.Empty).Trim();

        if (normalizedSession.Length == 0 || normalizedStation.Length == 0)
        {
            rejectionReason = "Missing session or station identity.";
            MarkUnavailable(normalizedSession);
            return false;
        }

        if (IsStationMetadataLookupDisabled(normalizedStation, deviceName))
        {
            rejectionReason = "Track metadata lookup is disabled for this station.";
            MarkUnavailable(normalizedSession);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(metadataError))
        {
            if (HasPendingMetadataDelay(normalizedSession, normalizedStation, deviceName))
            {
                isWaitingForStabilityDelay = true;
                rejectionReason = "Waiting for usable metadata while the configured stability delay is in progress.";
                return false;
            }

            rejectionReason = "The metadata reader reported an error.";
            MarkUnavailable(normalizedSession);
            return false;
        }

        if (!RadioMetadataReader.IsUsableStreamTitle(normalizedSong))
        {
            if (HasPendingMetadataDelay(normalizedSession, normalizedStation, deviceName))
            {
                isWaitingForStabilityDelay = true;
                rejectionReason = "Waiting for a usable song title while the configured stability delay is in progress.";
                return false;
            }

            rejectionReason = "The candidate has no usable song title.";
            MarkUnavailable(normalizedSession);
            return false;
        }

        var combinedTitle = string.IsNullOrWhiteSpace(streamTitle)
            ? (normalizedArtist.Length > 0 ? normalizedArtist + " - " + rawSongTitle : rawSongTitle)
            : streamTitle.Trim();

        if (IsMetadataPhraseIgnored(normalizedStation, rawSongTitle) ||
            IsMetadataPhraseIgnored(normalizedStation, normalizedSong) ||
            IsMetadataPhraseIgnored(normalizedStation, combinedTitle) ||
            (normalizedArtist.Length > 0 &&
             (IsMetadataPhraseIgnored(normalizedStation, normalizedArtist + " - " + rawSongTitle) ||
              IsMetadataPhraseIgnored(normalizedStation, normalizedArtist + " - " + normalizedSong))))
        {
            if (HasPendingMetadataDelay(normalizedSession, normalizedStation, deviceName))
            {
                isWaitingForStabilityDelay = true;
                rejectionReason = "Waiting for a valid song title while the configured stability delay is in progress.";
                return false;
            }

            rejectionReason = "The candidate matched a configured ignore phrase.";
            MarkUnavailable(normalizedSession);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var key = BuildTrackKey(normalizedStation, normalizedArtist, normalizedSong);
        var delaySeconds = GetStationMetadataDelaySeconds(normalizedStation, deviceName);

        if (delaySeconds <= 0)
        {
            _pendingTrackCandidates.TryRemove(normalizedSession, out _);
        }
        else
        {
            // Re-observing the currently accepted title cancels any transient
            // candidate. We never debounce a stable title simply because the
            // same title is reported repeatedly by the reader.
            if (_currentBySession.TryGetValue(normalizedSession, out var currentTrack) &&
                string.Equals(
                    BuildTrackKey(currentTrack.StationName, currentTrack.Artist, currentTrack.SongTitle),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingTrackCandidates.TryRemove(normalizedSession, out _);
            }
            else
            {
                // A changed candidate starts a fresh stability window. Repeated
                // observations of the same candidate keep the original start
                // time; a different title/artist resets it.
                var pending = _pendingTrackCandidates.AddOrUpdate(
                    normalizedSession,
                    _ => new PendingTrackCandidate(key, now),
                    (_, existing) => string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase)
                        ? existing
                        : new PendingTrackCandidate(key, now));

                if (now - pending.FirstSeen < TimeSpan.FromSeconds(delaySeconds))
                {
                    isWaitingForStabilityDelay = true;
                    rejectionReason = $"Waiting for this metadata to remain unchanged for {delaySeconds} second(s).";
                    return false;
                }

                _pendingTrackCandidates.TryRemove(normalizedSession, out _);
            }
        }

        shouldRecordHistory = !_lastHistoryKeys.TryGetValue(normalizedSession, out var lastKey) ||
            !string.Equals(lastKey, key, StringComparison.OrdinalIgnoreCase);

        if (shouldRecordHistory)
        {
            _lastHistoryKeys[normalizedSession] = key;
        }

        track = new RadioCurrentTrack
        {
            SessionId = normalizedSession,
            StationName = normalizedStation,
            Artist = normalizedArtist,
            SongTitle = normalizedSong,
            RawSongTitle = rawSongTitle,
            Source = normalizedSource.Length > 0 ? normalizedSource : "Unknown",
            UpdatedAt = now
        };

        _currentBySession[normalizedSession] = track;
        return true;
    }

    /// <summary>Writes an accepted track to persistent history at most once per
    /// track change in this session. The store also deduplicates across sessions.</summary>
    public bool RecordAcceptedTrack(RadioCurrentTrack track, bool shouldRecordHistory)
    {
        if (!shouldRecordHistory || track is null)
        {
            return false;
        }

        return _plugin.HistoryStore?.Record(
            track.StationName,
            track.Artist,
            track.SongTitle,
            track.RawSongTitle) == true;
    }

    /// <summary>
    /// Publishes accepted track metadata as machine-readable Radio Meta ProviderIds
    /// on the active session DTO. It deliberately does not write display fields such
    /// as Name, OriginalTitle, Artists, AlbumArtist, or Album; the Emby Web integration
    /// owns presentation, theme styling, and line layout.
    /// </summary>
    public bool TryPublishToSessionItem(object item, RadioCurrentTrack track)
    {
        if (item is null || track is null)
        {
            return false;
        }

        return TryPublishProviderIds(item, track);
    }

    /// <summary>
    /// True when the session item carries Radio Meta's machine-readable metadata.
    /// The method name is intentionally separate from display behavior: ProviderIds
    /// are a data handoff, not a presentation override.
    /// </summary>
    public bool HasPublishedRadioMetaMetadata(object? item) => HasRadioMetaProviderIds(item);

    /// <summary>
    /// Removes Radio Meta's session ProviderIds. The provider-only implementation
    /// does not alter or restore display fields, because it never changes them.
    /// The optional sessionId parameter is retained for existing internal call sites.
    /// </summary>
    public void ClearPublishedMetadata(object? item, string? sessionId = null)
    {
        ClearRadioMetaProviderIds(item);
    }

    private bool TryPublishProviderIds(object item, RadioCurrentTrack track)
    {
        try
        {
            var property = item.GetType().GetProperty("ProviderIds");
            if (property is null)
            {
                return false;
            }

            var value = property.GetValue(item);
            if (value is null)
            {
                if (!property.CanWrite)
                {
                    return false;
                }

                var propertyType = property.PropertyType;
                object? created = propertyType.IsInterface || propertyType.IsAbstract
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : Activator.CreateInstance(propertyType);

                if (created is null || !propertyType.IsInstanceOfType(created))
                {
                    return false;
                }

                property.SetValue(item, created);
                value = created;
            }

            if (value is IDictionary<string, string> genericIds)
            {
                foreach (var legacyKey in LegacyProviderKeys) genericIds.Remove(legacyKey);
                genericIds[StationKey] = track.StationName;
                genericIds[ArtistKey] = track.Artist;
                genericIds[SongKey] = track.SongTitle;
                return true;
            }

            if (value is IDictionary dictionaryIds)
            {
                foreach (var legacyKey in LegacyProviderKeys) dictionaryIds.Remove(legacyKey);
                dictionaryIds[StationKey] = track.StationName;
                dictionaryIds[ArtistKey] = track.Artist;
                dictionaryIds[SongKey] = track.SongTitle;
                return true;
            }
        }
        catch
        {
            // A failed ProviderIds handoff must not break playback or history.
        }

        return false;
    }






    private static bool HasRadioMetaProviderIds(object? item)
    {
        if (item is null)
        {
            return false;
        }

        try
        {
            var value = item.GetType().GetProperty("ProviderIds")?.GetValue(item);
            if (value is IDictionary<string, string> genericIds)
            {
                return genericIds.ContainsKey(StationKey) || genericIds.ContainsKey(SongKey) ||
                    genericIds.ContainsKey(ArtistKey);
            }

            if (value is IDictionary dictionaryIds)
            {
                return dictionaryIds.Contains(StationKey) || dictionaryIds.Contains(SongKey) ||
                    dictionaryIds.Contains(ArtistKey);
            }
        }
        catch
        {
            // Optional marker inspection must not affect playback.
        }

        return false;
    }

    private static void ClearRadioMetaProviderIds(object? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var property = item.GetType().GetProperty("ProviderIds");
            var value = property?.GetValue(item);
            if (value is IDictionary<string, string> genericIds)
            {
                genericIds.Remove(StationKey);
                genericIds.Remove(ArtistKey);
                genericIds.Remove(SongKey);
                genericIds.Remove(SourceKey);
                genericIds.Remove(UpdatedAtKey);
                foreach (var legacyKey in LegacyProviderKeys) genericIds.Remove(legacyKey);
            }
            else if (value is IDictionary dictionaryIds)
            {
                dictionaryIds.Remove(StationKey);
                dictionaryIds.Remove(ArtistKey);
                dictionaryIds.Remove(SongKey);
                dictionaryIds.Remove(SourceKey);
                dictionaryIds.Remove(UpdatedAtKey);
                foreach (var legacyKey in LegacyProviderKeys) dictionaryIds.Remove(legacyKey);
            }
        }
        catch
        {
            // ProviderIds clearing is best effort and must never interrupt playback.
        }
    }

    /// <summary>Removes only the current accepted value; history de-duplication is
    /// retained so a temporary metadata gap does not produce a duplicate entry.</summary>
    public void MarkUnavailable(string? sessionId)
    {
        var key = (sessionId ?? string.Empty).Trim();
        if (key.Length > 0)
        {
            _currentBySession.TryRemove(key, out _);
            _pendingTrackCandidates.TryRemove(key, out _);
        }
    }

    /// <summary>Ends tracking for a session or resets it when the session switches
    /// to a different station.</summary>
    public void EndSession(string? sessionId)
    {
        var key = (sessionId ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return;
        }

        _currentBySession.TryRemove(key, out _);
        _pendingTrackCandidates.TryRemove(key, out _);
        _activeStationsBySession.TryRemove(key, out _);
        _activeStationPathsBySession.TryRemove(key, out _);
        _activeDeviceNamesBySession.TryRemove(key, out _);
        _lastHistoryKeys.TryRemove(key, out _);
    }

    /// <summary>
    /// Removes accepted snapshots that no longer satisfy current station or
    /// ignore rules after configuration is edited. Session ProviderIds are
    /// cleared on the next playback progress event by the session publisher.
    /// </summary>
    public void RevalidateCurrentTracks()
    {
        foreach (var track in _currentBySession.Values)
        {
            _activeDeviceNamesBySession.TryGetValue(track.SessionId, out var activeDeviceName);
            var stationDisabled = IsStationMetadataLookupDisabled(track.StationName, activeDeviceName);
            var phraseIgnored = IsMetadataPhraseIgnored(track.StationName, track.RawSongTitle) ||
                IsMetadataPhraseIgnored(track.StationName, track.SongTitle) ||
                IsMetadataPhraseIgnored(track.StationName, track.Artist + " - " + track.RawSongTitle) ||
                IsMetadataPhraseIgnored(track.StationName, track.Artist + " - " + track.SongTitle);
            if (stationDisabled || phraseIgnored)
            {
                MarkUnavailable(track.SessionId);
                continue;
            }

            var cleanedTitle = CleanSongTitle(track.StationName, track.RawSongTitle);
            if (!string.Equals(cleanedTitle, track.SongTitle, StringComparison.Ordinal))
            {
                _currentBySession[track.SessionId] = new RadioCurrentTrack
                {
                    SessionId = track.SessionId,
                    StationName = track.StationName,
                    Artist = track.Artist,
                    SongTitle = cleanedTitle,
                    RawSongTitle = track.RawSongTitle,
                    Source = track.Source,
                    UpdatedAt = track.UpdatedAt
                };
                _lastHistoryKeys[track.SessionId] = BuildTrackKey(track.StationName, track.Artist, cleanedTitle);
            }
        }
    }

    public IReadOnlyList<RadioCurrentTrack> GetCurrentTracks() => _currentBySession.Values
        .OrderBy(track => track.StationName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(track => track.SessionId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string BuildTrackKey(string station, string artist, string song) =>
        station + "\u001f" + artist + "\u001f" + song;
}
