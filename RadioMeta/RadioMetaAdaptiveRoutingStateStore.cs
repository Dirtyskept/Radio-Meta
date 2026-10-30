using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace RadioMeta;

/// <summary>
/// Durable, bounded state for adaptive source selection and station-specific
/// Shoutcast endpoint health. This is intentionally separate from the main
/// plugin settings XML so routine retry checkpoints do not rewrite user settings.
/// The state file stores .strm station paths, not stream URLs or tokens.
/// </summary>
internal sealed class RadioMetaAdaptiveRoutingStateStore
{
    internal const int EndpointRecoveryLookupThreshold = 320;
    internal const int MaximumRouteStations = 512;
    internal const int MaximumEndpointStates = 1024;
    private const int CheckpointEveryMutations = 20;
    private static readonly int[] TransientRetryLookupSteps = { 10, 20, 40, 80 };
    private static readonly TimeSpan PermanentEndpointRecoveryInterval = TimeSpan.FromHours(24);

    private readonly object _sync = new();
    private readonly string? _filePath;
    private readonly RadioMetaDiagnosticEventStore? _diagnostics;
    private readonly Dictionary<string, RadioMetaAdaptiveRoutingStationSnapshot> _stations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EndpointState> _endpoints =
        new(StringComparer.OrdinalIgnoreCase);
    private int _pendingMutations;
    private DateTimeOffset? _lastPersistedUtc;
    private string _lastPersistenceError = string.Empty;
    private bool _persistenceErrorReported;
    private bool _stateFileLoadFailed;

    public RadioMetaAdaptiveRoutingStateStore(string? configurationDirectory, RadioMetaDiagnosticEventStore? diagnostics)
    {
        _diagnostics = diagnostics;
        _filePath = string.IsNullOrWhiteSpace(configurationDirectory)
            ? null
            : Path.Combine(configurationDirectory, "RadioMetaAdaptiveRoutingState.xml");

        LoadFromDisk();
        if (_filePath is not null && !File.Exists(_filePath))
        {
            lock (_sync) WriteLocked();
        }
    }

    public bool TryLoadStationRoutingState(string stationKey, out RadioMetaAdaptiveRoutingStationSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_stations.TryGetValue(NormalizeStationKey(stationKey), out var saved))
            {
                snapshot = Clone(saved);
                return true;
            }
        }

        snapshot = new RadioMetaAdaptiveRoutingStationSnapshot();
        return false;
    }

    public void SaveStationRoutingState(RadioMetaAdaptiveRoutingStationSnapshot snapshot, bool forcePersist = false)
    {
        if (string.IsNullOrWhiteSpace(snapshot.StationKey)) return;
        lock (_sync)
        {
            var key = NormalizeStationKey(snapshot.StationKey);
            snapshot.LastAccessUtc = DateTimeOffset.UtcNow;
            _stations[key] = Clone(snapshot);
            TrimRouteStatesLocked(key);
            MarkMutatedLocked(forcePersist);
        }
    }

    public void ClearStationRoutingStates()
    {
        lock (_sync)
        {
            if (_stations.Count == 0) return;
            _stations.Clear();
            MarkMutatedLocked(forcePersist: true);
        }
    }

    /// <summary>
    /// Advances only on an uncached, valid station lookup. Transient endpoint
    /// retry counters and rare automatic-block recovery progress are independent
    /// for each station's /stats and /7.html endpoints.
    /// </summary>
    public void AdvanceEligibleLookup(string stationKey)
    {
        if (string.IsNullOrWhiteSpace(stationKey)) return;
        var changed = false;
        lock (_sync)
        {
            var normalized = NormalizeStationKey(stationKey);
            foreach (var state in _endpoints.Values.Where(item =>
                         StringComparer.OrdinalIgnoreCase.Equals(item.StationKey, normalized)))
            {
                state.LastUpdatedUtc = DateTimeOffset.UtcNow;
                if (!state.ManualBlocked && !state.AutoBlocked && state.LookupsUntilRetry > 0)
                {
                    state.LookupsUntilRetry--;
                    if (state.LookupsUntilRetry == 0) state.SkipReported = false;
                    changed = true;
                }

                if (state.AutoBlocked && !state.ManualBlocked && state.LookupsSinceBlock < EndpointRecoveryLookupThreshold)
                {
                    state.LookupsSinceBlock++;
                    changed = true;
                }
            }

            if (changed) MarkMutatedLocked(forcePersist: false);
        }
    }

    /// <summary>
    /// Returns false for a manual block, an active automatic unsupported-endpoint
    /// block, or a transient retry countdown. A permanently unsupported endpoint
    /// is probed only after both 24 hours and 320 uncached station lookups.
    /// </summary>
    public bool ShouldAttemptEndpoint(string stationKey, string endpoint, out string skipReason)
    {
        skipReason = string.Empty;
        if (string.IsNullOrWhiteSpace(stationKey)) return true;
        lock (_sync)
        {
            if (!_endpoints.TryGetValue(EndpointKey(stationKey, endpoint), out var state)) return true;
            if (state.ManualBlocked)
            {
                if (!state.SkipReported)
                {
                    state.SkipReported = true;
                    skipReason = "Skipped by a manual XML block; it remains blocked until its manualBlock entry is removed or changed in RadioMetaAdaptiveRoutingState.xml.";
                }
                return false;
            }

            if (state.AutoBlocked)
            {
                var now = DateTimeOffset.UtcNow;
                var timeReady = state.NextRecoveryUtc.HasValue && now >= state.NextRecoveryUtc.Value;
                var lookupsReady = state.LookupsSinceBlock >= EndpointRecoveryLookupThreshold;
                if (!timeReady || !lookupsReady)
                {
                    if (!state.SkipReported)
                    {
                        state.SkipReported = true;
                        var hoursLeft = state.NextRecoveryUtc.HasValue
                            ? Math.Max(0, (int)Math.Ceiling((state.NextRecoveryUtc.Value - now).TotalHours))
                            : 24;
                        var lookupsLeft = Math.Max(0, EndpointRecoveryLookupThreshold - state.LookupsSinceBlock);
                        skipReason = "Automatic unsupported-endpoint block remains active; recovery probe requires " +
                                     hoursLeft + " more hour(s) and " + lookupsLeft + " more eligible station lookup(s).";
                    }
                    return false;
                }

                state.AutoBlocked = false;
                state.LookupsSinceBlock = EndpointRecoveryLookupThreshold;
                state.LastOutcome = "Automatic recovery probe started";
                state.LastOutcomeUtc = now;
                state.LastUpdatedUtc = now;
                state.SkipReported = false;
                MarkMutatedLocked(forcePersist: true);
                return true;
            }

            if (state.LookupsUntilRetry > 0)
            {
                if (!state.SkipReported)
                {
                    state.SkipReported = true;
                    skipReason = "Skipped during transient-failure backoff; retry in " + state.LookupsUntilRetry +
                                 " eligible uncached station lookup(s).";
                }
                return false;
            }

            state.SkipReported = false;
            return true;
        }
    }

    public void RecordEndpointFailure(string stationKey, string endpoint, int? statusCode, string outcome)
    {
        if (string.IsNullOrWhiteSpace(stationKey)) return;
        lock (_sync)
        {
            var key = EndpointKey(stationKey, endpoint);
            if (!_endpoints.TryGetValue(key, out var state))
            {
                state = new EndpointState
                {
                    StationKey = NormalizeStationKey(stationKey),
                    Endpoint = NormalizeEndpoint(endpoint)
                };
                _endpoints[key] = state;
            }
            if (state.ManualBlocked) return;

            var now = DateTimeOffset.UtcNow;
            state.FailureCount = Math.Min(100000, state.FailureCount + 1);
            state.StatusCode = statusCode;
            state.LastOutcome = Limit(outcome, 240);
            state.LastOutcomeUtc = now;
            state.LastUpdatedUtc = now;
            state.SkipReported = false;

            if (statusCode is 404 or 410 or 405)
            {
                state.AutoBlocked = true;
                state.BackoffLevel = 4;
                state.LookupsUntilRetry = 0;
                state.LookupsSinceBlock = 0;
                state.NextRecoveryUtc = now.Add(PermanentEndpointRecoveryInterval);
            }
            else
            {
                state.AutoBlocked = false;
                var stepIndex = Math.Min(Math.Max(state.FailureCount - 1, 0), TransientRetryLookupSteps.Length - 1);
                state.BackoffLevel = stepIndex + 1;
                state.LookupsUntilRetry = TransientRetryLookupSteps[stepIndex];
                state.LookupsSinceBlock = 0;
                state.NextRecoveryUtc = null;
            }

            TrimEndpointStatesLocked(key);
            MarkMutatedLocked(forcePersist: true);
        }
    }

    public void RecordEndpointSuccess(string stationKey, string endpoint, string outcome)
    {
        if (string.IsNullOrWhiteSpace(stationKey)) return;
        lock (_sync)
        {
            var key = EndpointKey(stationKey, endpoint);
            if (!_endpoints.TryGetValue(key, out var state) || state.ManualBlocked) return;
            _endpoints.Remove(key);
            MarkMutatedLocked(forcePersist: true);
        }
    }

    public RadioMetaAdaptiveRoutingStateSummary GetSummary()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            return new RadioMetaAdaptiveRoutingStateSummary
            {
                PersistentStationRoutingEntries = _stations.Count,
                PersistentEndpointStateEntries = _endpoints.Count,
                AutomaticallyBlockedEndpointEntries = _endpoints.Values.Count(item => item.AutoBlocked && !item.ManualBlocked),
                ManuallyBlockedEndpointEntries = _endpoints.Values.Count(item => item.ManualBlocked),
                EndpointBackoffEntries = _endpoints.Values.Count(item => !item.ManualBlocked && !item.AutoBlocked && item.LookupsUntilRetry > 0),
                EndpointsReadyForRecoveryProbe = _endpoints.Values.Count(item => item.AutoBlocked && !item.ManualBlocked &&
                    item.LookupsSinceBlock >= EndpointRecoveryLookupThreshold && item.NextRecoveryUtc.HasValue && now >= item.NextRecoveryUtc.Value),
                EndpointStateMaximumEntries = MaximumEndpointStates,
                EndpointRecoveryLookupThreshold = EndpointRecoveryLookupThreshold,
                EndpointPermanentRecoveryHours = (int)PermanentEndpointRecoveryInterval.TotalHours,
                TransientBackoffFirstLookups = TransientRetryLookupSteps[0],
                TransientBackoffMaximumLookups = TransientRetryLookupSteps[^1],
                RouteRecheckInitialLookups = RadioMetaAdaptiveMetadataRouting.SequentialLookupsBetweenParallelRechecks,
                RouteRecheckMaximumLookups = RadioMetaAdaptiveMetadataRouting.MaximumParallelRecheckInterval,
                LastPersistedUtc = _lastPersistedUtc,
                PersistenceEnabled = _filePath is not null,
                PersistenceHealthy = string.IsNullOrWhiteSpace(_lastPersistenceError),
                PersistenceError = _lastPersistenceError
            };
        }
    }

    private void LoadFromDisk()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;
        try
        {
            var document = XDocument.Load(_filePath, LoadOptions.None);
            var root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "RadioMetaAdaptiveRoutingState", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected adaptive routing state XML root.");

            lock (_sync)
            {
                _lastPersistedUtc = DateTimeOffset.TryParse((string?)root.Attribute("updatedUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var lastSaved)
                    ? lastSaved
                    : null;
                foreach (var element in root.Element("StationRouting")?.Elements("Station") ?? Enumerable.Empty<XElement>())
                {
                    var path = ReadAttribute(element, "stationPath", 2048);
                    if (path.Length == 0 || _stations.Count >= MaximumRouteStations) continue;
                    _stations[NormalizeStationKey(path)] = new RadioMetaAdaptiveRoutingStationSnapshot
                    {
                        StationKey = path,
                        PreferredSource = NormalizeSource(ReadAttribute(element, "preferredSource", 24)),
                        ConsecutiveShoutcastLosses = ParseInt(element, "consecutiveShoutcastLosses", 0, 0, 4),
                        ConsecutiveIcyLosses = ParseInt(element, "consecutiveIcyLosses", 0, 0, 4),
                        PreferredSourceMisses = ParseInt(element, "preferredSourceMisses", 0, 0, 2),
                        LookupsSinceParallelRecheck = ParseInt(element, "lookupsSinceParallelRecheck", 0, 0, 79),
                        ParallelRecheckInterval = ParseInt(element, "parallelRecheckInterval", 10, 10, 80),
                        ConsecutiveSuccessfulRechecks = ParseInt(element, "consecutiveSuccessfulRechecks", 0, 0, 100),
                        LastOutcome = Limit(ReadAttribute(element, "lastOutcome", 240), 240),
                        LastOutcomeUtc = ParseDate(element, "lastOutcomeUtc"),
                        LastAccessUtc = ParseDate(element, "lastAccessUtc") ?? DateTimeOffset.UtcNow
                    };
                }

                foreach (var element in root.Element("Endpoints")?.Elements("Endpoint") ?? Enumerable.Empty<XElement>())
                {
                    var stationPath = ReadAttribute(element, "stationPath", 2048);
                    var endpoint = NormalizeEndpoint(ReadAttribute(element, "endpoint", 16));
                    if (stationPath.Length == 0 || endpoint.Length == 0 || _endpoints.Count >= MaximumEndpointStates) continue;
                    var state = new EndpointState
                    {
                        StationKey = stationPath,
                        Endpoint = endpoint,
                        ManualBlocked = ParseBool(element, "manualBlock", false),
                        AutoBlocked = ParseBool(element, "autoBlocked", false),
                        FailureCount = ParseInt(element, "failureCount", 0, 0, 100000),
                        BackoffLevel = ParseInt(element, "backoffLevel", 0, 0, 4),
                        LookupsUntilRetry = ParseInt(element, "lookupsUntilRetry", 0, 0, 80),
                        LookupsSinceBlock = ParseInt(element, "lookupsSinceBlock", 0, 0, EndpointRecoveryLookupThreshold),
                        StatusCode = ParseNullableInt(element, "statusCode", 100, 599),
                        NextRecoveryUtc = ParseDate(element, "nextRecoveryUtc"),
                        LastOutcome = Limit(ReadAttribute(element, "lastOutcome", 240), 240),
                        LastOutcomeUtc = ParseDate(element, "lastOutcomeUtc"),
                        LastUpdatedUtc = ParseDate(element, "lastUpdatedUtc") ?? DateTimeOffset.UtcNow
                    };
                    _endpoints[EndpointKey(stationPath, endpoint)] = state;
                }
            }
        }
        catch (Exception ex)
        {
            _stateFileLoadFailed = true;
            _lastPersistenceError = "Load failed (" + ex.GetType().Name + "). The state file was left untouched; routing will relearn in memory until the XML is repaired.";
            _diagnostics?.Record("Adaptive routing state", "Persistence", "error", string.Empty, string.Empty, _lastPersistenceError);
        }
    }

    private void TrimRouteStatesLocked(string keepKey)
    {
        if (_stations.Count <= MaximumRouteStations) return;
        var excess = _stations.Count - MaximumRouteStations;
        foreach (var pair in _stations.Where(pair => !StringComparer.OrdinalIgnoreCase.Equals(pair.Key, keepKey))
                     .OrderBy(pair => pair.Value.LastAccessUtc).Take(excess).ToArray())
        {
            _stations.Remove(pair.Key);
        }

        // Enforce the hard cap even if the newest key was the only trim candidate.
        if (_stations.Count > MaximumRouteStations) _stations.Remove(keepKey);
    }

    private void TrimEndpointStatesLocked(string keepKey)
    {
        if (_endpoints.Count <= MaximumEndpointStates) return;
        var excess = _endpoints.Count - MaximumEndpointStates;
        foreach (var pair in _endpoints.Where(pair => !pair.Value.ManualBlocked && !StringComparer.OrdinalIgnoreCase.Equals(pair.Key, keepKey))
                     .OrderBy(pair => pair.Value.LastUpdatedUtc).Take(excess).ToArray())
        {
            _endpoints.Remove(pair.Key);
        }

        // Preserve manual blocks first, but never allow automatic state to grow without bound.
        if (_endpoints.Count > MaximumEndpointStates && _endpoints.TryGetValue(keepKey, out var keep) && !keep.ManualBlocked)
            _endpoints.Remove(keepKey);
        if (_endpoints.Count > MaximumEndpointStates)
        {
            foreach (var pair in _endpoints.Where(pair => !pair.Value.ManualBlocked)
                         .OrderBy(pair => pair.Value.LastUpdatedUtc).Take(_endpoints.Count - MaximumEndpointStates).ToArray())
                _endpoints.Remove(pair.Key);
        }
    }

    private void MarkMutatedLocked(bool forcePersist)
    {
        _pendingMutations++;
        if (forcePersist || _pendingMutations >= CheckpointEveryMutations) WriteLocked();
    }

    private void WriteLocked()
    {
        if (_filePath is null || _stateFileLoadFailed) return;
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("State-file directory is unavailable.");
            Directory.CreateDirectory(directory);
            temporaryPath = _filePath + ".tmp";
            var now = DateTimeOffset.UtcNow;

            var root = new XElement("RadioMetaAdaptiveRoutingState",
                new XAttribute("version", "1"),
                new XAttribute("updatedUtc", now.ToString("O", CultureInfo.InvariantCulture)),
                new XComment("Station paths identify local .strm files; stream URLs and tokens are not stored."),
                new XComment("To manually block an endpoint, set manualBlock=true on the Endpoint matching stationPath and endpoint (or add it under Endpoints if absent). Remove the manual Endpoint entry or set manualBlock=false to unblock it. Stop Emby before editing and restart Emby after saving."),
                new XElement("StationRouting", _stations.Values
                    .OrderBy(item => item.StationKey, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new XElement("Station",
                        new XAttribute("stationPath", item.StationKey),
                        new XAttribute("preferredSource", item.PreferredSource ?? string.Empty),
                        new XAttribute("consecutiveShoutcastLosses", item.ConsecutiveShoutcastLosses),
                        new XAttribute("consecutiveIcyLosses", item.ConsecutiveIcyLosses),
                        new XAttribute("preferredSourceMisses", item.PreferredSourceMisses),
                        new XAttribute("lookupsSinceParallelRecheck", item.LookupsSinceParallelRecheck),
                        new XAttribute("parallelRecheckInterval", item.ParallelRecheckInterval),
                        new XAttribute("consecutiveSuccessfulRechecks", item.ConsecutiveSuccessfulRechecks),
                        new XAttribute("lastOutcome", Limit(item.LastOutcome, 240)),
                        new XAttribute("lastOutcomeUtc", FormatDate(item.LastOutcomeUtc)),
                        new XAttribute("lastAccessUtc", FormatDate(item.LastAccessUtc))))),
                new XElement("Endpoints", _endpoints.Values
                    .OrderBy(item => item.StationKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Endpoint, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new XElement("Endpoint",
                        new XAttribute("stationPath", item.StationKey),
                        new XAttribute("endpoint", item.Endpoint),
                        new XAttribute("manualBlock", item.ManualBlocked),
                        new XAttribute("autoBlocked", item.AutoBlocked),
                        new XAttribute("failureCount", item.FailureCount),
                        new XAttribute("backoffLevel", item.BackoffLevel),
                        new XAttribute("lookupsUntilRetry", item.LookupsUntilRetry),
                        new XAttribute("lookupsSinceBlock", item.LookupsSinceBlock),
                        new XAttribute("statusCode", item.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                        new XAttribute("nextRecoveryUtc", FormatDate(item.NextRecoveryUtc)),
                        new XAttribute("lastOutcome", Limit(item.LastOutcome, 240)),
                        new XAttribute("lastOutcomeUtc", FormatDate(item.LastOutcomeUtc)),
                        new XAttribute("lastUpdatedUtc", FormatDate(item.LastUpdatedUtc))))));

            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(stream, SaveOptions.None);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            _lastPersistedUtc = now;
            _lastPersistenceError = string.Empty;
            _persistenceErrorReported = false;
            _pendingMutations = 0;
        }
        catch (Exception ex)
        {
            _lastPersistenceError = "Save failed (" + ex.GetType().Name + "). Latest state remains active in memory.";
            // Avoid retrying a failed routine checkpoint on every subsequent mutation.
            // Forced transition writes still retry immediately when another transition occurs.
            _pendingMutations = 0;
            if (!_persistenceErrorReported)
            {
                _diagnostics?.Record("Adaptive routing state", "Persistence", "error", string.Empty, string.Empty, _lastPersistenceError);
                _persistenceErrorReported = true;
            }
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static RadioMetaAdaptiveRoutingStationSnapshot Clone(RadioMetaAdaptiveRoutingStationSnapshot value) => new()
    {
        StationKey = value.StationKey,
        PreferredSource = value.PreferredSource,
        ConsecutiveShoutcastLosses = value.ConsecutiveShoutcastLosses,
        ConsecutiveIcyLosses = value.ConsecutiveIcyLosses,
        PreferredSourceMisses = value.PreferredSourceMisses,
        LookupsSinceParallelRecheck = value.LookupsSinceParallelRecheck,
        ParallelRecheckInterval = value.ParallelRecheckInterval,
        ConsecutiveSuccessfulRechecks = value.ConsecutiveSuccessfulRechecks,
        LastOutcome = value.LastOutcome,
        LastOutcomeUtc = value.LastOutcomeUtc,
        LastAccessUtc = value.LastAccessUtc
    };

    private static string EndpointKey(string stationKey, string endpoint) => NormalizeStationKey(stationKey) + "\u001f" + NormalizeEndpoint(endpoint);
    private static string NormalizeStationKey(string? value) => (value ?? string.Empty).Trim();
    private static string NormalizeSource(string? value) =>
        string.Equals(value, "ICY", StringComparison.OrdinalIgnoreCase) ? "ICY" :
        string.Equals(value, "Shoutcast", StringComparison.OrdinalIgnoreCase) ? "Shoutcast" : string.Empty;
    private static string NormalizeEndpoint(string? value) =>
        string.Equals(value, "stats", StringComparison.OrdinalIgnoreCase) ? "stats" :
        string.Equals(value, "7.html", StringComparison.OrdinalIgnoreCase) ? "7.html" : string.Empty;
    private static string Limit(string? value, int limit) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= limit ? value : value[..limit];
    private static string ReadAttribute(XElement element, string name, int maxLength) => Limit((string?)element.Attribute(name), maxLength).Trim();
    private static int ParseInt(XElement element, string name, int fallback, int min, int max) =>
        int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, min, max) : fallback;
    private static int? ParseNullableInt(XElement element, string name, int min, int max) =>
        int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, min, max) : null;
    private static bool ParseBool(XElement element, string name, bool fallback) =>
        bool.TryParse((string?)element.Attribute(name), out var value) ? value : fallback;
    private static DateTimeOffset? ParseDate(XElement element, string name) =>
        DateTimeOffset.TryParse((string?)element.Attribute(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : null;
    private static string FormatDate(DateTimeOffset? value) => value.HasValue ? value.Value.ToString("O", CultureInfo.InvariantCulture) : string.Empty;

    private sealed class EndpointState
    {
        public string StationKey { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public bool ManualBlocked { get; set; }
        public bool AutoBlocked { get; set; }
        public int FailureCount { get; set; }
        public int BackoffLevel { get; set; }
        public int LookupsUntilRetry { get; set; }
        public int LookupsSinceBlock { get; set; }
        public int? StatusCode { get; set; }
        public DateTimeOffset? NextRecoveryUtc { get; set; }
        public string LastOutcome { get; set; } = string.Empty;
        public DateTimeOffset? LastOutcomeUtc { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public bool SkipReported { get; set; }
    }
}

internal sealed class RadioMetaAdaptiveRoutingStationSnapshot
{
    public string StationKey { get; set; } = string.Empty;
    public string? PreferredSource { get; set; }
    public int ConsecutiveShoutcastLosses { get; set; }
    public int ConsecutiveIcyLosses { get; set; }
    public int PreferredSourceMisses { get; set; }
    public int LookupsSinceParallelRecheck { get; set; }
    public int ParallelRecheckInterval { get; set; } = 10;
    public int ConsecutiveSuccessfulRechecks { get; set; }
    public string LastOutcome { get; set; } = string.Empty;
    public DateTimeOffset? LastOutcomeUtc { get; set; }
    public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class RadioMetaAdaptiveRoutingStateSummary
{
    public int PersistentStationRoutingEntries { get; set; }
    public int PersistentEndpointStateEntries { get; set; }
    public int AutomaticallyBlockedEndpointEntries { get; set; }
    public int ManuallyBlockedEndpointEntries { get; set; }
    public int EndpointBackoffEntries { get; set; }
    public int EndpointsReadyForRecoveryProbe { get; set; }
    public int EndpointStateMaximumEntries { get; set; }
    public int EndpointRecoveryLookupThreshold { get; set; }
    public int EndpointPermanentRecoveryHours { get; set; }
    public int TransientBackoffFirstLookups { get; set; }
    public int TransientBackoffMaximumLookups { get; set; }
    public int RouteRecheckInitialLookups { get; set; }
    public int RouteRecheckMaximumLookups { get; set; }
    public DateTimeOffset? LastPersistedUtc { get; set; }
    public bool PersistenceEnabled { get; set; }
    public bool PersistenceHealthy { get; set; } = true;
    public string PersistenceError { get; set; } = string.Empty;
}
