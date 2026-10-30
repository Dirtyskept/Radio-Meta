using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RadioMeta;

public sealed partial class RadioMetadataReader
{
    // ICY is sometimes UTF-8 and sometimes a legacy single-byte encoding. Prefer
    // strict UTF-8 when the whole metadata block is valid UTF-8; otherwise retain
    // the historical Latin-1 fallback so legacy stations remain readable.
    private static readonly UTF8Encoding StrictIcyUtf8Encoding = new UTF8Encoding(false, true);

    private sealed record MetadataSourceReadResult(
        string Source,
        string Title,
        string Error,
        double DurationMs,
        int IntervalsAttempted,
        bool Cancelled);

    private void RecordMetadataSourceComparison(
        string stationName,
        MetadataSourceReadResult shoutcast,
        MetadataSourceReadResult icy,
        MetadataSourceReadResult selected)
    {
        var (shoutcastArtist, shoutcastTitle) = RadioStreamTitleParser.SplitNormalizedStreamTitle(shoutcast.Title);
        var (icyArtist, icyTitle) = RadioStreamTitleParser.SplitNormalizedStreamTitle(icy.Title);
        var sameArtist = ComparisonKey(shoutcastArtist) == ComparisonKey(icyArtist);
        var sameTitle = ComparisonKey(shoutcastTitle) == ComparisonKey(icyTitle);
        var agrees = sameArtist && sameTitle;

        var details = agrees
            ? "Shoutcast and direct ICY returned matching artist/title after case, spacing, and punctuation normalization."
            : "Both sources returned usable but different metadata. Selected the " + selected.Source +
              " result. Shoutcast: '" + shoutcast.Title + "'; ICY: '" + icy.Title + "'.";
        _diagnostics?.Record(stationName, "Metadata source comparison", agrees ? "match" : "conflict",
            selected.Title, string.Empty, details, selected.Title);
    }

    private static string ComparisonKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC);
        return new string(normalized.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());
    }

    public async Task<RadioStationMetadata> ReadNetworkMetadataAsync(
        string stationFilePath,
        int timeoutSeconds = LiveMetadataSourceTimeoutSeconds,
        bool forceRefresh = false,
        bool allowDirectIcy = true,
        CancellationToken cancellationToken = default,
        Func<string, string, bool>? ignoreMetadataPhrase = null,
        bool enableAdaptiveRouting = true)
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
                string.IsNullOrWhiteSpace(cached.Metadata.Error) ? "cache-hit" : "cached-empty", "network cache", shoutcastAttempted: false);
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
                "missing-stream-url", "none", shoutcastAttempted: false);
            return FailureForStation(stationName, artwork, string.Empty,
                "The station file contains no stream URL.");
        }

        // Progress retry/backoff counters only for a real, uncached station lookup.
        // The persistent store saves checkpoints instead of rewriting XML on every poll.
        _adaptiveRoutingStateStore.AdvanceEligibleLookup(fullPath);

        _diagnostics?.Record(stationName, "Metadata reader", "info", string.Empty, string.Empty,
            allowDirectIcy
                ? "Starting adaptive metadata source routing; the station URL is known."
                : "Direct ICY is disabled; querying Shoutcast only.");

        // The station URL is available now. The adaptive router begins by
        // racing the two existing readers, then tries the learned winner first
        // after four consecutive parallel losses by the other source.
        var sourceTimeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 15));
        using var stopOtherSource = new CancellationTokenSource();
        var icyMetrics = allowDirectIcy ? new IcyReadMetrics() : null;
        RadioMetaMetadataRoutePlan? routePlan = null;
        if (allowDirectIcy)
        {
            routePlan = enableAdaptiveRouting
                ? _adaptiveMetadataRouting.BeginLookup(fullPath)
                : new RadioMetaMetadataRoutePlan(
                    RadioMetaMetadataRoutingMode.ParallelLearning,
                    null,
                    0,
                    "Adaptive routing is disabled; using the existing parallel Shoutcast/direct-ICY lookup path.");
        }

        if (!allowDirectIcy)
        {
            _diagnostics?.Record(stationName, "Metadata reader", "info", string.Empty, string.Empty,
                "Direct ICY is disabled; querying Shoutcast only. Adaptive source learning is paused for this lookup.");
        }
        else
        {
            _diagnostics?.Record(stationName, "Adaptive routing", "info", string.Empty, string.Empty,
                routePlan!.Description);
        }

        MetadataSourceReadResult? shoutcastRead = null;
        MetadataSourceReadResult? icyRead = null;
        MetadataSourceReadResult? selectedRead = null;
        string sourceError = string.Empty;

        void RememberRead(MetadataSourceReadResult read)
        {
            if (string.Equals(read.Source, "Shoutcast", StringComparison.Ordinal))
                shoutcastRead = read;
            else if (string.Equals(read.Source, "ICY", StringComparison.Ordinal))
                icyRead = read;

            if (!string.IsNullOrWhiteSpace(read.Error))
            {
                sourceError = string.IsNullOrWhiteSpace(sourceError)
                    ? read.Error
                    : sourceError + "; " + read.Error;
            }
        }

        bool HasUsableTitle(MetadataSourceReadResult? read) =>
            read is not null && !string.IsNullOrWhiteSpace(read.Title);

        if (!allowDirectIcy)
        {
            var read = await ReadShoutcastCandidateAsync().ConfigureAwait(false);
            RememberRead(read);
            if (HasUsableTitle(read)) selectedRead = read;
        }
        else if (enableAdaptiveRouting && routePlan!.Mode == RadioMetaMetadataRoutingMode.PreferredFirst)
        {
            var preferredSource = routePlan.PreferredSource!;
            var firstRead = await ReadSourceAsync(preferredSource).ConfigureAwait(false);
            RememberRead(firstRead);
            if (HasUsableTitle(firstRead))
            {
                selectedRead = firstRead;
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fallbackSource = string.Equals(preferredSource, "Shoutcast", StringComparison.OrdinalIgnoreCase)
                    ? "ICY"
                    : "Shoutcast";
                _diagnostics?.Record(stationName, "Adaptive routing", "info", string.Empty, string.Empty,
                    "Preferred source " + preferredSource + " returned no usable title; trying " + fallbackSource + " as fallback.");
                var fallbackRead = await ReadSourceAsync(fallbackSource).ConfigureAwait(false);
                RememberRead(fallbackRead);
                if (HasUsableTitle(fallbackRead)) selectedRead = fallbackRead;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var routeFeedback = _adaptiveMetadataRouting.RecordSequentialOutcome(
                fullPath, preferredSource, selectedRead?.Source);
            if (!string.IsNullOrWhiteSpace(routeFeedback))
            {
                _diagnostics?.Record(stationName, "Adaptive routing", "info", string.Empty, string.Empty,
                    routeFeedback, selectedRead?.Title ?? string.Empty);
            }
        }
        else
        {
            // Invoke both async readers before awaiting either one. This is the
            // initial learning path and the periodic recheck path.
            var shoutcastTask = ReadShoutcastCandidateAsync();
            var icyTask = ReadIcyCandidateAsync();
            var allTasks = new List<Task<MetadataSourceReadResult>> { shoutcastTask, icyTask };
            var pending = new List<Task<MetadataSourceReadResult>>(allTasks);

            while (pending.Count > 0 && selectedRead is null)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var read = await completed.ConfigureAwait(false);
                RememberRead(read);

                if (cancellationToken.IsCancellationRequested)
                {
                    stopOtherSource.Cancel();
                    await Task.WhenAll(allTasks).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (HasUsableTitle(read)) selectedRead = read;
            }

            if (selectedRead is not null)
            {
                // If both were already complete before cancellation, use measured
                // elapsed time to break a tie rather than task-list ordering.
                foreach (var completed in pending.Where(task => task.IsCompleted).ToArray())
                {
                    pending.Remove(completed);
                    RememberRead(await completed.ConfigureAwait(false));
                }

                if (pending.Count > 0)
                {
                    stopOtherSource.Cancel();
                    var stoppedReads = await Task.WhenAll(pending).ConfigureAwait(false);
                    pending.Clear();
                    foreach (var read in stoppedReads) RememberRead(read);
                }
                else if (HasUsableTitle(shoutcastRead) && HasUsableTitle(icyRead))
                {
                    selectedRead = shoutcastRead!.DurationMs <= icyRead!.DurationMs ? shoutcastRead : icyRead;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (enableAdaptiveRouting)
            {
                if (HasUsableTitle(selectedRead))
                {
                    var loserSource = string.Equals(selectedRead!.Source, "Shoutcast", StringComparison.OrdinalIgnoreCase)
                        ? "ICY"
                        : "Shoutcast";
                    var routeFeedback = _adaptiveMetadataRouting.RecordParallelOutcome(
                        fullPath, selectedRead.Source, loserSource,
                        wasPeriodicRecheck: routePlan?.Mode == RadioMetaMetadataRoutingMode.ParallelRecheck);
                    _diagnostics?.Record(stationName, "Adaptive routing", "info", string.Empty, string.Empty,
                        routeFeedback, selectedRead.Title);
                }
                else
                {
                    var routeFeedback = _adaptiveMetadataRouting.RecordParallelNoWinner(
                        fullPath, wasPeriodicRecheck: routePlan?.Mode == RadioMetaMetadataRoutingMode.ParallelRecheck);
                    _diagnostics?.Record(stationName, "Adaptive routing", "info", string.Empty, string.Empty,
                        routeFeedback);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (HasUsableTitle(shoutcastRead) && HasUsableTitle(icyRead))
        {
            RecordMetadataSourceComparison(stationName, shoutcastRead!, icyRead!, selectedRead!);
        }

        var title = selectedRead?.Title ?? string.Empty;
        var resultSource = selectedRead?.Source ?? "none";
        var icyAttempted = icyRead is not null;
        double? icyDurationMs = icyRead?.DurationMs;
        var icyIntervalsAttempted = icyRead?.IntervalsAttempted ?? 0;
        var shoutcastDurationMs = shoutcastRead?.DurationMs ?? 0;

        if (selectedRead is not null && shoutcastRead?.Cancelled == true)
        {
            _diagnostics?.Record(stationName, "Shoutcast", "info", string.Empty, string.Empty,
                "Cancelled the slower Shoutcast request after direct ICY returned the first usable title.");
        }
        if (selectedRead is not null && icyRead?.Cancelled == true)
        {
            _diagnostics?.Record(stationName, "ICY", "info", string.Empty, string.Empty,
                "Cancelled the slower direct ICY request after Shoutcast returned the first usable title.");
        }

        async Task<MetadataSourceReadResult> ReadSourceAsync(string source) =>
            string.Equals(source, "Shoutcast", StringComparison.OrdinalIgnoreCase)
                ? await ReadShoutcastCandidateAsync().ConfigureAwait(false)
                : await ReadIcyCandidateAsync().ConfigureAwait(false);

        async Task<MetadataSourceReadResult> ReadShoutcastCandidateAsync()
        {
            var timer = Stopwatch.StartNew();
            var candidate = string.Empty;
            var error = string.Empty;
            var cancelled = false;
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopOtherSource.Token);
            requestCts.CancelAfter(sourceTimeout);
            try
            {
                candidate = await ReadShoutcastTitleAsync(
                    streamUrl,
                    stationName,
                    ignoreMetadataPhrase,
                    requestCts.Token,
                    _diagnostics,
                    stationKey: fullPath).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (OperationCanceledException) when (stopOtherSource.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (OperationCanceledException)
            {
                error = "Shoutcast metadata request timed out.";
                _diagnostics?.Record(stationName, "Shoutcast", "error", string.Empty, string.Empty, error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = "Station HTTP metadata request failed (" + ex.GetType().Name + ").";
                _diagnostics?.Record(stationName, "Shoutcast", "error", string.Empty, string.Empty, error);
            }
            finally { timer.Stop(); }

            if (cancelled || !RadioStreamTitleParser.IsUsableNormalizedStreamTitle(candidate) || IsIgnored(candidate))
                candidate = string.Empty;

            return new MetadataSourceReadResult("Shoutcast", candidate, error, timer.Elapsed.TotalMilliseconds, 0, cancelled);
        }

        async Task<MetadataSourceReadResult> ReadIcyCandidateAsync()
        {
            var timer = Stopwatch.StartNew();
            var candidate = string.Empty;
            var error = string.Empty;
            var cancelled = false;
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopOtherSource.Token);
            requestCts.CancelAfter(sourceTimeout);
            try
            {
                candidate = await ReadIcyTitleAsync(
                    streamUrl,
                    stationName,
                    ignoreMetadataPhrase,
                    requestCts.Token,
                    _diagnostics,
                    icyMetrics).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (OperationCanceledException) when (stopOtherSource.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (OperationCanceledException)
            {
                error = "ICY metadata request timed out.";
                _diagnostics?.Record(stationName, "ICY", "error", string.Empty, string.Empty,
                    "ICY metadata request timed out before a usable title was read.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = "ICY metadata request failed (" + ex.GetType().Name + ").";
                _diagnostics?.Record(stationName, "ICY", "error", string.Empty, string.Empty, error);
            }
            finally { timer.Stop(); }

            if (cancelled || !RadioStreamTitleParser.IsUsableNormalizedStreamTitle(candidate) || IsIgnored(candidate))
                candidate = string.Empty;

            return new MetadataSourceReadResult("ICY", candidate, error, timer.Elapsed.TotalMilliseconds,
                icyMetrics?.IntervalsAttempted ?? 0, cancelled);
        }

        if (!RadioStreamTitleParser.IsUsableNormalizedStreamTitle(title) || IsIgnored(title))
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
            StoreNetworkCache(fullPath, new NetworkCacheEntry(DateTimeOffset.UtcNow, failed));
            lookupTimer.Stop();
            _performance?.RecordNetworkLookup(stationName, lookupTimer.Elapsed.TotalMilliseconds,
                shoutcastDurationMs, icyDurationMs, icyIntervalsAttempted, icyAttempted,
                false, string.IsNullOrWhiteSpace(sourceError) ? "no-title" : "reader-error", "none",
                shoutcastAttempted: shoutcastRead is not null,
                shoutcastCancelled: shoutcastRead?.Cancelled == true,
                icyCancelled: icyRead?.Cancelled == true);
            return failed;
        }

        var (artist, songTitle) = RadioStreamTitleParser.SplitNormalizedStreamTitle(title);
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
        StoreNetworkCache(fullPath, new NetworkCacheEntry(DateTimeOffset.UtcNow, result));
        lookupTimer.Stop();
        _performance?.RecordNetworkLookup(stationName, lookupTimer.Elapsed.TotalMilliseconds,
            shoutcastDurationMs, icyDurationMs, icyIntervalsAttempted, icyAttempted,
            false, "success", resultSource,
            shoutcastAttempted: shoutcastRead is not null,
            shoutcastCancelled: shoutcastRead?.Cancelled == true,
            icyCancelled: icyRead?.Cancelled == true);
        return result;
    }

    private bool ShouldAttemptShoutcastEndpoint(
        string? stationKey,
        string endpoint,
        string stationName,
        string endpointLabel,
        RadioMetaDiagnosticEventStore? diagnostics)
    {
        if (string.IsNullOrWhiteSpace(stationKey)) return true;
        var shouldAttempt = _adaptiveRoutingStateStore.ShouldAttemptEndpoint(stationKey, endpoint, out var skipReason);
        if (!shouldAttempt && !string.IsNullOrWhiteSpace(skipReason))
        {
            diagnostics?.Record(stationName, endpointLabel, "info", string.Empty, string.Empty, skipReason +
                " Direct ICY metadata fallback remains available.");
        }
        return shouldAttempt;
    }

    private void RecordShoutcastEndpointFailure(
        string? stationKey,
        string endpoint,
        int? statusCode,
        string outcome)
    {
        if (!string.IsNullOrWhiteSpace(stationKey))
            _adaptiveRoutingStateStore.RecordEndpointFailure(stationKey, endpoint, statusCode, outcome);
    }

    private void RecordShoutcastEndpointSuccess(string? stationKey, string endpoint, string outcome)
    {
        if (!string.IsNullOrWhiteSpace(stationKey))
            _adaptiveRoutingStateStore.RecordEndpointSuccess(stationKey, endpoint, outcome);
    }

    private async Task<string> ReadShoutcastTitleAsync(
        string streamUrl,
        string stationName,
        Func<string, string, bool>? ignoreMetadataPhrase,
        CancellationToken cancellationToken,
        RadioMetaDiagnosticEventStore? diagnostics,
        bool validateRedirects = false,
        string? stationKey = null)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var streamUri))
            return string.Empty;

        var baseUrl = streamUri.GetLeftPart(UriPartial.Authority);
        var tryStats = ShouldAttemptShoutcastEndpoint(stationKey, "stats", stationName, "Shoutcast /stats", diagnostics);
        if (tryStats)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/stats?sid=1&json=1");
            request.Headers.UserAgent.ParseAdd("RadioMeta/1.0");

            try
            {
                using var response = await RadioMetadataHttpTransport.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken, validateRedirects).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    RecordShoutcastEndpointFailure(stationKey, "stats", (int)response.StatusCode,
                        "HTTP " + (int)response.StatusCode + " from /stats");
                    diagnostics?.Record(stationName, "Shoutcast /stats", "error", string.Empty, string.Empty,
                        "HTTP request returned status " + (int)response.StatusCode +
                        (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.MethodNotAllowed
                            ? ". Endpoint auto-blocked; recovery is checked after 24 hours and 320 uncached station lookups."
                            : ". Transient failures use 10/20/40/80 eligible-lookup backoff.") );
                }
                else
                {
                    var json = await RadioMetaHttpContentReader.ReadStringBoundedAsync(
                        response.Content, 256 * 1024, cancellationToken).ConfigureAwait(false);
                    var title = string.Empty;
                    var ignoreMatched = false;
                    var failureReason = "HTTP 200 response did not contain a usable songtitle field.";
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("songtitle", out var song))
                        {
                            title = RadioStreamTitleParser.NormalizeStreamTitle(song.GetString());
                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                if (ignoreMetadataPhrase?.Invoke(stationName, title) == true)
                                {
                                    ignoreMatched = true;
                                    RecordShoutcastEndpointSuccess(stationKey, "stats", "Valid /stats title was intentionally filtered by Ignore Metadata Phrases.");
                                    diagnostics?.Record(stationName, "Ignore Metadata Phrases", "filtered", title, string.Empty,
                                        "Title matched a configured ignore phrase.", title);
                                }
                                else if (RadioStreamTitleParser.IsUsableNormalizedStreamTitle(title))
                                {
                                    RecordShoutcastEndpointSuccess(stationKey, "stats", "Useful songtitle returned by /stats.");
                                    diagnostics?.Record(stationName, "Shoutcast /stats", "detected", title, string.Empty,
                                        "Non-empty songtitle value observed.", title);
                                    return title;
                                }
                                else
                                {
                                    failureReason = "HTTP 200 response supplied a songtitle that failed the usability check.";
                                    diagnostics?.Record(stationName, "Shoutcast /stats", "rejected", title, string.Empty,
                                        "Value did not pass the song-title usability check.", title);
                                }
                            }
                            else
                            {
                                failureReason = "HTTP 200 response had an empty songtitle field.";
                                diagnostics?.Record(stationName, "Shoutcast /stats", "empty", string.Empty, string.Empty,
                                    "The songtitle field was empty.");
                            }
                        }
                        else
                        {
                            failureReason = "HTTP 200 JSON response did not contain a songtitle field.";
                            diagnostics?.Record(stationName, "Shoutcast /stats", "empty", string.Empty, string.Empty,
                                "JSON response did not contain a songtitle field.");
                        }
                    }
                    catch (JsonException)
                    {
                        failureReason = "HTTP 200 response was not valid JSON.";
                        diagnostics?.Record(stationName, "Shoutcast /stats", "error", string.Empty, string.Empty,
                            "Endpoint response was not valid JSON.");
                    }

                    if (!ignoreMatched)
                        RecordShoutcastEndpointFailure(stationKey, "stats", null, failureReason);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RecordShoutcastEndpointFailure(stationKey, "stats", null, "Request failed (" + ex.GetType().Name + ")");
                diagnostics?.Record(stationName, "Shoutcast /stats", "error", string.Empty, string.Empty,
                    "Request failed (" + ex.GetType().Name + "); trying the legacy endpoint.");
            }
        }

        if (!ShouldAttemptShoutcastEndpoint(stationKey, "7.html", stationName, "Shoutcast /7.html", diagnostics))
            return string.Empty;

        try
        {
            using var legacyRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/7.html");
            legacyRequest.Headers.UserAgent.ParseAdd("RadioMeta/1.0");
            using var legacyResponse = await RadioMetadataHttpTransport.SendAsync(legacyRequest,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken, validateRedirects).ConfigureAwait(false);
            if (!legacyResponse.IsSuccessStatusCode)
            {
                RecordShoutcastEndpointFailure(stationKey, "7.html", (int)legacyResponse.StatusCode,
                    "HTTP " + (int)legacyResponse.StatusCode + " from /7.html");
                diagnostics?.Record(stationName, "Shoutcast /7.html", "error", string.Empty, string.Empty,
                    "HTTP request returned status " + (int)legacyResponse.StatusCode +
                    (legacyResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.MethodNotAllowed
                        ? ". Endpoint auto-blocked; recovery is checked after 24 hours and 320 uncached station lookups."
                        : ". Transient failures use 10/20/40/80 eligible-lookup backoff."));
                return string.Empty;
            }

            var html = await RadioMetaHttpContentReader.ReadStringBoundedAsync(
                legacyResponse.Content, 256 * 1024, cancellationToken).ConfigureAwait(false);
            var match = System.Text.RegularExpressions.Regex.Match(html,
                @"<body[^>]*>(.*?)</body>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            // Split raw comma-delimited fields before decoding the title so encoded commas in a title are not field boundaries.
            var body = (match.Success ? match.Groups[1].Value : html).Trim();
            var fields = body.Split(',');
            var legacyTitle = fields.Length >= 7
                ? RadioStreamTitleParser.NormalizeStreamTitle(fields[6])
                : string.Empty;
            if (string.IsNullOrWhiteSpace(legacyTitle))
            {
                RecordShoutcastEndpointFailure(stationKey, "7.html", null, "HTTP 200 response contained no usable title.");
                diagnostics?.Record(stationName, "Shoutcast /7.html", "empty", string.Empty, string.Empty,
                    "No song title was present in the legacy response.");
                return string.Empty;
            }

            diagnostics?.Record(stationName, "Shoutcast /7.html", "detected", legacyTitle, string.Empty,
                "Non-empty legacy title value observed.", legacyTitle);
            if (ignoreMetadataPhrase?.Invoke(stationName, legacyTitle) == true)
            {
                RecordShoutcastEndpointSuccess(stationKey, "7.html", "Valid legacy title was intentionally filtered by Ignore Metadata Phrases.");
                diagnostics?.Record(stationName, "Ignore Metadata Phrases", "filtered", legacyTitle, string.Empty,
                    "Title matched a configured ignore phrase.", legacyTitle);
                return string.Empty;
            }
            if (!RadioStreamTitleParser.IsUsableNormalizedStreamTitle(legacyTitle))
            {
                RecordShoutcastEndpointFailure(stationKey, "7.html", null, "HTTP 200 response supplied a title that failed the usability check.");
                diagnostics?.Record(stationName, "Shoutcast /7.html", "rejected", legacyTitle, string.Empty,
                    "Value did not pass the song-title usability check.", legacyTitle);
                return string.Empty;
            }

            RecordShoutcastEndpointSuccess(stationKey, "7.html", "Useful songtitle returned by /7.html.");
            return legacyTitle;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RecordShoutcastEndpointFailure(stationKey, "7.html", null, "Request failed (" + ex.GetType().Name + ")");
            diagnostics?.Record(stationName, "Shoutcast /7.html", "error", string.Empty, string.Empty,
                "Request failed (" + ex.GetType().Name + ").");
            return string.Empty;
        }
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
        using var response = await RadioMetadataHttpTransport.SendAsync(request,
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

            var block = DecodeIcyMetadataBlock(metadata).Trim('\0', ' ', '\r', '\n');
            var marker = block.IndexOf("StreamTitle=", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                diagnostics?.Record(stationName, "ICY", "empty", string.Empty, string.Empty, "ICY interval " + (attempt + 1) + " contained bytes but no StreamTitle field.");
                continue;
            }
            var value = RadioIcyStreamTitleParser.ExtractStreamTitleValue(block[(marker + "StreamTitle=".Length)..]);
            value = RadioStreamTitleParser.NormalizeStreamTitle(value);
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
            if (!RadioStreamTitleParser.IsUsableNormalizedStreamTitle(value))
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

    private static string DecodeIcyMetadataBlock(byte[] metadata)
    {
        try
        {
            return StrictIcyUtf8Encoding.GetString(metadata);
        }
        catch (DecoderFallbackException)
        {
            // Some older stations still send metadata in a legacy single-byte
            // encoding. Preserve the previous behavior for those payloads.
            return Encoding.Latin1.GetString(metadata);
        }
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
}
