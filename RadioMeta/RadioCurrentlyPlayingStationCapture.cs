using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>
/// Small, in-memory snapshot of an active Emby radio/channel playback item.
/// It is deliberately not written to configuration or history. Only fields
/// useful for pre-filling the station form are retained.
/// </summary>
public sealed class RadioCurrentlyPlayingStationSnapshot
{
    public string SessionKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string StreamUrl { get; init; } = string.Empty;
    public string ArtworkUrl { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string StationUuid { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset SeenAtUtc { get; init; }
}

/// <summary>
/// Caches only the latest details for likely radio/channel playback sessions,
/// for use by the authenticated "Add to Radio Meta" pre-fill action.
/// </summary>
public sealed class RadioCurrentlyPlayingStationStore
{
    private readonly ConcurrentDictionary<string, RadioCurrentlyPlayingStationSnapshot> _sessions =
        new(StringComparer.OrdinalIgnoreCase);


    public void Observe(PlaybackProgressEventArgs args)
    {
        if (args is null)
            return;

        // Some channel providers populate PlaySessionId before a fully hydrated
        // PlaySession object is available. Use the event's explicit id as a fallback.
        var playSession = args.PlaySession;
        var sessionKey = (playSession?.Id ?? string.Empty).Trim();
        if (sessionKey.Length == 0)
            sessionKey = (args.PlaySessionId ?? string.Empty).Trim();
        if (sessionKey.Length == 0)
            return;

        var candidate = RadioCurrentlyPlayingStationCandidateBuilder.BuildCandidate(sessionKey, args);
        if (candidate is null)
        {
            // Channel plugins can omit MediaInfo or MediaSource on individual
            // progress ticks even though the same stream is still playing. Do
            // not erase a useful capture on an incomplete tick; clear it when
            // the event clearly represents ordinary non-radio media instead.
            if (_sessions.TryGetValue(sessionKey, out var previous) &&
                !RadioCurrentlyPlayingStationCandidateBuilder.IsClearlyNonRadioPlayback(args))
            {
                _sessions[sessionKey] = RefreshSnapshot(previous);
                return;
            }

            _sessions.TryRemove(sessionKey, out _);
            return;
        }

        _sessions.AddOrUpdate(
            sessionKey,
            candidate,
            (_, previous) => MergeSameStationSnapshot(previous, candidate));
    }

    private static RadioCurrentlyPlayingStationSnapshot MergeSameStationSnapshot(
        RadioCurrentlyPlayingStationSnapshot previous,
        RadioCurrentlyPlayingStationSnapshot current)
    {
        // Keep a URL/artwork/UUID discovered on PlaybackStart when a later
        // progress tick reports the same station but omits those source fields.
        if (!string.Equals(previous.Name, current.Name, StringComparison.OrdinalIgnoreCase))
            return current;

        return new RadioCurrentlyPlayingStationSnapshot
        {
            SessionKey = current.SessionKey,
            Name = current.Name,
            StreamUrl = RadioMetaValueHelpers.FirstNonEmpty(current.StreamUrl, previous.StreamUrl),
            ArtworkUrl = RadioMetaValueHelpers.FirstNonEmpty(current.ArtworkUrl, previous.ArtworkUrl),
            ItemId = RadioMetaValueHelpers.FirstNonEmpty(current.ItemId, previous.ItemId),
            StationUuid = RadioMetaValueHelpers.FirstNonEmpty(current.StationUuid, previous.StationUuid),
            Source = current.StreamUrl.Length > 0 ? current.Source : previous.Source,
            SeenAtUtc = current.SeenAtUtc
        };
    }

    private static RadioCurrentlyPlayingStationSnapshot RefreshSnapshot(
        RadioCurrentlyPlayingStationSnapshot previous) => new()
    {
        SessionKey = previous.SessionKey,
        Name = previous.Name,
        StreamUrl = previous.StreamUrl,
        ArtworkUrl = previous.ArtworkUrl,
        ItemId = previous.ItemId,
        StationUuid = previous.StationUuid,
        Source = previous.Source,
        SeenAtUtc = DateTimeOffset.UtcNow
    };

    public void Remove(string? sessionKey)
    {
        var key = (sessionKey ?? string.Empty).Trim();
        if (key.Length > 0)
            _sessions.TryRemove(key, out _);
    }

    public RadioCurrentlyPlayingStationSnapshot? GetMostRecent()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-3);
        foreach (var entry in _sessions.Where(pair => pair.Value.SeenAtUtc < cutoff).ToArray())
            _sessions.TryRemove(entry.Key, out _);

        return _sessions.Values
            .OrderByDescending(item => item.SeenAtUtc)
            .FirstOrDefault();
    }


}

/// <summary>
/// Subscribes to Emby playback events to keep a short-lived, in-memory candidate
/// for the Radio Meta "Add to Radio Meta" pre-fill action.
/// </summary>
public sealed class RadioCurrentlyPlayingStationCapture : IDisposable
{
    private readonly RadioCurrentlyPlayingStationStore _store;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger _logger;
    private bool _started;

    public RadioCurrentlyPlayingStationCapture(
        RadioCurrentlyPlayingStationStore store,
        ISessionManager sessionManager,
        ILogger logger)
    {
        _store = store;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public bool IsStarted => _started;

    public void Start()
    {
        if (_started) return;
        _sessionManager.PlaybackStart += OnPlayback;
        _sessionManager.PlaybackProgress += OnPlayback;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _started = true;
    }

    private void OnPlayback(object? sender, PlaybackProgressEventArgs args)
    {
        try
        {
            _store.Observe(args);
        }
        catch (Exception ex)
        {
            var errorType = ex.GetType().Name;
            _logger.Debug("Radio Meta station pre-fill capture skipped ({0}).", errorType);
            // This is deliberately a sanitized breadcrumb: playback event details,
            // stream URLs, and session identifiers are not included.
            try
            {
                RadioMetaPlugin.Instance?.DiagnosticEvents.Record(
                    string.Empty,
                    "Station capture",
                    "warning",
                    "Playback station candidate inspection failed",
                    string.Empty,
                    "Candidate inspection failed (" + errorType + ").");
            }
            catch { /* Diagnostics must never interfere with playback event handling. */ }
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        try
        {
            _store.Remove(args.PlaySession?.Id ?? args.PlaySessionId);
        }
        catch (Exception ex)
        {
            try
            {
                RadioMetaPlugin.Instance?.DiagnosticEvents.Record(
                    string.Empty,
                    "Station capture",
                    "warning",
                    "Playback-stop station cleanup failed",
                    string.Empty,
                    "Station snapshot cleanup failed (" + ex.GetType().Name + ").");
            }
            catch { /* Best-effort cleanup diagnostics only. */ }
        }
    }

    public void Dispose()
    {
        if (!_started) return;
        _sessionManager.PlaybackStart -= OnPlayback;
        _sessionManager.PlaybackProgress -= OnPlayback;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _started = false;
    }
}

