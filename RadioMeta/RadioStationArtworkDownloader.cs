using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RadioMeta;

internal sealed record RadioArtworkDownloadResult(bool Success, byte[] Bytes, string Extension, string Error);

/// <summary>Privacy-safe, in-memory station-art download counters for diagnostics.</summary>
public sealed class RadioMetaArtworkDiagnosticDto
{
    public long DownloadAttemptsSinceStart { get; set; }
    public long ValidatedDownloadsSinceStart { get; set; }
    public long FailedDownloadsSinceStart { get; set; }
    public string LastDownloadStatus { get; set; } = "not attempted since plugin start";
    public DateTimeOffset? LastDownloadAtUtc { get; set; }
}

/// <summary>
/// Shared bounded image downloader. Only public HTTP(S) destinations are allowed;
/// redirects are manual so each destination is checked before another request is sent.
/// </summary>
internal static class RadioStationArtworkDownloader
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly HttpClient Client = new(
        RadioMetadataTestUrlSafety.CreatePublicOnlyHandler(DecompressionMethods.GZip | DecompressionMethods.Deflate))
    { Timeout = Timeout.InfiniteTimeSpan };
    private static long _downloadAttempts;
    private static long _validatedDownloads;
    private static long _failedDownloads;
    private static long _lastDownloadAtUnixMs;
    private static string _lastDownloadStatus = "not attempted since plugin start";

    internal static RadioArtworkDownloadResult Download(string inputUrl)
    {
        Interlocked.Increment(ref _downloadAttempts);
        try
        {
            var result = DownloadAsync(inputUrl).GetAwaiter().GetResult();
            RecordDownloadResult(result.Success);
            return result;
        }
        catch
        {
            RecordDownloadResult(false);
            throw;
        }
    }

    internal static RadioMetaArtworkDiagnosticDto GetDiagnosticsSnapshot()
    {
        var lastMs = Interlocked.Read(ref _lastDownloadAtUnixMs);
        return new RadioMetaArtworkDiagnosticDto
        {
            DownloadAttemptsSinceStart = Interlocked.Read(ref _downloadAttempts),
            ValidatedDownloadsSinceStart = Interlocked.Read(ref _validatedDownloads),
            FailedDownloadsSinceStart = Interlocked.Read(ref _failedDownloads),
            LastDownloadStatus = Volatile.Read(ref _lastDownloadStatus),
            LastDownloadAtUtc = lastMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastMs) : null
        };
    }

    private static void RecordDownloadResult(bool success)
    {
        if (success) Interlocked.Increment(ref _validatedDownloads);
        else Interlocked.Increment(ref _failedDownloads);
        Volatile.Write(ref _lastDownloadStatus, success ? "image validated successfully" : "download or image validation failed; details withheld");
        Interlocked.Exchange(ref _lastDownloadAtUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static async Task<RadioArtworkDownloadResult> DownloadAsync(string inputUrl)
    {
        if (string.IsNullOrWhiteSpace(inputUrl) || inputUrl.Length > 8192 ||
            !Uri.TryCreate(inputUrl.Trim(), UriKind.Absolute, out var current))
            return Fail("Enter a valid http:// or https:// logo URL.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            for (var redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                if (!await RadioMetadataTestUrlSafety.IsSafePublicHttpUrlAsync(current.AbsoluteUri, timeout.Token).ConfigureAwait(false))
                    return Fail("Logo URLs must resolve to a publicly routable HTTP(S) host. Local/private network destinations are blocked.");

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd("RadioMeta/1.3.98 artwork-fetch");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location is null) return Fail("The logo server returned an invalid redirect.");
                    if (redirect == MaxRedirects) return Fail("The logo URL redirected too many times.");
                    Uri target;
                    try { target = location.IsAbsoluteUri ? location : new Uri(current, location); }
                    catch { return Fail("The logo server returned an invalid redirect."); }
                    if (current.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps)
                        return Fail("The logo URL attempted an insecure HTTPS-to-HTTP redirect.");
                    current = target;
                    continue;
                }

                if (!response.IsSuccessStatusCode) return Fail("The logo server returned HTTP status " + (int)response.StatusCode + ".");
                if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxImageBytes)
                    return Fail("The logo image is larger than the 5 MB limit.");

                using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var chunk = new byte[81920];
                while (true)
                {
                    var count = await input.ReadAsync(chunk.AsMemory(0, chunk.Length), timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (output.Length + count > MaxImageBytes) return Fail("The logo image is larger than the 5 MB limit.");
                    await output.WriteAsync(chunk.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }

                var bytes = output.ToArray();
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                var pathExtension = Path.GetExtension(current.AbsolutePath).ToLowerInvariant();
                var extension = mediaType switch
                {
                    "image/jpeg" or "image/jpg" => ".jpg",
                    "image/png" => ".png",
                    _ => pathExtension is ".jpg" or ".jpeg" ? ".jpg" : pathExtension == ".png" ? ".png" : string.Empty
                };
                var isJpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
                var isPng = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
                if (extension.Length == 0) return Fail("The logo URL must point to a JPG or PNG image.");
                if (!((extension == ".jpg" && isJpeg) || (extension == ".png" && isPng)))
                    return Fail("The logo response was not a valid JPG or PNG image.");
                return new RadioArtworkDownloadResult(true, bytes, extension, string.Empty);
            }
            return Fail("The logo URL redirected too many times.");
        }
        catch (OperationCanceledException)
        {
            return Fail("The logo download timed out after 12 seconds.");
        }
        catch (HttpRequestException)
        {
            return Fail("The logo server could not be reached securely.");
        }
        catch (IOException)
        {
            return Fail("The logo image could not be read completely.");
        }
        catch (Exception ex) when (ex is UriFormatException || ex is ArgumentException)
        {
            return Fail("The logo URL or response was invalid.");
        }
    }

    private static RadioArtworkDownloadResult Fail(string message)
        => new(false, Array.Empty<byte>(), string.Empty, message);
}
