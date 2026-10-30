using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

public sealed class CurrentlyPlayingStationResponse
{
    public bool Available { get; set; }
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public string ArtworkUrl { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

[Route("/RadioMeta/Stations/CurrentlyPlaying", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetCurrentlyPlayingRadioStation : IReturn<CurrentlyPlayingStationResponse>
{
}

/// <summary>
/// Read-only endpoint used by the Radio Meta page to pre-fill the existing
/// station form. This endpoint never creates or edits files.
/// </summary>
[Authenticated(Roles = "Admin")]
public sealed class RadioCurrentlyPlayingStationService : IService
{
    public object Get(GetCurrentlyPlayingRadioStation request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new CurrentlyPlayingStationResponse
            {
                Error = "Radio Meta is not initialized."
            };
        }

        var current = plugin.CurrentlyPlayingStationStore.GetMostRecent();
        if (current is null)
        {
            return new CurrentlyPlayingStationResponse
            {
                Error = "No current radio station was detected from Emby playback yet. Start the station in Emby, wait a few seconds, and try again. Some channel plugins do not expose their original stream URL to other plugins."
            };
        }

        var name = (current.Name ?? string.Empty).Trim();
        var streamUrl = (current.StreamUrl ?? string.Empty).Trim();
        var artworkUrl = (current.ArtworkUrl ?? string.Empty).Trim();
        var source = (current.Source ?? "Emby playback").Trim();
        var notes = new List<string>();

        // A station's artwork/URL is not always exposed by the active Emby item.
        // When needed, make a best-effort exact-name Radio Browser lookup. Never
        // replace a stream URL supplied by Emby with a different catalogue URL.
        if ((streamUrl.Length == 0 || artworkUrl.Length == 0) && name.Length > 0)
        {
            var match = RadioBrowserStationResolver.TryLookupStation(name, current.StationUuid, streamUrl, out var lookupMessage);
            if (match is not null)
            {
                if (streamUrl.Length == 0 && !string.IsNullOrWhiteSpace(match.StreamUrl))
                {
                    streamUrl = match.StreamUrl;
                    source += " + Radio Browser catalogue";
                }
                if (artworkUrl.Length == 0 && !string.IsNullOrWhiteSpace(match.ArtworkUrl))
                {
                    artworkUrl = match.ArtworkUrl;
                    if (!source.Contains("Radio Browser catalogue", StringComparison.OrdinalIgnoreCase))
                        source += " + Radio Browser artwork lookup";
                }
            }
            else if (!string.IsNullOrWhiteSpace(lookupMessage))
            {
                notes.Add(lookupMessage);
            }
        }

        if (name.Length == 0)
        {
            return new CurrentlyPlayingStationResponse
            {
                Error = "Emby reported playback, but did not expose a usable station name. No form fields were changed."
            };
        }

        if (streamUrl.Length == 0)
            notes.Add("Emby did not expose a usable public stream URL and no unambiguous Radio Browser match was found; enter the stream URL manually.");
        if (artworkUrl.Length == 0)
            notes.Add("A station logo URL was not available; you can enter one manually or leave it blank.");

        return new CurrentlyPlayingStationResponse
        {
            Available = true,
            Name = name,
            StreamUrl = streamUrl,
            ArtworkUrl = artworkUrl,
            ItemId = current.ItemId,
            Source = source,
            Message = notes.Count == 0
                ? "Station details were retrieved. Review or edit the fields before creating the .strm file."
                : string.Join(" ", notes) + " Review or edit the fields before creating the .strm file."
        };
    }

    internal static string ResolveStreamUrlForMetadataTest(
        string stationName, string stationUuid, out string message) =>
        RadioBrowserStationResolver.ResolveStreamUrlForMetadataTest(stationName, stationUuid, out message);

}
