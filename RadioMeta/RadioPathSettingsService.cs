using System;
using System.IO;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

[Authenticated(Roles = "Admin")]
public sealed class RadioPathSettingsService : IService
{
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
        configuration.StationFolder = stationFolder;
        configuration.FfprobePath = ffprobePath;
        configuration.EnableAdaptiveMetadataRouting = request.EnableAdaptiveMetadataRouting ?? previousAdaptiveRouting;

        try
        {
            plugin.UpdateConfiguration(configuration);
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
            configuration.StationFolder = previousStationFolder;
            configuration.FfprobePath = previousFfprobePath;
            configuration.EnableAdaptiveMetadataRouting = previousAdaptiveRouting;
            return new RadioPathSettingsResponse
            {
                Error = "The path settings could not be saved to the plugin XML configuration."
            };
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

        return response;
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
            EnableAdaptiveMetadataRouting = plugin.Configuration.EnableAdaptiveMetadataRouting
        };
    }
}
