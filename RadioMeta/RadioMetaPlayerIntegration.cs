using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>
/// Adds and removes the DLL-owned bootstrap for the embedded native-player
/// integration in Emby's dashboard-ui/index.html. The complete player script
/// remains an embedded DLL resource.
/// </summary>
/// <remarks>
/// Follows the rules shared with other plugins that write index.html (Top Songs):
/// 1. Write only if needed: an existing, identical block is left untouched.
/// 2. The block is removed only on uninstall (<see cref="RemoveForUninstall"/>,
///    called from OnUninstalling), not at normal shutdown.
/// 3. Every read-modify-write holds the system-wide named mutex
///    <see cref="SharedMutexName"/>; the name must be identical in every plugin.
/// 4. The file is re-read just before the swap; if it changed, start over.
/// 5. Atomic write: temp file in the same folder, then move over the original;
///    line endings kept; UTF-8 without BOM; Radio Meta's own temp prefix.
/// 6. About 500 ms after installing, check the block is still there; retry a
///    couple of times. This runs in the background, so Emby's startup is never
///    delayed.
/// 7. Only Radio Meta's own markers are touched; the file is left alone if they
///    are incomplete or there is no closing body tag.
/// 8. The block has no version number, so plugin updates do not rewrite it.
/// </remarks>
public sealed class RadioMetaPlayerIntegration : IDisposable
{
    private const string BeginMarker = "<!-- BEGIN RADIOMETA DLL PLAYER INTEGRATION -->";
    private const string EndMarker = "<!-- END RADIOMETA DLL PLAYER INTEGRATION -->";

    /// <summary>Shared with other plugins that write index.html. Must not change.</summary>
    internal const string SharedMutexName = @"Global\EmbyDashboardIndexWrite";

    private static readonly TimeSpan SharedLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromMilliseconds(500);
    private const int MaxWriteAttempts = 3;
    private const int MaxVerifyAttempts = 3;

    // No version number in this block (rule 8): it is identical in every release.
    private const string ScriptReference =
        "<script data-radiometa-player-integration-bootstrap>\n" +
        "(function (w) {\n" +
        "    // Radio Meta: load its player script (embedded in RadioMeta.dll) once Emby is ready.\n" +
        "    var tries = 0;\n" +
        "    (function load() {\n" +
        "        if (w.__radioMetaPlayerBootstrap) return;\n" +
        "        if (!w.Emby || !w.Emby.importModule || !w.require) {\n" +
        "            if (++tries <= 120) w.setTimeout(load, 250); // wait up to 30 seconds\n" +
        "            return;\n" +
        "        }\n" +
        "        w.__radioMetaPlayerBootstrap = true;\n" +
        "        w.Emby.importModule('./modules/common/pluginmanager.js').then(function (m) {\n" +
        "            m = m && m.default ? m.default : m;\n" +
        "            return w.require([m.getConfigurationResourceUrl('RadioMetaPlayerIntegration.js')]);\n" +
        "        }).catch(function (e) { console.error('Radio Meta: player script not loaded', e); });\n" +
        "    })();\n" +
        "})(window);\n" +
        "</script>";

    private static readonly object FileLock = new();
    private static volatile bool _isInstalled;
    private static string _installStatus = "not attempted";
    private static ILogger? _logger;

    public static bool IsInstalled => _isInstalled;
    public static string InstallStatus => _installStatus;

    private readonly string _indexPath;
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    public RadioMetaPlayerIntegration(IApplicationPaths applicationPaths, ILogger logger)
    {
        if (applicationPaths is null) throw new ArgumentNullException(nameof(applicationPaths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _indexPath = GetIndexPath(applicationPaths);
    }

    private static string GetIndexPath(IApplicationPaths applicationPaths) =>
        Path.Combine(applicationPaths.ProgramSystemPath, "dashboard-ui", "index.html");

    /// <summary>
    /// Adds or updates the marked bootstrap in the background, then checks it is
    /// still there. Returns at once, so Emby's startup is never delayed.
    /// </summary>
    public void Install()
    {
        SetInstallState(false, "installation in progress");
        var token = _stopping.Token;
        Task.Run(() => InstallAndVerify(token));
    }

    private void InstallAndVerify(CancellationToken token)
    {
        try
        {
            InstallAndVerifyCore(token);
        }
        catch (Exception ex)
        {
            SetInstallState(false, "installation failed: " + ex.GetType().Name);
            _logger?.Error("Radio Meta " + RadioMetaBuildInfo.Version + " player integration background install failed ({0}).", ex.GetType().Name);
        }
    }

    private void InstallAndVerifyCore(CancellationToken token)
    {
        for (var attempt = 1; attempt <= MaxVerifyAttempts; attempt++)
        {
            if (token.IsCancellationRequested) return;
            if (!TryInstallOnce()) return;

            // Rule 6: another writer could have replaced the file without our block.
            if (token.WaitHandle.WaitOne(VerifyDelay)) return;
            if (IsBlockPresent(_indexPath))
            {
                if (attempt > 1) SetInstallState(true, "installed after " + attempt + " attempts");
                return;
            }

            _logger?.Warn("Radio Meta's player integration block was missing shortly after it was written; writing it again.");
        }

        SetInstallState(false, "the block kept disappearing after it was written");
        _logger?.Warn("Radio Meta's player integration block kept disappearing from the dashboard index; another program may be rewriting the file.");
    }

    /// <summary>One locked read-modify-write. Returns true when the block should now be in the file.</summary>
    private bool TryInstallOnce()
    {
        lock (FileLock)
        using (SharedIndexLock.Acquire())
        {
            try
            {
                for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
                {
                    if (!File.Exists(_indexPath))
                    {
                        return LeaveAlone("Emby dashboard index was not found");
                    }

                    var html = File.ReadAllText(_indexPath);
                    if (!TryBuildInstalled(html, out var updated, out var problem))
                    {
                        return LeaveAlone(problem);
                    }

                    // Rule 1: the correct block is already there.
                    if (string.Equals(updated, html, StringComparison.Ordinal))
                    {
                        SetInstallState(true, "already installed");
                        return true;
                    }

                    // Rule 4: if the file changed since it was read, start over.
                    var current = File.ReadAllText(_indexPath);
                    if (!string.Equals(current, html, StringComparison.Ordinal)) continue;

                    try
                    {
                        WriteAtomically(_indexPath, updated);
                    }
                    catch (IOException) when (attempt < MaxWriteAttempts)
                    {
                        // The file is briefly in use (for example a virus scan); wait and retry.
                        // This runs in the background, so it never delays Emby.
                        Thread.Sleep(250);
                        continue;
                    }

                    SetInstallState(true, "installed");
                    _logger?.Info("Radio Meta " + RadioMetaBuildInfo.Version + " installed its DLL-managed player bootstrap; the complete integration remains embedded in RadioMeta.dll.");
                    return true;
                }

                SetInstallState(false, "the dashboard index kept changing while installing");
                _logger?.Warn("Radio Meta did not install its player integration because the dashboard index kept changing while it was being updated.");
                return false;
            }
            catch (Exception ex)
            {
                SetInstallState(false, "installation failed: " + ex.GetType().Name);
                _logger?.Error("Radio Meta " + RadioMetaBuildInfo.Version + " could not install its player integration reference ({0}).", ex.GetType().Name);
                return false;
            }
        }
    }

    /// <summary>
    /// Builds the file content with exactly one current block. Existing current or
    /// blocks are replaced where the first one was, so no duplicate is added.
    /// </summary>
    private static bool TryBuildInstalled(string html, out string updated, out string problem)
    {
        updated = html;
        problem = string.Empty;
        var newline = html.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var integrationBlock = string.Join(newline, new[] { BeginMarker, ScriptReference, EndMarker, string.Empty });

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
            problem = "an existing player integration marker is incomplete";
            return false;
        }

        if (firstRemovedBlockStart >= 0)
        {
            updated = rewritten.Insert(Math.Min(firstRemovedBlockStart, rewritten.Length), integrationBlock);
            return true;
        }

        var closingBodyIndex = rewritten.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closingBodyIndex < 0)
        {
            problem = "dashboard index has no closing body tag";
            return false;
        }

        updated = rewritten.Insert(closingBodyIndex, integrationBlock);
        return true;
    }

    private static bool IsBlockPresent(string indexPath)
    {
        try
        {
            if (!File.Exists(indexPath)) return false;
            var html = File.ReadAllText(indexPath);
            return html.Contains(BeginMarker, StringComparison.Ordinal) &&
                   html.Contains(ScriptReference, StringComparison.Ordinal) &&
                   html.Contains(EndMarker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes Radio Meta's blocks. Called only when the plugin is
    /// being uninstalled (rule 2); at normal shutdown the block stays in place.
    /// </summary>
    internal static void RemoveForUninstall(IApplicationPaths applicationPaths)
    {
        var indexPath = GetIndexPath(applicationPaths);
        lock (FileLock)
        using (SharedIndexLock.Acquire())
        {
            try
            {
                for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
                {
                    if (!File.Exists(indexPath))
                    {
                        SetInstallState(false, "dashboard index unavailable during uninstall");
                        return;
                    }

                    var html = File.ReadAllText(indexPath);
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
                        SetInstallState(false, "incomplete integration marker found during uninstall");
                        _logger?.Warn("Radio Meta found an incomplete player integration marker during uninstall; no automatic removal was attempted.");
                        return;
                    }

                    if (!removedAny)
                    {
                        SetInstallState(false, "no marked integration block found during uninstall");
                        return;
                    }

                    var current = File.ReadAllText(indexPath);
                    if (!string.Equals(current, html, StringComparison.Ordinal)) continue;

                    WriteAtomically(indexPath, rewritten);
                    SetInstallState(false, "removed during uninstall");
                    _logger?.Info("Radio Meta removed its DLL-managed player integration reference from the dashboard index during uninstall.");
                    return;
                }

                _logger?.Warn("Radio Meta could not remove its player integration reference because the dashboard index kept changing.");
            }
            catch (Exception ex)
            {
                SetInstallState(false, "uninstall cleanup failed: " + ex.GetType().Name);
                _logger?.Warn("Radio Meta could not remove its player integration reference during uninstall ({0}).", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Stops a background install or check that is still running. The block is
    /// deliberately left in index.html (rule 2), so a browser that loads the page
    /// while Emby restarts still gets it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Not disposed: a background check may still be waiting on its token.
        try { _stopping.Cancel(); } catch { }
    }

    private bool LeaveAlone(string status)
    {
        SetInstallState(false, status);
        _logger?.Warn("Radio Meta " + RadioMetaBuildInfo.Version + " left the dashboard index unchanged: " + status + ".");
        return false;
    }

    private static void SetInstallState(bool installed, string status)
    {
        _isInstalled = installed;
        _installStatus = status;
    }

    /// <summary>
    /// The system-wide named mutex shared with other plugins (rule 3). If it can't be
    /// opened, or another holder doesn't release it within 10 seconds, Radio Meta goes
    /// on without it; the re-read before the swap and the later check still protect
    /// the file.
    /// </summary>
    private sealed class SharedIndexLock : IDisposable
    {
        private Mutex? _mutex;
        private bool _owned;

        internal static SharedIndexLock Acquire()
        {
            var sharedLock = new SharedIndexLock();
            try
            {
                sharedLock._mutex = new Mutex(false, SharedMutexName);
                try
                {
                    sharedLock._owned = sharedLock._mutex.WaitOne(SharedLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder exited without releasing it; we now own it.
                    sharedLock._owned = true;
                }

                if (!sharedLock._owned)
                {
                    _logger?.Warn("Radio Meta waited 10 seconds for the shared dashboard index lock; continuing without it.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("Radio Meta could not open the shared dashboard index lock ({0}); continuing without it.", ex.GetType().Name);
            }

            return sharedLock;
        }

        public void Dispose()
        {
            if (_owned)
            {
                try { _mutex?.ReleaseMutex(); } catch { }
                _owned = false;
            }

            _mutex?.Dispose();
            _mutex = null;
        }
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

        var selectedIndex = html.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (selectedIndex < 0) return false;
        var endIndex = html.IndexOf(EndMarker, selectedIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            markerError = "matching end marker not found";
            return false;
        }

        blockStart = html.LastIndexOf('\n', selectedIndex);
        blockStart = blockStart < 0 ? 0 : blockStart + 1;
        blockEnd = endIndex + EndMarker.Length;
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
