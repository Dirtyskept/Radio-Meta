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
        const long MaxArtworkBytesPerScan = 10L * 1024 * 1024;
        long artworkBytesIncluded = 0;

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

                // Resolve the .strm file to its indexed Emby library item so the
                // station artwork can open the real item detail page. Failure to
                // resolve is non-fatal: the logo remains visible but unlinked.
                try
                {
                    var embyItem = _libraryManager.FindByPath(fullPath, false);
                    if (embyItem is not null)
                        station.EmbyItemId = embyItem.Id.ToString("N");
                }
                catch
                {
                    station.EmbyItemId = string.Empty;
                }

                var streamFile = RadioStationFileStore.ReadStationStreamFile(fullPath);
                station.StreamUrl = streamFile.StreamUrl;
                station.StreamStatus = streamFile.Status;
                station.UseProxy = RadioMetaProxyService.TryGetUpstreamUrl(fullPath, out var proxyUpstream);
                // Show the editable upstream URL in the dashboard, not the internal loopback relay URL.
                if (station.UseProxy) station.StreamUrl = proxyUpstream;

                try
                {
                    var artwork = RadioStationFileStore.FindMatchingArtwork(fullPath);
                    if (artwork is not null)
                    {
                        station.ArtworkPath = artwork;
                        station.ArtworkStatus = "Found";
                        // Embed existing local artwork for the dashboard; the file store never writes here during a scan.
                        station.ArtworkDataUri = RadioStationFileStore.ReadArtworkDataUri(
                            artwork, MaxArtworkBytesPerScan - artworkBytesIncluded, out var includedBytes);
                        artworkBytesIncluded += includedBytes;
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
