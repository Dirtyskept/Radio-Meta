using System;
using System.Collections.Concurrent;

namespace RadioMeta;

/// <summary>
/// Tracks playback-session coordination state independently from metadata probing:
/// station path, in-flight metadata work, and whether the one-time startup delay ran.
/// </summary>
internal sealed class RadioMetaPlaybackSessionStateStore
{
    private readonly ConcurrentDictionary<string, string> _sessionStations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _pendingUpdates =
        new(StringComparer.OrdinalIgnoreCase);

    // The startup delay applies once per playback session, not to every subsequent refresh.
    private readonly ConcurrentDictionary<string, byte> _initialDelayCompleted =
        new(StringComparer.OrdinalIgnoreCase);

    // Keep the diagnostic report clear: log an intentional StationOnly skip once per
    // session/station, not on every playback-progress event.
    private readonly ConcurrentDictionary<string, string> _stationOnlySkipLogged =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool TryRemoveStation(string sessionKey, out string? stationPath) =>
        _sessionStations.TryRemove(sessionKey, out stationPath);

    internal bool TryGetStationPath(string sessionKey, out string? stationPath) =>
        _sessionStations.TryGetValue(sessionKey, out stationPath);

    internal void SetStationPath(string sessionKey, string stationPath)
    {
        // Station paths have already been normalized by RadioMetaPlaybackStationResolver.
        _sessionStations.AddOrUpdate(
            sessionKey,
            stationPath,
            (_, existing) => string.Equals(existing, stationPath, StringComparison.OrdinalIgnoreCase)
                ? existing
                : stationPath);
    }

    internal bool TryBeginMetadataUpdate(string sessionKey) =>
        _pendingUpdates.TryAdd(sessionKey, 0);

    internal void FinishMetadataUpdate(string sessionKey) =>
        _pendingUpdates.TryRemove(sessionKey, out _);

    internal bool HasCompletedInitialDelay(string sessionKey) =>
        _initialDelayCompleted.ContainsKey(sessionKey);

    internal void MarkInitialDelayCompleted(string sessionKey) =>
        _initialDelayCompleted.TryAdd(sessionKey, 0);

    internal void ResetInitialDelay(string sessionKey) =>
        _initialDelayCompleted.TryRemove(sessionKey, out _);

    internal bool TryMarkStationOnlySkip(string sessionKey, string stationPath)
    {
        var key = (sessionKey ?? string.Empty).Trim();
        var path = (stationPath ?? string.Empty).Trim();
        if (key.Length == 0 || path.Length == 0) return false;
        while (true)
        {
            if (!_stationOnlySkipLogged.TryGetValue(key, out var existing))
                return _stationOnlySkipLogged.TryAdd(key, path);
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return false;
            if (_stationOnlySkipLogged.TryUpdate(key, path, existing)) return true;
        }
    }

    internal void ClearStationOnlySkip(string sessionKey) =>
        _stationOnlySkipLogged.TryRemove((sessionKey ?? string.Empty).Trim(), out _);

    internal void Clear()
    {
        _sessionStations.Clear();
        _pendingUpdates.Clear();
        _initialDelayCompleted.Clear();
        _stationOnlySkipLogged.Clear();
    }
}
