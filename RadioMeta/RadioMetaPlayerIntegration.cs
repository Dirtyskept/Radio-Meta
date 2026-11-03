using System;
using System.IO;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>
/// Installs and removes the DLL-owned bootstrap for the embedded native-player
/// integration. The complete player script remains an embedded DLL resource.
/// </summary>
public sealed class RadioMetaPlayerIntegration : IDisposable
{
    private const string BeginMarker = "<!-- BEGIN RADIOMETA DLL PLAYER INTEGRATION -->";
    private const string EndMarker = "<!-- END RADIOMETA DLL PLAYER INTEGRATION -->";
    private const string LegacyBeginMarker19 = "<!-- BEGIN RADIOMETA DLL PLAYER INTEGRATION 1.9 -->";
    private const string LegacyEndMarker19 = "<!-- END RADIOMETA DLL PLAYER INTEGRATION 1.9 -->";

    private const string ScriptReference =
        "<script data-radiometa-player-integration-bootstrap=\"1.3.128\">\n" +
        "(function (w) {\n" +
        "    'use strict';\n" +
        "    var key = '__radioMetaPlayerBootstrap1328';\n" +
        "    var state = w[key];\n" +
        "    if (!state || typeof state !== 'object') {\n" +
        "        state = w[key] = { started: false, loading: false, attempts: 0, timer: null };\n" +
        "    }\n" +
        "    if (state.started || state.loading) return;\n" +
        "\n" +
        "    function retryWhenEmbyIsReady() {\n" +
        "        if (state.started || state.loading) return;\n" +
        "        var emby = w.Emby;\n" +
        "        if (!emby || typeof emby.importModule !== 'function' || typeof w.require !== 'function') {\n" +
        "            state.attempts++;\n" +
        "            if (state.attempts <= 120) {\n" +
        "                state.timer = w.setTimeout(retryWhenEmbyIsReady, 250);\n" +
        "            } else {\n" +
        "                console.warn('Radio Meta 1.3.128: Emby AMD module loader was not available after 30 seconds.');\n" +
        "            }\n" +
        "            return;\n" +
        "        }\n" +
        "\n" +
        "        state.loading = true;\n" +
        "        emby.importModule('./modules/common/pluginmanager.js').then(function (pluginManager) {\n" +
        "            pluginManager = pluginManager && pluginManager.default ? pluginManager.default : pluginManager;\n" +
        "            if (!pluginManager || typeof pluginManager.getConfigurationResourceUrl !== 'function') {\n" +
        "                throw new Error('Emby PluginManager did not expose getConfigurationResourceUrl().');\n" +
        "            }\n" +
        "\n" +
        "            var resourceUrl = pluginManager.getConfigurationResourceUrl('RadioMetaPlayerIntegration.js');\n" +
        "            if (typeof resourceUrl !== 'string' || !resourceUrl) {\n" +
        "                throw new Error('Emby PluginManager returned an empty player integration resource URL.');\n" +
        "            }\n" +
        "\n" +
        "            console.info('Radio Meta 1.3.128: resolved its embedded resource through Emby PluginManager.');\n" +
        "            return w.require([resourceUrl]);\n" +
        "        }).then(function () {\n" +
        "            state.started = true;\n" +
        "            console.info('Radio Meta 1.3.128: DLL-embedded player integration resource loaded.');\n" +
        "        }).catch(function (error) {\n" +
        "            var message = error && error.message ? error.message : String(error || 'Unknown error');\n" +
        "            console.error('Radio Meta 1.3.128: DLL-embedded player integration could not be loaded: ' + message, error);\n" +
        "        }).finally(function () {\n" +
        "            state.loading = false;\n" +
        "        });\n" +
        "    }\n" +
        "\n" +
        "    if (w.document.readyState === 'loading') {\n" +
        "        w.document.addEventListener('DOMContentLoaded', retryWhenEmbyIsReady, { once: true });\n" +
        "    } else {\n" +
        "        retryWhenEmbyIsReady();\n" +
        "    }\n" +
        "})(window);\n" +
        "</script>";

    private static readonly object FileLock = new();
    private static volatile bool _isInstalled;
    private static string _installStatus = "not attempted";

    public static bool IsInstalled => _isInstalled;
    public static string InstallStatus => _installStatus;

    private readonly string _indexPath;
    private readonly ILogger _logger;
    private bool _disposed;

    public RadioMetaPlayerIntegration(IApplicationPaths applicationPaths, ILogger logger)
    {
        if (applicationPaths is null) throw new ArgumentNullException(nameof(applicationPaths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _indexPath = Path.Combine(
            applicationPaths.ProgramSystemPath,
            "dashboard-ui",
            "index.html");
    }

    /// <summary>
    /// Installs or upgrades the marked bootstrap. Current and legacy 1.9 marker
    /// blocks are both recognized, so promotion does not append duplicate scripts.
    /// Only Radio Meta's own marked blocks are replaced.
    /// </summary>
    public bool Install()
    {
        lock (FileLock)
        {
            SetInstallState(false, "installation not completed");
            try
            {
                if (!File.Exists(_indexPath))
                {
                    SetInstallState(false, "Emby dashboard index was not found");
                    _logger.Warn("Radio Meta 1.3.128 player integration was not installed because Emby's dashboard index was not found.");
                    return false;
                }

                var html = File.ReadAllText(_indexPath);
                var newline = html.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var integrationBlock = string.Join(newline, new[]
                {
                    BeginMarker,
                    ScriptReference,
                    EndMarker,
                    string.Empty
                });

                var rewritten = html;
                var firstRemovedBlockStart = -1;
                string markerError;
                while (TryFindManagedBlock(rewritten, out var blockStart, out var blockEnd, out markerError))
                {
                    if (firstRemovedBlockStart < 0) firstRemovedBlockStart = blockStart;
                    rewritten = rewritten.Remove(blockStart, blockEnd - blockStart);
                }

                if (!string.IsNullOrEmpty(markerError))
                {
                    SetInstallState(false, "an existing player integration marker is incomplete");
                    _logger.Warn("Radio Meta found an incomplete player integration marker in the dashboard index; no automatic replacement was attempted.");
                    return false;
                }

                if (firstRemovedBlockStart >= 0)
                {
                    var insertAt = Math.Min(firstRemovedBlockStart, rewritten.Length);
                    var upgraded = rewritten.Insert(insertAt, integrationBlock);
                    if (!string.Equals(upgraded, html, StringComparison.Ordinal))
                    {
                        WriteAtomically(_indexPath, upgraded);
                    }

                    SetInstallState(true, "installed or upgraded");
                    _logger.Info("Radio Meta 1.3.128 installed or upgraded its DLL-managed player integration reference.");
                    return true;
                }

                var closingBodyIndex = rewritten.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (closingBodyIndex < 0)
                {
                    SetInstallState(false, "dashboard index has no closing body tag");
                    _logger.Warn("Radio Meta 1.3.128 player integration was not installed because the dashboard index has no closing body tag.");
                    return false;
                }

                var updated = rewritten.Insert(closingBodyIndex, integrationBlock);
                WriteAtomically(_indexPath, updated);
                SetInstallState(true, "installed");
                _logger.Info("Radio Meta 1.3.128 installed its DLL-managed player bootstrap; the complete integration remains embedded in RadioMeta.dll.");
                return true;
            }
            catch (Exception ex)
            {
                SetInstallState(false, "installation failed: " + ex.GetType().Name);
                _logger.Error("Radio Meta 1.3.128 could not install or upgrade its player integration reference ({0}).", ex.GetType().Name);
                return false;
            }
        }
    }

    /// <summary>Removes only current or legacy Radio Meta marker blocks.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (FileLock)
        {
            try
            {
                if (!File.Exists(_indexPath))
                {
                    SetInstallState(false, "dashboard index unavailable during shutdown");
                    return;
                }

                var html = File.ReadAllText(_indexPath);
                var rewritten = html;
                var removedAny = false;
                string markerError;
                while (TryFindManagedBlock(rewritten, out var blockStart, out var blockEnd, out markerError))
                {
                    rewritten = rewritten.Remove(blockStart, blockEnd - blockStart);
                    removedAny = true;
                }

                if (!string.IsNullOrEmpty(markerError))
                {
                    SetInstallState(false, "incomplete integration marker found during shutdown");
                    _logger.Warn("Radio Meta found an incomplete player integration marker during shutdown; no automatic removal was attempted.");
                    return;
                }

                if (removedAny)
                {
                    WriteAtomically(_indexPath, rewritten);
                    _logger.Info("Radio Meta removed its DLL-managed player integration reference from the dashboard index.");
                }
                SetInstallState(false, removedAny ? "removed during plugin shutdown" : "no marked integration block found during shutdown");
            }
            catch (Exception ex)
            {
                SetInstallState(false, "shutdown cleanup failed: " + ex.GetType().Name);
                _logger.Warn("Radio Meta could not remove its player integration reference during shutdown ({0}).", ex.GetType().Name);
            }
        }
    }

    private static void SetInstallState(bool installed, string status)
    {
        _isInstalled = installed;
        _installStatus = status;
    }

    private static bool TryFindManagedBlock(
        string html,
        out int blockStart,
        out int blockEnd,
        out string markerError)
    {
        blockStart = -1;
        blockEnd = -1;
        markerError = string.Empty;

        var beginMarkers = new[] { BeginMarker, LegacyBeginMarker19 };
        var endMarkers = new[] { EndMarker, LegacyEndMarker19 };
        var selectedIndex = -1;
        var selectedPair = -1;

        for (var i = 0; i < beginMarkers.Length; i++)
        {
            var candidate = html.IndexOf(beginMarkers[i], StringComparison.Ordinal);
            if (candidate >= 0 && (selectedIndex < 0 || candidate < selectedIndex))
            {
                selectedIndex = candidate;
                selectedPair = i;
            }
        }

        if (selectedIndex < 0) return false;
        var endIndex = html.IndexOf(endMarkers[selectedPair], selectedIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            markerError = "matching end marker not found";
            return false;
        }

        blockStart = html.LastIndexOf('\n', selectedIndex);
        blockStart = blockStart < 0 ? 0 : blockStart + 1;
        blockEnd = endIndex + endMarkers[selectedPair].Length;
        if (blockEnd < html.Length && html[blockEnd] == '\r') blockEnd++;
        if (blockEnd < html.Length && html[blockEnd] == '\n') blockEnd++;
        return true;
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Dashboard index directory is unavailable.");
        var temporaryPath = Path.Combine(
            directory,
            ".radiometa-index-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }
}
