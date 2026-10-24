using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

public sealed class RadioMetaSessionTitleInjector : IDisposable
{
    private readonly RadioMetaPlugin _plugin;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, SessionStationState>
        _sessionStations = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte>
        _pendingUpdates = new(StringComparer.OrdinalIgnoreCase);

    // The startup delay applies once per playback session, not to every
    // subsequent metadata refresh.
    private readonly ConcurrentDictionary<string, byte>
        _initialDelayCompleted = new(StringComparer.OrdinalIgnoreCase);

    private bool _started;

    private sealed class SessionStationState
    {
        public string StationPath { get; }

        public SessionStationState(string stationPath)
        {
            StationPath = stationPath;
        }
    }

    public RadioMetaSessionTitleInjector(
        RadioMetaPlugin plugin,
        ISessionManager sessionManager,
        ILogger logger)
    {
        _plugin = plugin;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _sessionManager.PlaybackStart += OnPlayback;
        _sessionManager.PlaybackProgress += OnPlayback;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        _started = true;

        _logger.Info("Radio Meta current-track service and session publisher started.");
    }

    private void OnPlayback(
        object? sender,
        PlaybackProgressEventArgs args)
    {
        try
        {
            var session = args.Session;
            var playSession = args.PlaySession;

            if (session is null || playSession is null)
            {
                return;
            }

            var sessionKey = playSession.Id;

            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return;
            }

            var candidates = new[]
            {
                (Path: args.Item?.Path, Item: (object?)args.Item),
                (Path: playSession.NowPlayingItem?.Path, Item: (object?)playSession.NowPlayingItem),
                (Path: playSession.FullNowPlayingItem?.Path, Item: (object?)playSession.FullNowPlayingItem)
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
            .ToArray();

            if (candidates.Length == 0)
            {
                return;
            }

            var stationPath = candidates
                .Select(candidate => ResolveStationPath(candidate.Path, candidate.Item))
                .FirstOrDefault(path => path is not null);

            if (stationPath is null)
            {
                // Do not change a non-radio playback item. If this session was
                // previously tracking a radio station, end its canonical
                // current-track state as it moves to ordinary playback.
                if (_sessionStations.TryRemove(sessionKey, out _))
                {
                    _plugin.CurrentTrackService.EndSession(sessionKey);
                    _initialDelayCompleted.TryRemove(sessionKey, out _);
                }
                else if (candidates.Any(candidate =>
                    candidate.Path is not null && IsStrmPath(candidate.Path)))
                {
                    // A .strm item which does not pass the configured-folder or
                    // automatic audio-item checks is not owned by this plugin.
                    return;
                }

                return;
            }

            var currentItem = playSession.NowPlayingItem;

            if (currentItem is null)
            {
                return;
            }

            var stationName = Path.GetFileNameWithoutExtension(stationPath);
            var proposedState = new SessionStationState(stationPath);
            var hasPreviousState = _sessionStations.TryGetValue(sessionKey, out var previousState);
            var stationChanged = hasPreviousState &&
                !PathsEqual(previousState!.StationPath, stationPath);

            if (stationChanged)
            {
                _initialDelayCompleted.TryRemove(sessionKey, out _);
                _plugin.CurrentTrackService.EndSession(sessionKey);
            }

            // In Carplay mode, do not carry injected metadata from the previous
            // station into the new station's initial display. Clearing only our
            // ProviderIds leaves Emby's .strm title and station artwork intact;
            // the normal configured stability delay then gates the new song data.
            if (_plugin.CurrentTrackService.IsStationCarplayMode(stationName, session.DeviceName) &&
                (!hasPreviousState || stationChanged))
            {
                _plugin.CurrentTrackService.ClearPublishedMetadata(currentItem, sessionKey);
            }

            _sessionStations.AddOrUpdate(
                sessionKey,
                proposedState,
                (_, existing) =>
                    PathsEqual(existing.StationPath, stationPath)
                        ? existing
                        : proposedState);

            _plugin.CurrentTrackService.MarkStationActive(sessionKey, stationName, stationPath, session.DeviceName);
            if (_plugin.CurrentTrackService.IsStationMetadataLookupDisabled(stationName, session.DeviceName))
            {
                // StationOnly means no item-metadata inspection, HTTP/ICY read,
                // FFprobe, provider-ID update, or new history entry for this station.
                // Clear any previously published track so stale data cannot linger.
                lock (playSession)
                {
                    var item = playSession.NowPlayingItem;
                    if (item is not null && SessionStillUsesStation(playSession, stationPath))
                    {
                        _plugin.CurrentTrackService.ClearPublishedMetadata(item, sessionKey);
                    }
                }
                _plugin.CurrentTrackService.MarkUnavailable(sessionKey);
                _initialDelayCompleted.TryRemove(sessionKey, out _);
                return;
            }

            // Only one metadata read per session may be in flight.
            if (!_pendingUpdates.TryAdd(sessionKey, 0))
            {
                return;
            }

            _ = Task.Run(() =>
                UpdateSessionMetadataAsync(session, playSession, sessionKey,
                    stationPath));
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Radio session metadata playback handler failed ({0}).",
                ex.GetType().Name);
        }
    }

    private async Task UpdateSessionMetadataAsync(
        SessionInfo session,
        PlaySessionInfo originalPlaySession,
        string sessionKey,
        string stationPath)
    {
        try
        {
            var needsInitialDelay = !_initialDelayCompleted.ContainsKey(sessionKey);
            var stationName = Path.GetFileNameWithoutExtension(stationPath);

            // Check Emby's own item metadata immediately. If it already has
            // both fields, no external metadata reader is needed.
            var beforeProbe = session.GetPlaySessionInfo(sessionKey)
                ?? originalPlaySession;
            if (!SessionStillUsesStation(beforeProbe, stationPath))
            {
                return;
            }

            if (_plugin.CurrentTrackService.IsStationMetadataLookupDisabled(stationName, session.DeviceName))
            {
                ClearSessionTrackMetadata(session, originalPlaySession, sessionKey, stationPath);
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
                _initialDelayCompleted.TryAdd(sessionKey, 0);
            }
            else
            {
                // First try the low-bandwidth station metadata endpoint; if
                // that is unavailable, briefly read ICY metadata from the
                // stream and close the connection after a few metadata blocks.
                metadata = await _plugin.MetadataReader
                    .ReadNetworkMetadataAsync(
                        stationPath,
                        timeoutSeconds: 5,
                        allowDirectIcy: _plugin.Configuration.EnableDirectIcyFallback,
                        ignoreMetadataPhrase: _plugin.CurrentTrackService.IsMetadataPhraseIgnored)
                    .ConfigureAwait(false);

                var hasNetworkMetadata = string.IsNullOrWhiteSpace(metadata.Error) &&
                    (!string.IsNullOrWhiteSpace(metadata.Artist) ||
                     !string.IsNullOrWhiteSpace(metadata.SongTitle));

                if (hasNetworkMetadata)
                {
                    metadataSource = "Station HTTP/ICY";
                    _logger.Info("Usable radio metadata was obtained from the station HTTP/ICY path.");
                    _initialDelayCompleted.TryAdd(sessionKey, 0);
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
                    if (!SessionStillUsesStation(beforeProbe, stationPath))
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

                    _initialDelayCompleted.TryAdd(sessionKey, 0);
                }
            }

            // Re-check after awaited network/FFprobe work in case StationOnly was
            // enabled while a lookup already in flight was finishing.
            if (_plugin.CurrentTrackService.IsStationMetadataLookupDisabled(stationName, session.DeviceName))
            {
                ClearSessionTrackMetadata(session, originalPlaySession, sessionKey, stationPath);
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
                if (item is null || !SessionStillUsesStation(playSession, stationPath))
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
            _pendingUpdates.TryRemove(sessionKey, out _);
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
            var currentItem = liveSession.NowPlayingItem;
            if (currentItem is not null && SessionStillUsesStation(liveSession, stationPath))
            {
                _plugin.CurrentTrackService.ClearPublishedMetadata(currentItem, sessionKey);
            }
        }
    }

    private void OnPlaybackStopped(
        object? sender,
        PlaybackStopEventArgs args)
    {
        try
        {
            var playSession = args.PlaySession;
            var sessionKey = playSession?.Id ?? args.PlaySessionId;

            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return;
            }

            _pendingUpdates.TryRemove(sessionKey, out _);
            _initialDelayCompleted.TryRemove(sessionKey, out _);
            _plugin.CurrentTrackService.EndSession(sessionKey);

            _sessionStations.TryRemove(sessionKey, out _);
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Radio title injector stop handler failed ({0}).",
                ex.GetType().Name);
        }
    }

    private string? ResolveStationPath(string? path, object? item)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsStrmPath(path))
        {
            return null;
        }

        try
        {
            var normalizedPath = NormalizePath(path);
            if (!File.Exists(normalizedPath))
            {
                return null;
            }

            var configuredFolder = _plugin.GetConfiguredStationFolder();
            if (configuredFolder.Length == 0)
            {
                // In automatic mode only classify an item as a radio station
                // when Emby identifies the active .strm library item as Audio.
                // This avoids claiming arbitrary movie/TV .strm files.
                return IsAudioMediaItem(item) ? normalizedPath : null;
            }

            var folder = _plugin.GetStationFolder();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            var normalizedFolder = NormalizePath(folder);
            var relative = Path.GetRelativePath(normalizedFolder, normalizedPath);

            if (Path.IsPathRooted(relative) ||
                relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            return normalizedPath;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAudioMediaItem(object? item)
    {
        if (item is null)
        {
            return false;
        }

        // Reflection intentionally avoids binding this check to one specific
        // Emby DTO/BaseItem implementation. Both expose MediaType in supported
        // versions, and its Audio enum/string representation is stable.
        try
        {
            var mediaTypeProperty = item.GetType().GetProperty("MediaType");
            var mediaType = mediaTypeProperty?.GetValue(item)?.ToString();
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                return string.Equals(mediaType, "Audio", StringComparison.OrdinalIgnoreCase);
            }

            var typeProperty = item.GetType().GetProperty("Type");
            var itemType = typeProperty?.GetValue(item)?.ToString();
            return string.Equals(itemType, "Audio", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool SessionStillUsesStation(
        PlaySessionInfo playSession,
        string stationPath)
    {
        var paths = new[]
        {
            playSession.NowPlayingItem?.Path,
            playSession.FullNowPlayingItem?.Path
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .ToArray();

        return paths.Any(path => PathsEqual(path!, stationPath));
    }

    private static bool IsStrmPath(string path) =>
        Path.GetExtension(path).Equals(
            ".strm",
            StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                NormalizePath(first),
                NormalizePath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        var value = path.Trim();

        if (value.StartsWith(
            @"\\?\UNC\",
            StringComparison.OrdinalIgnoreCase))
        {
            value = @"\\" + value.Substring(8);
        }
        else if (value.StartsWith(
            @"\\?\",
            StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4);
        }

        return Path.GetFullPath(value);
    }

    public void Dispose()
    {
        if (_started)
        {
            _sessionManager.PlaybackStart -= OnPlayback;
            _sessionManager.PlaybackProgress -= OnPlayback;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _started = false;
        }

        _sessionStations.Clear();
        _pendingUpdates.Clear();
        _initialDelayCompleted.Clear();
    }
}
