using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RadioMeta;

/// <summary>A privacy-safe summary of one recent station-file operation.</summary>
public sealed class RadioMetaStationOperationEventDto
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string FileChangeStatus { get; set; } = string.Empty;
    public string ProxySidecarStatus { get; set; } = string.Empty;
    public string LibraryRefreshStatus { get; set; } = string.Empty;
    public string StationListRefreshStatus { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

/// <summary>
/// Keeps a small in-memory record of recent create/edit/delete/proxy operations and
/// the dashboard's follow-up library refresh. It intentionally stores no paths,
/// stream URLs, proxy tokens, credentials, or Emby IDs. Records reset when the
/// plugin/server process restarts.
/// </summary>
public sealed class RadioMetaStationOperationStore
{
    private const int MaximumEvents = 30;
    private static readonly Regex UrlRegex = new(@"(?i)\b(?:https?|icy)://[^\s<>""']+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretRegex = new(@"(?i)\b(api[_-]?key|access[_-]?token|token|password)=([^\s&;]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _sync = new();
    private readonly List<Entry> _events = new();

    private sealed class Entry
    {
        public string Id { get; init; } = string.Empty;
        public RadioMetaStationOperationEventDto Event { get; init; } = new();
    }

    public string RecordFileOperation(
        string operation,
        string? stationName,
        string fileChangeStatus,
        string proxySidecarStatus,
        bool libraryScanTargetFound,
        string? details = null)
    {
        var entry = new Entry
        {
            Id = Guid.NewGuid().ToString("N"),
            Event = new RadioMetaStationOperationEventDto
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Operation = SafeText(operation, 32),
                StationName = SafeStationName(stationName),
                FileChangeStatus = SafeStatus(fileChangeStatus, "unknown", 36),
                ProxySidecarStatus = SafeStatus(proxySidecarStatus, "not checked", 48),
                LibraryRefreshStatus = libraryScanTargetFound ? "pending result" : "not requested: no matching library",
                StationListRefreshStatus = "pending result",
                Details = SafeText(details ?? "Server completed the station-file operation.", 140)
            }
        };

        lock (_sync)
        {
            _events.Add(entry);
            if (_events.Count > MaximumEvents)
                _events.RemoveRange(0, _events.Count - MaximumEvents);
        }

        return entry.Id;
    }

    /// <summary>Records a dashboard-reported failure when the server did not return a successful file-operation response.</summary>
    public void RecordClientFailure(string? operation, string? stationName, string? failureCode)
    {
        var safeOperation = NormalizeOperation(operation);
        if (safeOperation.Length == 0) return;

        var (fileStatus, details) = (failureCode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "server-rejected" => ("server rejected / not confirmed", "The server did not confirm a successful file change."),
            "response-unexpected" => ("outcome unconfirmed", "The dashboard received an unexpected response; file state should be checked."),
            _ => ("outcome unconfirmed", "The dashboard request failed or did not receive a confirmation; file state should be checked.")
        };

        var entry = new Entry
        {
            Id = Guid.NewGuid().ToString("N"),
            Event = new RadioMetaStationOperationEventDto
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Operation = safeOperation,
                StationName = SafeStationName(stationName),
                FileChangeStatus = fileStatus,
                ProxySidecarStatus = "not verified",
                LibraryRefreshStatus = "not attempted",
                StationListRefreshStatus = "not attempted",
                Details = details
            }
        };

        lock (_sync)
        {
            _events.Add(entry);
            if (_events.Count > MaximumEvents)
                _events.RemoveRange(0, _events.Count - MaximumEvents);
        }
    }

    /// <summary>Updates only the result fields supplied by the dashboard.</summary>
    public bool UpdateDashboardResults(string? operationId, string? libraryRefreshStatus, string? stationListRefreshStatus)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return false;
        var normalizedLibrary = string.IsNullOrWhiteSpace(libraryRefreshStatus) ? null : NormalizeLibraryRefresh(libraryRefreshStatus);
        var normalizedStationList = string.IsNullOrWhiteSpace(stationListRefreshStatus) ? null : NormalizeStationListRefresh(stationListRefreshStatus);
        if ((!string.IsNullOrWhiteSpace(libraryRefreshStatus) && normalizedLibrary is null) ||
            (!string.IsNullOrWhiteSpace(stationListRefreshStatus) && normalizedStationList is null))
            return false;

        lock (_sync)
        {
            var entry = _events.LastOrDefault(item => string.Equals(item.Id, operationId.Trim(), StringComparison.Ordinal));
            if (entry is null) return false;
            if (normalizedLibrary is not null) entry.Event.LibraryRefreshStatus = normalizedLibrary;
            if (normalizedStationList is not null) entry.Event.StationListRefreshStatus = normalizedStationList;
            if (normalizedLibrary is not null || normalizedStationList is not null)
            {
                var suffix = normalizedLibrary switch
                {
                    "accepted by Emby" => "Emby accepted the targeted library refresh request; the scan may continue in the background.",
                    "failed" => "Emby did not accept the targeted library refresh request.",
                    "not requested: no matching library" => "No unique matching Emby library was found; no targeted library refresh request was sent.",
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(suffix)) entry.Event.Details = suffix;
            }
            return true;
        }
    }

    public List<RadioMetaStationOperationEventDto> GetRecent(int count = 20)
    {
        lock (_sync)
        {
            return _events
                .TakeLast(Math.Clamp(count, 1, MaximumEvents))
                .Reverse()
                .Select(entry => new RadioMetaStationOperationEventDto
                {
                    TimestampUtc = entry.Event.TimestampUtc,
                    Operation = entry.Event.Operation,
                    StationName = entry.Event.StationName,
                    FileChangeStatus = entry.Event.FileChangeStatus,
                    ProxySidecarStatus = entry.Event.ProxySidecarStatus,
                    LibraryRefreshStatus = entry.Event.LibraryRefreshStatus,
                    StationListRefreshStatus = entry.Event.StationListRefreshStatus,
                    Details = entry.Event.Details
                })
                .ToList();
        }
    }

    private static string NormalizeOperation(string? value)
    {
        var candidate = SafeText(value, 32);
        return candidate.ToLowerInvariant() switch
        {
            "create" or "create station" => "Create station",
            "edit/rename" or "edit/rename station" or "edit" or "rename" => "Edit/rename station",
            "delete" or "delete station" or "disable station" => "Delete station",
            "enable proxy" or "proxy enable" => "Enable proxy",
            "disable proxy" or "proxy disable" => "Disable proxy",
            _ => string.Empty
        };
    }

    private static string? NormalizeLibraryRefresh(string value) => value.Trim().ToLowerInvariant() switch
    {
        "accepted" => "accepted by Emby",
        "failed" => "failed",
        "no-target" => "not requested: no matching library",
        _ => null!
    };

    private static string? NormalizeStationListRefresh(string value) => value.Trim().ToLowerInvariant() switch
    {
        "completed" => "completed",
        "completed-zero-stations" => "completed: zero stations returned",
        "failed" => "failed",
        "not-run-busy" => "not run: another scan was already running",
        "unexpected-response" => "failed: unexpected response",
        "not-started" => "not started",
        _ => null!
    };

    private static string SafeStationName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        // Valid Windows .strm station filenames cannot contain slash, backslash or
        // drive separators. Treat such values as invalid/path-like input instead of
        // accidentally retaining directory fragments supplied in a failed form.
        if (value.Contains('/') || value.Contains('\\') || Regex.IsMatch(value, @"(?i)^\s*[a-z]:"))
            return "[path-like station name omitted]";
        return SafeText(value, 100);
    }

    private static string SafeStatus(string? value, string fallback, int limit)
    {
        var safe = SafeText(value, limit);
        return safe.Length == 0 ? fallback : safe;
    }

    private static string SafeText(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var safe = UrlRegex.Replace(value, "[URL omitted]");
        safe = SecretRegex.Replace(safe, "$1=[redacted]");
        safe = safe.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Replace('|', '/').Trim();
        safe = Regex.Replace(safe, @"\s{2,}", " ");
        if (safe.Length > limit) safe = safe[..limit] + "…";
        return safe;
    }
}
