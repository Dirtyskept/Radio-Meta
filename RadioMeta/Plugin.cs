
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace RadioMeta;

/// <summary>
/// A configurable metadata phrase to reject before it can be treated as a song.
/// An empty StationName means the rule applies to every station; otherwise it
/// matches the station's .strm filename (without the extension), case-insensitively.
/// </summary>
public sealed class RadioMetadataIgnorePhraseRule
{
    public string StationName { get; set; } = string.Empty;
    public string Phrase { get; set; } = string.Empty;
}


/// <summary>
/// Metadata mode scoped by a .strm station title or an exact Emby device name.
/// Station matches take priority over device-wide matches. Modes are Automatic,
/// StationOnly (disable lookup), StationDelay (show the first song immediately,
/// then delay later changes), or Carplay (preserve Carplay's existing transition behavior).
/// </summary>
public sealed class RadioStationMetadataModeRule
{
    public string StationName { get; set; } = string.Empty;
    public string Mode { get; set; } = "Automatic";

    // Wait this many seconds for a candidate title to remain unchanged before
    // accepting it. Zero preserves the original immediate-acceptance behavior.
    public int MetadataDelaySeconds { get; set; } = 0;

    // Legacy property from the previous UI. Kept so existing XML rules retain
    // their station-specific device restriction; new rules use StationName for
    // either the station title or the device name.
    public string MetadataDelayDeviceName { get; set; } = string.Empty;
}



/// <summary>
/// A configurable cleanup applied to the normalized song title before it is
/// published to Current Track or written to history. Blank StationName applies
/// the rule to every station.
/// </summary>
public sealed class RadioTitleCleanupRule
{
    public string StationName { get; set; } = string.Empty;
    // Supported values: TrailingBracketCode4, TrailingSquareBracketAny,
    // TrailingParenthesisAny, ExactSuffix.
    public string RuleType { get; set; } = "TrailingBracketCode4";
    public string Suffix { get; set; } = string.Empty;
    // Optional comma/semicolon-separated labels to preserve for trailing
    // bracket/parenthesis cleanup, in addition to the built-in common labels.
    public string PreserveLabels { get; set; } = string.Empty;
}

/// <summary>
/// Exact station/title correction created from the inline Radio History editor.
/// This applies only when the incoming title exactly matches OriginalTitle.
/// </summary>
public sealed class RadioTitleCorrectionRule
{
    public string StationName { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string CorrectedTitle { get; set; } = string.Empty;
    // Optional exact artist override; blank keeps the incoming artist unchanged.
    public string CorrectedArtist { get; set; } = string.Empty;
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string StationFolder { get; set; } = string.Empty;

    // Leave blank to search PATH for ffprobe/ffprobe.exe on the host OS.
    // A full executable path can be supplied in the plugin XML when needed.
    public string FfprobePath { get; set; } = string.Empty;

    // Delay probing after playback begins, giving Emby time to expose any
    // metadata it already has. Set to 0 to probe immediately.
    public int FfprobeDelaySeconds { get; set; } = 10;

    // If the station's lightweight status API is unavailable, permit a short
    // ICY metadata request. This request closes after reading metadata and is
    // not a persistent second audio stream.
    public bool EnableDirectIcyFallback { get; set; } = true;

    // Per-station adaptive source learning. Disabling this restores the
    // existing parallel Shoutcast/direct-ICY lookup behavior without rebuilding.
    public bool EnableAdaptiveMetadataRouting { get; set; } = true;

    // Number of most-recent played tracks retained in the history JSON.
    public int RadioHistoryLimit { get; set; } = 200;

    // Phrases that should not be treated as song metadata. A blank station name
    // applies the rule globally; a station-specific rule matches the .strm name.
    public List<RadioMetadataIgnorePhraseRule> IgnoreMetadataPhrases { get; set; } = new();

    // Per-station mode overrides. Missing entries default to Automatic.
    // StationOnly skips track metadata lookup. StationDelay publishes the first
    // valid song immediately on station entry, then delays subsequent changes.
    public List<RadioStationMetadataModeRule> StationMetadataModes { get; set; } = new();

    // Optional station-specific or global rules that clean suffixes from valid song titles.
    public List<RadioTitleCleanupRule> TitleCleanupRules { get; set; } = new();

    // Exact title corrections created through the inline History editor.
    // Corrections match both station and incoming title, case-insensitively.
    public List<RadioTitleCorrectionRule> TitleCorrections { get; set; } = new();
}

public sealed class RadioMetaPlugin :
    BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
{
    public static RadioMetaPlugin? Instance { get; private set; }

    public RadioMetaDiagnosticEventStore DiagnosticEvents { get; } = new();

    /// <summary>Bounded in-memory history of station-file and post-change refresh results.</summary>
    public RadioMetaStationOperationStore StationOperationEvents { get; } = new();

    public RadioMetaPerformanceMetricsStore PerformanceMetrics { get; } = new();

    /// <summary>Bounded five-minute process-memory history for leak-trend diagnostics.</summary>
    public RadioMetaMemoryDiagnosticsStore MemoryDiagnostics { get; } = new();

    public RadioMetadataReader MetadataReader { get; }

    /// <summary>
    /// Authoritative accepted-track state and validation path for plugin-owned
    /// consumers such as session metadata handoff and play history.
    /// </summary>
    public RadioCurrentTrackService CurrentTrackService { get; }

    /// <summary>Short-lived active playback details used to pre-fill the station form.
    /// This in-memory cache is not persisted to history or configuration.</summary>
    public RadioCurrentlyPlayingStationStore CurrentlyPlayingStationStore { get; } = new();

    public RadioPlayHistoryStore? HistoryStore { get; private set; }

    /// <summary>Runtime state for diagnostic-only health checks of background event handlers.</summary>
    public bool StationCaptureStarted { get; internal set; }
    public bool SessionMetadataPublisherStarted { get; internal set; }

    public void InitializeHistoryStore(ILogger logger)
    {
        var configurationDirectory = Path.GetDirectoryName(ConfigurationFilePath);

        if (string.IsNullOrWhiteSpace(configurationDirectory))
        {
            throw new InvalidOperationException(
                "The Radio Meta configuration directory could not be determined.");
        }

        var historyPath = Path.Combine(
            configurationDirectory,
            "RadioMetaHistory.json");

        HistoryStore = new RadioPlayHistoryStore(
            historyPath,
            logger,
            () => Configuration.RadioHistoryLimit);
    }

    public RadioMetaPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Configuration ??= new PluginConfiguration();
        var configurationDirectory = Path.GetDirectoryName(ConfigurationFilePath);
        MetadataReader = new RadioMetadataReader(DiagnosticEvents, PerformanceMetrics, configurationDirectory);
        CurrentTrackService = new RadioCurrentTrackService(this);
        Instance = this;
    }

    public override Guid Id =>
        Guid.Parse("8a7f6e31-9d42-4b85-a103-72c6f0e54b19");

    public override string ConfigurationFileName =>
        "8a7f6e31-9d42-4b85-a103-72c6f0e54b19.xml";

    public override string Name => "Radio Meta";

    public override string Description =>
        "Displays radio station artwork, current-track metadata, and radio history.";

    /// <summary>
    /// Supplies the Radio Meta artwork shown in Emby's plugin listing.
    /// The thumbnail is embedded so the plugin does not depend on an external file.
    /// </summary>
    public Stream GetThumbImage()
    {
        var resourceName = $"{GetType().Namespace}.thumb.jpg";
        return GetType().Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded plugin thumbnail resource '{resourceName}' was not found.");
    }

    public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

    /// <summary>
    /// Returns the configured StationFolder value exactly as stored in XML.
    /// A blank value enables automatic detection of the currently playing
    /// audio .strm item. Environment variables are supported in nonblank paths.
    /// </summary>
    public string GetConfiguredStationFolder() =>
        (Configuration.StationFolder ?? string.Empty).Trim();

    /// <summary>Returns the runtime-resolved station folder, or an empty string
    /// when automatic active-item detection is enabled.</summary>
    public string GetStationFolder()
    {
        var configured = GetConfiguredStationFolder();
        return configured.Length == 0
            ? string.Empty
            : Environment.ExpandEnvironmentVariables(configured).Trim();
    }

    public string GetFfprobePath()
    {
        var configuredValue = (Configuration.FfprobePath ?? string.Empty).Trim();
        var configured = configuredValue.Length == 0
            ? string.Empty
            : Environment.ExpandEnvironmentVariables(configuredValue).Trim();

        if (configured.Length > 0 && File.Exists(configured))
        {
            try
            {
                return Path.GetFullPath(configured);
            }
            catch
            {
                return configured;
            }
        }

        // A configured bare command name is also allowed, as is automatic
        // discovery through PATH. This avoids machine-specific defaults.
        var commandName = configured.Length > 0
            ? configured
            : (OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        var fromPath = FindExecutableOnPath(commandName);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        if (OperatingSystem.IsWindows() &&
            !commandName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return FindExecutableOnPath(commandName + ".exe") ?? string.Empty;
        }

        return string.Empty;
    }

    public int GetFfprobeDelaySeconds() =>
        Math.Clamp(Configuration.FfprobeDelaySeconds, 0, 120);

    public int GetRadioHistoryLimit() =>
        Math.Clamp(Configuration.RadioHistoryLimit, 1, 5000);

    /// <summary>
    /// Compatibility forwarding method. Metadata decisions are centralized in
    /// CurrentTrackService so readers and session consumers use the same rules.
    /// </summary>
    public bool IsMetadataPhraseIgnored(string? stationName, string? candidate) =>
        CurrentTrackService.IsMetadataPhraseIgnored(stationName, candidate);

    /// <summary>
    /// Compatibility forwarding method for the station-specific metadata mode.
    /// The centralized current-track service owns the lookup-mode decision.
    /// </summary>
    public bool IsStationMetadataLookupDisabled(string? stationName, string? deviceName = null) =>
        CurrentTrackService.IsStationMetadataLookupDisabled(stationName, deviceName);

    private static string? FindExecutableOnPath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        if (Path.IsPathRooted(executableName) ||
            executableName.Contains(Path.DirectorySeparatorChar) ||
            executableName.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executableName) ? Path.GetFullPath(executableName) : null;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        return null;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "RadioMetaDashboard",
                DisplayName = "Radio Meta",
                EmbeddedResourcePath =
                    "RadioMeta.WebUI.RadioMetaDashboard.html",
                EnableInMainMenu = true,
                MenuIcon = "radio"
            },
            new PluginPageInfo
            {
                Name = "RadioMetaDashboard.js",
                EmbeddedResourcePath =
                    "RadioMeta.WebUI.RadioMetaDashboard.js"
            },
            new PluginPageInfo
            {
                // Embedded AMD loader module. The WebLoader contains only a small
                // bootstrap pointer; retry and import logic remains in this DLL resource.
                Name = "RadioMetaDisplayLoader.js",
                EmbeddedResourcePath =
                    "RadioMeta.WebUI.RadioMetaDisplayLoader.js"
            },
            new PluginPageInfo
            {
                // Embedded AMD module containing the integrated display helper.
                Name = "RadioMetaIntegratedDisplay.js",
                EmbeddedResourcePath =
                    "RadioMeta.WebUI.RadioMetaIntegratedDisplay.js"
            }
        };
    }
}

public sealed class RadioMetaEntryPoint : IServerEntryPoint
{
    private static RadioMetaProxyService? _activeProxyService;
    internal static string GetProxyBaseUrl() => _activeProxyService?.BaseUrl ?? string.Empty;
    internal static RadioMetaProxyService? GetActiveProxyService() => _activeProxyService;

    private readonly ILogger _logger;
    private readonly RadioMetaPlugin? _plugin;
    private readonly ISessionManager _sessionManager;

    private RadioMetaSessionTitleInjector? _titleInjector;
    private RadioCurrentlyPlayingStationCapture? _currentlyPlayingStationCapture;
    private RadioMetaProxyService? _proxyService;

    public RadioMetaEntryPoint(IApplicationHost applicationHost)
    {
        _logger = applicationHost.Resolve<ILogManager>()
            .GetLogger(nameof(RadioMetaEntryPoint));

        _plugin = applicationHost.Plugins
            .OfType<RadioMetaPlugin>()
            .FirstOrDefault();

        _sessionManager = applicationHost.Resolve<ISessionManager>();
    }

    public void Run()
    {
        if (_plugin is null)
        {
            _logger.Error(
                "Radio Meta plugin instance was not found.");
            return;
        }

        _plugin.MemoryDiagnostics.Start();

        var configuredFolder = _plugin.GetConfiguredStationFolder();
        var folder = _plugin.GetStationFolder();
        var ffprobePath = _plugin.GetFfprobePath();
        var folderAvailable = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        var automaticStationDetection = string.IsNullOrWhiteSpace(configuredFolder);

        _logger.Info(
            "Radio Meta configuration loaded. Station folder configured: {0}; station folder available: {1}; automatic active .strm detection: {2}; ffprobe available: {3}; direct ICY fallback enabled: {4}; history limit: {5}.",
            !automaticStationDetection,
            folderAvailable,
            automaticStationDetection,
            !string.IsNullOrWhiteSpace(ffprobePath) && File.Exists(ffprobePath),
            _plugin.Configuration.EnableDirectIcyFallback,
            _plugin.GetRadioHistoryLimit());

        try
        {
            _plugin.InitializeHistoryStore(_logger);
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Radio play history could not be initialized ({0}).",
                ex.GetType().Name);
        }

        if (!automaticStationDetection && !folderAvailable)
        {
            _logger.Warn(
                "The configured StationFolder is unavailable. Radio tracking remains active, but station paths will not be accepted until the folder path is corrected or cleared for automatic detection.");
        }
        else if (automaticStationDetection)
        {
            _logger.Info(
                "StationFolder is blank. Automatic detection is enabled for currently playing audio .strm items that Emby identifies as Audio.");
        }

        _currentlyPlayingStationCapture = new RadioCurrentlyPlayingStationCapture(
            _plugin.CurrentlyPlayingStationStore,
            _sessionManager,
            _logger);
        _currentlyPlayingStationCapture.Start();
        _plugin.StationCaptureStarted = _currentlyPlayingStationCapture.IsStarted;

        if (folderAvailable)
        {
            try
            {
                var ffmpegPath = RadioMetaProxyService.ResolveFfmpegPath(ffprobePath);
                _proxyService = new RadioMetaProxyService(folder, ffmpegPath, _logger);
                _activeProxyService = _proxyService;
            }
            catch (Exception ex)
            {
                _logger.Warn("Radio Meta proxy did not start ({0}); normal station playback remains unchanged.", ex.GetType().Name);
            }
        }

        _titleInjector = new RadioMetaSessionTitleInjector(
            _plugin,
            _sessionManager,
            _logger);

        _titleInjector.Start();
        _plugin.SessionMetadataPublisherStarted = _titleInjector.IsStarted;

        if (string.IsNullOrWhiteSpace(ffprobePath) ||
            !File.Exists(ffprobePath))
        {
            _logger.Warn(
                "ffprobe was not found. Automatic FFprobe fallback is unavailable until it is installed or configured.");
        }

        if (folderAvailable)
        {
            var stationFiles = Directory.GetFiles(folder, "*.strm")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _logger.Info("Radio Meta found {0} station files.", stationFiles.Length);

            var artworkCount = 0;
            foreach (var stationFile in stationFiles)
            {
                var name = Path.GetFileNameWithoutExtension(stationFile);
                var artwork = new[] { ".jpg", ".jpeg", ".png", ".webp" }
                    .Select(extension => Path.Combine(folder, name + extension))
                    .FirstOrDefault(File.Exists);
                if (artwork is not null) artworkCount++;
            }
            _logger.Info(
                "Matching station artwork found for {0} of {1} station files.",
                artworkCount,
                stationFiles.Length);
        }
        else
        {
            _logger.Info(
                "Station file inventory skipped because StationFolder is blank or unavailable. In automatic mode, the active .strm path is used per session.");
        }
    }

    public void Dispose()
    {
        if (_plugin is not null) _plugin.MemoryDiagnostics.Stop();
        _currentlyPlayingStationCapture?.Dispose();
        _currentlyPlayingStationCapture = null;
        if (_plugin is not null) _plugin.StationCaptureStarted = false;
        _titleInjector?.Dispose();
        _titleInjector = null;
        if (_plugin is not null) _plugin.SessionMetadataPublisherStarted = false;
        _proxyService?.Dispose();
        if (ReferenceEquals(_activeProxyService, _proxyService)) _activeProxyService = null;
        _proxyService = null;
    }
}
