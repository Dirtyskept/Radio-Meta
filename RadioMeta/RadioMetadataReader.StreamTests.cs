using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RadioMeta;

public sealed partial class RadioMetadataReader
{
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
        int timeoutSeconds = ManualStreamTestTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        stationName = (stationName ?? string.Empty).Trim();
        streamUrl = (streamUrl ?? string.Empty).Trim();
        var timer = Stopwatch.StartNew();

        if (stationName.Length == 0 ||
            !Uri.TryCreate(streamUrl, UriKind.Absolute, out var streamUri) ||
            !RadioMetaUriSchemeHelpers.IsHttpOrHttps(streamUri) ||
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

        try
        {
            // Try the lightweight station endpoints first, matching the normal
            // metadata pipeline. They commonly fail quickly with HTTP 404 for
            // non-Shoutcast stations, allowing the direct ICY attempt to run.
            try
            {
                title = await ReadShoutcastTitleAsync(
                    streamUrl, stationName, null, timeout.Token, _diagnostics, validateRedirects: true).ConfigureAwait(false);
                if (RadioStreamTitleParser.IsUsableNormalizedStreamTitle(title)) source = "Shoutcast";
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

            if (!RadioStreamTitleParser.IsUsableNormalizedStreamTitle(title) && !timeout.IsCancellationRequested)
            {
                try
                {
                    title = await ReadIcyTitleAsync(
                        streamUrl, stationName, null, timeout.Token, _diagnostics, metrics: null, validateRedirects: true).ConfigureAwait(false);
                    if (RadioStreamTitleParser.IsUsableNormalizedStreamTitle(title)) source = "ICY";
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
            else if (!RadioStreamTitleParser.IsUsableNormalizedStreamTitle(title) && timeout.IsCancellationRequested && error.Length == 0)
            {
                error = "The metadata test timed out before a usable title was received.";
            }
        }
        finally
        {
            timer.Stop();
        }

        if (RadioStreamTitleParser.IsUsableNormalizedStreamTitle(title))
        {
            var (artist, songTitle) = RadioStreamTitleParser.SplitNormalizedStreamTitle(title);
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
}
