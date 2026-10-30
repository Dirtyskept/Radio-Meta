using System;
using System.Collections.Generic;

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
    private readonly RadioMetaPlugin _plugin;
    private readonly RadioMetadataPolicyService _metadataPolicy;
    private readonly RadioCurrentTrackStateStore _state = new();
    private readonly RadioCurrentTrackPublisher _publisher = new();

    public RadioCurrentTrackService(RadioMetaPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _metadataPolicy = new RadioMetadataPolicyService(_plugin);
    }

    /// <summary>Applies configured metadata-ignore rules. Kept as a stable forwarding API.</summary>
    public bool IsMetadataPhraseIgnored(string? stationName, string? candidate) =>
        _metadataPolicy.IsMetadataPhraseIgnored(stationName, candidate);

    /// <summary>Returns whether metadata lookup is disabled for this station/device scope.</summary>
    public bool IsStationMetadataLookupDisabled(string? stationName, string? deviceName = null) =>
        _metadataPolicy.IsStationMetadataLookupDisabled(stationName, deviceName);

    /// <summary>Returns true when any identity associated with the active playback matches StationOnly.</summary>
    public bool IsStationMetadataLookupDisabledForAny(IEnumerable<string?> stationNames, string? deviceName = null) =>
        _metadataPolicy.GetStationMetadataModeDiagnostic(stationNames, deviceName).LookupDisabled;

    /// <summary>Explains the effective metadata mode and which saved rule (if any) matched.</summary>
    internal RadioStationMetadataModeDiagnostic GetStationMetadataModeDiagnostic(
        IEnumerable<string?> stationNames, string? deviceName = null) =>
        _metadataPolicy.GetStationMetadataModeDiagnostic(stationNames, deviceName);

    /// <summary>Returns whether the applicable rule uses Carplay mode.</summary>
    public bool IsStationCarplayMode(string? stationName, string? deviceName = null) =>
        _metadataPolicy.IsStationCarplayMode(stationName, deviceName);

    /// <summary>Returns whether the applicable rule is StationDelay mode.</summary>
    public bool IsStationDelayMode(string? stationName, string? deviceName = null) =>
        _metadataPolicy.IsStationDelayMode(stationName, deviceName);

    /// <summary>Returns the configured station/device stability delay.</summary>
    public int GetStationMetadataDelaySeconds(string? stationName, string? deviceName = null) =>
        _metadataPolicy.GetStationMetadataDelaySeconds(stationName, deviceName);

    /// <summary>Restarts any in-progress stability windows after station settings change.</summary>
    public void ResetPendingMetadataDelays() => _state.ResetPendingMetadataDelays();


    private bool HasPendingMetadataDelay(string sessionId, string stationName, string? deviceName) =>
        GetStationMetadataDelaySeconds(stationName, deviceName) > 0 &&
        _state.HasPendingCandidate(sessionId);

    /// <summary>Applies an exact saved artist correction for a station and incoming title.</summary>
    public string CleanSongArtist(string? stationName, string? songTitle, string? artist) =>
        _metadataPolicy.CleanSongArtist(stationName, songTitle, artist);

    /// <summary>Applies configured exact corrections and title cleanup rules.</summary>
    public string CleanSongTitle(string? stationName, string? songTitle) =>
        _metadataPolicy.CleanSongTitle(stationName, songTitle);

    /// <summary>Marks which station a tracked Emby playback session is playing, even if metadata is unavailable.</summary>
    public void MarkStationActive(
        string? sessionId,
        string? stationName,
        string? stationPath = null,
        string? deviceName = null) =>
        _state.MarkStationActive(sessionId, stationName, stationPath, deviceName);

    /// <summary>Gets station paths for active sessions when automatic folder mode is enabled.</summary>
    public IReadOnlyList<(string StationName, string Path)> GetActiveStationPaths() =>
        _state.GetActiveStationPaths();

    /// <summary>Gets the active station .strm path for one playback session, when known.</summary>
    internal string? GetActiveStationPath(string? sessionId) => _state.GetActiveStationPath(sessionId);

    /// <summary>Gets the active Emby client name for one playback session without exposing its ID.</summary>
    internal string? GetActiveDeviceName(string? sessionId) => _state.GetActiveDeviceName(sessionId);

    public bool IsStationActive(string? stationName) => _state.IsStationActive(stationName);

    /// <summary>Returns the latest accepted track for a station when one or more sessions are playing it.</summary>
    public RadioCurrentTrack? GetCurrentTrackForStation(string? stationName) =>
        _state.GetCurrentTrackForStation(stationName);

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
        var rawSongTitle = (songTitle ?? string.Empty).Trim();
        var normalizedArtist = _metadataPolicy.CleanSongArtist(normalizedStation, rawSongTitle, artist);
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
        var isStationDelayMode = IsStationDelayMode(normalizedStation, deviceName);
        var hasAcceptedTrackForSession = _state.HasAcceptedTrackForSession(normalizedSession);

        // StationDelay means: show the current song as soon as we arrive at a
        // station, then debounce later track changes. EndSession clears the
        // accepted-track marker on station changes. Carplay's established behavior
        // is intentionally not changed by this new mode.
        var bypassInitialStationDelay = isStationDelayMode && !hasAcceptedTrackForSession;

        if (delaySeconds <= 0 || bypassInitialStationDelay)
        {
            _state.ClearPendingCandidate(normalizedSession);
        }
        else
        {
            // Re-observing the currently accepted title cancels any transient
            // candidate. We never debounce a stable title simply because the
            // same title is reported repeatedly by the reader.
            if (_state.TryGetCurrentTrack(normalizedSession, out var currentTrack) &&
                currentTrack is not null &&
                string.Equals(
                    BuildTrackKey(currentTrack.StationName, currentTrack.Artist, currentTrack.SongTitle),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                _state.ClearPendingCandidate(normalizedSession);
            }
            else
            {
                // A changed candidate starts a fresh stability window. Repeated
                // observations of the same candidate keep the original start
                // time; a different title/artist resets it.
                var pending = _state.GetOrUpdatePendingCandidate(normalizedSession, key, now);

                if (now - pending.FirstSeen < TimeSpan.FromSeconds(delaySeconds))
                {
                    isWaitingForStabilityDelay = true;
                    rejectionReason = $"Waiting for this metadata to remain unchanged for {delaySeconds} second(s).";
                    return false;
                }

                _state.ClearPendingCandidate(normalizedSession);
            }
        }

        shouldRecordHistory = _state.ShouldRecordHistory(normalizedSession, key);

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

        _state.SetCurrentTrack(normalizedSession, track);
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

    /// <summary>Publishes accepted track metadata as machine-readable ProviderIds on the active Emby session DTO.</summary>
    public bool TryPublishToSessionItem(object item, RadioCurrentTrack track) =>
        _publisher.TryPublishToSessionItem(item, track);

    /// <summary>True when the session item carries Radio Meta's machine-readable metadata.</summary>
    public bool HasPublishedRadioMetaMetadata(object? item) =>
        _publisher.HasPublishedRadioMetaMetadata(item);

    /// <summary>Removes Radio Meta's session ProviderIds without touching Emby display fields.</summary>
    public void ClearPublishedMetadata(object? item, string? sessionId = null) =>
        _publisher.ClearPublishedMetadata(item, sessionId);

    /// <summary>Removes only the current accepted value; history de-duplication is
    /// retained so a temporary metadata gap does not produce a duplicate entry.</summary>
    public void MarkUnavailable(string? sessionId) => _state.MarkUnavailable(sessionId);

    public void EndSession(string? sessionId) => _state.EndSession(sessionId);

    /// <summary>
    /// Removes accepted snapshots that no longer satisfy current station or
    /// ignore rules after configuration is edited. Session ProviderIds are
    /// cleared on the next playback progress event by the session publisher.
    /// </summary>
    public void RevalidateCurrentTracks()
    {
        foreach (var track in _state.CurrentTracks)
        {
            _state.TryGetActiveDeviceName(track.SessionId, out var activeDeviceName);
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

            var cleanedArtist = _metadataPolicy.CleanSongArtist(track.StationName, track.RawSongTitle, track.Artist);
            var cleanedTitle = CleanSongTitle(track.StationName, track.RawSongTitle);
            if (!string.Equals(cleanedArtist, track.Artist, StringComparison.Ordinal) ||
                !string.Equals(cleanedTitle, track.SongTitle, StringComparison.Ordinal))
            {
                _state.UpdateCurrentTrackAndHistoryKey(
                    track.SessionId,
                    new RadioCurrentTrack
                    {
                        SessionId = track.SessionId,
                        StationName = track.StationName,
                        Artist = cleanedArtist,
                        SongTitle = cleanedTitle,
                        RawSongTitle = track.RawSongTitle,
                        Source = track.Source,
                        UpdatedAt = track.UpdatedAt
                    },
                    BuildTrackKey(track.StationName, cleanedArtist, cleanedTitle));
            }
        }
    }

    public IReadOnlyList<RadioCurrentTrack> GetCurrentTracks() => _state.GetCurrentTracks();

    private static string BuildTrackKey(string station, string artist, string song) =>
        station + "\u001f" + artist + "\u001f" + song;
}
