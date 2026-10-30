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
    public bool DisplayLoaderResourceEmbedded { get; set; }
    public string DisplayLoaderResourceName { get; set; } = string.Empty;

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

    // Station mode is important diagnostic context: an empty metadata cache is expected
    // when the user has explicitly selected StationOnly (do not look for metadata).
    public string CurrentStationMetadataMode { get; set; } = "Unknown";
    public bool CurrentStationMetadataLookupDisabled { get; set; }
    public string CurrentStationMetadataModeRule { get; set; } = string.Empty;
    public string CurrentStationMetadataModeMatch { get; set; } = string.Empty;
    public string CurrentStationMetadataModeMessage { get; set; } = string.Empty;
    public List<string> CurrentStationMetadataIdentityNames { get; set; } = new();
    public List<string> ConfiguredStationOnlyRuleNames { get; set; } = new();

    public int CurrentTrackCount { get; set; }
    public int LiveMetadataSourceTimeoutSeconds { get; set; }
    public int ManualStreamTestTimeoutSeconds { get; set; }
    public int DirectIcyMaxIntervals { get; set; }
    public bool DirectIcyFallbackEnabled { get; set; }
    public List<RadioMetaDiagnosticTrackDto> CurrentTracks { get; set; } = new();
    public List<RadioMetaDiagnosticEventDto> RecentEvents { get; set; } = new();
    public List<RadioMetaStationOperationEventDto> RecentStationOperations { get; set; } = new();
    public RadioMetaProxyDiagnosticDto Proxy { get; set; } = new();
    public RadioMetaMetadataCacheDiagnosticDto MetadataCache { get; set; } = new();
    public RadioMetaArtworkDiagnosticDto ArtworkDownloads { get; set; } = new();
    public RadioMetaStationArtworkInventoryDto StationArtworkInventory { get; set; } = new();
    public RadioMetaRuntimeComponentDiagnosticDto RuntimeComponents { get; set; } = new();
    public RadioMetaPerformanceSnapshotDto Performance { get; set; } = new();
    public RadioMetaMemoryDiagnosticsSnapshotDto Memory { get; set; } = new();
}

public sealed class RadioMetaDiagnosticTrackDto
{
    public string StationName { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Privacy-safe proxy health. Never add executable paths, upstream URLs, tokens,
/// sidecar paths, session IDs, or arbitrary FFmpeg output to this DTO.
/// </summary>
public sealed class RadioMetaProxyDiagnosticDto
{
    public string ListenerStatus { get; set; } = "unknown";
    public bool ListenerRunning { get; set; }
    public string BindScope { get; set; } = "loopback";
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = RadioMetaProxyService.ProxyPort;
    public bool FfmpegExecutableAvailable { get; set; }
    public int MaxConcurrentClients { get; set; } = RadioMetaProxyService.MaxConcurrentClients;
    public int ActiveClients { get; set; }
    public int MaxConcurrentFfmpegProcesses { get; set; } = RadioMetaProxyService.MaxConcurrentFfmpegProcesses;
    public int ActiveFfmpegProcesses { get; set; }
    public long TotalClientConnectionsSinceStart { get; set; }
    public long FfmpegStartAttemptsSinceStart { get; set; }
    public long FfmpegStartedProcessesSinceStart { get; set; }
    public long SuccessfulAudioResponsesSinceStart { get; set; }
    public long FfmpegStartupFailuresSinceStart { get; set; }
    public bool SidecarScanCompleted { get; set; }
    public string SidecarScanStatus { get; set; } = "not checked";
    public int ProxySidecarCount { get; set; }
    public int EnabledStationCount { get; set; }
    public int InvalidSidecarCount { get; set; }
    public int OrphanSidecarCount { get; set; }
    public int SidecarMismatchCount { get; set; }
}

/// <summary>Counts and retention limits only; cache keys and URLs are never returned.</summary>
public sealed class RadioMetaMetadataCacheDiagnosticDto
{
    public int ProbeCacheEntries { get; set; }
    public int ProbeCacheFreshEntries { get; set; }
    public int ProbeCacheExpiredEntries { get; set; }
    public int ProbeCacheMaximumEntries { get; set; }
    public int ProbeCacheDurationSeconds { get; set; }
    public int NetworkCacheEntries { get; set; }
    public int NetworkCacheFreshEntries { get; set; }
    public int NetworkCacheExpiredEntries { get; set; }
    public int NetworkCacheMaximumEntries { get; set; }
    public int NetworkCacheDurationSeconds { get; set; }
    public int UnsupportedShoutcastEndpointEntries { get; set; }
    public int UnsupportedShoutcastEndpointActiveEntries { get; set; }
    public int UnsupportedShoutcastEndpointExpiredEntries { get; set; }
    public int UnsupportedShoutcastEndpointMaximumEntries { get; set; }
    public int UnsupportedShoutcastEndpointDurationSeconds { get; set; }
    public int PersistentAdaptiveStationRoutingEntries { get; set; }
    public int PersistentEndpointStateEntries { get; set; }
    public int AutomaticallyBlockedEndpointEntries { get; set; }
    public int ManuallyBlockedEndpointEntries { get; set; }
    public int EndpointBackoffEntries { get; set; }
    public int EndpointsReadyForRecoveryProbe { get; set; }
    public int EndpointRecoveryLookupThreshold { get; set; }
    public int EndpointPermanentRecoveryHours { get; set; }
    public int TransientBackoffFirstLookups { get; set; }
    public int TransientBackoffMaximumLookups { get; set; }
    public int RouteRecheckInitialLookups { get; set; }
    public int RouteRecheckMaximumLookups { get; set; }
    public bool RoutingStatePersistenceEnabled { get; set; }
    public bool RoutingStatePersistenceHealthy { get; set; }
    public string RoutingStatePersistenceError { get; set; } = string.Empty;
    public DateTimeOffset? RoutingStateLastPersistedUtc { get; set; }
}

/// <summary>Read-only local artwork inventory; no file names or paths are returned.</summary>
public sealed class RadioMetaStationArtworkInventoryDto
{
    public bool Completed { get; set; }
    public string Status { get; set; } = "not checked";
    public int ScannedStationFiles { get; set; }
    public int StationFilesWithArtwork { get; set; }
    public int StationFilesWithoutArtwork { get; set; }
    public int SafetyLimit { get; set; } = 2048;
}

/// <summary>Read-only state of background event handlers initialized by the plugin entry point.</summary>
public sealed class RadioMetaRuntimeComponentDiagnosticDto
{
    public bool StationCaptureStarted { get; set; }
    public bool SessionMetadataPublisherStarted { get; set; }
    public bool HistoryStoreInitialized { get; set; }
}

[Route("/RadioMeta/Diagnostics/Health", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetRadioMetaDiagnosticsHealth : IReturn<RadioMetaDiagnosticsHealthResponse>
{
}

/// <summary>
/// Authenticated, read-only diagnostics endpoint. Do not add raw URLs, tokens,
/// filesystem paths, session IDs, or device IDs to this response.
/// </summary>
[Authenticated(Roles = "Admin")]
public sealed class RadioMetaDiagnosticsHealthService : IService
{
    private const string DisplayHelperResource = "RadioMeta.WebUI.RadioMetaIntegratedDisplay.js";
    private const string DisplayLoaderResource = "RadioMeta.WebUI.RadioMetaDisplayLoader.js";

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
        var activeStationPath = station is null ? null : plugin.CurrentTrackService.GetActiveStationPath(station.SessionKey);
        var currentStationDeviceName = station is null ? null : plugin.CurrentTrackService.GetActiveDeviceName(station.SessionKey);
        var currentStationName = !string.IsNullOrWhiteSpace(station?.Name)
            ? station!.Name.Trim()
            : (allTracks.FirstOrDefault()?.StationName ?? string.Empty).Trim();
        string? activeStationTitle = null;
        if (!string.IsNullOrWhiteSpace(activeStationPath))
        {
            try
            {
                activeStationTitle = Path.GetFileNameWithoutExtension(activeStationPath);
                if (currentStationName.Length == 0) currentStationName = activeStationTitle;
            }
            catch
            {
                // The station's captured display name remains available if its path cannot be parsed.
            }
        }
        var currentStationIdentities = new List<string?> { currentStationName, activeStationTitle };
        var stationMode = plugin.CurrentTrackService.GetStationMetadataModeDiagnostic(currentStationIdentities, currentStationDeviceName);
        var stationModeMessage = currentStationName.Length == 0
            ? "No current station identity is available to resolve station metadata settings."
            : stationMode.LookupDisabled
                ? "StationOnly rule matched the current station title or .strm filename (rule: \"" + stationMode.MatchedRuleStationName + "\")." +
                    (stationMode.MatchType.Contains("bitrate suffix", StringComparison.OrdinalIgnoreCase)
                        ? " The rule matched after ignoring the trailing bitrate suffix in the configured station name."
                        : string.Empty) +
                    " Metadata lookup is intentionally disabled; an empty accepted-track cache and absent RadioMeta song/artist fields are expected."
                : stationMode.Mode == "StationDelay"
                    ? "StationDelay mode is active for \"" + currentStationName + "\". The first valid current song is published immediately on station entry; later song/artist changes use the configured stability delay."
                    : stationMode.Mode + " mode is active for current station identity \"" + currentStationName + "\" (" + stationMode.MatchType + ")." +
                        (stationMode.ConfiguredStationOnlyRuleNames.Count > 0
                            ? " Configured StationOnly rules: " + string.Join(", ", stationMode.ConfiguredStationOnlyRuleNames.Take(12)) + ". The active station title or .strm filename must match a rule."
                            : " No StationOnly rules are configured for stations.");

        var metadataCacheDiagnostics = plugin.MetadataReader.GetCacheDiagnosticsSnapshot();
        var memoryDiagnostics = plugin.MemoryDiagnostics.GetSnapshot();
        var artworkDownloadDiagnostics = RadioStationArtworkDownloader.GetDiagnosticsSnapshot();
        var stationArtworkInventory = InspectStationArtwork(resolvedFolder, stationFolderAvailable, automatic);
        var runtimeComponentDiagnostics = new RadioMetaRuntimeComponentDiagnosticDto
        {
            StationCaptureStarted = plugin.StationCaptureStarted,
            SessionMetadataPublisherStarted = plugin.SessionMetadataPublisherStarted,
            HistoryStoreInitialized = plugin.HistoryStore is not null
        };

        var proxyService = RadioMetaEntryPoint.GetActiveProxyService();
        var proxyInventory = InspectProxySidecars(resolvedFolder, stationFolderAvailable);
        var proxyRunning = proxyService?.IsRunning == true;
        var ffmpegCandidate = RadioMetaProxyService.ResolveFfmpegPath(ffprobePath);
        var proxyDiagnostics = new RadioMetaProxyDiagnosticDto
        {
            ListenerStatus = proxyService is null
                ? (automatic ? "not started: Station Folder is in automatic mode" : !stationFolderAvailable ? "not started: Station Folder unavailable" : "not running: proxy listener unavailable")
                : proxyRunning ? "running" : "stopped",
            ListenerRunning = proxyRunning,
            FfmpegExecutableAvailable = proxyService?.FfmpegExecutableAvailable ?? RadioMetaProxyService.IsExecutableAvailable(ffmpegCandidate),
            ActiveClients = proxyService?.ActiveClientCount ?? 0,
            ActiveFfmpegProcesses = proxyService?.ActiveFfmpegProcessCount ?? 0,
            TotalClientConnectionsSinceStart = proxyService?.TotalClientConnections ?? 0,
            FfmpegStartAttemptsSinceStart = proxyService?.FfmpegStartAttempts ?? 0,
            FfmpegStartedProcessesSinceStart = proxyService?.FfmpegStartedProcesses ?? 0,
            SuccessfulAudioResponsesSinceStart = proxyService?.SuccessfulAudioResponses ?? 0,
            FfmpegStartupFailuresSinceStart = proxyService?.FfmpegStartupFailures ?? 0,
            SidecarScanCompleted = proxyInventory.Completed,
            SidecarScanStatus = proxyInventory.Status,
            ProxySidecarCount = proxyInventory.SidecarCount,
            EnabledStationCount = proxyInventory.EnabledStationCount,
            InvalidSidecarCount = proxyInventory.InvalidSidecarCount,
            OrphanSidecarCount = proxyInventory.OrphanSidecarCount,
            SidecarMismatchCount = proxyInventory.SidecarMismatchCount
        };

        return new RadioMetaDiagnosticsHealthResponse
        {
            Available = true,
            PluginVersion = version,
            ConfigurationAvailable = plugin.Configuration is not null,
            HistoryStoreInitialized = plugin.HistoryStore is not null,
            DisplayHelperResourceEmbedded = resourceNames.Contains(DisplayHelperResource, StringComparer.Ordinal),
            DisplayHelperResourceName = DisplayHelperResource,
            DisplayLoaderResourceEmbedded = resourceNames.Contains(DisplayLoaderResource, StringComparer.Ordinal),
            DisplayLoaderResourceName = DisplayLoaderResource,
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
            CurrentStationMetadataMode = stationMode.Mode,
            CurrentStationMetadataLookupDisabled = stationMode.LookupDisabled,
            CurrentStationMetadataModeRule = stationMode.MatchedRuleStationName,
            CurrentStationMetadataModeMatch = stationMode.MatchType,
            CurrentStationMetadataModeMessage = stationModeMessage,
            CurrentStationMetadataIdentityNames = currentStationIdentities
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList(),
            ConfiguredStationOnlyRuleNames = stationMode.ConfiguredStationOnlyRuleNames.ToList(),
            CurrentTrackCount = allTracks.Length,
            LiveMetadataSourceTimeoutSeconds = RadioMetadataReader.LiveMetadataSourceTimeoutSeconds,
            ManualStreamTestTimeoutSeconds = RadioMetadataReader.ManualStreamTestTimeoutSeconds,
            DirectIcyMaxIntervals = RadioMetadataReader.DirectIcyMaxIntervals,
            DirectIcyFallbackEnabled = plugin.Configuration?.EnableDirectIcyFallback ?? false,
            CurrentTracks = tracks,
            RecentEvents = plugin.DiagnosticEvents.GetRecent(40),
            RecentStationOperations = plugin.StationOperationEvents.GetRecent(20),
            Proxy = proxyDiagnostics,
            MetadataCache = metadataCacheDiagnostics,
            ArtworkDownloads = artworkDownloadDiagnostics,
            StationArtworkInventory = stationArtworkInventory,
            RuntimeComponents = runtimeComponentDiagnostics,
            Performance = plugin.PerformanceMetrics.GetSnapshot(),
            Memory = memoryDiagnostics
        };
    }
    private static RadioMetaStationArtworkInventoryDto InspectStationArtwork(
        string folder, bool folderAvailable, bool automatic)
    {
        const int maximumStationFilesToInspect = 2048;
        if (automatic)
            return new RadioMetaStationArtworkInventoryDto
            { Completed = false, Status = "Skipped: automatic station-folder mode has no fixed folder to inventory.", SafetyLimit = maximumStationFilesToInspect };
        if (!folderAvailable || string.IsNullOrWhiteSpace(folder))
            return new RadioMetaStationArtworkInventoryDto
            { Completed = false, Status = "Skipped: configured Station Folder is unavailable.", SafetyLimit = maximumStationFilesToInspect };

        try
        {
            var scanned = 0;
            var withArtwork = 0;
            foreach (var stationFile in Directory.EnumerateFiles(folder, "*.strm", SearchOption.TopDirectoryOnly))
            {
                if (scanned >= maximumStationFilesToInspect)
                    return new RadioMetaStationArtworkInventoryDto
                    {
                        Completed = false,
                        Status = "Inventory stopped at the safety limit; counts are partial.",
                        ScannedStationFiles = scanned,
                        StationFilesWithArtwork = withArtwork,
                        StationFilesWithoutArtwork = scanned - withArtwork,
                        SafetyLimit = maximumStationFilesToInspect
                    };

                scanned++;
                var stationName = Path.GetFileNameWithoutExtension(stationFile);
                var hasArtwork = new[] { ".jpg", ".jpeg", ".png", ".webp" }
                    .Any(extension => File.Exists(Path.Combine(folder, stationName + extension)));
                if (hasArtwork) withArtwork++;
            }

            return new RadioMetaStationArtworkInventoryDto
            {
                Completed = true,
                Status = "Inventory completed. Artwork is optional; missing artwork is informational.",
                ScannedStationFiles = scanned,
                StationFilesWithArtwork = withArtwork,
                StationFilesWithoutArtwork = scanned - withArtwork,
                SafetyLimit = maximumStationFilesToInspect
            };
        }
        catch
        {
            return new RadioMetaStationArtworkInventoryDto
            { Completed = false, Status = "Inventory failed; filesystem details were withheld.", SafetyLimit = maximumStationFilesToInspect };
        }
    }

    private static (bool Completed, string Status, int SidecarCount, int EnabledStationCount, int InvalidSidecarCount, int OrphanSidecarCount, int SidecarMismatchCount)
        InspectProxySidecars(string folder, bool folderAvailable)
    {
        if (!folderAvailable || string.IsNullOrWhiteSpace(folder))
            return (false, "Station Folder is unavailable or automatic detection is enabled; sidecar inventory was not run.", 0, 0, 0, 0, 0);

        try
        {
            const int maximumSidecarsToInspect = 2048;
            var sidecarCount = 0;
            var enabledStations = 0;
            var invalidSidecars = 0;
            var orphanSidecars = 0;
            var sidecarMismatches = 0;

            foreach (var sidecarPath in Directory.EnumerateFiles(folder, "*.radiometa-proxy.json", SearchOption.TopDirectoryOnly))
            {
                sidecarCount++;
                if (sidecarCount > maximumSidecarsToInspect)
                    return (false, "Sidecar inventory stopped at its safety limit; counts are partial.", sidecarCount - 1, enabledStations, invalidSidecars, orphanSidecars, sidecarMismatches);

                const string suffix = ".radiometa-proxy.json";
                var fileName = Path.GetFileName(sidecarPath);
                if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    invalidSidecars++;
                    continue;
                }

                var stationName = fileName.Substring(0, fileName.Length - suffix.Length);
                var stationFilePath = Path.Combine(folder, stationName + ".strm");
                if (!File.Exists(stationFilePath))
                {
                    orphanSidecars++;
                    continue;
                }

                if (!RadioMetaProxyService.TryGetProxyDetails(stationFilePath, out _, out var expectedToken))
                {
                    invalidSidecars++;
                    continue;
                }

                if (RadioMetaProxyService.IsProxyStationFile(stationFilePath, expectedToken)) enabledStations++;
                else sidecarMismatches++;
            }

            return (true, "Sidecar inventory completed; paths, URLs, and tokens were not returned.", sidecarCount, enabledStations, invalidSidecars, orphanSidecars, sidecarMismatches);
        }
        catch
        {
            return (false, "Sidecar inventory failed; detailed filesystem information was withheld.", 0, 0, 0, 0, 0);
        }
    }

}
