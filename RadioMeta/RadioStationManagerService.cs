using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Services;

namespace RadioMeta;

public sealed class RadioStationScanResponse
{
    public string Error { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string ConfiguredFolder { get; set; } = string.Empty;
    public int DirectoriesExamined { get; set; }
    public int DirectoryErrors { get; set; }
    public int ScannedFiles { get; set; }
    public List<RadioDiscoveredStationDto> Stations { get; set; } = new();
}

public sealed class RadioDiscoveredStationDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public string ArtworkPath { get; set; } = string.Empty;
    public string ArtworkDataUri { get; set; } = string.Empty;
    public string ArtworkStatus { get; set; } = string.Empty;
    public string StreamStatus { get; set; } = string.Empty;
}

[Route("/RadioMeta/Stations/Scan", "POST")]
public sealed class ScanRadioStations : IReturn<RadioStationScanResponse>
{
    public bool IncludeSubfolders { get; set; }
}


public sealed class CreateRadioStationResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ArtworkPath { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool LibraryScanTargetFound { get; set; }
    public string LibraryScanItemId { get; set; } = string.Empty;
    public string LibraryScanLibraryName { get; set; } = string.Empty;
    public string LibraryScanLocation { get; set; } = string.Empty;
    public string LibraryScanMessage { get; set; } = string.Empty;
}

[Route("/RadioMeta/Stations/Create", "POST")]
public sealed class CreateRadioStation : IReturn<CreateRadioStationResponse>
{
    public string Name { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public string ArtworkUrl { get; set; } = string.Empty;
}

public sealed class EditRadioStationResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

[Route("/RadioMeta/Stations/Edit", "POST")]
public sealed class EditRadioStation : IReturn<EditRadioStationResponse>
{
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public string ArtworkUrl { get; set; } = string.Empty;
}

public sealed class DeleteRadioStationResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ArtworkPath { get; set; } = string.Empty;
    public bool LibraryScanTargetFound { get; set; }
    public string LibraryScanItemId { get; set; } = string.Empty;
    public string LibraryScanLibraryName { get; set; } = string.Empty;
    public string LibraryScanLocation { get; set; } = string.Empty;
    public string LibraryScanMessage { get; set; } = string.Empty;
}

[Route("/RadioMeta/Stations/Delete", "POST")]
public sealed class DeleteRadioStation : IReturn<DeleteRadioStationResponse>
{
    public string FilePath { get; set; } = string.Empty;
}

[Authenticated]
public sealed class RadioStationManagerService : IService
{
    private readonly ILibraryManager _libraryManager;

    // Emby resolves service constructor dependencies from its native service container.
    public RadioStationManagerService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    private sealed record LibraryScanTarget(string ItemId, string LibraryName, string Location);

    private bool TryResolveLibraryScanTarget(string stationFolder, out LibraryScanTarget? target, out string message)
    {
        target = null;
        string normalizedStationFolder;
        try
        {
            normalizedStationFolder = NormalizeFolderPath(stationFolder);
        }
        catch (Exception ex)
        {
            message = "Could not normalize the configured Station Folder for a targeted Emby scan: " + ex.Message;
            return false;
        }

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var matches = new List<(string ItemId, string LibraryName, string Location, int LocationLength)>();

        try
        {
            foreach (var library in _libraryManager.GetVirtualFolders())
            {
                var itemId = !string.IsNullOrWhiteSpace(library.ItemId) ? library.ItemId : library.Id;
                if (string.IsNullOrWhiteSpace(itemId) || library.Locations is null)
                    continue;

                foreach (var rawLocation in library.Locations)
                {
                    if (string.IsNullOrWhiteSpace(rawLocation))
                        continue;

                    string normalizedLocation;
                    try
                    {
                        normalizedLocation = NormalizeFolderPath(rawLocation);
                    }
                    catch
                    {
                        continue;
                    }

                    // Match only a library root that contains the .strm destination.
                    // If a library path is below the destination, it cannot index the
                    // .strm files in the destination itself.
                    if (IsSameOrDescendantPath(normalizedStationFolder, normalizedLocation, pathComparison))
                    {
                        matches.Add((itemId, library.Name ?? "Emby library", normalizedLocation, normalizedLocation.Length));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            message = "Could not inspect Emby's library locations: " + ex.Message;
            return false;
        }

        if (matches.Count == 0)
        {
            message = "No Emby library location contains the configured Station Folder (" + normalizedStationFolder + "). No full-library scan was started; add this folder to an Emby library or scan the library manually.";
            return false;
        }

        var longestLocationLength = matches.Max(match => match.LocationLength);
        var bestMatches = matches
            .Where(match => match.LocationLength == longestLocationLength)
            .GroupBy(match => match.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (bestMatches.Count != 1)
        {
            var names = string.Join(", ", bestMatches.Select(match => match.LibraryName).Distinct(StringComparer.OrdinalIgnoreCase));
            message = "The Station Folder matches more than one Emby library at the same path specificity (" + names + "). No scan was started; make sure the station folder is assigned to one Emby library.";
            return false;
        }

        var match = bestMatches[0];
        target = new LibraryScanTarget(match.ItemId, match.LibraryName, match.Location);
        message = "Resolved targeted scan to Emby library '" + match.LibraryName + "' at " + match.Location + ".";
        return true;
    }

    private static string NormalizeFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A folder path is required.", nameof(path));

        var candidate = path.Trim();

        // Emby's virtual-folder API may return Windows paths using the extended
        // device-path form (for example, \\?\J:\Music\Radio\Stations), while
        // Radio Meta's setting is usually entered as J:\Music\Radio\Stations.
        // Strip the equivalent prefixes before normalization so both represent the
        // same path. Convert extended UNC forms back to ordinary UNC syntax first.
        if (OperatingSystem.IsWindows())
        {
            const string extendedUncPrefix = @"\\?\UNC\";
            const string extendedPrefix = @"\\?\";
            const string ntUncPrefix = @"\??\UNC\";
            const string ntPrefix = @"\??\";

            if (candidate.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
                candidate = @"\\" + candidate.Substring(extendedUncPrefix.Length);
            else if (candidate.StartsWith(ntUncPrefix, StringComparison.OrdinalIgnoreCase))
                candidate = @"\\" + candidate.Substring(ntUncPrefix.Length);
            else if (candidate.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
                candidate = candidate.Substring(extendedPrefix.Length);
            else if (candidate.StartsWith(ntPrefix, StringComparison.OrdinalIgnoreCase))
                candidate = candidate.Substring(ntPrefix.Length);
        }

        var fullPath = Path.GetFullPath(candidate);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(pathRoot) && string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
            return fullPath;
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSameOrDescendantPath(string path, string parent, StringComparison comparison)
    {
        if (string.Equals(path, parent, comparison))
            return true;

        var prefix = parent;
        if (!prefix.EndsWith(Path.DirectorySeparatorChar) && !prefix.EndsWith(Path.AltDirectorySeparatorChar))
            prefix += Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, comparison);
    }

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
        if (!File.Exists(path)) return new DeleteRadioStationResponse { Error = "The station file no longer exists. Scan the folder again." };
        var baseName = Path.GetFileNameWithoutExtension(path);
        var directory = Path.GetDirectoryName(path)!;
        var logo = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase));
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new DeleteRadioStationResponse { Error = "Could not delete the station file: " + ex.Message };
        }

        // The station file is gone. A logo-delete problem must not suppress the
        // library scan, because Emby still needs to reconcile the missing .strm.
        var warning = string.Empty;
        var artworkDeleted = string.Empty;
        if (logo != null && File.Exists(logo))
        {
            try
            {
                File.Delete(logo);
                artworkDeleted = logo;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                warning = "The station file was deleted, but its matching logo could not be deleted: " + ex.Message;
            }
        }

        var targetFound = TryResolveLibraryScanTarget(folder, out var scanTarget, out var scanMessage);

        return new DeleteRadioStationResponse
        {
            Success = true,
            Warning = warning,
            FilePath = path,
            ArtworkPath = artworkDeleted,
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
        if (artworkUrl.Length > 0 && (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artworkUriCheck) || (artworkUriCheck.Scheme != Uri.UriSchemeHttp && artworkUriCheck.Scheme != Uri.UriSchemeHttps)))
            return new EditRadioStationResponse { Error = "Enter a valid http:// or https:// logo URL, or leave it blank to keep the current logo." };
        if (name.Length == 0 || name.Length > 160 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') || name == "." || name == "..")
            return new EditRadioStationResponse { Error = "Enter a valid station title." };
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "icy"))
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
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
            return new EditRadioStationResponse { Error = "A station with that title already exists. No files were changed." };

        string downloadedArtworkPath = string.Empty;
        string downloadedArtworkExtension = string.Empty;
        if (artworkUrl.Length > 0)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                using var response = client.GetAsync(artworkUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                downloadedArtworkExtension = mediaType switch
                {
                    "image/jpeg" or "image/jpg" => ".jpg",
                    "image/png" => ".png",
                    _ => Path.GetExtension(new Uri(artworkUrl).AbsolutePath).ToLowerInvariant() is ".jpg" or ".jpeg" ? ".jpg" :
                         Path.GetExtension(new Uri(artworkUrl).AbsolutePath).ToLowerInvariant() == ".png" ? ".png" : string.Empty
                };
                if (downloadedArtworkExtension.Length == 0) return new EditRadioStationResponse { Error = "The logo URL must point to a JPG or PNG image." };
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > 5 * 1024 * 1024)
                    return new EditRadioStationResponse { Error = "The logo image is larger than the 5 MB limit." };
                using var input = response.Content.ReadAsStream();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920]; int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0) { if (buffer.Length + read > 5 * 1024 * 1024) return new EditRadioStationResponse { Error = "The logo image is larger than the 5 MB limit." }; buffer.Write(chunk, 0, read); }
                var bytes = buffer.ToArray();
                var isJpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
                var isPng = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
                if (!(downloadedArtworkExtension == ".jpg" && isJpeg) && !(downloadedArtworkExtension == ".png" && isPng)) return new EditRadioStationResponse { Error = "The logo response was not a valid JPG or PNG image." };
                downloadedArtworkPath = Path.Combine(Path.GetDirectoryName(oldPath)!, "." + Guid.NewGuid().ToString("N") + downloadedArtworkExtension);
                using var output = new FileStream(downloadedArtworkPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is UnauthorizedAccessException || ex is UriFormatException)
            { return new EditRadioStationResponse { Error = "Could not download the station logo: " + ex.Message }; }
        }

        var oldBase = Path.GetFileNameWithoutExtension(oldPath);
        var newBase = Path.GetFileNameWithoutExtension(newPath);
        var directory = Path.GetDirectoryName(oldPath)!;
        var artwork = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(f), oldBase, StringComparison.OrdinalIgnoreCase));
        var targetArtwork = artwork is null ? null : Path.Combine(directory, newBase + Path.GetExtension(artwork));
        if (artwork is not null && targetArtwork is not null && !string.Equals(artwork, targetArtwork, StringComparison.OrdinalIgnoreCase) && File.Exists(targetArtwork))
            return new EditRadioStationResponse { Error = "A logo file already exists for the new title. No files were changed." };

        var tempPath = oldPath + ".edit-tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false))) { writer.WriteLine(streamUrl); }
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) File.Move(oldPath, newPath);
            try { File.Replace(tempPath, newPath, null); }
            catch (PlatformNotSupportedException) { File.Delete(newPath); File.Move(tempPath, newPath); }
            catch (IOException) { File.Delete(newPath); File.Move(tempPath, newPath); }
            if (artwork is not null && targetArtwork is not null && !string.Equals(artwork, targetArtwork, StringComparison.OrdinalIgnoreCase)) File.Move(artwork, targetArtwork);
            if (downloadedArtworkPath.Length > 0)
            {
                var finalArtwork = Path.Combine(directory, newBase + downloadedArtworkExtension);
                if (File.Exists(finalArtwork)) File.Delete(finalArtwork);
                File.Move(downloadedArtworkPath, finalArtwork);
                if (artwork is not null && File.Exists(artwork) && !string.Equals(artwork, finalArtwork, StringComparison.OrdinalIgnoreCase)) File.Delete(artwork);
                if (targetArtwork is not null && File.Exists(targetArtwork) && !string.Equals(targetArtwork, finalArtwork, StringComparison.OrdinalIgnoreCase)) File.Delete(targetArtwork);
            }
            return new EditRadioStationResponse { Success = true, Name = name, FilePath = newPath };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return new EditRadioStationResponse { Error = "Could not save station changes: " + ex.Message + ". Scan the folder to verify the current files." };
        }
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
        if (name.Length == 0)
            return new CreateRadioStationResponse { Error = "Enter a station name." };
        if (name.Length > 160 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\') || name == "." || name == "..")
            return new CreateRadioStationResponse { Error = "The station name contains invalid filename characters or is too long." };
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "icy"))
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
                (artworkUri.Scheme != Uri.UriSchemeHttp && artworkUri.Scheme != Uri.UriSchemeHttps))
                return new CreateRadioStationResponse { Error = "Enter a valid http:// or https:// logo URL, or leave the logo field blank." };

            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                using var response = client.GetAsync(artworkUri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                var extension = mediaType switch
                {
                    "image/jpeg" or "image/jpg" => ".jpg",
                    "image/png" => ".png",
                    _ => Path.GetExtension(artworkUri.AbsolutePath).ToLowerInvariant() is ".jpg" or ".jpeg" ? ".jpg" :
                         Path.GetExtension(artworkUri.AbsolutePath).ToLowerInvariant() == ".png" ? ".png" : string.Empty
                };
                if (extension.Length == 0)
                    return new CreateRadioStationResponse { Error = "The logo URL must point to a JPG or PNG image." };
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > 5 * 1024 * 1024)
                    return new CreateRadioStationResponse { Error = "The logo image is larger than the 5 MB limit." };

                using var input = response.Content.ReadAsStream();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > 5 * 1024 * 1024)
                        return new CreateRadioStationResponse { Error = "The logo image is larger than the 5 MB limit." };
                    buffer.Write(chunk, 0, read);
                }
                var bytes = buffer.ToArray();
                var looksLikeJpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
                var looksLikePng = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
                if (!(extension == ".jpg" && looksLikeJpeg) && !(extension == ".png" && looksLikePng))
                    return new CreateRadioStationResponse { Error = "The logo response was not a valid JPG or PNG image." };
                artworkPath = Path.Combine(root, name + extension);
                if (File.Exists(artworkPath))
                    return new CreateRadioStationResponse { Error = "A logo file already exists at " + artworkPath + ". No existing file was overwritten." };
                using var imageFile = new FileStream(artworkPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                imageFile.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return new CreateRadioStationResponse { Error = "Could not download or save the station logo: " + ex.Message };
            }
        }

        try
        {
            // Close the file before asking Emby to scan so the scanner sees a complete .strm.
            // CreateNew prevents overwriting an existing station if another process races this check.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.WriteLine(streamUrl);
                writer.Flush();
            }

            var targetFound = TryResolveLibraryScanTarget(folder, out var scanTarget, out var scanMessage);
            return new CreateRadioStationResponse
            {
                Success = true,
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
            return new CreateRadioStationResponse { Error = "Could not create the station file: " + ex.Message };
        }
        catch (UnauthorizedAccessException)
        {
            if (artworkPath.Length > 0) { try { File.Delete(artworkPath); } catch { } }
            return new CreateRadioStationResponse { Error = "The server does not have permission to create files in the configured Station Folder." };
        }
    }

    public object Post(ScanRadioStations request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
            return new RadioStationScanResponse { Error = "Radio Meta is not initialized." };

        var configured = plugin.GetConfiguredStationFolder();
        var folder = plugin.GetStationFolder();
        if (configured.Length == 0)
            return new RadioStationScanResponse { ConfiguredFolder = configured, Error = "Station Folder is blank. Set it in Station Folder and FFprobe Paths before scanning." };
        if (folder.Length == 0 || !Directory.Exists(folder))
            return new RadioStationScanResponse { ConfiguredFolder = configured, Folder = folder, Error = "The configured Station Folder does not exist or is unavailable. Resolved path: " + folder };

        string root;
        try { root = Path.GetFullPath(folder); }
        catch { return new RadioStationScanResponse { Error = "The configured Station Folder is not a valid path." }; }

        var response = new RadioStationScanResponse { Folder = root, ConfiguredFolder = configured };
        var pending = new Stack<string>();
        pending.Push(root);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            response.DirectoriesExamined++;
            try { files = Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch { response.DirectoryErrors++; continue; }

            foreach (var file in files)
            {
                if (!string.Equals(Path.GetExtension(file), ".strm", StringComparison.OrdinalIgnoreCase))
                    continue;
                string fullPath;
                try { fullPath = Path.GetFullPath(file); } catch { continue; }
                var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !seenFiles.Add(fullPath))
                    continue;

                response.ScannedFiles++;
                var name = Path.GetFileNameWithoutExtension(fullPath);
                if (string.IsNullOrWhiteSpace(name) || !seenStations.Add(fullPath))
                    continue;

                var station = new RadioDiscoveredStationDto
                {
                    Id = fullPath,
                    Name = name,
                    FilePath = fullPath,
                    StreamStatus = "Unreadable",
                    ArtworkStatus = "Missing"
                };

                try
                {
                    using var reader = new StreamReader(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                    while (!reader.EndOfStream)
                    {
                        var line = (reader.ReadLine() ?? string.Empty).Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                            continue;
                        station.StreamUrl = line;
                        station.StreamStatus = Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "icy")
                            ? "Readable"
                            : "Read; URL format unverified";
                        break;
                    }
                    if (station.StreamUrl.Length == 0)
                        station.StreamStatus = "No stream URL found";
                }
                catch (Exception ex)
                {
                    station.StreamStatus = "Unreadable (" + ex.GetType().Name + ")";
                }

                try
                {
                    var directory = Path.GetDirectoryName(fullPath) ?? root;
                    var basename = Path.GetFileNameWithoutExtension(fullPath);
                    var artwork = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                        .Where(candidate =>
                        {
                            var ext = Path.GetExtension(candidate);
                            return string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase);
                        })
                        .FirstOrDefault(candidate => string.Equals(Path.GetFileNameWithoutExtension(candidate), basename, StringComparison.OrdinalIgnoreCase));
                    if (artwork is not null)
                    {
                        station.ArtworkPath = artwork;
                        station.ArtworkStatus = "Found";
                        // Embed the existing local artwork for the dashboard; never write to the station folder.
                        try
                        {
                            var extension = Path.GetExtension(artwork);
                            var mime = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                            var bytes = File.ReadAllBytes(artwork);
                            if (bytes.Length <= 5 * 1024 * 1024)
                                station.ArtworkDataUri = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
                        }
                        catch { /* Keep scan successful if artwork cannot be read. */ }
                    }
                }
                catch { station.ArtworkStatus = "Could not check"; }

                response.Stations.Add(station);
            }

            if (request.IncludeSubfolders)
            {
                IEnumerable<string> directories;
                try { directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).ToArray(); }
                catch { response.DirectoryErrors++; continue; }
                foreach (var directory in directories)
                {
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        var fullDirectory = Path.GetFullPath(directory);
                        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
                        if (fullDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            pending.Push(fullDirectory);
                    }
                    catch { }
                }
            }
        }

        response.Stations = response.Stations
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return response;
    }
}
