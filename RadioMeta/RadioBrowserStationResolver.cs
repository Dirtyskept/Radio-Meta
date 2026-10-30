using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace RadioMeta;

/// <summary>
/// Owns optional Radio Browser catalogue lookup and the selection rules used
/// to fill missing station URL/artwork data. It never replaces a stream URL
/// supplied by the active Emby item.
/// </summary>
internal static class RadioBrowserStationResolver
{
    private static readonly HttpClient RadioBrowserClient = CreateHttpClient();

    internal static string ResolveStreamUrlForMetadataTest(
        string stationName, string stationUuid, out string message)
    {
        var match = TryLookupStation(
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

    internal static RadioBrowserMatch? TryLookupStation(
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
            using var response = RadioBrowserClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                message = "The optional Radio Browser catalogue lookup was unavailable.";
                return null;
            }

            string json;
            try { json = RadioMetaHttpContentReader.ReadStringBoundedAsync(response.Content, 2 * 1024 * 1024, timeout.Token).GetAwaiter().GetResult(); }
            catch (InvalidDataException)
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
        if (!RadioMetaUriSchemeHelpers.IsHttpHttpsOrIcy(uri)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return uri.Query.IndexOf("api_key", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("access_token", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("emby_token", StringComparison.OrdinalIgnoreCase) < 0 &&
               uri.Query.IndexOf("x-emby-token", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool IsSafeArtworkUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!RadioMetaUriSchemeHelpers.IsHttpOrHttps(uri)) return false;
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

    internal sealed class RadioBrowserMatch
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
