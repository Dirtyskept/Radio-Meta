using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RadioMeta;

/// <summary>A sanitized, in-memory breadcrumb for understanding the metadata pipeline.</summary>
public sealed class RadioMetaDiagnosticEventDto
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string RawMetadata { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

/// <summary>
/// Retains a bounded, in-memory history of metadata-reader decisions. It never stores
/// stream URLs, authentication tokens, filesystem paths, or Emby session identifiers.
/// </summary>
public sealed class RadioMetaDiagnosticEventStore
{
    private const int MaximumEvents = 80;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(8);
    private static readonly Regex UrlRegex = new("(?i)\\bhttps?://[^\\s<>\"']+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretRegex = new(@"(?i)\b(api[_-]?key|access[_-]?token|token|password)=([^\s&;]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _sync = new();
    private readonly List<RadioMetaDiagnosticEventDto> _events = new();

    public void Record(
        string? stationName,
        string? source,
        string? status,
        string? title,
        string? artist,
        string? details,
        string? rawMetadata = null)
    {
        var item = new RadioMetaDiagnosticEventDto
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            StationName = SafeText(stationName, 100),
            Source = SafeText(source, 48),
            Status = NormalizeStatus(status),
            Title = SafeText(title, 180),
            Artist = SafeText(artist, 120),
            RawMetadata = SafeText(rawMetadata, 240),
            Details = SafeText(details, 260)
        };

        lock (_sync)
        {
            var recent = _events.LastOrDefault(existing =>
                DateTimeOffset.UtcNow - existing.TimestampUtc <= DuplicateWindow &&
                string.Equals(existing.StationName, item.StationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Source, item.Source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Status, item.Status, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Title, item.Title, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Details, item.Details, StringComparison.OrdinalIgnoreCase));
            if (recent is not null)
            {
                return;
            }

            _events.Add(item);
            if (_events.Count > MaximumEvents)
            {
                _events.RemoveRange(0, _events.Count - MaximumEvents);
            }
        }
    }

    public List<RadioMetaDiagnosticEventDto> GetRecent(int count = 40)
    {
        lock (_sync)
        {
            return _events
                .TakeLast(Math.Clamp(count, 1, MaximumEvents))
                .Reverse()
                .Select(item => new RadioMetaDiagnosticEventDto
                {
                    TimestampUtc = item.TimestampUtc,
                    StationName = item.StationName,
                    Source = item.Source,
                    Status = item.Status,
                    Title = item.Title,
                    Artist = item.Artist,
                    RawMetadata = item.RawMetadata,
                    Details = item.Details
                })
                .ToList();
        }
    }

    private static string NormalizeStatus(string? value)
    {
        var normalized = SafeText(value, 24).ToLowerInvariant();
        return normalized.Length == 0 ? "info" : normalized;
    }

    private static string SafeText(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var safe = UrlRegex.Replace(value, "[URL omitted]");
        safe = SecretRegex.Replace(safe, "$1=[redacted]");
        safe = safe.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
        safe = Regex.Replace(safe, @"\s{2,}", " ");
        if (safe.Length > limit) safe = safe[..limit] + "…";
        return safe;
    }
}
