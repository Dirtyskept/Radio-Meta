using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

/// <summary>
/// Server-side health data for the on-demand Radio Meta diagnostics panel.
/// It intentionally excludes stream URLs, credentials, filesystem paths,
/// session IDs, and device IDs so the report can be shared safely.
/// </summary>
public sealed class RadioMetaDiagnosticsHealthResponse
{
    public bool Available { get; set; }
    public string Error { get; set; } = string.Empty;
    public string PluginVersion { get; set; } = string.Empty;
    public bool ConfigurationAvailable { get; set; }
    public bool HistoryStoreInitialized { get; set; }
    public bool DisplayHelperResourceEmbedded { get; set; }
    public string DisplayHelperResourceName { get; set; } = string.Empty;

    public bool AutomaticStationDetection { get; set; }
    public bool StationFolderConfigured { get; set; }
    public bool StationFolderAvailable { get; set; }
    public bool FfprobeConfigured { get; set; }
    public bool FfprobeAvailable { get; set; }

    public bool CurrentlyPlayingStationDetected { get; set; }
    public string CurrentlyPlayingStationName { get; set; } = string.Empty;
    public string CurrentlyPlayingStationSource { get; set; } = string.Empty;
    public bool CurrentlyPlayingStreamUrlAvailable { get; set; }
    public DateTimeOffset? CurrentlyPlayingStationSeenAtUtc { get; set; }

    public int CurrentTrackCount { get; set; }
    public int DirectIcyMaxIntervals { get; set; }
    public List<RadioMetaDiagnosticTrackDto> CurrentTracks { get; set; } = new();
    public List<RadioMetaDiagnosticEventDto> RecentEvents { get; set; } = new();
    public RadioMetaPerformanceSnapshotDto Performance { get; set; } = new();
}

public sealed class RadioMetaDiagnosticTrackDto
{
    public string StationName { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

[Route("/RadioMeta/Diagnostics/Health", "GET")]
public sealed class GetRadioMetaDiagnosticsHealth : IReturn<RadioMetaDiagnosticsHealthResponse>
{
}

/// <summary>
/// Authenticated, read-only diagnostics endpoint. Do not add raw URLs, tokens,
/// filesystem paths, session IDs, or device IDs to this response.
/// </summary>
[Authenticated]
public sealed class RadioMetaDiagnosticsHealthService : IService
{
    private const string DisplayHelperResource = "RadioMeta.WebUI.RadioMetaIntegratedDisplay.js";

    public object Get(GetRadioMetaDiagnosticsHealth request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioMetaDiagnosticsHealthResponse
            {
                Available = false,
                Error = "Radio Meta is not initialized."
            };
        }

        var assembly = plugin.GetType().Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "unknown";
        var resourceNames = assembly.GetManifestResourceNames();
        var configuredFolder = plugin.GetConfiguredStationFolder();
        var resolvedFolder = plugin.GetStationFolder();
        var automatic = configuredFolder.Length == 0;
        var stationFolderAvailable = !automatic &&
            !string.IsNullOrWhiteSpace(resolvedFolder) &&
            Directory.Exists(resolvedFolder);
        var ffprobePath = plugin.GetFfprobePath();

        var allTracks = plugin.CurrentTrackService.GetCurrentTracks()
            .OrderByDescending(track => track.UpdatedAt)
            .ToArray();
        var tracks = allTracks
            .Take(10)
            .Select(track => new RadioMetaDiagnosticTrackDto
            {
                StationName = track.StationName,
                SongTitle = track.SongTitle,
                Artist = track.Artist,
                Source = track.Source,
                UpdatedAt = track.UpdatedAt
            })
            .ToList();

        var station = plugin.CurrentlyPlayingStationStore.GetMostRecent();

        return new RadioMetaDiagnosticsHealthResponse
        {
            Available = true,
            PluginVersion = version,
            ConfigurationAvailable = plugin.Configuration is not null,
            HistoryStoreInitialized = plugin.HistoryStore is not null,
            DisplayHelperResourceEmbedded = resourceNames.Contains(DisplayHelperResource, StringComparer.Ordinal),
            DisplayHelperResourceName = DisplayHelperResource,
            AutomaticStationDetection = automatic,
            StationFolderConfigured = !automatic,
            StationFolderAvailable = stationFolderAvailable,
            FfprobeConfigured = !string.IsNullOrWhiteSpace(plugin.Configuration?.FfprobePath),
            FfprobeAvailable = !string.IsNullOrWhiteSpace(ffprobePath) && File.Exists(ffprobePath),
            CurrentlyPlayingStationDetected = station is not null,
            CurrentlyPlayingStationName = station?.Name ?? string.Empty,
            CurrentlyPlayingStationSource = station?.Source ?? string.Empty,
            CurrentlyPlayingStreamUrlAvailable = !string.IsNullOrWhiteSpace(station?.StreamUrl),
            CurrentlyPlayingStationSeenAtUtc = station?.SeenAtUtc,
            CurrentTrackCount = allTracks.Length,
            DirectIcyMaxIntervals = RadioMetadataReader.DirectIcyMaxIntervals,
            CurrentTracks = tracks,
            RecentEvents = plugin.DiagnosticEvents.GetRecent(40),
            Performance = plugin.PerformanceMetrics.GetSnapshot()
        };
    }
}
