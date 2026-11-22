using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>One save of the Settings tab, without the paths themselves.</summary>
public sealed class RadioMetaSettingsSaveEventDto
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string Device { get; set; } = string.Empty;
    public string StationFolder { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
    public string AdaptiveRouting { get; set; } = string.Empty;
    public string StationsOutsideFolder { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
}

/// <summary>
/// Records each Settings save (1.3.153), so a station folder or FFprobe path that
/// was cleared can be traced to the save, time and device that did it. Paths are
/// not stored: only whether each was set, changed, cleared or left unchanged.
/// Kept in memory (shown in Diagnostics) and written as one line to the Emby
/// server log, which survives restarts.
/// </summary>
public sealed class RadioMetaSettingsSaveStore
{
    private const int MaximumEvents = 20;
    private readonly object _sync = new();
    private readonly List<RadioMetaSettingsSaveEventDto> _events = new();

    /// <summary>Set by the entry point at start-up; null until then.</summary>
    public ILogger? Logger { get; set; }

    public static string DescribePathChange(string? before, string? after)
    {
        var oldValue = (before ?? string.Empty).Trim();
        var newValue = (after ?? string.Empty).Trim();
        if (oldValue.Length == 0 && newValue.Length == 0) return "unchanged (blank)";
        if (oldValue.Length == 0) return "set";
        if (newValue.Length == 0) return "CLEARED";
        return string.Equals(oldValue, newValue, StringComparison.Ordinal) ? "unchanged (set)" : "changed";
    }

    public static string DescribeSwitch(bool before, bool after) =>
        (after ? "on" : "off") + (before == after ? string.Empty : " (changed)");

    public void Record(RadioMetaSettingsSaveEventDto saveEvent)
    {
        lock (_sync)
        {
            _events.Insert(0, saveEvent);
            if (_events.Count > MaximumEvents) _events.RemoveRange(MaximumEvents, _events.Count - MaximumEvents);
        }

        var logger = Logger;
        if (logger is null) return;
        var line = string.Format(
            "Radio Meta settings saved from {0}: station folder {1}; FFprobe path {2}; adaptive routing {3}; stations outside the folder {4}; {5}.",
            string.IsNullOrWhiteSpace(saveEvent.Device) ? "an unknown device" : saveEvent.Device,
            saveEvent.StationFolder, saveEvent.FfprobePath, saveEvent.AdaptiveRouting,
            saveEvent.StationsOutsideFolder, saveEvent.Result);
        try
        {
            // Passed as an argument, so braces in a device name can't break the format.
            if (saveEvent.StationFolder == "CLEARED" || saveEvent.FfprobePath == "CLEARED") logger.Warn("{0}", line);
            else logger.Info("{0}", line);
        }
        catch
        {
            // Logging must never affect saving.
        }
    }

    public List<RadioMetaSettingsSaveEventDto> GetRecent(int count)
    {
        lock (_sync)
        {
            return _events.Take(Math.Max(0, count)).ToList();
        }
    }
}
