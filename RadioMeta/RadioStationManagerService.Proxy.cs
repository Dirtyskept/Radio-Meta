using System;
using System.IO;
using System.Linq;
using System.Text;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

public sealed class SetRadioStationProxyResponse
{
    public bool Success { get; set; }
    public string DiagnosticOperationId { get; set; } = string.Empty;
    public bool UseProxy { get; set; }
    public string Error { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool LibraryScanTargetFound { get; set; }
    public string LibraryScanItemId { get; set; } = string.Empty;
    public string LibraryScanLibraryName { get; set; } = string.Empty;
    public string LibraryScanLocation { get; set; } = string.Empty;
    public string LibraryScanMessage { get; set; } = string.Empty;
}

[Route("/RadioMeta/Stations/Proxy", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class SetRadioStationProxy : IReturn<SetRadioStationProxyResponse>
{
    public string FilePath { get; set; } = string.Empty;
    public bool UseProxy { get; set; }
}

public sealed partial class RadioStationManagerService
{
    public object Post(SetRadioStationProxy request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null) return new SetRadioStationProxyResponse { Error = "Radio Meta is not initialized." };
        var folder = plugin.GetStationFolder();
        if (folder.Length == 0 || !Directory.Exists(folder))
            return new SetRadioStationProxyResponse { Error = "The configured Station Folder is unavailable." };

        string root, path;
        try
        {
            root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            path = Path.GetFullPath((request.FilePath ?? string.Empty).Trim());
        }
        catch { return new SetRadioStationProxyResponse { Error = "The station path is invalid." }; }
        var prefix = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            return new SetRadioStationProxyResponse { Error = "The selected station is not inside the configured Station Folder." };
        if (!File.Exists(path)) return new SetRadioStationProxyResponse { Error = "The station file no longer exists. Scan the folder again." };

        var sidecar = RadioMetaProxyService.SidecarPath(path);
        var currentlyEnabled = RadioMetaProxyService.TryGetUpstreamUrl(path, out var savedUpstream);
        if (File.Exists(sidecar) && !currentlyEnabled)
            return new SetRadioStationProxyResponse { Error = "The proxy settings file exists but is unreadable. No files were changed; inspect the .radiometa-proxy.json file before changing proxy mode." };
        if (currentlyEnabled == request.UseProxy)
        {
            var unchangedTargetFound = TryResolveLibraryScanTarget(folder, out var unchangedTarget, out var unchangedMessage);
            var unchangedSidecarStatus = currentlyEnabled
                ? (RadioMetaProxyService.TryGetProxyDetails(path, out _, out _) ? "present and valid" : "missing or invalid")
                : "not present: direct station";
            var unchangedOperationId = plugin.StationOperationEvents.RecordFileOperation(
                request.UseProxy ? "Enable proxy" : "Disable proxy", Path.GetFileNameWithoutExtension(path),
                "unchanged", unchangedSidecarStatus, unchangedTargetFound,
                "The requested proxy mode already matched the station's current mode.");
            return new SetRadioStationProxyResponse
            {
                Success = true,
                DiagnosticOperationId = unchangedOperationId,
                UseProxy = currentlyEnabled,
                FilePath = path,
                LibraryScanTargetFound = unchangedTargetFound,
                LibraryScanItemId = unchangedTarget?.ItemId ?? string.Empty,
                LibraryScanLibraryName = unchangedTarget?.LibraryName ?? string.Empty,
                LibraryScanLocation = unchangedTarget?.Location ?? string.Empty,
                LibraryScanMessage = unchangedMessage
            };
        }

        string upstream;
        if (currentlyEnabled) upstream = savedUpstream;
        else
        {
            var read = RadioStationFileStore.ReadStationStreamFile(path);
            upstream = read.StreamUrl;
        }
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new SetRadioStationProxyResponse { Error = "Proxy mode requires a valid http:// or https:// stream URL." };

        string replacementUrl = upstream;
        string token = string.Empty;
        if (request.UseProxy)
        {
            var proxyBase = RadioMetaEntryPoint.GetProxyBaseUrl();
            if (string.IsNullOrWhiteSpace(proxyBase))
                return new SetRadioStationProxyResponse { Error = "The proxy listener is not running. Check the Emby server log and Station Folder configuration." };
            token = Guid.NewGuid().ToString("N");
            replacementUrl = proxyBase + "/proxy/" + token;
            try { RadioMetaProxyService.WriteProxySidecar(path, upstream, token); }
            catch (Exception ex) { return new SetRadioStationProxyResponse { Error = "Could not save proxy settings (" + ex.GetType().Name + ")." }; }
        }

        var temp = path + ".radiometa-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RadioStationFileStore.WriteTemporaryStationStreamFile(temp, replacementUrl);
            File.Move(temp, path, true);
            if (!request.UseProxy)
            {
                try { if (File.Exists(sidecar)) File.Delete(sidecar); }
                catch (Exception ex) { return new SetRadioStationProxyResponse { Error = "The direct stream URL was restored, but proxy settings cleanup failed (" + ex.GetType().Name + "). Rescan the folder before changing it again." }; }
            }
            var targetFound = TryResolveLibraryScanTarget(folder, out var scanTarget, out var scanMessage);
            var sidecarStatus = request.UseProxy
                ? (RadioMetaProxyService.TryGetProxyDetails(path, out _, out _) ? "present and valid" : "missing or invalid")
                : (File.Exists(sidecar) ? "cleanup incomplete" : "removed");
            var diagnosticOperationId = plugin.StationOperationEvents.RecordFileOperation(
                request.UseProxy ? "Enable proxy" : "Disable proxy", Path.GetFileNameWithoutExtension(path),
                "success", sidecarStatus, targetFound,
                request.UseProxy ? "The station stream file was switched to proxy playback." : "The direct stream URL was restored and proxy mode was disabled.");
            return new SetRadioStationProxyResponse
            {
                Success = true,
                DiagnosticOperationId = diagnosticOperationId,
                UseProxy = request.UseProxy,
                FilePath = path,
                LibraryScanTargetFound = targetFound,
                LibraryScanItemId = scanTarget?.ItemId ?? string.Empty,
                LibraryScanLibraryName = scanTarget?.LibraryName ?? string.Empty,
                LibraryScanLocation = scanTarget?.Location ?? string.Empty,
                LibraryScanMessage = scanMessage
            };
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            if (request.UseProxy) { try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { } }
            return new SetRadioStationProxyResponse { Error = "Could not update the station stream file (" + ex.GetType().Name + "). No successful change was confirmed." };
        }
    }
}
