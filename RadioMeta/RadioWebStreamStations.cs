using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace RadioMeta;

/// <summary>
/// Gives metadata to audio that Emby plays from a web address with no .strm
/// file behind it, for example a WebStreams library. The stream address is
/// taken from the media source Emby attaches to the playback event, with the
/// same logic "Add to Radio Meta" uses; WebStreams itself is never asked.
/// </summary>
/// <remarks>
/// Each such stream gets a virtual station path,
/// &lt;Radio Meta configuration folder&gt;\RadioMetaWebStreams\&lt;Emby item name&gt;.strm.
/// The file is never written: the metadata readers ask this class for the
/// address before they would open a station file (as they already do for the
/// proxy). The link lives in memory and is rebuilt from the next playback event
/// after a restart. Rules match by station name (the Emby item name), and
/// History records the play with the Emby library item.
/// This file is the plain core (testable without Emby); the Emby-facing part is
/// RadioWebStreamStations.Emby.cs.
/// </remarks>
internal static partial class RadioWebStreamStations
{
    internal const string FolderName = "RadioMetaWebStreams";

    // After a stream has shown no ICY header and the whole lookup (ICY and
    // FFprobe) found nothing, stop asking that address for this long. This keeps
    // podcasts and other audio without song data from causing repeated requests.
    internal static readonly TimeSpan NoMetadataRestPeriod = TimeSpan.FromMinutes(30);

    private const int MaxNameLength = 120;

    private sealed class Entry
    {
        public Entry(string streamUrl, string itemKey)
        {
            StreamUrl = streamUrl;
            ItemKey = itemKey;
        }

        public string StreamUrl { get; }
        public string ItemKey { get; }
        public volatile bool NoIcySeen;
        public long RestUntilUtcTicks;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    // An item whose media sources had no web address isn't asked again for this long.
    internal static readonly TimeSpan AddressLookupRetryPeriod = TimeSpan.FromMinutes(10);

    // Emby item number -> UTC ticks before which it must not be asked again.
    // long.MaxValue while a lookup is running.
    private static readonly ConcurrentDictionary<string, long> AddressLookups =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Claims the one media-source lookup for an Emby item. False while one is
    /// running, or within the retry period after one found no web address.
    /// </summary>
    internal static bool TryBeginAddressLookup(string? itemKey)
    {
        if (string.IsNullOrWhiteSpace(itemKey)) return false;
        var key = itemKey!.Trim();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var now = DateTimeOffset.UtcNow.UtcTicks;
            if (!AddressLookups.TryGetValue(key, out var notBefore))
            {
                if (AddressLookups.TryAdd(key, long.MaxValue)) return true;
                continue;
            }

            if (notBefore > now) return false;
            if (AddressLookups.TryUpdate(key, long.MaxValue, notBefore)) return true;
        }

        return false;
    }

    /// <summary>Ends a media-source lookup; one that found no web address is rested.</summary>
    internal static void FinishAddressLookup(string? itemKey, bool foundAddress)
    {
        if (string.IsNullOrWhiteSpace(itemKey)) return;
        var key = itemKey!.Trim();
        if (foundAddress)
        {
            AddressLookups.TryRemove(key, out _);
            return;
        }

        AddressLookups[key] = (DateTimeOffset.UtcNow + AddressLookupRetryPeriod).UtcTicks;
    }

    /// <summary>
    /// Records a virtual station and returns its path, or an empty string when the
    /// address isn't http/https or the name can't make a path. A changed address
    /// or item starts afresh.
    /// </summary>
    internal static string Register(string root, string name, string streamUrl, string itemKey)
    {
        if (!IsHttpUrl(streamUrl) || string.IsNullOrWhiteSpace(itemKey)) return string.Empty;
        var path = BuildStationPath(root, name);
        if (path.Length == 0) return string.Empty;
        Entries.AddOrUpdate(
            path,
            _ => new Entry(streamUrl, itemKey),
            (_, existing) => string.Equals(existing.StreamUrl, streamUrl, StringComparison.Ordinal) &&
                             string.Equals(existing.ItemKey, itemKey, StringComparison.Ordinal)
                ? existing
                : new Entry(streamUrl, itemKey));
        return path;
    }

    /// <summary>True when the path is a virtual web-stream station.</summary>
    internal static bool IsVirtualStation(string? stationPath) =>
        TryGetEntry(stationPath, out _);

    /// <summary>The stream address for a virtual station, used by the metadata readers.</summary>
    internal static bool TryGetStreamUrl(string? stationPath, out string streamUrl)
    {
        streamUrl = string.Empty;
        if (!TryGetEntry(stationPath, out var entry)) return false;
        streamUrl = entry.StreamUrl;
        return true;
    }

    /// <summary>True when an Emby item number is the one a virtual station was created for.</summary>
    internal static bool ItemKeyMatches(string? stationPath, string? itemKey) =>
        !string.IsNullOrWhiteSpace(itemKey) &&
        TryGetEntry(stationPath, out var entry) &&
        string.Equals(entry.ItemKey, itemKey!.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Finds the virtual station already made for an Emby item, for playback
    /// updates that don't carry the stream address.
    /// </summary>
    internal static bool TryFindByItemKey(string? itemKey, out string stationPath)
    {
        stationPath = string.Empty;
        if (string.IsNullOrWhiteSpace(itemKey) || Entries.IsEmpty) return false;
        var key = itemKey!.Trim();
        foreach (var pair in Entries)
        {
            if (string.Equals(pair.Value.ItemKey, key, StringComparison.Ordinal))
            {
                stationPath = pair.Key;
                return true;
            }
        }

        return false;
    }

    /// <summary>Called by the ICY reader when a server sends no icy-metaint header.</summary>
    internal static void MarkNoIcy(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl) || Entries.IsEmpty) return;
        foreach (var entry in Entries.Values)
        {
            if (string.Equals(entry.StreamUrl, streamUrl, StringComparison.Ordinal))
                entry.NoIcySeen = true;
        }
    }

    /// <summary>
    /// Called when a whole lookup (ICY and FFprobe) found no song data. Only a
    /// stream that also had no ICY header is rested, so a live station that is
    /// between songs or playing an advert keeps being asked.
    /// </summary>
    internal static void MarkLookupFoundNothing(string stationPath)
    {
        if (!TryGetEntry(stationPath, out var entry) || !entry.NoIcySeen) return;
        System.Threading.Interlocked.Exchange(ref entry.RestUntilUtcTicks,
            (DateTimeOffset.UtcNow + NoMetadataRestPeriod).UtcTicks);
    }

    /// <summary>Called when song data was found, so the stream is asked normally again.</summary>
    internal static void MarkLookupFoundMetadata(string stationPath)
    {
        if (!TryGetEntry(stationPath, out var entry)) return;
        entry.NoIcySeen = false;
        System.Threading.Interlocked.Exchange(ref entry.RestUntilUtcTicks, 0);
    }

    /// <summary>True while a stream with no song data should not be asked again.</summary>
    internal static bool IsResting(string stationPath) =>
        TryGetEntry(stationPath, out var entry) &&
        System.Threading.Interlocked.Read(ref entry.RestUntilUtcTicks) > DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>
    /// Builds the virtual path from the Emby item name. Characters that can't be
    /// in a file name become "_", so the station name stays readable in rules,
    /// History and Diagnostics.
    /// </summary>
    internal static string BuildStationPath(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((name ?? string.Empty).Trim()
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray()).Trim().TrimEnd('.');
        if (safe.Length > MaxNameLength) safe = safe.Substring(0, MaxNameLength).Trim();
        if (safe.Length == 0) safe = "Web stream";
        try
        {
            return Path.GetFullPath(Path.Combine(root, safe + ".strm"));
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>For tests: forget all virtual stations.</summary>
    internal static void Clear()
    {
        Entries.Clear();
        AddressLookups.Clear();
    }

    private static bool TryGetEntry(string? stationPath, out Entry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(stationPath) || Entries.IsEmpty) return false;
        string key;
        try { key = Path.GetFullPath(stationPath!); }
        catch { return false; }
        if (Entries.TryGetValue(key, out var found))
        {
            entry = found;
            return true;
        }

        return false;
    }

    private static bool IsHttpUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        RadioMetaUriSchemeHelpers.IsHttpOrHttps(uri);
}
