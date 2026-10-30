using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace RadioMeta;

/// <summary>
/// Owns per-session accepted-track snapshots, station activity, pending stability
/// candidates, and history de-duplication keys. Policy and acceptance decisions remain
/// in RadioCurrentTrackService; this class stores and retrieves the resulting state.
/// </summary>
internal sealed class RadioCurrentTrackStateStore
{
    internal sealed record PendingTrackCandidate(string Key, DateTimeOffset FirstSeen);

    private readonly ConcurrentDictionary<string, RadioCurrentTrack> _currentBySession =
        new(StringComparer.OrdinalIgnoreCase);
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

    internal IEnumerable<RadioCurrentTrack> CurrentTracks => _currentBySession.Values;

    internal void ResetPendingMetadataDelays() => _pendingTrackCandidates.Clear();

    internal bool HasPendingCandidate(string sessionId) => _pendingTrackCandidates.ContainsKey(sessionId);

    internal void ClearPendingCandidate(string sessionId) => _pendingTrackCandidates.TryRemove(sessionId, out _);

    internal PendingTrackCandidate GetOrUpdatePendingCandidate(string sessionId, string key, DateTimeOffset now) =>
        _pendingTrackCandidates.AddOrUpdate(
            sessionId,
            _ => new PendingTrackCandidate(key, now),
            (_, existing) => string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase)
                ? existing
                : new PendingTrackCandidate(key, now));

    internal bool TryGetCurrentTrack(string sessionId, out RadioCurrentTrack? track) =>
        _currentBySession.TryGetValue(sessionId, out track);

    // This survives MarkUnavailable while the session remains on the same station,
    // but EndSession clears it on a station transition. It therefore distinguishes
    // "first valid song after tuning in" from a temporary metadata gap.
    internal bool HasAcceptedTrackForSession(string sessionId) =>
        _lastHistoryKeys.ContainsKey(sessionId);

    internal void SetCurrentTrack(string sessionId, RadioCurrentTrack track) =>
        _currentBySession[sessionId] = track;

    internal bool ShouldRecordHistory(string sessionId, string key)
    {
        var shouldRecord = !_lastHistoryKeys.TryGetValue(sessionId, out var lastKey) ||
            !string.Equals(lastKey, key, StringComparison.OrdinalIgnoreCase);
        if (shouldRecord)
        {
            _lastHistoryKeys[sessionId] = key;
        }
        return shouldRecord;
    }

    internal void MarkStationActive(string? sessionId, string? stationName, string? stationPath, string? deviceName)
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

    internal string? GetActiveStationPath(string? sessionId)
    {
        var session = (sessionId ?? string.Empty).Trim();
        return session.Length > 0 && _activeStationPathsBySession.TryGetValue(session, out var path)
            ? path
            : null;
    }

    internal IReadOnlyList<(string StationName, string Path)> GetActiveStationPaths()
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

    internal bool IsStationActive(string? stationName)
    {
        var station = (stationName ?? string.Empty).Trim();
        return station.Length > 0 && _activeStationsBySession.Values.Any(active =>
            string.Equals(active, station, StringComparison.OrdinalIgnoreCase));
    }

    internal RadioCurrentTrack? GetCurrentTrackForStation(string? stationName)
    {
        var station = (stationName ?? string.Empty).Trim();
        if (station.Length == 0) return null;
        return _currentBySession.Values
            .Where(track => string.Equals(track.StationName, station, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(track => track.UpdatedAt)
            .FirstOrDefault();
    }

    internal bool TryGetActiveDeviceName(string sessionId, out string? deviceName) =>
        _activeDeviceNamesBySession.TryGetValue(sessionId, out deviceName);

    internal string? GetActiveDeviceName(string? sessionId)
    {
        var session = (sessionId ?? string.Empty).Trim();
        return session.Length > 0 && _activeDeviceNamesBySession.TryGetValue(session, out var deviceName)
            ? deviceName
            : null;
    }

    internal void UpdateCurrentTrackAndHistoryKey(string sessionId, RadioCurrentTrack track, string historyKey)
    {
        _currentBySession[sessionId] = track;
        _lastHistoryKeys[sessionId] = historyKey;
    }

    internal void MarkUnavailable(string? sessionId)
    {
        var key = (sessionId ?? string.Empty).Trim();
        if (key.Length > 0)
        {
            _currentBySession.TryRemove(key, out _);
            _pendingTrackCandidates.TryRemove(key, out _);
        }
    }

    internal void EndSession(string? sessionId)
    {
        var key = (sessionId ?? string.Empty).Trim();
        if (key.Length == 0) return;
        _currentBySession.TryRemove(key, out _);
        _pendingTrackCandidates.TryRemove(key, out _);
        _activeStationsBySession.TryRemove(key, out _);
        _activeStationPathsBySession.TryRemove(key, out _);
        _activeDeviceNamesBySession.TryRemove(key, out _);
        _lastHistoryKeys.TryRemove(key, out _);
    }

    internal IReadOnlyList<RadioCurrentTrack> GetCurrentTracks() => _currentBySession.Values
        .OrderBy(track => track.StationName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(track => track.SessionId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
