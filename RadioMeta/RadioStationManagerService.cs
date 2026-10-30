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
    public string EmbyItemId { get; set; } = string.Empty;
    public string ArtworkStatus { get; set; } = string.Empty;
    public string StreamStatus { get; set; } = string.Empty;
    public bool UseProxy { get; set; }
}

[Route("/RadioMeta/Stations/Scan", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class ScanRadioStations : IReturn<RadioStationScanResponse>
{
    public bool IncludeSubfolders { get; set; }
}


public sealed class CreateRadioStationResponse
{
    public bool Success { get; set; }
    public string DiagnosticOperationId { get; set; } = string.Empty;
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
[Authenticated(Roles = "Admin")]
public sealed class CreateRadioStation : IReturn<CreateRadioStationResponse>
{
    public string Name { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public string ArtworkUrl { get; set; } = string.Empty;
    public bool UseProxy { get; set; }
}

public sealed class EditRadioStationResponse
{
    public bool Success { get; set; }
    public string DiagnosticOperationId { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool LibraryScanTargetFound { get; set; }
    public string LibraryScanItemId { get; set; } = string.Empty;
    public string LibraryScanLibraryName { get; set; } = string.Empty;
    public string LibraryScanLocation { get; set; } = string.Empty;
    public string LibraryScanMessage { get; set; } = string.Empty;
}

[Route("/RadioMeta/Stations/Edit", "POST")]
[Authenticated(Roles = "Admin")]
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
    public string DiagnosticOperationId { get; set; } = string.Empty;
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
[Authenticated(Roles = "Admin")]
public sealed class DeleteRadioStation : IReturn<DeleteRadioStationResponse>
{
    public string FilePath { get; set; } = string.Empty;
}

[Authenticated(Roles = "Admin")]
public sealed partial class RadioStationManagerService : IService
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


}
