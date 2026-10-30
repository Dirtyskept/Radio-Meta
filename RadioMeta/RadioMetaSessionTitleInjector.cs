using System;
using System.Collections.Generic;
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

    private readonly RadioMetaPlaybackSessionStateStore _sessionState = new();
    private readonly RadioMetaPlaybackStationResolver _stationResolver;
    private readonly RadioMetaSessionMetadataProcessor _metadataProcessor;

    private bool _started;

    public RadioMetaSessionTitleInjector(
        RadioMetaPlugin plugin,
        ISessionManager sessionManager,
        ILogger logger)
    {
        _plugin = plugin;
        _sessionManager = sessionManager;
        _logger = logger;
        _stationResolver = new RadioMetaPlaybackStationResolver(plugin);
        _metadataProcessor = new RadioMetaSessionMetadataProcessor(
            plugin, logger, _sessionState, _stationResolver);
    }

    public bool IsStarted => _started;

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
                // The session's currently playing item is the strongest signal. During a brief
                // transition gap it may be null; then prefer the event item before full-info data,
                // which can momentarily still describe the previous station.
                (Path: playSession.NowPlayingItem?.Path, Item: (object?)playSession.NowPlayingItem),
                (Path: args.Item?.Path, Item: (object?)args.Item),
                (Path: playSession.FullNowPlayingItem?.Path, Item: (object?)playSession.FullNowPlayingItem)
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
            .ToArray();

            if (candidates.Length == 0)
            {
                return;
            }

            var stationPath = candidates
                .Select(candidate => _stationResolver.ResolveStationPath(candidate.Path, candidate.Item))
                .FirstOrDefault(path => path is not null);

            if (stationPath is null)
            {
                // Do not change a non-radio playback item. If this session was
                // previously tracking a radio station, end its canonical
                // current-track state as it moves to ordinary playback.
                if (_sessionState.TryRemoveStation(sessionKey, out _))
                {
                    _plugin.CurrentTrackService.EndSession(sessionKey);
                    _sessionState.ResetInitialDelay(sessionKey);
                }
                else if (candidates.Any(candidate =>
                    candidate.Path is not null && _stationResolver.IsStrmPath(candidate.Path)))
                {
                    // A .strm item which does not pass the configured-folder or
                    // automatic audio-item checks is not owned by this plugin.
                    return;
                }

                return;
            }

            var currentItem = playSession.NowPlayingItem;

            // Resolve the station policy before requiring a hydrated NowPlayingItem. Emby can
            // briefly omit that item during a station handoff; a StationOnly rule must still
            // prevent any new metadata request during that gap.
            var stationName = Path.GetFileNameWithoutExtension(stationPath);
            // Don't let a stale name/ProviderId from a different candidate item decide this
            // station's mode. Only inspect title/ProviderId identities attached to the item(s)
            // whose path resolves to the selected station .strm.
            var stationIdentityItems = candidates
                .Where(candidate => candidate.Item is not null && !string.IsNullOrWhiteSpace(candidate.Path) &&
                    _stationResolver.PathsEqual(candidate.Path!, stationPath))
                .Select(candidate => candidate.Item)
                .Cast<object?>()
                .ToArray();
            var stationIdentityCandidates = _stationResolver.GetStationNameCandidates(stationPath, stationIdentityItems);
            var metadataMode = _plugin.CurrentTrackService.GetStationMetadataModeDiagnostic(
                stationIdentityCandidates, session.DeviceName);
            var hasPreviousState = _sessionState.TryGetStationPath(sessionKey, out var previousStationPath);
            var stationChanged = hasPreviousState &&
                !_stationResolver.PathsEqual(previousStationPath!, stationPath);

            if (stationChanged)
            {
                _sessionState.ResetInitialDelay(sessionKey);
                _plugin.CurrentTrackService.EndSession(sessionKey);
                // Remove only Radio Meta's machine-readable song fields from the new station item.
                // Emby's own Name/title and artwork remain untouched, so the .strm title can show.
                ClearPublishedMetadataForStationCandidates(playSession, candidates, stationPath, sessionKey);
            }

            // In Carplay mode, do not carry injected metadata from the previous
            // station into the new station's initial display. Clearing only our
            // ProviderIds leaves Emby's .strm title and station artwork intact;
            // the normal configured stability delay then gates the new song data.
            if (string.Equals(metadataMode.Mode, "Carplay", StringComparison.OrdinalIgnoreCase) && !hasPreviousState)
            {
                // On the first observation in Carplay mode, clear old session-only ProviderIds too.
                // Later station changes are already cleared above for every mode.
                ClearPublishedMetadataForStationCandidates(playSession, candidates, stationPath, sessionKey);
            }

            _sessionState.SetStationPath(sessionKey, stationPath);

            _plugin.CurrentTrackService.MarkStationActive(sessionKey, stationName, stationPath, session.DeviceName);
            if (metadataMode.LookupDisabled)
            {
                // StationOnly means the station is intentionally not queried. This is a normal
                // configured state, not a failed metadata lookup. Clear old song metadata so the
                // .strm title remains the player's identity, and emit an explicit diagnostic note.
                ClearPublishedMetadataForStationCandidates(playSession, candidates, stationPath, sessionKey);
                _plugin.CurrentTrackService.MarkUnavailable(sessionKey);
                _sessionState.ResetInitialDelay(sessionKey);
                if (_sessionState.TryMarkStationOnlySkip(sessionKey, stationPath))
                {
                    _plugin.DiagnosticEvents.Record(stationName, "Station metadata mode", "skipped", string.Empty, string.Empty,
                        "StationOnly rule matched (" + metadataMode.MatchedRuleStationName + "); metadata lookups intentionally disabled. Playback continues using the station title.");
                }
                return;
            }
            _sessionState.ClearStationOnlySkip(sessionKey);

            // Do not start a lookup while Emby has no hydrated item. The station path and policy
            // are already recorded; the next progress event will retry once the item is present.
            if (currentItem is null)
            {
                return;
            }

            // Only one metadata read per session may be in flight.
            if (!_sessionState.TryBeginMetadataUpdate(sessionKey))
            {
                return;
            }

            _ = Task.Run(() =>
                _metadataProcessor.UpdateSessionMetadataAsync(session, playSession, sessionKey,
                    stationPath));
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Radio session metadata playback handler failed ({0}).",
                ex.GetType().Name);
        }
    }

    private void ClearPublishedMetadataForStationCandidates(
        PlaySessionInfo playSession,
        IEnumerable<(string? Path, object? Item)> candidates,
        string stationPath,
        string sessionKey)
    {
        // Candidate objects are captured from this playback event. Only clear ProviderIds on
        // objects whose path is the selected station's .strm; never alter Emby's display title.
        // Share the same lock used by the session publisher to avoid racing a metadata write.
        lock (playSession)
        {
            // In some Emby session DTOs NowPlayingItem exposes the resolved stream URL while
            // FullNowPlayingItem retains the .strm path. If either proves this session is on the
            // selected station, clear Radio Meta ProviderIds from the live NowPlayingItem too.
            var sessionStillUsesStation = _stationResolver.SessionStillUsesStation(playSession, stationPath);
            var liveItem = playSession.NowPlayingItem;
            if (sessionStillUsesStation && liveItem is not null)
            {
                _plugin.CurrentTrackService.ClearPublishedMetadata(liveItem, sessionKey);
            }

            foreach (var candidate in candidates)
            {
                if (candidate.Item is null || string.IsNullOrWhiteSpace(candidate.Path))
                    continue;

                if (_stationResolver.PathsEqual(candidate.Path!, stationPath) && !ReferenceEquals(candidate.Item, liveItem))
                    _plugin.CurrentTrackService.ClearPublishedMetadata(candidate.Item, sessionKey);
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

            _sessionState.FinishMetadataUpdate(sessionKey);
            _sessionState.ResetInitialDelay(sessionKey);
            _sessionState.ClearStationOnlySkip(sessionKey);
            _plugin.CurrentTrackService.EndSession(sessionKey);
            _sessionState.TryRemoveStation(sessionKey, out _);
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Radio title injector stop handler failed ({0}).",
                ex.GetType().Name);
        }
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

        _sessionState.Clear();
    }
}
