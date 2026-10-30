using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>
/// Runs the metadata-source pipeline for a playback session. Playback event routing,
/// session state, and station path classification live in their own components.
/// </summary>
internal sealed class RadioMetaSessionMetadataProcessor
{
    private readonly RadioMetaPlugin _plugin;
    private readonly ILogger _logger;
    private readonly RadioMetaPlaybackSessionStateStore _sessionState;
    private readonly RadioMetaPlaybackStationResolver _stationResolver;

    internal RadioMetaSessionMetadataProcessor(
        RadioMetaPlugin plugin,
        ILogger logger,
        RadioMetaPlaybackSessionStateStore sessionState,
        RadioMetaPlaybackStationResolver stationResolver)
    {
        _plugin = plugin;
        _logger = logger;
        _sessionState = sessionState;
        _stationResolver = stationResolver;
    }

    internal async Task UpdateSessionMetadataAsync(
        SessionInfo session,
        PlaySessionInfo originalPlaySession,
        string sessionKey,
        string stationPath)
    {
        try
        {
            var needsInitialDelay = !_sessionState.HasCompletedInitialDelay(sessionKey);
            var stationName = Path.GetFileNameWithoutExtension(stationPath);

            // Check Emby's own item metadata immediately. If it already has
            // both fields, no external metadata reader is needed.
            var beforeProbe = session.GetPlaySessionInfo(sessionKey)
                ?? originalPlaySession;
            if (!_stationResolver.SessionStillUsesStation(beforeProbe, stationPath))
            {
                return;
            }

            var initialStationIdentities = GetStationNameCandidatesForPath(beforeProbe, stationPath);
            if (_plugin.CurrentTrackService.IsStationMetadataLookupDisabledForAny(initialStationIdentities, session.DeviceName))
            {
                ClearSessionTrackMetadata(session, originalPlaySession, sessionKey, stationPath);
                RecordStationOnlySkipIfNeeded(sessionKey, stationPath, stationName, initialStationIdentities, session.DeviceName);
                return;
            }

            RadioStationMetadata metadata;
            var metadataSource = "Unknown";
            if (TryGetExistingArtistAndSong(
                    stationName,
                    beforeProbe.NowPlayingItem,
                    out var existingArtist,
                    out var existingSong) ||
                TryGetExistingArtistAndSong(
                    stationName,
                    beforeProbe.FullNowPlayingItem,
                    out existingArtist,
                    out existingSong))
            {
                metadata = new RadioStationMetadata
                {
                    StationName = stationName,
                    Artist = existingArtist,
                    SongTitle = existingSong,
                    StreamTitle = existingArtist + " - " + existingSong
                };
                metadataSource = "Emby item metadata";
                _sessionState.MarkInitialDelayCompleted(sessionKey);
            }
            else
            {
                // First try the low-bandwidth station metadata endpoint; if
                // that is unavailable, briefly read ICY metadata from the
                // stream and close the connection after a few metadata blocks.
                metadata = await _plugin.MetadataReader
                    .ReadNetworkMetadataAsync(
                        stationPath,
                        timeoutSeconds: RadioMetadataReader.LiveMetadataSourceTimeoutSeconds,
                        allowDirectIcy: _plugin.Configuration.EnableDirectIcyFallback,
                        ignoreMetadataPhrase: _plugin.CurrentTrackService.IsMetadataPhraseIgnored,
                        enableAdaptiveRouting: _plugin.Configuration.EnableAdaptiveMetadataRouting)
                    .ConfigureAwait(false);

                // Re-check the station and policy immediately after the network read. If the
                // user selected StationOnly while the request was in flight, discard its result
                // and do not launch the more expensive FFprobe fallback.
                var afterNetworkSession = session.GetPlaySessionInfo(sessionKey) ?? originalPlaySession;
                if (!_stationResolver.SessionStillUsesStation(afterNetworkSession, stationPath))
                {
                    return;
                }

                var afterNetworkIdentities = GetStationNameCandidatesForPath(afterNetworkSession, stationPath);
                if (_plugin.CurrentTrackService.IsStationMetadataLookupDisabledForAny(afterNetworkIdentities, session.DeviceName))
                {
                    ClearSessionTrackMetadata(session, originalPlaySession, sessionKey, stationPath);
                    RecordStationOnlySkipIfNeeded(sessionKey, stationPath, stationName, afterNetworkIdentities, session.DeviceName);
                    return;
                }

                var hasNetworkMetadata = string.IsNullOrWhiteSpace(metadata.Error) &&
                    (!string.IsNullOrWhiteSpace(metadata.Artist) ||
                     !string.IsNullOrWhiteSpace(metadata.SongTitle));

                if (hasNetworkMetadata)
                {
                    metadataSource = "Station HTTP/ICY";
                    _logger.Info("Usable radio metadata was obtained from the station HTTP/ICY path.");
                    _sessionState.MarkInitialDelayCompleted(sessionKey);
                }
                else
                {
                    _logger.Info("Station HTTP/ICY metadata was unusable; trying the FFprobe fallback.");
                    _plugin.DiagnosticEvents.Record(stationName, "Metadata pipeline", "fallback", string.Empty, string.Empty,
                        "HTTP/ICY returned no usable title; falling back to FFprobe.");
                    // Only delay FFprobe on the initial attempt. Network
                    // metadata is checked first so successful stations do not
                    // wait 10 seconds. Later progress events can retry readers.
                    if (needsInitialDelay)
                    {
                        var delaySeconds = _plugin.GetFfprobeDelaySeconds();
                        if (delaySeconds > 0)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds))
                                .ConfigureAwait(false);
                        }
                    }

                    beforeProbe = session.GetPlaySessionInfo(sessionKey)
                        ?? originalPlaySession;
                    if (!_stationResolver.SessionStillUsesStation(beforeProbe, stationPath))
                    {
                        return;
                    }

                    // Re-check Emby metadata after the delay; it may have
                    // appeared while playback was warming up.
                    if (TryGetExistingArtistAndSong(
                            stationName,
                            beforeProbe.NowPlayingItem,
                            out existingArtist,
                            out existingSong) ||
                        TryGetExistingArtistAndSong(
                            stationName,
                            beforeProbe.FullNowPlayingItem,
                            out existingArtist,
                            out existingSong))
                    {
                        metadata = new RadioStationMetadata
                        {
                            StationName = stationName,
                            Artist = existingArtist,
                            SongTitle = existingSong,
                            StreamTitle = existingArtist + " - " + existingSong
                        };
                        metadataSource = "Emby item metadata";
                    }
                    else
                    {
                        metadata = _plugin.MetadataReader.Read(
                            stationPath,
                            _plugin.GetFfprobePath(),
                            ignoreMetadataPhrase: _plugin.CurrentTrackService.IsMetadataPhraseIgnored);
                        metadataSource = "FFprobe";
                        if (string.IsNullOrWhiteSpace(metadata.Error) &&
                            RadioMetadataReader.IsUsableStreamTitle(metadata.SongTitle))
                        {
                            _logger.Info("Usable radio metadata was obtained from the FFprobe fallback.");
                        }
                    }

                    _sessionState.MarkInitialDelayCompleted(sessionKey);
                }
            }

            // Re-check after awaited network/FFprobe work in case StationOnly was
            // enabled while a lookup already in flight was finishing.
            var finalPlaySession = session.GetPlaySessionInfo(sessionKey) ?? originalPlaySession;
            var finalStationIdentities = GetStationNameCandidatesForPath(finalPlaySession, stationPath);
            if (_plugin.CurrentTrackService.IsStationMetadataLookupDisabledForAny(finalStationIdentities, session.DeviceName))
            {
                ClearSessionTrackMetadata(session, originalPlaySession, sessionKey, stationPath);
                RecordStationOnlySkipIfNeeded(sessionKey, stationPath, stationName, finalStationIdentities, session.DeviceName);
                return;
            }

            // All sources flow through one canonical validator/state service.
            // The service applies StationOnly/ignore rules, stores accepted
            // current-track state, and decides when a changed track merits a
            // history entry. It hands accepted values to the active Emby session
            // as machine-readable ProviderIds only; Emby Web controls presentation.
            var fullArtist = (metadata.Artist ?? string.Empty).Trim();
            var fullSong = (metadata.SongTitle ?? string.Empty).Trim();
            var playSession = session.GetPlaySessionInfo(sessionKey)
                ?? originalPlaySession;
            RadioCurrentTrack? currentTrack = null;
            var shouldRecordHistory = false;
            var accepted = false;
            var isWaitingForStabilityDelay = false;
            var sessionMetadataPublished = false;
            var rejectionReason = string.Empty;

            lock (playSession)
            {
                var item = playSession.NowPlayingItem;
                if (item is null || !_stationResolver.SessionStillUsesStation(playSession, stationPath))
                {
                    _plugin.CurrentTrackService.MarkUnavailable(sessionKey);
                    return;
                }

                accepted = _plugin.CurrentTrackService.TryAcceptTrack(
                    sessionKey,
                    stationName,
                    fullArtist,
                    fullSong,
                    metadataSource,
                    metadata.Error,
                    metadata.StreamTitle,
                    session.DeviceName,
                    out currentTrack,
                    out rejectionReason,
                    out shouldRecordHistory,
                    out isWaitingForStabilityDelay);

                if (accepted && currentTrack is not null)
                {
                    sessionMetadataPublished = _plugin.CurrentTrackService
                        .TryPublishToSessionItem(item, currentTrack);
                }
                else if (!isWaitingForStabilityDelay)
                {
                    // While a candidate is within its configured stability
                    // window, keep the last accepted session title published.
                    // A real rejection still clears stale injected metadata.
                    _plugin.CurrentTrackService.ClearPublishedMetadata(item, sessionKey);
                }
            }

            if (!accepted || currentTrack is null)
            {
                var candidateTitle = !string.IsNullOrWhiteSpace(fullSong)
                    ? fullSong
                    : (metadata.StreamTitle ?? string.Empty);
                var diagnosticStatus = isWaitingForStabilityDelay
                    ? "waiting"
                    : rejectionReason.Contains("ignore phrase", StringComparison.OrdinalIgnoreCase)
                        ? "filtered"
                        : string.IsNullOrWhiteSpace(candidateTitle) ? "empty" : "rejected";
                _plugin.DiagnosticEvents.Record(
                    stationName,
                    metadataSource,
                    diagnosticStatus,
                    candidateTitle,
                    fullArtist,
                    string.IsNullOrWhiteSpace(rejectionReason) ? "Candidate was not accepted as the current track." : rejectionReason,
                    metadata.StreamTitle);
                _logger.Info(
                    isWaitingForStabilityDelay
                        ? "Radio metadata candidate is waiting for its station stability delay: {0}"
                        : "Radio metadata candidate was not accepted as the current track: {0}",
                    string.IsNullOrWhiteSpace(rejectionReason) ? "candidate rejected" : rejectionReason);
                return;
            }

            _plugin.DiagnosticEvents.Record(
                stationName,
                currentTrack.Source,
                "accepted",
                currentTrack.SongTitle,
                currentTrack.Artist,
                sessionMetadataPublished
                    ? "Candidate passed validation and was published to the Emby session."
                    : "Candidate passed validation, but publishing to the Emby session failed.",
                metadata.StreamTitle);

            if (sessionMetadataPublished)
            {
                _logger.Info(
                    "Canonical radio track metadata was published to the active session (source: {0}).",
                    currentTrack.Source);
            }
            else
            {
                _plugin.DiagnosticEvents.Record(stationName, currentTrack.Source, "error", currentTrack.SongTitle, currentTrack.Artist,
                    "The active session item did not expose a writable ProviderIds dictionary.", metadata.StreamTitle);
                _logger.Error(
                    "Could not publish canonical radio track metadata: the active session item does not expose a writable ProviderIds dictionary.");
            }

            if (_plugin.CurrentTrackService.RecordAcceptedTrack(currentTrack, shouldRecordHistory))
            {
                _logger.Info("Radio play history recorded one track from the canonical current-track service.");
            }
        }
        catch (Exception ex)
        {
            _plugin.DiagnosticEvents.Record("", "Session pipeline", "error", string.Empty, string.Empty,
                "Radio session metadata update failed (" + ex.GetType().Name + ").");
            _logger.Error(
                "Radio session metadata update failed ({0}).",
                ex.GetType().Name);
        }
        finally
        {
            _sessionState.FinishMetadataUpdate(sessionKey);
        }
    }


    private bool TryGetExistingArtistAndSong(
        string stationName,
        object? item,
        out string artist,
        out string song)
    {
        artist = string.Empty;
        song = string.Empty;

        if (item is null || _plugin.CurrentTrackService.HasPublishedRadioMetaMetadata(item))
        {
            // Do not re-read Radio Meta's own published metadata as if it were
            // source metadata. Source readers remain responsible for track updates.
            return false;
        }

        artist = ReadMetadataProperty(item, "Artists", allowCollection: true);
        if (artist.Length == 0)
        {
            artist = ReadMetadataProperty(item, "ArtistItems", allowCollection: true);
        }
        if (artist.Length == 0)
        {
            artist = ReadMetadataProperty(item, "AlbumArtist", allowCollection: false);
        }
        if (artist.Length == 0)
        {
            artist = ReadMetadataProperty(item, "AlbumArtists", allowCollection: true);
        }

        song = ReadMetadataProperty(item, "OriginalTitle", allowCollection: false);
        if (song.Length == 0)
        {
            song = ReadMetadataProperty(item, "TrackName", allowCollection: false);
        }
        if (song.Length == 0)
        {
            song = ReadMetadataProperty(item, "SongTitle", allowCollection: false);
        }

        return artist.Length > 0 && song.Length > 0 &&
            !_plugin.CurrentTrackService.IsMetadataPhraseIgnored(stationName, song) &&
            !_plugin.CurrentTrackService.IsMetadataPhraseIgnored(stationName, artist + " - " + song);
    }

    private static string ReadMetadataProperty(
        object item,
        string propertyName,
        bool allowCollection)
    {
        try
        {
            var property = item.GetType().GetProperty(propertyName);
            var value = property?.GetValue(item);
            if (value is string text)
            {
                return text.Trim();
            }

            if (allowCollection && value is IEnumerable values)
            {
                foreach (var entry in values)
                {
                    if (entry is string entryText && !string.IsNullOrWhiteSpace(entryText))
                    {
                        return entryText.Trim();
                    }

                    if (entry is not null)
                    {
                        var nameProperty = entry.GetType().GetProperty("Name");
                        var name = nameProperty?.GetValue(entry) as string;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            return name.Trim();
                        }
                    }
                }
            }
        }
        catch
        {
            // Metadata is optional; fall back to FFprobe if the DTO does not
            // expose one of these properties on this Emby version.
        }

        return string.Empty;
    }

    private IReadOnlyList<string> GetStationNameCandidatesForPath(PlaySessionInfo playSession, string stationPath)
    {
        // Avoid matching a StationOnly rule against a stale FullNowPlayingItem from a different
        // station during a handoff. Only item objects belonging to the selected .strm path may
        // contribute names or RadioMetaStation ProviderIds; the filename itself is always checked.
        var items = new[]
        {
            (Path: playSession.NowPlayingItem?.Path, Item: (object?)playSession.NowPlayingItem),
            (Path: playSession.FullNowPlayingItem?.Path, Item: (object?)playSession.FullNowPlayingItem)
        }
        .Where(candidate => candidate.Item is not null && !string.IsNullOrWhiteSpace(candidate.Path) &&
            _stationResolver.PathsEqual(candidate.Path!, stationPath))
        .Select(candidate => candidate.Item)
        .Cast<object?>()
        .ToArray();

        return _stationResolver.GetStationNameCandidates(stationPath, items);
    }

    private void RecordStationOnlySkipIfNeeded(
        string sessionKey, string stationPath, string stationName, IReadOnlyList<string> stationIdentities, string? deviceName)
    {
        if (!_sessionState.TryMarkStationOnlySkip(sessionKey, stationPath)) return;
        var mode = _plugin.CurrentTrackService.GetStationMetadataModeDiagnostic(stationIdentities, deviceName);
        _plugin.DiagnosticEvents.Record(stationName, "Station metadata mode", "skipped", string.Empty, string.Empty,
            "StationOnly rule matched (" + mode.MatchedRuleStationName + ") while a lookup was in flight; its result was discarded and metadata was cleared.");
    }

    private void ClearSessionTrackMetadata(
        SessionInfo session,
        PlaySessionInfo originalPlaySession,
        string sessionKey,
        string stationPath)
    {
        _plugin.CurrentTrackService.MarkUnavailable(sessionKey);
        var liveSession = session.GetPlaySessionInfo(sessionKey) ?? originalPlaySession;
        lock (liveSession)
        {
            if (_stationResolver.SessionStillUsesStation(liveSession, stationPath))
            {
                var currentItem = liveSession.NowPlayingItem;
                if (currentItem is not null)
                {
                    _plugin.CurrentTrackService.ClearPublishedMetadata(currentItem, sessionKey);
                }

                var fullItem = liveSession.FullNowPlayingItem;
                if (fullItem is not null && !ReferenceEquals(fullItem, currentItem) &&
                    !string.IsNullOrWhiteSpace(fullItem.Path) && _stationResolver.PathsEqual(fullItem.Path, stationPath))
                {
                    _plugin.CurrentTrackService.ClearPublishedMetadata(fullItem, sessionKey);
                }
            }
        }
    }

}
