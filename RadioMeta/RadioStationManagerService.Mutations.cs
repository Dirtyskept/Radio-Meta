using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Services;

namespace RadioMeta;

public sealed partial class RadioStationManagerService
{
    public object Post(DeleteRadioStation request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null) return new DeleteRadioStationResponse { Error = "Radio Meta is not initialized." };
        var folder = plugin.GetStationFolder();
        if (folder.Length == 0 || !Directory.Exists(folder))
            return new DeleteRadioStationResponse { Error = "The configured Station Folder is unavailable." };
        string root, path;
        try
        {
            root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            path = Path.GetFullPath((request.FilePath ?? string.Empty).Trim());
        }
        catch { return new DeleteRadioStationResponse { Error = "The station path is invalid." }; }
        var prefix = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            return new DeleteRadioStationResponse { Error = "The selected station is not inside the configured Station Folder." };
        var deletion = RadioStationFileStore.DeleteStationAndArtwork(path);
        if (!deletion.Success)
            return new DeleteRadioStationResponse { Error = deletion.Error };
        var proxySidecarStatus = "not present";
        try
        {
            var proxySidecar = RadioMetaProxyService.SidecarPath(path);
            if (File.Exists(proxySidecar)) File.Delete(proxySidecar);
            proxySidecarStatus = File.Exists(proxySidecar) ? "cleanup incomplete" : "removed or not present";
        }
        catch
        {
            proxySidecarStatus = "cleanup failed";
        }

        var sidecarWarning = proxySidecarStatus == "cleanup failed" || proxySidecarStatus == "cleanup incomplete"
            ? "The station file was removed, but its proxy sidecar could not be confirmed as removed. Check the station folder before re-creating this station."
            : string.Empty;
        var combinedWarning = string.Join(" ", new[] { deletion.Warning, sidecarWarning }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var targetFound = TryResolveLibraryScanTarget(folder, out var scanTarget, out var scanMessage);
        var diagnosticOperationId = plugin.StationOperationEvents.RecordFileOperation(
            "Delete station", Path.GetFileNameWithoutExtension(path), "success", proxySidecarStatus, targetFound,
            proxySidecarStatus == "cleanup failed" || proxySidecarStatus == "cleanup incomplete"
                ? "The station file was removed; proxy sidecar cleanup needs attention. Station-specific plugin configuration was retained."
                : "The station file, matching artwork, and proxy sidecar were removed; station-specific plugin configuration was retained.");

        return new DeleteRadioStationResponse
        {
            Success = true,
            DiagnosticOperationId = diagnosticOperationId,
            Warning = combinedWarning,
            FilePath = path,
            ArtworkPath = deletion.ArtworkDeleted,
            LibraryScanTargetFound = targetFound,
            LibraryScanItemId = scanTarget?.ItemId ?? string.Empty,
            LibraryScanLibraryName = scanTarget?.LibraryName ?? string.Empty,
            LibraryScanLocation = scanTarget?.Location ?? string.Empty,
            LibraryScanMessage = scanMessage
        };
    }

    public object Post(EditRadioStation request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null) return new EditRadioStationResponse { Error = "Radio Meta is not initialized." };
        var folder = plugin.GetStationFolder();
        if (folder.Length == 0 || !Directory.Exists(folder))
            return new EditRadioStationResponse { Error = "The configured Station Folder is unavailable." };

        var oldPathInput = (request.FilePath ?? string.Empty).Trim();
        var name = (request.Name ?? string.Empty).Trim();
        var streamUrl = (request.StreamUrl ?? string.Empty).Trim();
        var artworkUrl = (request.ArtworkUrl ?? string.Empty).Trim();
        if (streamUrl.Length > 8192) return new EditRadioStationResponse { Error = "The stream URL is longer than the 8,192-character limit." };
        if (artworkUrl.Length > 8192) return new EditRadioStationResponse { Error = "The logo URL is longer than the 8,192-character limit." };
        if (artworkUrl.Length > 0 && (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artworkUriCheck) || !RadioMetaUriSchemeHelpers.IsHttpOrHttps(artworkUriCheck)))
            return new EditRadioStationResponse { Error = "Enter a valid http:// or https:// logo URL, or leave it blank to keep the current logo." };
        if (!IsValidStationFileName(name))
            return new EditRadioStationResponse { Error = "Enter a valid station title." };
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) || !RadioMetaUriSchemeHelpers.IsHttpHttpsOrIcy(uri))
            return new EditRadioStationResponse { Error = "Enter a valid http://, https://, or icy:// stream URL." };

        string root, oldPath, newPath;
        try
        {
            root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            oldPath = Path.GetFullPath(oldPathInput);
            newPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(oldPath) ?? root, name + ".strm"));
        }
        catch { return new EditRadioStationResponse { Error = "The station path or title is invalid." }; }
        var prefix = root + Path.DirectorySeparatorChar;
        if (!oldPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !oldPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            return new EditRadioStationResponse { Error = "The selected station is not inside the configured Station Folder." };
        if (!File.Exists(oldPath)) return new EditRadioStationResponse { Error = "The station file no longer exists. Scan the folder again." };
        var proxyEnabled = RadioMetaProxyService.TryGetProxyDetails(oldPath, out _, out _);
        var proxySidecarPath = RadioMetaProxyService.SidecarPath(oldPath);
        // A malformed sidecar is not safe to overwrite: fail clearly rather than silently converting the station to direct playback.
        if (File.Exists(proxySidecarPath) && !proxyEnabled)
            return new EditRadioStationResponse { Error = "The proxy settings file is unreadable. No station files were changed; inspect the .radiometa-proxy.json file before editing." };
        // The dashboard intentionally locks these fields while proxy is enabled. Enforce that
        // rule at the API too; partial edits across .strm, artwork, and sidecar are not safe.
        if (proxyEnabled)
            return new EditRadioStationResponse { Error = "Disable the proxy before editing this station. No files were changed." };
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
            return new EditRadioStationResponse { Error = "A station with that title already exists. No files were changed." };

        string downloadedArtworkPath = string.Empty;
        string downloadedArtworkExtension = string.Empty;
        if (artworkUrl.Length > 0)
        {
            var downloaded = RadioStationArtworkDownloader.Download(artworkUrl);
            if (!downloaded.Success) return new EditRadioStationResponse { Error = downloaded.Error };
            downloadedArtworkExtension = downloaded.Extension;
            downloadedArtworkPath = Path.Combine(Path.GetDirectoryName(oldPath)!, "." + Guid.NewGuid().ToString("N") + downloadedArtworkExtension);
            try { RadioStationFileStore.WriteArtworkFile(downloadedArtworkPath, downloaded.Bytes, FileShare.None); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { return new EditRadioStationResponse { Error = "The station logo was downloaded but could not be staged safely (" + ex.GetType().Name + "). No station files were changed." }; }
        }

        var directory = Path.GetDirectoryName(oldPath)!;
        var artwork = RadioStationFileStore.FindMatchingArtwork(oldPath);
        var newBase = Path.GetFileNameWithoutExtension(newPath);
        var targetArtwork = artwork is null ? null : Path.Combine(directory, newBase + Path.GetExtension(artwork));
        if (artwork is not null && targetArtwork is not null && !string.Equals(artwork, targetArtwork, StringComparison.OrdinalIgnoreCase) && File.Exists(targetArtwork))
        {
            try { if (downloadedArtworkPath.Length > 0 && File.Exists(downloadedArtworkPath)) File.Delete(downloadedArtworkPath); } catch { }
            return new EditRadioStationResponse { Error = "A logo file already exists for the new title. No files were changed." };
        }

        var streamUrlToWrite = streamUrl;
        if (!RadioStationFileStore.TryCommitEditedStationFiles(
                oldPath, newPath, streamUrlToWrite, artwork, targetArtwork,
                downloadedArtworkPath, downloadedArtworkExtension, out var editError))
        {
            try { if (downloadedArtworkPath.Length > 0 && File.Exists(downloadedArtworkPath)) File.Delete(downloadedArtworkPath); } catch { }
            return new EditRadioStationResponse { Error = "Could not save station changes: " + editError + ". Scan the folder to verify the current files." };
        }
        var targetFound = TryResolveLibraryScanTarget(folder, out var scanTarget, out var scanMessage);
        var diagnosticOperationId = plugin.StationOperationEvents.RecordFileOperation(
            "Edit/rename station", name, "success", "not used: proxy was disabled for editing", targetFound,
            "The station stream file and applicable artwork update completed.");
        return new EditRadioStationResponse
        {
            Success = true,
            DiagnosticOperationId = diagnosticOperationId,
            Name = name,
            FilePath = newPath,
            LibraryScanTargetFound = targetFound,
            LibraryScanItemId = scanTarget?.ItemId ?? string.Empty,
            LibraryScanLibraryName = scanTarget?.LibraryName ?? string.Empty,
            LibraryScanLocation = scanTarget?.Location ?? string.Empty,
            LibraryScanMessage = scanMessage
        };
    }

    public object Post(CreateRadioStation request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
            return new CreateRadioStationResponse { Error = "Radio Meta is not initialized." };

        var configured = plugin.GetConfiguredStationFolder();
        var folder = plugin.GetStationFolder();
        if (configured.Length == 0 || folder.Length == 0)
            return new CreateRadioStationResponse { Error = "Set a Station Folder in the plugin settings before creating a station." };
        if (!Directory.Exists(folder))
            return new CreateRadioStationResponse { Error = "The configured Station Folder does not exist or is unavailable: " + folder };

        var name = (request.Name ?? string.Empty).Trim();
        var streamUrl = (request.StreamUrl ?? string.Empty).Trim();
        var artworkUrl = (request.ArtworkUrl ?? string.Empty).Trim();
        if (streamUrl.Length > 8192) return new CreateRadioStationResponse { Error = "The stream URL is longer than the 8,192-character limit." };
        if (artworkUrl.Length > 8192) return new CreateRadioStationResponse { Error = "The logo URL is longer than the 8,192-character limit." };
        if (name.Length == 0)
            return new CreateRadioStationResponse { Error = "Enter a station name." };
        if (!IsValidStationFileName(name))
            return new CreateRadioStationResponse { Error = "The station name contains invalid filename characters or is too long." };
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) ||
            !RadioMetaUriSchemeHelpers.IsHttpHttpsOrIcy(uri))
            return new CreateRadioStationResponse { Error = "Enter a valid http://, https://, or icy:// stream URL." };

        string root;
        string path;
        try
        {
            root = Path.GetFullPath(folder);
            path = Path.GetFullPath(Path.Combine(root, name + ".strm"));
        }
        catch
        {
            return new CreateRadioStationResponse { Error = "The configured Station Folder or station name is not a valid path." };
        }
        if (!string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                           root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                           StringComparison.OrdinalIgnoreCase))
            return new CreateRadioStationResponse { Error = "The station filename must remain directly inside the configured Station Folder." };
        if (File.Exists(path))
            return new CreateRadioStationResponse { Error = "A station file with that name already exists. No file was overwritten: " + path };

        string artworkPath = string.Empty;
        string warning = string.Empty;
        if (artworkUrl.Length > 0)
        {
            if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artworkUri) ||
                !RadioMetaUriSchemeHelpers.IsHttpOrHttps(artworkUri))
                return new CreateRadioStationResponse { Error = "Enter a valid http:// or https:// logo URL, or leave the logo field blank." };

            var downloaded = RadioStationArtworkDownloader.Download(artworkUrl);
            if (!downloaded.Success) return new CreateRadioStationResponse { Error = downloaded.Error };
            artworkPath = Path.Combine(root, name + downloaded.Extension);
            if (File.Exists(artworkPath))
                return new CreateRadioStationResponse { Error = "A logo file already exists for that station title. No existing file was overwritten." };
            try { RadioStationFileStore.WriteArtworkFile(artworkPath, downloaded.Bytes, FileShare.Read); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new CreateRadioStationResponse { Error = "The downloaded station logo could not be saved (" + ex.GetType().Name + ")." };
            }
        }

        string storedStreamUrl = streamUrl;
        string proxySidecarPath = RadioMetaProxyService.SidecarPath(path);
        string proxyToken = string.Empty;
        if (request.UseProxy)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                try { if (artworkPath.Length > 0 && File.Exists(artworkPath)) File.Delete(artworkPath); } catch { }
                return new CreateRadioStationResponse { Error = "The proxy requires an http:// or https:// source URL." };
            }
            var proxy = RadioMetaEntryPoint.GetProxyBaseUrl();
            if (string.IsNullOrWhiteSpace(proxy))
            {
                try { if (artworkPath.Length > 0 && File.Exists(artworkPath)) File.Delete(artworkPath); } catch { }
                return new CreateRadioStationResponse { Error = "The proxy listener is not running. Check the Emby server log and verify the Station Folder is available." };
            }
            proxyToken = Guid.NewGuid().ToString("N");
            try
            {
                RadioMetaProxyService.WriteProxySidecar(path, streamUrl, proxyToken);
                storedStreamUrl = proxy + "/proxy/" + proxyToken;
            }
            catch (Exception ex)
            {
                // WriteProxySidecar removes only a partial sidecar created by this call. Do not
                // delete a pre-existing orphan/corrupt sidecar when CreateNew itself failed.
                try { if (artworkPath.Length > 0 && File.Exists(artworkPath)) File.Delete(artworkPath); } catch { }
                return new CreateRadioStationResponse { Error = "Could not save proxy station settings (" + ex.GetType().Name + "). No station file was created." };
            }
        }

        try
        {
            RadioStationFileStore.WriteNewStationStreamFile(path, storedStreamUrl);

            var targetFound = TryResolveLibraryScanTarget(folder, out var scanTarget, out var scanMessage);
            var sidecarStatus = !request.UseProxy
                ? "not used: direct station"
                : (RadioMetaProxyService.TryGetProxyDetails(path, out _, out _) ? "present and valid" : "missing or invalid");
            var diagnosticOperationId = plugin.StationOperationEvents.RecordFileOperation(
                "Create station", name, "success", sidecarStatus, targetFound,
                request.UseProxy ? "The proxy-enabled station file was created." : "The direct station file was created.");
            return new CreateRadioStationResponse
            {
                Success = true,
                DiagnosticOperationId = diagnosticOperationId,
                Name = name,
                FilePath = path,
                ArtworkPath = artworkPath,
                Warning = warning,
                LibraryScanTargetFound = targetFound,
                LibraryScanItemId = scanTarget?.ItemId ?? string.Empty,
                LibraryScanLibraryName = scanTarget?.LibraryName ?? string.Empty,
                LibraryScanLocation = scanTarget?.Location ?? string.Empty,
                LibraryScanMessage = scanMessage
            };
        }
        catch (IOException ex)
        {
            if (artworkPath.Length > 0) { try { File.Delete(artworkPath); } catch { } }
            if (request.UseProxy) { try { if (File.Exists(proxySidecarPath)) File.Delete(proxySidecarPath); } catch { } }
            return new CreateRadioStationResponse { Error = "Could not create the station file: " + ex.Message };
        }
        catch (UnauthorizedAccessException)
        {
            if (artworkPath.Length > 0) { try { File.Delete(artworkPath); } catch { } }
            if (request.UseProxy) { try { if (File.Exists(proxySidecarPath)) File.Delete(proxySidecarPath); } catch { } }
            return new CreateRadioStationResponse { Error = "The server does not have permission to create files in the configured Station Folder." };
        }
    }

    // Create and Edit must apply the same filename safety rules to station titles.
    // Keep empty-name messaging in Create's caller; this validator also rejects empty input for Edit.
    private static bool IsValidStationFileName(string name)
    {
        return !string.IsNullOrEmpty(name) &&
               name.Length <= 160 &&
               name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               !name.Contains('/') &&
               !name.Contains('\\') &&
               name != "." &&
               name != "..";
    }

}
