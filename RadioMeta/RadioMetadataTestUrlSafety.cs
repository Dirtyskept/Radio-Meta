using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RadioMeta;

/// <summary>URL validation policy for the user-triggered stream metadata test.</summary>
/// <remarks>Normal playback keeps its established request behavior; this validator is used by the explicit test endpoint and its redirects.</remarks>
internal static class RadioMetadataTestUrlSafety
{
    internal static async Task<bool> IsSafeExternalTestUrlAsync(
        string value, CancellationToken cancellationToken = default)
    {
        if (!TryGetSafeTestUri(value, out var uri)) return false;
        return await IsPublicHttpUriAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validate a generic HTTP(S) destination against loopback/private/link-local DNS targets.</summary>
    internal static async Task<bool> IsSafePublicHttpUrlAsync(
        string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !RadioMetaUriSchemeHelpers.IsHttpOrHttps(uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host))
            return false;
        return await IsPublicHttpUriAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an HTTP handler that connects only to public IP addresses. This re-checks DNS
    /// results at connection time, reducing the DNS-rebinding gap between validation and connect.
    /// Callers must still validate each redirect target explicitly.
    /// </summary>
    internal static SocketsHttpHandler CreatePublicOnlyHandler(DecompressionMethods decompression)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = decompression,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                IPAddress[] addresses;
                if (IPAddress.TryParse(host, out var literal))
                    addresses = new[] { literal };
                else
                    addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

                if (addresses.Length == 0 || addresses.Any(address => !IsPubliclyRoutableAddress(address)))
                    throw new HttpRequestException("The destination did not resolve exclusively to public IP addresses.");

                Exception? lastError = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException)
                    {
                        socket.Dispose();
                        throw;
                    }
                    catch (Exception ex) when (ex is SocketException || ex is IOException || ex is HttpRequestException)
                    {
                        socket.Dispose();
                        lastError = ex;
                    }
                }

                throw new HttpRequestException("Could not connect to a validated public destination.", lastError);
            }
        };
    }

    private static async Task<bool> IsPublicHttpUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("home.arpa", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase))
            return false;

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
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            !RadioMetaUriSchemeHelpers.IsHttpOrHttps(parsed) ||
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

    internal static bool IsPubliclyRoutableAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
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
                (bytes[0] == 0x20 && bytes[1] == 0x02) || // 6to4 transition addresses
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
}
