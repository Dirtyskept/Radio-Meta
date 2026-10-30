using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

namespace RadioMeta;

/// <summary>
/// Builds a station snapshot from the varied Emby playback-event shapes.
/// Keeps reflective item inspection and stream/artwork discovery out of the
/// snapshot store, while preserving the existing qualification rules.
/// </summary>
internal static class RadioCurrentlyPlayingStationCandidateBuilder
{
    internal static bool IsClearlyNonRadioPlayback(PlaybackProgressEventArgs args)
    {
        var possibleItems = new List<object>();
        AddObject(possibleItems, args.MediaInfo);
        AddObject(possibleItems, args.Item);
        AddObject(possibleItems, ReadProperty(args.PlaySession, "FullNowPlayingItem"));
        AddObject(possibleItems, ReadProperty(args.PlaySession, "NowPlayingItem"));
        AddObject(possibleItems, ReadProperty(args.Session, "NowPlayingItem"));

        foreach (var item in possibleItems)
        {
            var mediaType = ReadString(item, "MediaType");
            var type = ReadString(item, "Type");
            if (mediaType.Equals("Video", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Movie", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Episode", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Video", StringComparison.OrdinalIgnoreCase))
                return true;

            var path = ReadString(item, "Path");
            if (string.IsNullOrWhiteSpace(path) ||
                string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase))
                continue;

            // A local audio file is evidence of ordinary library playback, but
            // never treat a remote HTTP stream's .mp3/.aac suffix as such.
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) ||
                !RadioMetaUriSchemeHelpers.IsHttpHttpsOrIcy(uri))
            {
                var extension = Path.GetExtension(path);
                if (new[] { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma", ".mp4", ".mkv", ".avi", ".mov" }
                    .Contains(extension, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    internal static RadioCurrentlyPlayingStationSnapshot? BuildCandidate(
        string sessionKey,
        PlaybackProgressEventArgs args)
    {
        var playSession = args.PlaySession;
        var items = new List<object>();

        // Providers do not all populate the same part of the Emby playback event.
        // Collect the item, DTO, and session variants instead of relying only on
        // PlaySessionInfo.FullNowPlayingItem / NowPlayingItem.
        AddObject(items, ReadProperty(playSession, "FullNowPlayingItem"));
        AddObject(items, ReadProperty(playSession, "NowPlayingItem"));
        AddObject(items, args.MediaInfo);
        AddObject(items, args.Item);
        AddObject(items, ReadProperty(args.Session, "FullNowPlayingItem"));
        AddObject(items, ReadProperty(args.Session, "NowPlayingItem"));
        // A few integrations expose only a MediaSource and no hydrated item DTO.
        // Treat that source as the last-resort candidate root; the radio-evidence
        // checks below still have to pass before it can be returned.
        if (items.Count == 0)
            AddObject(items, args.MediaSource);
        if (items.Count == 0)
            return null;

        var expandedItems = new List<object>(items);
        AddObject(expandedItems, args.MediaSource);
        var roots = new List<object>();
        AddObject(roots, args.MediaInfo);
        AddObject(roots, args.MediaSource);
        AddObject(roots, playSession);
        AddObject(roots, args.Session);
        foreach (var item in items)
            AddObject(roots, item);

        // PlaybackProgressEventArgs.MediaSource is the most important extra
        // source for channel plugins: it can contain the resolved external URL
        // even when the item DTO does not expose one itself.
        foreach (var root in roots)
        {
            AddSourceObjects(expandedItems, ReadProperty(root, "MediaSources"));
            AddSourceObjects(expandedItems, ReadProperty(root, "MediaSource"));
            AddSourceObjects(expandedItems, ReadProperty(root, "MediaSourceInfo"));
            AddSourceObjects(expandedItems, ReadProperty(root, "NowPlayingMediaSource"));
            AddSourceObjects(expandedItems, ReadProperty(root, "MediaInfo"));
            AddSourceObjects(expandedItems, ReadProperty(root, "LiveStream"));
            // Some channel providers keep the resolved station record beneath a
            // Station/Channel wrapper rather than copying its fields to the DTO.
            AddSourceObjects(expandedItems, ReadProperty(root, "Station"));
            AddSourceObjects(expandedItems, ReadProperty(root, "Channel"));
            AddSourceObjects(expandedItems, ReadProperty(root, "ChannelItem"));
            AddSourceObjects(expandedItems, ReadProperty(root, "ResolvedStation"));
        }

        // A MediaSourceInfo may itself wrap a secondary source list/path, so do
        // one bounded expansion pass over objects already discovered.
        foreach (var item in expandedItems.ToArray())
        {
            AddSourceObjects(expandedItems, ReadProperty(item, "MediaSources"));
            AddSourceObjects(expandedItems, ReadProperty(item, "MediaSource"));
            AddSourceObjects(expandedItems, ReadProperty(item, "MediaSourceInfo"));
            AddSourceObjects(expandedItems, ReadProperty(item, "Station"));
            AddSourceObjects(expandedItems, ReadProperty(item, "Channel"));
            AddSourceObjects(expandedItems, ReadProperty(item, "ChannelItem"));
            AddSourceObjects(expandedItems, ReadProperty(item, "ResolvedStation"));
        }

        var channelItems = items.Concat(expandedItems).Distinct().Where(IsChannelLike).ToList();
        var hasChannelIdentity = channelItems.Count > 0;
        var isAudio = items.Any(IsAudioLike);
        var isInfiniteStream = expandedItems.Any(IsInfiniteStreamLike);

        var stationIdPair = items.Concat(expandedItems)
            .Distinct()
            .SelectMany(ReadProviderIds)
            .FirstOrDefault(item => IsRadioBrowserUuidKey(item.Key));
        var stationUuid = stationIdPair.Value ?? string.Empty;
        if (stationUuid.Length == 0)
        {
            stationUuid = items.Concat(expandedItems).Distinct()
                .Select(item => ReadString(item, "StationUuid"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        var streamUrl = FindStreamUrl(expandedItems);
        var artworkUrl = FindArtworkUrl(expandedItems);
        // Keep the actual Emby library item id so the dashboard can request its
        // Primary image even when the provider supplies no path or provider ids.
        var embyItemId = items.Select(item => RadioMetaValueHelpers.FirstNonEmpty(ReadString(item, "Id"), ReadString(item, "ItemId")))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var strmPath = items.Select(item => ReadProperty(item, "Path") as string)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .FirstOrDefault(path => string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase) && File.Exists(path));

        if (streamUrl.Length == 0 && isAudio && !string.IsNullOrWhiteSpace(strmPath))
            streamUrl = ReadStreamFromStrm(strmPath!);

        // Accept clear channel identity even if a provider hides the original URL.
        // Also accept infinite streams (common for TuneIn/Radio Browser channels),
        // or audio backed by an externally addressable URL / real .strm file.
        // This still rejects ordinary audio/video items without radio-like source
        // evidence, while allowing a channel to be pre-filled when its URL is hidden.
        if (!hasChannelIdentity && !isInfiniteStream &&
            !(isAudio && (streamUrl.Length > 0 || !string.IsNullOrWhiteSpace(strmPath))))
            return null;

        string name = string.Empty;
        foreach (var item in channelItems)
        {
            name = ReadString(item, "ChannelName");
            if (name.Length == 0) name = ReadString(item, "Name");
            if (name.Length > 0) break;
        }

        if (name.Length == 0)
        {
            foreach (var item in items)
            {
                name = ReadString(item, "ChannelName");
                if (name.Length == 0) name = ReadString(item, "Name");
                if (name.Length > 0) break;
            }
        }

        if (name.Length == 0)
        {
            // Some channel integrations expose a useful label only on their
            // selected media source. Do not use any source name until the item
            // has passed the radio-candidate checks above.
            name = expandedItems.Select(item => ReadString(item, "ChannelName"))
                .Concat(expandedItems.Select(item => ReadString(item, "Name")))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        if (name.Length == 0 && !string.IsNullOrWhiteSpace(strmPath))
            name = Path.GetFileNameWithoutExtension(strmPath);
        if (name.Length == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(strmPath) && streamUrl.Length == 0)
            streamUrl = ReadStreamFromStrm(strmPath!);

        var source = hasChannelIdentity
            ? "Emby channel session"
            : isInfiniteStream
                ? "Emby infinite stream session"
                : "Emby audio stream session";

        return new RadioCurrentlyPlayingStationSnapshot
        {
            SessionKey = sessionKey,
            Name = Trim(name, 160),
            StreamUrl = streamUrl,
            ArtworkUrl = artworkUrl,
            ItemId = embyItemId,
            StationUuid = stationUuid,
            Source = source,
            SeenAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static bool IsInfiniteStreamLike(object item)
    {
        var value = ReadProperty(item, "IsInfiniteStream");
        return value is bool isInfinite && isInfinite;
    }

    private static bool IsChannelLike(object item)
    {
        var type = ReadString(item, "Type");
        var clrType = item.GetType().Name;
        if (type.IndexOf("channel", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clrType.IndexOf("channel", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (ReadString(item, "ChannelId").Length > 0 || ReadString(item, "ChannelName").Length > 0 ||
            ReadString(item, "StationUuid").Length > 0 || ReadString(item, "StationId").Length > 0)
            return true;
        return ReadProviderIds(item).Any(pair => LooksLikeStationIdKey(pair.Key));
    }

    private static bool IsAudioLike(object item)
    {
        var mediaType = ReadString(item, "MediaType");
        if (mediaType.Equals("Audio", StringComparison.OrdinalIgnoreCase))
            return true;
        var type = ReadString(item, "Type");
        if (type.Equals("Audio", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("MusicTrack", StringComparison.OrdinalIgnoreCase))
            return true;
        return item.GetType().Name.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadProviderIds(object item)
    {
        var providerIds = ReadProperty(item, "ProviderIds");
        if (providerIds is not IEnumerable values || providerIds is string)
            yield break;
        foreach (var entry in values)
        {
            if (entry is null) continue;
            var key = ReadString(entry, "Key");
            var value = ReadString(entry, "Value");
            if (key.Length > 0 && value.Length > 0)
                yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static bool LooksLikeStationIdKey(string key) =>
        IsRadioBrowserUuidKey(key) ||
        key.IndexOf("channelid", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsRadioBrowserUuidKey(string key) =>
        key.IndexOf("radiobrowser", StringComparison.OrdinalIgnoreCase) >= 0 ||
        key.IndexOf("stationuuid", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string FindStreamUrl(IEnumerable<object> objects)
    {
        var propertyNames = new[]
        {
            "DirectStreamUrl", "StreamUrl", "ResolvedUrl", "ResolvedStreamUrl",
            "UrlResolved", "url_resolved", "Url", "Uri", "Path",
            "MediaSourcePath", "RemoteUrl", "OriginalUrl", "PlaybackUrl"
        };
        foreach (var item in objects)
        {
            foreach (var propertyName in propertyNames)
            {
                var value = ReadString(item, propertyName);
                var accepted = ValidateExternalStreamUrl(value);
                if (accepted.Length > 0) return accepted;
            }
        }
        return string.Empty;
    }

    private static string FindArtworkUrl(IEnumerable<object> objects)
    {
        var propertyNames = new[] { "StationLogoUrl", "LogoUrl", "ArtworkUrl", "FaviconUrl", "Favicon", "PrimaryImageUrl", "ImageUrl", "IconUrl" };
        foreach (var item in objects)
        {
            foreach (var propertyName in propertyNames)
            {
                var value = ReadString(item, propertyName);
                if (IsSafePublicHttpUrl(value)) return value;
            }
        }
        return string.Empty;
    }

    private static string ValidateExternalStreamUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return string.Empty;
        if (!RadioMetaUriSchemeHelpers.IsHttpHttpsOrIcy(uri))
            return string.Empty;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return string.Empty;
        if (uri.Query.IndexOf("api_key", StringComparison.OrdinalIgnoreCase) >= 0 ||
            uri.Query.IndexOf("access_token", StringComparison.OrdinalIgnoreCase) >= 0 ||
            uri.Query.IndexOf("emby_token", StringComparison.OrdinalIgnoreCase) >= 0 ||
            uri.Query.IndexOf("x-emby-token", StringComparison.OrdinalIgnoreCase) >= 0)
            return string.Empty;
        var path = uri.AbsolutePath;
        if (path.StartsWith("/Audio/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Videos/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/emby/", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return uri.AbsoluteUri;
    }

    private static bool IsSafePublicHttpUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (!RadioMetaUriSchemeHelpers.IsHttpOrHttps(uri) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.AbsolutePath.StartsWith("/emby/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/Images/", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Query.IndexOf("api_key", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("access_token", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("emby_token", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("x-emby-token", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static string ReadStreamFromStrm(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (!reader.EndOfStream)
            {
                var line = (reader.ReadLine() ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                return ValidateExternalStreamUrl(line);
            }
        }
        catch { }
        return string.Empty;
    }

    private static void AddSourceObjects(List<object> result, object? value)
    {
        if (value is null || value is string) return;
        if (value is IEnumerable sequence)
        {
            var count = 0;
            foreach (var item in sequence)
            {
                if (item is not null && item is not string) result.Add(item);
                if (++count >= 12) break;
            }
        }
        else if (value.GetType().IsClass)
        {
            result.Add(value);
        }
    }

    private static void AddObject(List<object> result, object? value)
    {
        if (value is not null && !result.Contains(value)) result.Add(value);
    }

    private static object? ReadProperty(object? item, string name)
    {
        if (item is null) return null;
        try { return item.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(item); }
        catch { return null; }
    }

    private static string ReadString(object? item, string name)
    {
        var value = ReadProperty(item, name);
        if (value is string text) return text.Trim();
        if (value is Enum || value is Guid || value is Uri ||
            value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong)
            return value.ToString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private static string Trim(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
