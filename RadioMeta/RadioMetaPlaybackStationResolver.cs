using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.Session;

namespace RadioMeta;

/// <summary>
/// Resolves whether an Emby playback item belongs to a Radio Meta station and
/// centralizes path normalization and active-session station checks.
/// </summary>
internal sealed class RadioMetaPlaybackStationResolver
{
    private readonly RadioMetaPlugin _plugin;

    internal RadioMetaPlaybackStationResolver(RadioMetaPlugin plugin)
    {
        _plugin = plugin;
    }

    internal string? ResolveStationPath(string? path, object? item)
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

    /// <summary>
    /// Gets bounded, non-sensitive station identity candidates. This is used to apply StationOnly
    /// rules even if Emby's display title and the underlying .strm filename differ during handoff.
    /// </summary>
    internal IReadOnlyList<string> GetStationNameCandidates(string stationPath, params object?[] items)
    {
        var names = new List<string>();
        void Add(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length > 0 && !names.Contains(normalized, StringComparer.OrdinalIgnoreCase) && names.Count < 12)
                names.Add(normalized);
        }

        string? fileStationName = null;
        try
        {
            fileStationName = Path.GetFileNameWithoutExtension(stationPath);
            Add(fileStationName);
        }
        catch { /* identity is best-effort */ }

        var itemsWithNames = new List<(object Item, string? Name)>();
        foreach (var item in items ?? Array.Empty<object?>())
        {
            if (item is null) continue;
            var itemName = ReadStringProperty(item, "Name");
            Add(itemName);
            itemsWithNames.Add((item, itemName));
        }

        // ProviderIds can momentarily carry the previous station's RadioMetaStation value
        // during an Emby handoff. Do not let a ProviderId that contradicts both the selected
        // .strm filename and the selected item's visible name activate the previous station's
        // StationOnly/Carplay rule. The filename and current item name are the stronger signals.
        foreach (var namedItem in itemsWithNames)
        {
            var providerStation = ReadRadioMetaStationProviderId(namedItem.Item);
            if (string.IsNullOrWhiteSpace(providerStation)) continue;
            var agreesWithFile = !string.IsNullOrWhiteSpace(fileStationName) &&
                string.Equals(providerStation.Trim(), fileStationName.Trim(), StringComparison.OrdinalIgnoreCase);
            var agreesWithItemName = !string.IsNullOrWhiteSpace(namedItem.Name) &&
                string.Equals(providerStation.Trim(), namedItem.Name.Trim(), StringComparison.OrdinalIgnoreCase);
            if (agreesWithFile || agreesWithItemName)
                Add(providerStation);
        }
        return names;
    }

    private static string? ReadStringProperty(object item, string propertyName)
    {
        try
        {
            var value = item.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
            return value as string ?? value?.ToString();
        }
        catch { return null; }
    }

    private static string? ReadRadioMetaStationProviderId(object item)
    {
        try
        {
            var providerIds = item.GetType().GetProperty("ProviderIds", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
            if (providerIds is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (string.Equals(Convert.ToString(entry.Key), "RadioMetaStation", StringComparison.OrdinalIgnoreCase))
                        return Convert.ToString(entry.Value);
                }
            }
            if (providerIds is IEnumerable entries)
            {
                foreach (var entry in entries)
                {
                    if (entry is null) continue;
                    var key = ReadStringProperty(entry, "Key");
                    if (string.Equals(key, "RadioMetaStation", StringComparison.OrdinalIgnoreCase))
                        return ReadStringProperty(entry, "Value");
                }
            }
        }
        catch { /* ProviderIds vary across Emby DTO versions; other identities remain available. */ }
        return null;
    }

    internal bool SessionStillUsesStation(
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

    internal bool IsStrmPath(string path) =>
        Path.GetExtension(path).Equals(
            ".strm",
            StringComparison.OrdinalIgnoreCase);

    internal bool PathsEqual(string first, string second)
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

    internal static string NormalizePath(string path)
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

}
