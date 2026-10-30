using System;
using System.Collections;
using System.Collections.Generic;

namespace RadioMeta;

/// <summary>
/// Publishes and clears Radio Meta's machine-readable ProviderIds on Emby session DTOs.
/// It never changes display-facing fields such as Name, OriginalTitle, Artists or Album.
/// </summary>
internal sealed class RadioCurrentTrackPublisher
{
    private const string StationKey = "RadioMetaStation";
    private const string ArtistKey = "RadioMetaArtist";
    private const string SongKey = "RadioMetaSong";
    private const string SourceKey = "RadioMetaSource";
    private const string UpdatedAtKey = "RadioMetaUpdatedAt";

    // Remove these legacy session-only values when publishing/clearing so a stale value
    // from the previous RadioNowPlaying helper cannot override current RadioMeta metadata.
    private static readonly string[] LegacyProviderKeys =
    {
        "RadioNowPlayingStation",
        "RadioNowPlayingArtist",
        "RadioNowPlayingSong",
        "RadioNowPlayingSource",
        "RadioNowPlayingUpdatedAt"
    };

    internal bool TryPublishToSessionItem(object item, RadioCurrentTrack track)
    {
        if (item is null || track is null)
        {
            return false;
        }

        return TryPublishProviderIds(item, track);
    }

    internal bool HasPublishedRadioMetaMetadata(object? item) => HasRadioMetaProviderIds(item);

    internal void ClearPublishedMetadata(object? item, string? sessionId = null)
    {
        ClearRadioMetaProviderIds(item);
    }

    private bool TryPublishProviderIds(object item, RadioCurrentTrack track)
    {
        try
        {
            var property = item.GetType().GetProperty("ProviderIds");
            if (property is null)
            {
                return false;
            }

            var value = property.GetValue(item);
            if (value is null)
            {
                if (!property.CanWrite)
                {
                    return false;
                }

                var propertyType = property.PropertyType;
                object? created = propertyType.IsInterface || propertyType.IsAbstract
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : Activator.CreateInstance(propertyType);

                if (created is null || !propertyType.IsInstanceOfType(created))
                {
                    return false;
                }

                property.SetValue(item, created);
                value = created;
            }

            if (value is IDictionary<string, string> genericIds)
            {
                foreach (var legacyKey in LegacyProviderKeys) genericIds.Remove(legacyKey);
                genericIds[StationKey] = track.StationName;
                genericIds[ArtistKey] = track.Artist;
                genericIds[SongKey] = track.SongTitle;
                return true;
            }

            if (value is IDictionary dictionaryIds)
            {
                foreach (var legacyKey in LegacyProviderKeys) dictionaryIds.Remove(legacyKey);
                dictionaryIds[StationKey] = track.StationName;
                dictionaryIds[ArtistKey] = track.Artist;
                dictionaryIds[SongKey] = track.SongTitle;
                return true;
            }
        }
        catch
        {
            // A failed ProviderIds handoff must not break playback or history.
        }

        return false;
    }






    private static bool HasRadioMetaProviderIds(object? item)
    {
        if (item is null)
        {
            return false;
        }

        try
        {
            var value = item.GetType().GetProperty("ProviderIds")?.GetValue(item);
            if (value is IDictionary<string, string> genericIds)
            {
                return genericIds.ContainsKey(StationKey) || genericIds.ContainsKey(SongKey) ||
                    genericIds.ContainsKey(ArtistKey);
            }

            if (value is IDictionary dictionaryIds)
            {
                return dictionaryIds.Contains(StationKey) || dictionaryIds.Contains(SongKey) ||
                    dictionaryIds.Contains(ArtistKey);
            }
        }
        catch
        {
            // Optional marker inspection must not affect playback.
        }

        return false;
    }

    private static void ClearRadioMetaProviderIds(object? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var property = item.GetType().GetProperty("ProviderIds");
            var value = property?.GetValue(item);
            if (value is IDictionary<string, string> genericIds)
            {
                genericIds.Remove(StationKey);
                genericIds.Remove(ArtistKey);
                genericIds.Remove(SongKey);
                genericIds.Remove(SourceKey);
                genericIds.Remove(UpdatedAtKey);
                foreach (var legacyKey in LegacyProviderKeys) genericIds.Remove(legacyKey);
            }
            else if (value is IDictionary dictionaryIds)
            {
                dictionaryIds.Remove(StationKey);
                dictionaryIds.Remove(ArtistKey);
                dictionaryIds.Remove(SongKey);
                dictionaryIds.Remove(SourceKey);
                dictionaryIds.Remove(UpdatedAtKey);
                foreach (var legacyKey in LegacyProviderKeys) dictionaryIds.Remove(legacyKey);
            }
        }
        catch
        {
            // ProviderIds clearing is best effort and must never interrupt playback.
        }
    }

}
