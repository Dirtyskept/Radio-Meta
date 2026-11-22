using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>
/// Writes one Emby log line per added or removed metadata rule (1.3.153), with
/// the rule itself, so a rule deleted by mistake can be read back from the Emby
/// server log. A changed rule shows as removed (old) and added (new). Rules hold
/// station names, device names and title text only; no paths, addresses or tokens.
/// </summary>
public static class RadioMetaRuleChangeLog
{
    public static void Log<T>(ILogger? logger, string kind, IEnumerable<T>? before, IEnumerable<T>? after, Func<T, string> describe)
    {
        if (logger is null) return;
        try
        {
            var oldRules = (before ?? Enumerable.Empty<T>()).Select(describe).ToList();
            var newRules = (after ?? Enumerable.Empty<T>()).Select(describe).ToList();
            foreach (var removed in Subtract(oldRules, newRules))
            {
                logger.Info("{0}", "Radio Meta " + kind + " removed: " + removed);
            }
            foreach (var added in Subtract(newRules, oldRules))
            {
                logger.Info("{0}", "Radio Meta " + kind + " added: " + added);
            }
        }
        catch
        {
            // Logging must never affect saving.
        }
    }

    public static string Quote(string? text) => "\"" + (text ?? string.Empty).Trim() + "\"";

    public static string StationOrAll(string? station) =>
        string.IsNullOrWhiteSpace(station) ? "all stations" : Quote(station);

    // Items in "from" that are not in "remove", counting duplicates.
    private static IEnumerable<string> Subtract(List<string> from, List<string> remove)
    {
        var left = new List<string>(remove);
        foreach (var item in from)
        {
            var index = left.IndexOf(item);
            if (index >= 0) left.RemoveAt(index);
            else yield return item;
        }
    }
}
