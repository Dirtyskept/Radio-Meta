using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RadioMeta;

/// <summary>
/// Metadata returned for one radio station.
/// </summary>
public sealed class RadioStationMetadata
{
    public string StationName { get; init; } = string.Empty;
    public string ArtworkPath { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string SongTitle { get; init; } = string.Empty;
    public string StreamTitle { get; init; } = string.Empty;
    public string StreamUrl { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;

    public string StationLine => LimitText(StationName, 26);
    public string ArtistLine => LimitText(Artist, 26);
    public string SongTitleLine => LimitText(SongTitle, 26);

    private static string LimitText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();

        if (value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 3)
        {
            return value[..maxLength];
        }

        return value[..(maxLength - 3)] + "...";
    }
}


/// <summary>Result of an explicit, one-shot stream metadata test from the dashboard.</summary>
public sealed class RadioStreamMetadataTestResult
{
    public string StationName { get; init; } = string.Empty;
    public string StreamTitle { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string SongTitle { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public double ElapsedMilliseconds { get; init; }
}

/// <summary>
/// Reads radio stream metadata using ffprobe.
/// Results are cached per station file to reduce repeated probes.
/// </summary>
public sealed class RadioMetadataReader
{
    public const int DirectIcyMaxIntervals = 40;

    private readonly RadioMetaDiagnosticEventStore? _diagnostics;
    private readonly RadioMetaPerformanceMetricsStore? _performance;

    public RadioMetadataReader(
        RadioMetaDiagnosticEventStore? diagnostics = null,
        RadioMetaPerformanceMetricsStore? performance = null)
    {
        _diagnostics = diagnostics;
        _performance = performance;
    }

    private sealed class IcyReadMetrics
    {
        public int IntervalsAttempted { get; set; }
    }

    private static readonly TimeSpan CacheDuration =
        TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(12);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, NetworkCacheEntry> _networkCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });

    // The user-triggered Current Playback test follows redirects manually so
    // every redirect target can be checked before a connection is opened. The
    // normal playback reader keeps its existing HttpClient behavior.
    private static readonly HttpClient TestHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });

    private static readonly TimeSpan NetworkCacheDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Clears both metadata caches after ignore rules are changed.</summary>
    /// <summary>
    /// Builds the station-only card data without opening the stream or starting
    /// FFprobe. Artwork lookup is local-file-only.
    /// </summary>
    public RadioStationMetadata ReadStationInfoOnly(string stationFilePath)
    {
        var stationName = Path.GetFileNameWithoutExtension(stationFilePath);
        var folder = Path.GetDirectoryName(stationFilePath) ?? string.Empty;
        return new RadioStationMetadata
        {
            StationName = stationName,
            ArtworkPath = FindArtwork(folder, stationName),
            Artist = string.Empty,
            SongTitle = string.Empty,
            StreamTitle = string.Empty,
            StreamUrl = string.Empty
        };
    }

    public void ClearCaches()
    {
        _cache.Clear();
        _networkCache.Clear();
    }

    public RadioStationMetadata Read(
        string stationFilePath,
        string ffprobePath,
        bool forceRefresh = false,
        Func<string, string, bool>? ignoreMetadataPhrase = null)
    {
        if (string.IsNullOrWhiteSpace(stationFilePath))
        {
            return Failure("Station file path is empty.");
        }

        if (string.IsNullOrWhiteSpace(ffprobePath) ||
            !File.Exists(ffprobePath))
        {
            _diagnostics?.Record(Path.GetFileNameWithoutExtension(stationFilePath), "FFprobe", "error", string.Empty, string.Empty, "FFprobe executable was not found.");
            return Failure("ffprobe executable was not found.");
        }

        string fullStationPath;

        try
        {
            fullStationPath = Path.GetFullPath(stationFilePath);
        }
        catch (Exception ex)
        {
            return Failure("Invalid station file path (" + ex.GetType().Name + ").");
        }

        if (!File.Exists(fullStationPath))
        {
            return Failure("Station file was not found.");
        }

        var stationName = Path.GetFileNameWithoutExtension(fullStationPath);
        if (!forceRefresh &&
            _cache.TryGetValue(fullStationPath, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < CacheDuration &&
            !IsIgnoredMetadata(cached.Metadata, stationName, ignoreMetadataPhrase))
        {
            return cached.Metadata;
        }

        var metadata = ReadUncached(
            fullStationPath,
            ffprobePath,
            ignoreMetadataPhrase);
        if (!string.IsNullOrWhiteSpace(metadata.Error))
        {
            _diagnostics?.Record(stationName, "FFprobe", "error", metadata.SongTitle, metadata.Artist, metadata.Error, metadata.StreamTitle);
        }
        else if (!string.IsNullOrWhiteSpace(metadata.SongTitle))
        {
            _diagnostics?.Record(stationName, "FFprobe", "parsed", metadata.SongTitle, metadata.Artist, "FFprobe returned a usable title for the current-track validator.", metadata.StreamTitle);
        }

        // Keep the previous successful result if a refresh fails.
        if (!string.IsNullOrEmpty(metadata.Error) &&
            _cache.TryGetValue(fullStationPath, out var previous) &&
            string.IsNullOrEmpty(previous.Metadata.Error) &&
            !IsIgnoredMetadata(previous.Metadata, stationName, ignoreMetadataPhrase))
        {
            return previous.Metadata;
        }

        _cache[fullStationPath] = new CacheEntry(
            DateTimeOffset.UtcNow, metadata);

        return metadata;
    }

    /// <summary>
    /// Validates the explicit Current Playback test URL on the server. Only
    /// public HTTP/HTTPS destinations are allowed. This is intentionally scoped
    /// to the user-triggered test; normal station playback handling is unchanged.
    /// </summary>
    internal static async Task<bool> IsSafeExternalTestUrlAsync(
        string value, CancellationToken cancellationToken = default)
    {
        if (!TryGetSafeTestUri(value, out var uri)) return false;

        var host = uri.DnsSafeHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("home.arpa", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var literalAddress))
            return IsPubliclyRoutableAddress(literalAddress);

        try
        {
            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var addresses = await Dns.GetHostAddressesAsync(host, dnsTimeout.Token).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(IsPubliclyRoutableAddress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool TryGetSafeTestUri(string value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        var path = parsed.AbsolutePath;
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

        // Never forward Emby/server authentication tokens to a stream host.
        foreach (var part in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            string key;
            try { key = Uri.UnescapeDataString(separator >= 0 ? part[..separator] : part); }
            catch { return false; }
            if (key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("x-emby-token", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        uri = parsed;
        return true;
    }

    private static bool IsPubliclyRoutableAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6Teredo)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            if (a == 0 || a == 10 || a == 127 || a >= 224 ||
                (a == 100 && b >= 64 && b <= 127) ||
                (a == 169 && b == 254) ||
                (a == 172 && b >= 16 && b <= 31) ||
                (a == 192 && b == 168) ||
                (a == 192 && b == 0 && (c == 0 || c == 2)) ||
                (a == 198 && (b == 18 || b == 19)) ||
                (a == 198 && b == 51 && c == 100) ||
                (a == 203 && b == 0 && c == 113))
            {
                return false;
            }
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Unique-local (fc00::/7), link-local (fe80::/10), multicast
            // (ff00::/8), and documentation (2001:db8::/32) are not public.
            if ((bytes[0] & 0xFE) == 0xFC ||
                (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) ||
                (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0) ||
                bytes[0] == 0xFF ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8))
            {
                return false;
            }
            return true;
        }

        return false;
    }

    private static async Task<HttpResponseMessage> SendMetadataRequestAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken,
        bool validateRedirects)
    {
        if (!validateRedirects)
            return await Http.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);

        if (request.RequestUri is null)
            throw new InvalidOperationException("The metadata test request had no URL.");

        var currentUri = request.RequestUri;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var redirectedRequest = new HttpRequestMessage(request.Method, currentUri);
            foreach (var header in request.Headers)
                redirectedRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

            var response = await TestHttp.SendAsync(redirectedRequest, completionOption, cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            var isRedirect = statusCode is 301 or 302 or 303 or 307 or 308;
            if (!isRedirect || response.Headers.Location is null)
                return response;

            var location = response.Headers.Location;
            Uri target;
            try
            {
                target = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            }
            catch
            {
                response.Dispose();
                throw new InvalidOperationException("The metadata test encountered an invalid redirect target.");
            }

            response.Dispose();
            if (redirect == 5)
                throw new InvalidOperationException("The metadata test exceeded the redirect limit.");
            if (!await IsSafeExternalTestUrlAsync(target.AbsoluteUri, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("A stream redirect target did not pass public URL safety checks.");
            currentUri = target;
        }

        throw new InvalidOperationException("The metadata test exceeded the redirect limit.");
    }

    /// <summary>
    /// Runs an uncached, read-only metadata test against a stream URL for the
    /// Add to Radio Meta panel. It checks the Shoutcast endpoints and then direct
    /// ICY metadata, returns as soon as a usable title is found, does not launch
    /// FFprobe or apply Ignore Metadata Phrases, and has a 30-second maximum.
    /// The manual test validates redirect destinations separately from the
    /// normal playback reader.
    /// </summary>
    public async Task<RadioStreamMetadataTestResult> TestStreamMetadataAsync(
        string stationName,
        string streamUrl,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        stationName = (stationName ?? string.Empty).Trim();
        streamUrl = (streamUrl ?? string.Empty).Trim();
        var timer = Stopwatch.StartNew();

        if (stationName.Length == 0 ||
            !Uri.TryCreate(streamUrl, UriKind.Absolute, out var streamUri) ||
            (streamUri.Scheme != Uri.UriSchemeHttp && streamUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(streamUri.UserInfo) ||
            streamUri.AbsolutePath.Contains("/emby/Audio/", StringComparison.OrdinalIgnoreCase))
        {
            timer.Stop();
            return new RadioStreamMetadataTestResult
            {
                StationName = stationName,
                Error = "A testable HTTP or HTTPS station stream URL was not available.",
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 30)));
        string title = string.Empty;
        string source = string.Empty;
        string error = string.Empty;
        var icyMetrics = new IcyReadMetrics();

        try
        {
            // Try the lightweight station endpoints first, matching the normal
            // metadata pipeline. They commonly fail quickly with HTTP 404 for
            // non-Shoutcast stations, allowing the direct ICY attempt to run.
            try
            {
                title = await ReadShoutcastTitleAsync(
                    streamUrl, stationName, null, timeout.Token, _diagnostics, validateRedirects: true).ConfigureAwait(false);
                if (IsUsableStreamTitle(title)) source = "Shoutcast";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = "The metadata test reached its timeout while checking station endpoints.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _diagnostics?.Record(stationName, "Current Playback stream test", "error", string.Empty, string.Empty,
                    "Shoutcast test request failed (" + ex.GetType().Name + "); trying direct ICY if time remains.");
            }

            if (!IsUsableStreamTitle(title) && !timeout.IsCancellationRequested)
            {
                try
                {
                    title = await ReadIcyTitleAsync(
                        streamUrl, stationName, null, timeout.Token, _diagnostics, icyMetrics, validateRedirects: true).ConfigureAwait(false);
                    if (IsUsableStreamTitle(title)) source = "ICY";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    error = "The metadata test timed out before a usable title was received.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = "The ICY metadata test failed (" + ex.GetType().Name + ").";
                    _diagnostics?.Record(stationName, "Current Playback stream test", "error", string.Empty, string.Empty, error);
                }
            }
            else if (!IsUsableStreamTitle(title) && timeout.IsCancellationRequested && error.Length == 0)
            {
                error = "The metadata test timed out before a usable title was received.";
            }
        }
        finally
        {
            timer.Stop();
        }

        if (IsUsableStreamTitle(title))
        {
            var (artist, songTitle) = SplitStreamTitle(title);
            _diagnostics?.Record(stationName, "Current Playback stream test", "detected", songTitle, artist,
                "One-shot stream metadata test detected a title via " + (source.Length == 0 ? "the stream" : source) +
                " in " + Math.Round(timer.Elapsed.TotalMilliseconds) + " ms. Ignore Metadata Phrases are not applied to this test.", title);
            return new RadioStreamMetadataTestResult
            {
                StationName = stationName,
                StreamTitle = title,
                Artist = artist,
                SongTitle = songTitle,
                Source = source,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
            };
        }

        if (error.Length == 0)
            error = "No usable song title was returned by the Shoutcast endpoints or direct ICY metadata.";

        _diagnostics?.Record(stationName, "Current Playback stream test",
            error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ? "error" : "empty",
            string.Empty, string.Empty, error + " Elapsed " + Math.Round(timer.Elapsed.TotalMilliseconds) + " ms.");
        return new RadioStreamMetadataTestResult
        {
            StationName = stationName,
            Source = source,
            Error = error,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
        };
    }

    public async Task<RadioStationMetadata> ReadNetworkMetadataAsync(
        string stationFilePath,
        int timeoutSeconds = 5,
        bool forceRefresh = false,
        bool allowDirectIcy = true,
        CancellationToken cancellationToken = default,
        Func<string, string, bool>? ignoreMetadataPhrase = null)
    {
        if (string.IsNullOrWhiteSpace(stationFilePath) || !File.Exists(stationFilePath))
        {
            return Failure("Station file was not found.");
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(stationFilePath); }
        catch (Exception ex) { return Failure("Invalid station file path (" + ex.GetType().Name + ")."); }

        var stationName = Path.GetFileNameWithoutExtension(fullPath);
        var lookupTimer = Stopwatch.StartNew();
        if (!forceRefresh && _networkCache.TryGetValue(fullPath, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < NetworkCacheDuration &&
            !IsIgnoredMetadata(cached.Metadata, stationName, ignoreMetadataPhrase))
        {
            lookupTimer.Stop();
            _diagnostics?.Record(stationName, "Metadata cache", "cached", cached.Metadata.SongTitle, cached.Metadata.Artist, "Using the recent network metadata result.", cached.Metadata.StreamTitle);
            _performance?.RecordNetworkLookup(stationName, lookupTimer.Elapsed.TotalMilliseconds, 0, null, 0, false, true,
                string.IsNullOrWhiteSpace(cached.Metadata.Error) ? "cache-hit" : "cached-empty", "network cache");
            return cached.Metadata;
        }

        _diagnostics?.Record(stationName, "Metadata reader", "info", string.Empty, string.Empty,
            "Started metadata lookup. HTTP/ICY timeout is capped at " + Math.Clamp(timeoutSeconds, 1, 15) + " seconds; direct ICY reads up to " + DirectIcyMaxIntervals + " metadata intervals.");

        bool IsIgnored(string? candidate) =>
            !string.IsNullOrWhiteSpace(candidate) &&
            ignoreMetadataPhrase?.Invoke(stationName, candidate) == true;

        var folder = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var artwork = FindArtwork(folder, stationName);
        var streamUrl = ReadStreamUrl(fullPath);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            lookupTimer.Stop();
            _diagnostics?.Record(stationName, "Metadata reader", "error", string.Empty, string.Empty,
                "The station file did not contain a valid HTTP or HTTPS stream URL.");
            _performance?.RecordNetworkLookup(stationName, lookupTimer.Elapsed.TotalMilliseconds, 0, null, 0, false, false,
                "missing-stream-url", "none");
            return FailureForStation(stationName, artwork, string.Empty,
                "The station file contains no stream URL.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 15)));

        string title = string.Empty;
        string sourceError = string.Empty;
        var shoutcastTimer = Stopwatch.StartNew();
        try
        {
            title = await ReadShoutcastTitleAsync(
                streamUrl,
                stationName,
                ignoreMetadataPhrase,
                timeout.Token,
                _diagnostics).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sourceError = "Shoutcast metadata request timed out.";
            _diagnostics?.Record(stationName, "Shoutcast", "error", string.Empty, string.Empty, sourceError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sourceError = "Station HTTP metadata request failed (" + ex.GetType().Name + ").";
            _diagnostics?.Record(stationName, "Shoutcast", "error", string.Empty, string.Empty, sourceError);
        }
        finally
        {
            shoutcastTimer.Stop();
        }

        // Status text or a tracklist link is not song metadata. Reject it before
        // deciding whether the ICY or FFprobe fallback is needed.
        if (!IsUsableStreamTitle(title) || IsIgnored(title))
        {
            title = string.Empty;
        }

        var resultSource = string.IsNullOrWhiteSpace(title) ? "none" : "Shoutcast";
        double? icyDurationMs = null;
        var icyIntervalsAttempted = 0;
        var icyAttempted = false;
        if (string.IsNullOrWhiteSpace(title) && allowDirectIcy)
        {
            icyAttempted = true;
            var icyMetrics = new IcyReadMetrics();
            var icyTimer = Stopwatch.StartNew();
            try
            {
                // Use a fresh short timeout for the ICY fallback.
                using var icyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                icyTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 15)));
                title = await ReadIcyTitleAsync(
                    streamUrl,
                    stationName,
                    ignoreMetadataPhrase,
                    icyTimeout.Token,
                    _diagnostics,
                    icyMetrics).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                sourceError = string.IsNullOrWhiteSpace(sourceError)
                    ? "ICY metadata request timed out."
                    : sourceError + "; ICY metadata request timed out.";
                _diagnostics?.Record(stationName, "ICY", "error", string.Empty, string.Empty, "ICY metadata request timed out before a usable title was read.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var safeError = "ICY metadata request failed (" + ex.GetType().Name + ").";
                sourceError = string.IsNullOrWhiteSpace(sourceError) ? safeError : sourceError + " " + safeError;
                _diagnostics?.Record(stationName, "ICY", "error", string.Empty, string.Empty, safeError);
            }
            finally
            {
                icyTimer.Stop();
                icyDurationMs = icyTimer.Elapsed.TotalMilliseconds;
                icyIntervalsAttempted = icyMetrics.IntervalsAttempted;
            }
            if (!string.IsNullOrWhiteSpace(title)) resultSource = "ICY";
        }

        if (!IsUsableStreamTitle(title) || IsIgnored(title))
        {
            title = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            var failed = FailureForStation(stationName, artwork, streamUrl,
                string.IsNullOrWhiteSpace(sourceError)
                    ? "No song title was available from the Shoutcast API or ICY metadata."
                    : "No station metadata was available: " + sourceError);
            _diagnostics?.Record(stationName, "Metadata reader", string.IsNullOrWhiteSpace(sourceError) ? "empty" : "error", string.Empty, string.Empty,
                string.IsNullOrWhiteSpace(sourceError)
                    ? "No usable title returned by Shoutcast endpoints or the first " + DirectIcyMaxIntervals + " direct ICY intervals."
                    : "No usable station metadata was returned. " + sourceError);
            _networkCache[fullPath] = new NetworkCacheEntry(DateTimeOffset.UtcNow, failed);
            lookupTimer.Stop();
            _performance?.RecordNetworkLookup(stationName, lookupTimer.Elapsed.TotalMilliseconds,
                shoutcastTimer.Elapsed.TotalMilliseconds, icyDurationMs, icyIntervalsAttempted, icyAttempted,
                false, string.IsNullOrWhiteSpace(sourceError) ? "no-title" : "reader-error", "none");
            return failed;
        }

        var (artist, songTitle) = SplitStreamTitle(title);
        _diagnostics?.Record(stationName, "Station HTTP/ICY", "parsed", songTitle, artist, "Metadata reader parsed a title for the current-track validator.", title);
        var result = new RadioStationMetadata
        {
            StationName = stationName,
            ArtworkPath = artwork,
            StreamUrl = streamUrl,
            StreamTitle = title,
            Artist = artist,
            SongTitle = songTitle
        };
        _networkCache[fullPath] = new NetworkCacheEntry(DateTimeOffset.UtcNow, result);
        lookupTimer.Stop();
        _performance?.RecordNetworkLookup(stationName, lookupTimer.Elapsed.TotalMilliseconds,
            shoutcastTimer.Elapsed.TotalMilliseconds, icyDurationMs, icyIntervalsAttempted, icyAttempted,
            false, "success", resultSource);
        return result;
    }

    private async Task<string> ReadShoutcastTitleAsync(
        string streamUrl,
        string stationName,
        Func<string, string, bool>? ignoreMetadataPhrase,
        CancellationToken cancellationToken,
        RadioMetaDiagnosticEventStore? diagnostics,
        bool validateRedirects = false)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var streamUri))
            return string.Empty;

        var baseUrl = streamUri.GetLeftPart(UriPartial.Authority);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            baseUrl + "/stats?sid=1&json=1");
        request.Headers.UserAgent.ParseAdd("RadioMeta/1.0");

        try
        {
            using var response = await SendMetadataRequestAsync(request,
                HttpCompletionOption.ResponseContentRead, cancellationToken, validateRedirects).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                diagnostics?.Record(stationName, "Shoutcast /stats", "info", string.Empty, string.Empty, "HTTP response succeeded; checking songtitle field.");
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("songtitle", out var song))
                    {
                        var title = song.GetString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            diagnostics?.Record(stationName, "Shoutcast /stats", "detected", title, string.Empty, "Non-empty songtitle value observed.", title);
                            if (ignoreMetadataPhrase?.Invoke(stationName, title) == true)
                            {
                                diagnostics?.Record(stationName, "Ignore Metadata Phrases", "filtered", title, string.Empty, "Title matched a configured ignore phrase.", title);
                            }
                            else if (IsUsableStreamTitle(title))
                            {
                                return title;
                            }
                            else
                            {
                                diagnostics?.Record(stationName, "Shoutcast /stats", "rejected", title, string.Empty, "Value did not pass the song-title usability check.", title);
                            }
                        }
                        else
                        {
                            diagnostics?.Record(stationName, "Shoutcast /stats", "empty", string.Empty, string.Empty, "The songtitle field was empty.");
                        }
                    }
                    else
                    {
                        diagnostics?.Record(stationName, "Shoutcast /stats", "empty", string.Empty, string.Empty, "JSON response did not contain a songtitle field.");
                    }
                }
                catch (JsonException)
                {
                    diagnostics?.Record(stationName, "Shoutcast /stats", "error", string.Empty, string.Empty, "Endpoint response was not valid JSON.");
                }
            }
            else
            {
                diagnostics?.Record(stationName, "Shoutcast /stats", "error", string.Empty, string.Empty, "HTTP request returned status " + (int)response.StatusCode + ".");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            diagnostics?.Record(stationName, "Shoutcast /stats", "error", string.Empty, string.Empty, "Request failed (" + ex.GetType().Name + "); trying the legacy endpoint.");
        }

        using var legacyRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/7.html");
        legacyRequest.Headers.UserAgent.ParseAdd("RadioMeta/1.0");
        using var legacyResponse = await SendMetadataRequestAsync(legacyRequest,
            HttpCompletionOption.ResponseContentRead, cancellationToken, validateRedirects).ConfigureAwait(false);
        if (!legacyResponse.IsSuccessStatusCode)
        {
            diagnostics?.Record(stationName, "Shoutcast /7.html", "error", string.Empty, string.Empty, "HTTP request returned status " + (int)legacyResponse.StatusCode + ".");
            return string.Empty;
        }

        var html = await legacyResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var match = System.Text.RegularExpressions.Regex.Match(html,
            @"<body[^>]*>(.*?)</body>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        var body = WebUtility.HtmlDecode(match.Success ? match.Groups[1].Value : html).Trim();
        var fields = body.Split(',');
        var legacyTitle = fields.Length >= 7 ? fields[6].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(legacyTitle))
        {
            diagnostics?.Record(stationName, "Shoutcast /7.html", "empty", string.Empty, string.Empty, "No song title was present in the legacy response.");
            return string.Empty;
        }
        diagnostics?.Record(stationName, "Shoutcast /7.html", "detected", legacyTitle, string.Empty, "Non-empty legacy title value observed.", legacyTitle);
        if (ignoreMetadataPhrase?.Invoke(stationName, legacyTitle) == true)
        {
            diagnostics?.Record(stationName, "Ignore Metadata Phrases", "filtered", legacyTitle, string.Empty, "Title matched a configured ignore phrase.", legacyTitle);
            return string.Empty;
        }
        if (!IsUsableStreamTitle(legacyTitle))
        {
            diagnostics?.Record(stationName, "Shoutcast /7.html", "rejected", legacyTitle, string.Empty, "Value did not pass the song-title usability check.", legacyTitle);
            return string.Empty;
        }
        return legacyTitle;
    }

    private async Task<string> ReadIcyTitleAsync(
        string streamUrl,
        string stationName,
        Func<string, string, bool>? ignoreMetadataPhrase,
        CancellationToken cancellationToken,
        RadioMetaDiagnosticEventStore? diagnostics,
        IcyReadMetrics? metrics = null,
        bool validateRedirects = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
        request.Headers.UserAgent.ParseAdd("RadioMeta/1.0");
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        using var response = await SendMetadataRequestAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken, validateRedirects).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            diagnostics?.Record(stationName, "ICY", "error", string.Empty, string.Empty, "Stream request returned HTTP status " + (int)response.StatusCode + ".");
            return string.Empty;
        }

        var metaint = GetHeaderInt(response, "icy-metaint");
        if (metaint <= 0)
        {
            diagnostics?.Record(stationName, "ICY", "error", string.Empty, string.Empty, "Server did not advertise a positive icy-metaint header.");
            return string.Empty;
        }
        diagnostics?.Record(stationName, "ICY", "info", string.Empty, string.Empty, "HTTP response succeeded; icy-metaint=" + metaint + ". Reader will examine up to " + DirectIcyMaxIntervals + " intervals.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var audioBuffer = new byte[8192];
        // The first metadata blocks can be empty. Read up to DirectIcyMaxIntervals intervals,
        // return immediately when a usable title is found, and retain the existing timeout.
        // Always close this short-lived connection rather than maintaining another audio stream.
        for (var attempt = 0; attempt < DirectIcyMaxIntervals; attempt++)
        {
            if (metrics is not null) metrics.IntervalsAttempted = attempt + 1;
            var remaining = metaint;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(audioBuffer.AsMemory(0, Math.Min(audioBuffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    diagnostics?.Record(stationName, "ICY", "empty", string.Empty, string.Empty, "Stream ended before audio interval " + (attempt + 1) + " was complete.");
                    return string.Empty;
                }
                remaining -= read;
            }

            var lengthByte = new byte[1];
            var lengthRead = await stream.ReadAsync(lengthByte.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (lengthRead == 0)
            {
                diagnostics?.Record(stationName, "ICY", "empty", string.Empty, string.Empty, "Stream ended before the metadata length byte for interval " + (attempt + 1) + ".");
                return string.Empty;
            }
            var metadataLength = lengthByte[0] * 16;
            if (metadataLength == 0)
            {
                diagnostics?.Record(stationName, "ICY", "empty", string.Empty, string.Empty, "ICY interval " + (attempt + 1) + " of " + DirectIcyMaxIntervals + " had zero metadata bytes.");
                continue;
            }

            var metadata = new byte[metadataLength];
            var offset = 0;
            while (offset < metadata.Length)
            {
                var read = await stream.ReadAsync(metadata.AsMemory(offset, metadata.Length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    diagnostics?.Record(stationName, "ICY", "empty", string.Empty, string.Empty, "Metadata interval " + (attempt + 1) + " ended before its declared length was read.");
                    return string.Empty;
                }
                offset += read;
            }

            var block = Encoding.Latin1.GetString(metadata).Trim('\0', ' ', '\r', '\n');
            var marker = block.IndexOf("StreamTitle=", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                diagnostics?.Record(stationName, "ICY", "empty", string.Empty, string.Empty, "ICY interval " + (attempt + 1) + " contained bytes but no StreamTitle field.");
                continue;
            }
            var value = block[(marker + "StreamTitle=".Length)..].Trim();
            if (value.Length > 0 && (value[0] == '\'' || value[0] == '"'))
            {
                var quote = value[0];
                var end = value.IndexOf(quote, 1);
                if (end > 0) value = value[1..end];
            }
            else
            {
                var end = value.IndexOf(';');
                if (end >= 0) value = value[..end];
            }

            value = WebUtility.HtmlDecode(value.Trim());
            if (string.IsNullOrWhiteSpace(value))
            {
                diagnostics?.Record(stationName, "ICY", "empty", string.Empty, string.Empty, "StreamTitle was present but empty in interval " + (attempt + 1) + ".");
                continue;
            }
            diagnostics?.Record(stationName, "ICY", "detected", value, string.Empty, "Non-empty StreamTitle observed in interval " + (attempt + 1) + ".", value);
            if (ignoreMetadataPhrase?.Invoke(stationName, value) == true)
            {
                diagnostics?.Record(stationName, "Ignore Metadata Phrases", "filtered", value, string.Empty, "Title matched a configured ignore phrase.", value);
                continue;
            }
            if (!IsUsableStreamTitle(value))
            {
                diagnostics?.Record(stationName, "ICY", "rejected", value, string.Empty, "Value did not pass the song-title usability check.", value);
                continue;
            }
            diagnostics?.Record(stationName, "ICY", "parsed", value, string.Empty, "Direct ICY reader extracted StreamTitle from interval " + (attempt + 1) + ".", value);
            return value;
        }
        diagnostics?.Record(stationName, "ICY", "empty", string.Empty, string.Empty, "No usable StreamTitle found in the first " + DirectIcyMaxIntervals + " metadata intervals.");
        return string.Empty;
    }

    private static int GetHeaderInt(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) ||
            response.Content.Headers.TryGetValues(name, out values))
        {
            var first = values.FirstOrDefault();
            if (int.TryParse(first, out var value)) return value;
        }
        return 0;
    }

    private static bool IsIgnoredMetadata(
        RadioStationMetadata metadata,
        string stationName,
        Func<string, string, bool>? ignoreMetadataPhrase)
    {
        if (ignoreMetadataPhrase is null)
        {
            return false;
        }

        return ignoreMetadataPhrase(stationName, metadata.StreamTitle) ||
               ignoreMetadataPhrase(stationName, metadata.SongTitle) ||
               ignoreMetadataPhrase(stationName, metadata.Artist + " - " + metadata.SongTitle);
    }

    private RadioStationMetadata ReadUncached(
        string stationFilePath,
        string ffprobePath,
        Func<string, string, bool>? ignoreMetadataPhrase)
    {
        var stationName =
            Path.GetFileNameWithoutExtension(stationFilePath);

        var folder = Path.GetDirectoryName(stationFilePath)
            ?? string.Empty;

        var artwork = FindArtwork(folder, stationName);
        var streamUrl = ReadStreamUrl(stationFilePath);

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return new RadioStationMetadata
            {
                StationName = stationName,
                ArtworkPath = artwork,
                Error = "The station file contains no stream URL."
            };
        }

        var probeTimer = Stopwatch.StartNew();
        var processStarted = false;
        var probeOutcome = "exception";
        try
        {
            _diagnostics?.Record(stationName, "FFprobe", "info", string.Empty, string.Empty, "Starting FFprobe stream metadata probe.");
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-rw_timeout");
            startInfo.ArgumentList.Add("10000000");
            startInfo.ArgumentList.Add("-probesize");
            startInfo.ArgumentList.Add("1000000");
            startInfo.ArgumentList.Add("-analyzeduration");
            startInfo.ArgumentList.Add("3000000");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format=format_name:format_tags");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(streamUrl);

            using var process = new Process
            {
                StartInfo = startInfo
            };

            if (!process.Start())
            {
                probeOutcome = "start-failed";
                _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe process could not be started.");
                return FailureForStation(
                    stationName, artwork, streamUrl,
                    "Could not start ffprobe.");
            }

            processStarted = true;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(
                (int)ProbeTimeout.TotalMilliseconds))
            {
                probeOutcome = "timeout";
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may already have exited.
                }

                _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe timed out before returning metadata.");
                return FailureForStation(
                    stationName, artwork, streamUrl,
                    "ffprobe timed out.");
            }

            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult(); // drain stderr, but do not expose URLs/paths from it

            if (process.ExitCode != 0)
            {
                probeOutcome = "exit-code-" + process.ExitCode;
                _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe returned exit code " + process.ExitCode + ".");
                return FailureForStation(
                    stationName, artwork, streamUrl,
                    "ffprobe could not read stream metadata (exit code " + process.ExitCode + ").");
            }

            using var document = JsonDocument.Parse(output);

            var streamTitle = FindStreamTitle(document.RootElement);
            if (!string.IsNullOrWhiteSpace(streamTitle))
            {
                _diagnostics?.Record(stationName, "FFprobe", "detected", streamTitle, string.Empty, "A title-like metadata value was found in FFprobe output.", streamTitle);
            }
            if (ignoreMetadataPhrase?.Invoke(stationName, streamTitle) == true)
            {
                probeOutcome = "filtered";
                _diagnostics?.Record(stationName, "Ignore Metadata Phrases", "filtered", streamTitle, string.Empty, "FFprobe title matched a configured ignore phrase.", streamTitle);
                return FailureForStation(stationName, artwork, string.Empty, "ffprobe metadata was blocked by an ignore phrase.");
            }
            if (!IsUsableStreamTitle(streamTitle))
            {
                probeOutcome = "no-usable-title";
                _diagnostics?.Record(stationName, "FFprobe", "rejected", streamTitle, string.Empty, "FFprobe returned no usable song title.", streamTitle);
                return FailureForStation(
                    stationName, artwork, string.Empty,
                    "ffprobe returned no usable song metadata.");
            }

            var (artist, songTitle) = SplitStreamTitle(streamTitle);
            if (string.IsNullOrWhiteSpace(songTitle))
            {
                probeOutcome = "split-rejected";
                _diagnostics?.Record(stationName, "FFprobe", "rejected", streamTitle, artist, "The stream title could not be split into a usable song title.", streamTitle);
                return FailureForStation(
                    stationName, artwork, string.Empty,
                    "ffprobe returned no usable song title.");
            }

            probeOutcome = "success";
            return new RadioStationMetadata
            {
                StationName = stationName,
                ArtworkPath = artwork,
                StreamUrl = streamUrl,
                StreamTitle = streamTitle,
                Artist = artist,
                SongTitle = songTitle
            };
        }
        catch (JsonException)
        {
            probeOutcome = "invalid-json";
            _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe output was not valid JSON.");
            return FailureForStation(
                stationName, artwork, streamUrl,
                "ffprobe returned invalid JSON.");
        }
        catch (Exception ex)
        {
            probeOutcome = "exception-" + ex.GetType().Name;
            _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "Unable to read stream metadata (" + ex.GetType().Name + ").");
            return FailureForStation(
                stationName, artwork, string.Empty,
                "Unable to read stream metadata (" + ex.GetType().Name + ").");
        }
        finally
        {
            probeTimer.Stop();
            _performance?.RecordFfprobeAttempt(stationName, probeTimer.Elapsed.TotalMilliseconds, processStarted, probeOutcome);
        }
    }

    private static string ReadStreamUrl(string stationFilePath)
    {
        try
        {
            foreach (var line in File.ReadLines(stationFilePath))
            {
                var value = line.Trim();

                if (value.Length == 0 || value.StartsWith("#"))
                {
                    continue;
                }

                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp ||
                     uri.Scheme == Uri.UriSchemeHttps))
                {
                    return uri.ToString();
                }

                return string.Empty;
            }
        }
        catch
        {
            // The caller will receive an empty-URL fallback.
        }

        return string.Empty;
    }

    private static string FindArtwork(string folder, string stationName)
    {
        string[] extensions = { ".jpg", ".jpeg", ".png", ".webp" };

        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(folder, stationName + extension);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns false for empty metadata, bare URLs, and station status values
    /// such as "Tracklist: https://example.invalid". This prevents status text
    /// from blocking the FFprobe fallback or being written to play history.
    /// </summary>
    public static bool IsUsableStreamTitle(string? streamTitle)
    {
        if (string.IsNullOrWhiteSpace(streamTitle))
        {
            return false;
        }

        var value = WebUtility.HtmlDecode(streamTitle)
            .Trim()
            .Trim('\0', '\'', '"');
        if (value.Length == 0)
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return false;
        }

        var lower = value.ToLowerInvariant();
        var containsUrl = lower.Contains("http://", StringComparison.Ordinal) ||
                          lower.Contains("https://", StringComparison.Ordinal) ||
                          lower.Contains("www.", StringComparison.Ordinal);
        if (containsUrl &&
            (lower.Contains("tracklist:", StringComparison.Ordinal) ||
             lower.Contains("track list:", StringComparison.Ordinal) ||
             lower.Contains("playlist:", StringComparison.Ordinal) ||
             lower.Contains("listen live:", StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    private static string FindStreamTitle(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format) ||
            !format.TryGetProperty("tags", out var tags))
        {
            return string.Empty;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (string.Equals(
                tag.Name, "StreamTitle",
                StringComparison.OrdinalIgnoreCase))
            {
                return tag.Value.GetString()?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static (string Artist, string SongTitle) SplitStreamTitle(
        string streamTitle)
    {
        if (!IsUsableStreamTitle(streamTitle))
        {
            return (string.Empty, string.Empty);
        }

        // Split on the first " - " only, preserving separators
        // that may appear later in the song title.
        var separator = streamTitle.IndexOf(
            " - ", StringComparison.Ordinal);

        if (separator > 0 && separator + 3 < streamTitle.Length)
        {
            return (
                streamTitle[..separator].Trim(),
                streamTitle[(separator + 3)..].Trim());
        }

        // If the station supplies a title without a recognizable
        // separator, show it as the song title.
        return (string.Empty, streamTitle.Trim());
    }

    private static RadioStationMetadata Failure(string message)
    {
        return new RadioStationMetadata { Error = message };
    }

    private static RadioStationMetadata FailureForStation(
        string stationName,
        string artwork,
        string streamUrl,
        string message)
    {
        return new RadioStationMetadata
        {
            StationName = stationName,
            ArtworkPath = artwork,
            StreamUrl = streamUrl,
            Error = message
        };
    }

    private sealed record CacheEntry(
        DateTimeOffset CreatedAt,
        RadioStationMetadata Metadata);

    private sealed record NetworkCacheEntry(
        DateTimeOffset CreatedAt,
        RadioStationMetadata Metadata);
}