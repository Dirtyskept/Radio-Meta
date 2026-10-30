using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

public sealed class CurrentStreamMetadataTestResponse
{
    public bool Available { get; set; }
    public bool MetadataDetected { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string StreamTitle { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int ElapsedMilliseconds { get; set; }
}

// Retain the GET endpoint for compatibility with older dashboard scripts.
[Route("/RadioMeta/Stations/TestCurrentStreamMetadata", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetCurrentStreamMetadataTest : IReturn<CurrentStreamMetadataTestResponse>
{
    public string StationName { get; set; } = string.Empty;
}

// POST keeps the resolved stream URL in the request body rather than the URL/query string.
[Route("/RadioMeta/Stations/TestCurrentStreamMetadata", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class PostCurrentStreamMetadataTest : IReturn<CurrentStreamMetadataTestResponse>
{
    public string StationName { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
}

/// <summary>
/// Runs an explicit, read-only metadata test against the supplied station URL.
/// A supplied station name and URL do not require a matching in-memory capture;
/// legacy clients without a URL may use the active capture as a fallback. The
/// URL is accepted only after server-side public HTTP/HTTPS validation. FFprobe
/// is intentionally not launched by this interactive test.
/// </summary>
[Authenticated(Roles = "Admin")]
public sealed class CurrentStreamMetadataTestService : IService
{
    public Task<object> Get(GetCurrentStreamMetadataTest request) =>
        RunTest(request.StationName, string.Empty);

    public Task<object> Post(PostCurrentStreamMetadataTest request) =>
        RunTest(request.StationName, request.StreamUrl);

    private static async Task<object> RunTest(string? requestedStationName, string? requestedStreamUrl)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return Failure("Radio Meta is not initialized.");
        }

        var expectedName = (requestedStationName ?? string.Empty).Trim();
        var suppliedUrl = (requestedStreamUrl ?? string.Empty).Trim();
        var current = plugin.CurrentlyPlayingStationStore.GetMostRecent();
        var stationName = string.Empty;
        var streamUrl = string.Empty;

        // The Current Playback UI may resolve the active item through Emby's
        // playback-info API even when Radio Meta's own capture store has no
        // entry. When the caller supplies both a station name and its resolved
        // external stream URL, test that exact pair independently of the
        // capture store. This is the primary path for the manual UI test.
        if (suppliedUrl.Length > 0)
        {
            if (!IsSyntacticallyTestableUrl(suppliedUrl))
            {
                return Failure("Test not started: the resolved stream URL was not a valid external HTTP/HTTPS URL.", expectedName);
            }

            stationName = expectedName;
            if (stationName.Length == 0 && current is not null && !string.IsNullOrWhiteSpace(current.Name))
            {
                stationName = current.Name.Trim();
            }

            if (stationName.Length == 0)
            {
                return Failure("Test not started: a station name was not available for the supplied stream URL.");
            }

            streamUrl = suppliedUrl;
        }
        else
        {
            // Preserve the earlier fallback path for clients that do not send
            // a resolved URL: use the active capture and, if needed, the
            // Radio Browser station lookup.
            if (current is null || string.IsNullOrWhiteSpace(current.Name))
            {
                return Failure("No active radio station was available to test. Use Add to Radio Meta to supply the resolved stream URL.", expectedName);
            }

            stationName = current.Name.Trim();
            if (expectedName.Length > 0 &&
                !string.Equals(expectedName, stationName, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("The active station changed before the test started. Click Add to Radio Meta again.", stationName);
            }

            var capturedUrl = (current.StreamUrl ?? string.Empty).Trim();
            if (IsSyntacticallyTestableUrl(capturedUrl))
            {
                streamUrl = capturedUrl;
            }
            else
            {
                streamUrl = RadioCurrentlyPlayingStationService.ResolveStreamUrlForMetadataTest(
                    stationName, current.StationUuid, out _);
            }
        }

        if (!IsSyntacticallyTestableUrl(streamUrl))
        {
            return Failure("Test not started: no external stream URL was available. Try Add to Radio Meta again while the station is playing.", stationName);
        }

        // DNS/IP checks run on the server, not only in browser JavaScript. The
        // same validation is applied to each redirect in the manual test.
        if (!await RadioMetadataReader.IsSafeExternalTestUrlAsync(streamUrl).ConfigureAwait(false))
        {
            return Failure("Test not started: the resolved stream URL did not pass public URL safety checks.", stationName);
        }

        try
        {
            var result = await plugin.MetadataReader.TestStreamMetadataAsync(
                stationName, streamUrl, timeoutSeconds: 30).ConfigureAwait(false);
            var detected = !string.IsNullOrWhiteSpace(result.SongTitle);
            return new CurrentStreamMetadataTestResponse
            {
                Available = true,
                MetadataDetected = detected,
                StationName = stationName,
                Artist = result.Artist,
                SongTitle = result.SongTitle,
                StreamTitle = result.StreamTitle,
                Source = result.Source,
                Message = result.Error,
                ElapsedMilliseconds = (int)Math.Round(result.ElapsedMilliseconds)
            };
        }
        catch (Exception ex)
        {
            // Never return the URL or arbitrary exception text; stream URLs may
            // contain provider credentials in their query strings.
            return new CurrentStreamMetadataTestResponse
            {
                Available = true,
                MetadataDetected = false,
                StationName = stationName,
                Message = "The stream metadata test failed (" + ex.GetType().Name + ").",
                ElapsedMilliseconds = 0
            };
        }
    }

    private static bool IsSyntacticallyTestableUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !RadioMetaUriSchemeHelpers.IsHttpOrHttps(uri) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (path.StartsWith("/emby/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Audio/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Videos/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Video/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/LiveTv/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/PlaybackInfo/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Sessions/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Do not forward Emby/server authentication tokens to a stream host.
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Uri.UnescapeDataString(separator >= 0 ? part[..separator] : part);
            if (key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("x-emby-token", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static CurrentStreamMetadataTestResponse Failure(string message, string stationName = "") => new()
    {
        Available = false,
        MetadataDetected = false,
        StationName = stationName,
        Message = message
    };
}
