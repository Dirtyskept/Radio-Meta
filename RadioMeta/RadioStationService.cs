
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

public sealed class RadioStationsResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioStationDto> Stations { get; set; } = new();
}

public sealed class RadioStationDto
{
    public string StationName { get; set; } = string.Empty;
    public string StationLine { get; set; } = string.Empty;
    public string ArtistLine { get; set; } = string.Empty;
    public string SongTitleLine { get; set; } = string.Empty;
    public string ArtworkDataUrl { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

[Route("/RadioMeta/Stations", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetRadioStations : IReturn<RadioStationsResponse>
{
}

[Authenticated(Roles = "Admin")]
public sealed class RadioStationService : IService
{
    public object Get(GetRadioStations request)
    {
        var plugin = RadioMetaPlugin.Instance;

        if (plugin is null)
        {
            return new RadioStationsResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        var configuredFolder = plugin.GetConfiguredStationFolder();
        var folder = plugin.GetStationFolder();
        var ffprobePath = plugin.GetFfprobePath();
        var automaticMode = configuredFolder.Length == 0;
        (string StationName, string Path)[] stationFiles;

        if (automaticMode)
        {
            // Without a folder configured, show only stations whose .strm item
            // is currently active. The session service has already verified
            // the active item's media type/path before registering it.
            stationFiles = plugin.CurrentTrackService.GetActiveStationPaths()
                .Where(item => File.Exists(item.Path))
                .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return new RadioStationsResponse
            {
                Error = "The configured Station folder was not found. Correct the path or clear it to enable automatic detection."
            };
        }
        else
        {
            stationFiles = Directory.GetFiles(folder, "*.strm")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => (Path.GetFileNameWithoutExtension(path), path))
                .ToArray();
        }

        var stations = new List<RadioStationDto>();

        long artworkBytesIncluded = 0;
        const long MaxArtworkBytesPerResponse = 10L * 1024 * 1024;
        foreach (var stationFile in stationFiles)
        {
            var stationName = stationFile.StationName;
            var stationPath = stationFile.Path;
            var stationOnly = plugin.CurrentTrackService.IsStationMetadataLookupDisabled(stationName);
            var activeSession = plugin.CurrentTrackService.IsStationActive(stationName);
            var currentTrack = stationOnly
                ? null
                : plugin.CurrentTrackService.GetCurrentTrackForStation(stationName);

            // For an active Emby playback session, show only the canonical
            // accepted track. Do not independently probe a station whose live
            // metadata is unavailable or rejected; that could reintroduce a
            // slogan that the Current Track service has already rejected.
            // For inactive stations, preserve the existing station-list lookup.
            var metadata = stationOnly || activeSession
                ? plugin.MetadataReader.ReadStationInfoOnly(stationPath)
                : plugin.MetadataReader.Read(
                    stationPath,
                    ffprobePath,
                    ignoreMetadataPhrase: plugin.CurrentTrackService.IsMetadataPhraseIgnored);

            var songTitleForDisplay = currentTrack is not null
                ? currentTrack.SongTitle
                : stationOnly || activeSession
                    ? string.Empty
                    : plugin.CurrentTrackService.CleanSongTitle(stationName, metadata.SongTitle);

            var artworkDataUrl = MakeArtworkDataUrl(
                metadata.ArtworkPath, MaxArtworkBytesPerResponse - artworkBytesIncluded, out var includedArtworkBytes);
            artworkBytesIncluded += includedArtworkBytes;
            var artistForDisplay = currentTrack is not null
                ? currentTrack.Artist
                : stationOnly || activeSession
                    ? string.Empty
                    : plugin.CurrentTrackService.CleanSongArtist(stationName, metadata.SongTitle, metadata.Artist);

            stations.Add(new RadioStationDto
            {
                StationName = metadata.StationName,
                StationLine = metadata.StationLine,
                ArtistLine = RadioMetaValueHelpers.LimitText(artistForDisplay, 26),
                SongTitleLine = RadioMetaValueHelpers.LimitText(songTitleForDisplay, 26),
                ArtworkDataUrl = artworkDataUrl,
                Error = metadata.Error
            });
        }

        return new RadioStationsResponse
        {
            Stations = stations,
            Message = automaticMode
                ? "Automatic mode lists currently active audio .strm stations only. Configure a Station folder to enumerate all station files."
                : string.Empty
        };
    }

    private static string MakeArtworkDataUrl(string artworkPath, long remainingBudgetBytes, out long bytesIncluded)
    {
        bytesIncluded = 0;
        if (string.IsNullOrWhiteSpace(artworkPath) || !File.Exists(artworkPath)) return string.Empty;
        try
        {
            var extension = Path.GetExtension(artworkPath).ToLowerInvariant();
            var mimeType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(mimeType)) return string.Empty;

            const long MaxArtworkBytesPerFile = 2L * 1024 * 1024;
            var length = new FileInfo(artworkPath).Length;
            if (length <= 0 || length > MaxArtworkBytesPerFile || length > remainingBudgetBytes) return string.Empty;
            if (!RadioStationFileStore.TryReadCappedFile(artworkPath, Math.Min(MaxArtworkBytesPerFile, remainingBudgetBytes), out var bytes)) return string.Empty;
            bytesIncluded = bytes.LongLength;
            return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return string.Empty; }
    }
}
