using System;
using System.IO;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

[Authenticated(Roles = "Admin")]
public sealed class RadioPathSettingsService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext _authorizationContext;

    public RadioPathSettingsService(IAuthorizationContext authorizationContext)
    {
        _authorizationContext = authorizationContext;
    }

    /// <summary>Set by Emby for each request (IRequiresRequest); used to name the saving device.</summary>
    public IRequest Request { get; set; } = null!;

    public object Get(GetRadioPathSettings request)
    {
        var plugin = RadioMetaPlugin.Instance;
        return plugin is null
            ? new RadioPathSettingsResponse { Error = "The Radio Meta plugin is not initialized." }
            : CreatePathSettingsResponse(plugin);
    }

    public object Post(SaveRadioPathSettings request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioPathSettingsResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        var stationFolder = (request.StationFolder ?? string.Empty).Trim();
        var ffprobePath = (request.FfprobePath ?? string.Empty).Trim();
        if (stationFolder.Length > 1024 || ffprobePath.Length > 1024)
        {
            return new RadioPathSettingsResponse
            {
                Error = "Paths must be 1024 characters or fewer."
            };
        }

        var configuration = plugin.Configuration;
        var previousStationFolder = configuration.StationFolder;
        var previousFfprobePath = configuration.FfprobePath;
        var previousAdaptiveRouting = configuration.EnableAdaptiveMetadataRouting;
        var previousOutsideFolder = configuration.EnableStationsOutsideFolder;
        configuration.StationFolder = stationFolder;
        configuration.FfprobePath = ffprobePath;
        configuration.EnableAdaptiveMetadataRouting = request.EnableAdaptiveMetadataRouting ?? previousAdaptiveRouting;
        configuration.EnableStationsOutsideFolder = request.EnableStationsOutsideFolder ?? previousOutsideFolder;

        // Recorded for Diagnostics and the Emby log (1.3.152): which device saved,
        // and whether each path was set, changed, cleared or left alone.
        var saveEvent = new RadioMetaSettingsSaveEventDto
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Device = DescribeCaller(),
            StationFolder = RadioMetaSettingsSaveStore.DescribePathChange(previousStationFolder, stationFolder),
            FfprobePath = RadioMetaSettingsSaveStore.DescribePathChange(previousFfprobePath, ffprobePath),
            AdaptiveRouting = RadioMetaSettingsSaveStore.DescribeSwitch(previousAdaptiveRouting, configuration.EnableAdaptiveMetadataRouting),
            StationsOutsideFolder = RadioMetaSettingsSaveStore.DescribeSwitch(previousOutsideFolder, configuration.EnableStationsOutsideFolder)
        };

        try
        {
            plugin.UpdateConfiguration(configuration);
            saveEvent.Result = "saved";
            plugin.SettingsSaveEvents.Record(saveEvent);
            plugin.MetadataReader.ClearCaches();
            if (previousAdaptiveRouting != configuration.EnableAdaptiveMetadataRouting)
            {
                plugin.MetadataReader.ResetAdaptiveMetadataRouting();
            }
            plugin.CurrentTrackService.RevalidateCurrentTracks();
        }
        catch (Exception ex)
        {
            plugin.DiagnosticEvents.Record("Dashboard API", "Configuration", "error", string.Empty, string.Empty, "Save path settings failed while saving configuration (" + ex.GetType().Name + ").");
            saveEvent.Result = "not saved (" + ex.GetType().Name + ")";
            plugin.SettingsSaveEvents.Record(saveEvent);
            configuration.StationFolder = previousStationFolder;
            configuration.FfprobePath = previousFfprobePath;
            configuration.EnableAdaptiveMetadataRouting = previousAdaptiveRouting;
            configuration.EnableStationsOutsideFolder = previousOutsideFolder;
            return new RadioPathSettingsResponse
            {
                Error = "The path settings could not be saved to the plugin XML configuration."
            };
        }

        // A station folder that was missing when Emby started left the proxy off;
        // start it now instead of waiting for a restart. Does nothing if it is
        // already running or the folder still isn't there.
        var proxyWasRunning = !string.IsNullOrWhiteSpace(RadioMetaEntryPoint.GetProxyBaseUrl());
        var proxyRunning = proxyWasRunning;
        if (!proxyWasRunning)
        {
            try { proxyRunning = RadioMetaEntryPoint.EnsureProxyRunning(); }
            catch { proxyRunning = false; }
        }

        var response = CreatePathSettingsResponse(plugin);
        response.Message = response.AutomaticStationDetection
            ? "Path settings saved. Station folder is blank, so automatic active audio .strm detection is enabled."
            : response.StationFolderAvailable
                ? "Path settings saved. The configured Station folder is available."
                : "Path settings saved, but the Station folder could not be found under the Emby process account. Check the path or clear it to enable automatic detection.";
        if (!string.IsNullOrWhiteSpace(response.FfprobePath) && !response.FfprobeAvailable)
        {
            response.Message += " The configured FFprobe path did not resolve to an existing executable; a bare command name may still resolve through PATH.";
        }

        response.Message += response.EnableAdaptiveMetadataRouting
            ? " Adaptive metadata routing is enabled with per-station source learning."
            : " Adaptive metadata routing is disabled; Shoutcast and direct ICY use the existing parallel lookup path.";

        if (!response.AutomaticStationDetection && response.StationFolderAvailable && !proxyWasRunning)
        {
            response.Message += proxyRunning
                ? " The station proxy has been started."
                : " The station proxy could not be started; check the Emby server log.";
        }

        if (!response.AutomaticStationDetection)
        {
            response.Message += response.EnableStationsOutsideFolder
                ? " Audio stations outside the station folder, including web streams, also receive metadata."
                : " Only stations in the station folder receive metadata.";
        }

        return response;
    }

    private string DescribeCaller()
    {
        try
        {
            var caller = _authorizationContext.GetAuthorizationInfo(Request);
            var client = (caller?.Client ?? string.Empty).Trim();
            var device = (caller?.Device ?? string.Empty).Trim();
            var text = client.Length > 0 && device.Length > 0 ? client + " / " + device : client + device;
            return text.Length > 80 ? text.Substring(0, 80) : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static RadioPathSettingsResponse CreatePathSettingsResponse(RadioMetaPlugin plugin)
    {
        var configuredFolder = plugin.GetConfiguredStationFolder();
        var expandedFolder = plugin.GetStationFolder();
        var resolvedFfprobe = plugin.GetFfprobePath();
        var automatic = configuredFolder.Length == 0;
        var folderAvailable = !automatic && !string.IsNullOrWhiteSpace(expandedFolder) && Directory.Exists(expandedFolder);
        return new RadioPathSettingsResponse
        {
            StationFolder = configuredFolder,
            FfprobePath = (plugin.Configuration.FfprobePath ?? string.Empty).Trim(),
            ExpandedStationFolder = expandedFolder,
            AutomaticStationDetection = automatic,
            StationFolderAvailable = folderAvailable,
            ResolvedFfprobePath = resolvedFfprobe,
            FfprobeAvailable = !string.IsNullOrWhiteSpace(resolvedFfprobe) && File.Exists(resolvedFfprobe),
            EnableAdaptiveMetadataRouting = plugin.Configuration.EnableAdaptiveMetadataRouting,
            EnableStationsOutsideFolder = plugin.Configuration.EnableStationsOutsideFolder
        };
    }
}
