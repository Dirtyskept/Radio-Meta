using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
public sealed class GetCurrentlyPlayingRadioStation : IReturn<CurrentlyPlayingStationResponse>
{
}

/// <summary>
/// Read-only endpoint used by the Radio Meta page to pre-fill the existing
/// station form. This endpoint never creates or edits files.
/// </summary>
[Authenticated]
public sealed class RadioCurrentlyPlayingStationService : IService
{
    private static readonly HttpClient RadioBrowserClient = CreateHttpClient();

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
            var match = TryLookupRadioBrowserStation(name, current.StationUuid, streamUrl, out var lookupMessage);
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
        string stationName, string stationUuid, out string message)
    {
        var match = TryLookupRadioBrowserStation(
            (stationName ?? string.Empty).Trim(),
            (stationUuid ?? string.Empty).Trim(),
            string.Empty,
            out message);
        return match?.StreamUrl ?? string.Empty;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RadioMeta/1.3.41 station-prefill");
        return client;
    }

    private static RadioBrowserMatch? TryLookupRadioBrowserStation(
        string stationName,
        string stationUuid,
        string existingStreamUrl,
        out string message)
    {
        message = string.Empty;
        try
        {
            var endpoint = Guid.TryParse(stationUuid, out _)
                ? "https://de1.api.radio-browser.info/json/stations/byuuid/" + Uri.EscapeDataString(stationUuid)
                : "https://de1.api.radio-browser.info/json/stations/byname/" + Uri.EscapeDataString(stationName);
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = RadioBrowserClient.GetAsync(endpoint, timeout.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                message = "The optional Radio Browser catalogue lookup was unavailable.";
                return null;
            }

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (json.Length > 2 * 1024 * 1024)
            {
                message = "The Radio Browser response was unexpectedly large and was ignored.";
                return null;
            }

            using var document = JsonDocument.Parse(json);
            var records = new List<RadioBrowserMatch>();
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in root.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object)
                        records.Add(ParseRadioBrowserMatch(entry));
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                records.Add(ParseRadioBrowserMatch(root));
            }

            if (records.Count == 0) return null;

            List<RadioBrowserMatch> candidates;
            if (Guid.TryParse(stationUuid, out _))
            {
                candidates = records.Where(record =>
                    string.Equals(record.StationUuid, stationUuid, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 0) candidates = records;
            }
            else
            {
                candidates = records.Where(record =>
                    string.Equals(record.Name.Trim(), stationName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (candidates.Count == 0)
            {
                message = "No exact station-name match was found in the Radio Browser catalogue.";
                return null;
            }

            RadioBrowserMatch? selected = null;
            if (!string.IsNullOrWhiteSpace(existingStreamUrl))
            {
                selected = candidates.FirstOrDefault(record =>
                    UrlsEquivalent(existingStreamUrl, record.Url) || UrlsEquivalent(existingStreamUrl, record.ResolvedUrl));
                // If the name match is unique, its favicon may still be useful,
                // but never replace the stream URL supplied by the active item.
                if (selected is null && candidates.Count == 1) selected = candidates[0];
                if (selected is null && candidates.Count > 1)
                {
                    message = "The active stream URL did not match one unique exact-name Radio Browser entry, so no catalogue logo was selected.";
                    return null;
                }
            }
            else
            {
                var withUrls = candidates.Where(record => IsSafeStreamUrl(record.ResolvedUrl) || IsSafeStreamUrl(record.Url)).ToList();
                var uniqueUrls = withUrls
                    .Select(record => FirstSafeStreamUrl(record.ResolvedUrl, record.Url))
                    .Where(url => url.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (uniqueUrls.Count == 1)
                {
                    selected = withUrls.FirstOrDefault(record =>
                        string.Equals(FirstSafeStreamUrl(record.ResolvedUrl, record.Url), uniqueUrls[0], StringComparison.OrdinalIgnoreCase));
                }
                else if (uniqueUrls.Count > 1)
                {
                    message = "Several Radio Browser stations have the same name but different stream URLs. The URL was left blank to avoid selecting the wrong station.";
                    return null;
                }
                else if (candidates.Count == 1)
                {
                    selected = candidates[0];
                }
            }

            if (selected is null) return null;
            var stream = string.IsNullOrWhiteSpace(existingStreamUrl)
                ? FirstSafeStreamUrl(selected.ResolvedUrl, selected.Url)
                : existingStreamUrl;
            var artwork = IsSafeArtworkUrl(selected.Favicon) ? selected.Favicon : string.Empty;
            if (stream.Length == 0 && artwork.Length == 0) return null;
            return new RadioBrowserMatch
            {
                Name = selected.Name,
                StreamUrl = stream,
                ArtworkUrl = artwork,
                Url = selected.Url,
                ResolvedUrl = selected.ResolvedUrl,
                Favicon = selected.Favicon,
                StationUuid = selected.StationUuid
            };
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is UriFormatException || ex is InvalidOperationException)
        {
            message = "The optional Radio Browser catalogue lookup could not complete (" + ex.GetType().Name + ").";
            return null;
        }
    }

    private static RadioBrowserMatch ParseRadioBrowserMatch(JsonElement item)
    {
        return new RadioBrowserMatch
        {
            Name = ReadJsonString(item, "name"),
            Url = ReadJsonString(item, "url"),
            ResolvedUrl = ReadJsonString(item, "url_resolved"),
            Favicon = ReadJsonString(item, "favicon"),
            StationUuid = ReadJsonString(item, "stationuuid")
        };
    }

    private static string ReadJsonString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
    }

    private static string FirstSafeStreamUrl(string first, string second)
    {
        if (IsSafeStreamUrl(first)) return first.Trim();
        if (IsSafeStreamUrl(second)) return second.Trim();
        return string.Empty;
    }

    private static bool IsSafeStreamUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "icy") return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return uri.Query.IndexOf("api_key", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("access_token", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("emby_token", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("x-emby-token", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool IsSafeArtworkUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.Query.IndexOf("api_key", StringComparison.OrdinalIgnoreCase) >= 0 ||
            uri.Query.IndexOf("access_token", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        var ext = System.IO.Path.GetExtension(uri.AbsolutePath);
        return ext.Length == 0 || new[] { ".jpg", ".jpeg", ".png" }.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static bool UrlsEquivalent(string first, string second)
    {
        if (!Uri.TryCreate(first, UriKind.Absolute, out var a) || !Uri.TryCreate(second, UriKind.Absolute, out var b)) return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
               a.Port == b.Port &&
               string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.Query, b.Query, StringComparison.Ordinal);
    }

    private sealed class RadioBrowserMatch
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ResolvedUrl { get; set; } = string.Empty;
        public string Favicon { get; set; } = string.Empty;
        public string StationUuid { get; set; } = string.Empty;
        public string StreamUrl { get; set; } = string.Empty;
        public string ArtworkUrl { get; set; } = string.Empty;
    }
}
