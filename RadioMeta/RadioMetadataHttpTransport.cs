using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RadioMeta;

/// <summary>Shared HTTP transport for station metadata reads and explicit stream tests.</summary>
/// <remarks>Normal playback uses the existing redirect behavior. The manual test follows redirects only after validating each destination.</remarks>
internal static class RadioMetadataHttpTransport
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });

    // The user-triggered Current Playback test follows redirects manually so every
    // redirect target can be checked before a connection is opened.
    private static readonly HttpClient TestHttp = new(
        RadioMetadataTestUrlSafety.CreatePublicOnlyHandler(DecompressionMethods.GZip | DecompressionMethods.Deflate));

    internal static async Task<HttpResponseMessage> SendAsync(
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
            if (!await RadioMetadataTestUrlSafety.IsSafeExternalTestUrlAsync(target.AbsoluteUri, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("A stream redirect target did not pass public URL safety checks.");
            currentUri = target;
        }

        throw new InvalidOperationException("The metadata test exceeded the redirect limit.");
    }
}
