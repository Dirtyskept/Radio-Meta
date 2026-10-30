using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RadioMeta;

/// <summary>
/// Metadata returned for one radio station.
/// </summary>
public sealed class RadioStationMetadata
{
    public string StationName { get; init; } = string.Empty;
    public string ArtworkPath { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string SongTitle { get; init; } = string.Empty;
    public string StreamTitle { get; init; } = string.Empty;
    public string StreamUrl { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;

    public string StationLine => RadioMetaValueHelpers.LimitText(StationName, 26);
    public string ArtistLine => RadioMetaValueHelpers.LimitText(Artist, 26);
    public string SongTitleLine => RadioMetaValueHelpers.LimitText(SongTitle, 26);

}

/// <summary>Result of an explicit, one-shot stream metadata test from the dashboard.</summary>
public sealed class RadioStreamMetadataTestResult
{
    public string StationName { get; init; } = string.Empty;
    public string StreamTitle { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string SongTitle { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public double ElapsedMilliseconds { get; init; }
}

/// <summary>
/// Reads radio stream metadata from HTTP/ICY station metadata, with FFprobe as a fallback.
/// Results are cached per station file to reduce repeated work.
/// </summary>
public sealed partial class RadioMetadataReader
{
    // These are intentional reliability guardrails for live playback metadata.
    // Do not lower them solely for performance without testing stations whose
    // metadata can be delayed or intermittent (including Affinity Radio).
    public const int LiveMetadataSourceTimeoutSeconds = 5;
    public const int ManualStreamTestTimeoutSeconds = 30;
    public const int DirectIcyMaxIntervals = 40;

    private readonly RadioMetaDiagnosticEventStore? _diagnostics;
    private readonly RadioMetaPerformanceMetricsStore? _performance;
    private readonly RadioMetaAdaptiveRoutingStateStore _adaptiveRoutingStateStore;
    private readonly RadioMetaAdaptiveMetadataRouting _adaptiveMetadataRouting;

    public RadioMetadataReader(
        RadioMetaDiagnosticEventStore? diagnostics = null,
        RadioMetaPerformanceMetricsStore? performance = null,
        string? persistenceDirectory = null)
    {
        _diagnostics = diagnostics;
        _performance = performance;
        _adaptiveRoutingStateStore = new RadioMetaAdaptiveRoutingStateStore(persistenceDirectory, diagnostics);
        _adaptiveMetadataRouting = new RadioMetaAdaptiveMetadataRouting(_adaptiveRoutingStateStore);
    }

    /// <summary>Validates a user-supplied stream test URL and its redirect destinations.</summary>
    internal static Task<bool> IsSafeExternalTestUrlAsync(
        string value, CancellationToken cancellationToken = default)
        => RadioMetadataTestUrlSafety.IsSafeExternalTestUrlAsync(value, cancellationToken);

    private sealed class IcyReadMetrics
    {
        public int IntervalsAttempted { get; set; }
    }

    private static readonly TimeSpan CacheDuration =
        TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(12);

    private const int MaxMetadataCacheEntries = 512;
    private readonly object _cacheMaintenanceLock = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, NetworkCacheEntry> _networkCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan NetworkCacheDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Builds the station-only card data without opening the stream or starting
    /// FFprobe. Artwork lookup is local-file-only.
    /// </summary>
    public RadioStationMetadata ReadStationInfoOnly(string stationFilePath)
    {
        var stationName = Path.GetFileNameWithoutExtension(stationFilePath);
        var folder = Path.GetDirectoryName(stationFilePath) ?? string.Empty;
        return new RadioStationMetadata
        {
            StationName = stationName,
            ArtworkPath = FindArtwork(folder, stationName),
            Artist = string.Empty,
            SongTitle = string.Empty,
            StreamTitle = string.Empty,
            StreamUrl = string.Empty
        };
    }

    /// <summary>Clears volatile metadata-result caches. Durable endpoint health state is intentionally retained.</summary>
    public void ClearCaches()
    {
        _cache.Clear();
        _networkCache.Clear();
    }

    /// <summary>Clears learned per-station source preferences when the adaptive-routing setting changes.</summary>
    internal void ResetAdaptiveMetadataRouting() => _adaptiveMetadataRouting.Reset();

    /// <summary>Returns counts and configured limits without exposing cache keys or station URLs.</summary>
    public RadioMetaMetadataCacheDiagnosticDto GetCacheDiagnosticsSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var probe = _cache.ToArray();
        var network = _networkCache.ToArray();
        var routingState = _adaptiveRoutingStateStore.GetSummary();

        return new RadioMetaMetadataCacheDiagnosticDto
        {
            ProbeCacheEntries = probe.Length,
            ProbeCacheFreshEntries = probe.Count(item => now - item.Value.CreatedAt < CacheDuration),
            ProbeCacheExpiredEntries = probe.Count(item => now - item.Value.CreatedAt >= CacheDuration),
            ProbeCacheMaximumEntries = MaxMetadataCacheEntries,
            ProbeCacheDurationSeconds = (int)CacheDuration.TotalSeconds,
            NetworkCacheEntries = network.Length,
            NetworkCacheFreshEntries = network.Count(item => now - item.Value.CreatedAt < NetworkCacheDuration),
            NetworkCacheExpiredEntries = network.Count(item => now - item.Value.CreatedAt >= NetworkCacheDuration),
            NetworkCacheMaximumEntries = MaxMetadataCacheEntries,
            NetworkCacheDurationSeconds = (int)NetworkCacheDuration.TotalSeconds,
            UnsupportedShoutcastEndpointEntries = routingState.PersistentEndpointStateEntries,
            UnsupportedShoutcastEndpointActiveEntries = routingState.AutomaticallyBlockedEndpointEntries + routingState.ManuallyBlockedEndpointEntries,
            UnsupportedShoutcastEndpointExpiredEntries = routingState.EndpointsReadyForRecoveryProbe,
            UnsupportedShoutcastEndpointMaximumEntries = routingState.EndpointStateMaximumEntries,
            UnsupportedShoutcastEndpointDurationSeconds = routingState.EndpointPermanentRecoveryHours * 3600,
            PersistentAdaptiveStationRoutingEntries = routingState.PersistentStationRoutingEntries,
            PersistentEndpointStateEntries = routingState.PersistentEndpointStateEntries,
            AutomaticallyBlockedEndpointEntries = routingState.AutomaticallyBlockedEndpointEntries,
            ManuallyBlockedEndpointEntries = routingState.ManuallyBlockedEndpointEntries,
            EndpointBackoffEntries = routingState.EndpointBackoffEntries,
            EndpointsReadyForRecoveryProbe = routingState.EndpointsReadyForRecoveryProbe,
            EndpointRecoveryLookupThreshold = routingState.EndpointRecoveryLookupThreshold,
            EndpointPermanentRecoveryHours = routingState.EndpointPermanentRecoveryHours,
            TransientBackoffFirstLookups = routingState.TransientBackoffFirstLookups,
            TransientBackoffMaximumLookups = routingState.TransientBackoffMaximumLookups,
            RouteRecheckInitialLookups = routingState.RouteRecheckInitialLookups,
            RouteRecheckMaximumLookups = routingState.RouteRecheckMaximumLookups,
            RoutingStatePersistenceEnabled = routingState.PersistenceEnabled,
            RoutingStatePersistenceHealthy = routingState.PersistenceHealthy,
            RoutingStatePersistenceError = routingState.PersistenceError,
            RoutingStateLastPersistedUtc = routingState.LastPersistedUtc
        };
    }

    public RadioStationMetadata Read(
        string stationFilePath,
        string ffprobePath,
        bool forceRefresh = false,
        Func<string, string, bool>? ignoreMetadataPhrase = null)
    {
        if (string.IsNullOrWhiteSpace(stationFilePath))
        {
            return Failure("Station file path is empty.");
        }

        if (string.IsNullOrWhiteSpace(ffprobePath) ||
            !File.Exists(ffprobePath))
        {
            _diagnostics?.Record(Path.GetFileNameWithoutExtension(stationFilePath), "FFprobe", "error", string.Empty, string.Empty, "FFprobe executable was not found.");
            return Failure("ffprobe executable was not found.");
        }

        string fullStationPath;

        try
        {
            fullStationPath = Path.GetFullPath(stationFilePath);
        }
        catch (Exception ex)
        {
            return Failure("Invalid station file path (" + ex.GetType().Name + ").");
        }

        if (!File.Exists(fullStationPath))
        {
            return Failure("Station file was not found.");
        }

        var stationName = Path.GetFileNameWithoutExtension(fullStationPath);
        if (!forceRefresh &&
            _cache.TryGetValue(fullStationPath, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < CacheDuration &&
            !IsIgnoredMetadata(cached.Metadata, stationName, ignoreMetadataPhrase))
        {
            return cached.Metadata;
        }

        var metadata = ReadUncached(
            fullStationPath,
            ffprobePath,
            ignoreMetadataPhrase);
        if (!string.IsNullOrWhiteSpace(metadata.Error))
        {
            _diagnostics?.Record(stationName, "FFprobe", "error", metadata.SongTitle, metadata.Artist, metadata.Error, metadata.StreamTitle);
        }
        else if (!string.IsNullOrWhiteSpace(metadata.SongTitle))
        {
            _diagnostics?.Record(stationName, "FFprobe", "parsed", metadata.SongTitle, metadata.Artist, "FFprobe returned a usable title for the current-track validator.", metadata.StreamTitle);
        }

        // Keep the previous successful result if a refresh fails.
        if (!string.IsNullOrEmpty(metadata.Error) &&
            _cache.TryGetValue(fullStationPath, out var previous) &&
            string.IsNullOrEmpty(previous.Metadata.Error) &&
            !IsIgnoredMetadata(previous.Metadata, stationName, ignoreMetadataPhrase))
        {
            return previous.Metadata;
        }

        StoreProbeCache(fullStationPath, new CacheEntry(DateTimeOffset.UtcNow, metadata));

        return metadata;
    }

    private static bool IsIgnoredMetadata(
        RadioStationMetadata metadata,
        string stationName,
        Func<string, string, bool>? ignoreMetadataPhrase)
    {
        if (ignoreMetadataPhrase is null)
        {
            return false;
        }

        return ignoreMetadataPhrase(stationName, metadata.StreamTitle) ||
               ignoreMetadataPhrase(stationName, metadata.SongTitle) ||
               ignoreMetadataPhrase(stationName, metadata.Artist + " - " + metadata.SongTitle);
    }


    private void StoreProbeCache(string key, CacheEntry entry)
    {
        _cache[key] = entry;
        TrimCachesIfNeeded();
    }

    private void StoreNetworkCache(string key, NetworkCacheEntry entry)
    {
        _networkCache[key] = entry;
        TrimCachesIfNeeded();
    }

    private void TrimCachesIfNeeded()
    {
        if (_cache.Count <= MaxMetadataCacheEntries && _networkCache.Count <= MaxMetadataCacheEntries) return;
        lock (_cacheMaintenanceLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _cache)
                if (now - pair.Value.CreatedAt >= CacheDuration) _cache.TryRemove(pair.Key, out _);
            foreach (var pair in _networkCache)
                if (now - pair.Value.CreatedAt >= NetworkCacheDuration) _networkCache.TryRemove(pair.Key, out _);

            if (_cache.Count > MaxMetadataCacheEntries)
                foreach (var pair in _cache.OrderBy(item => item.Value.CreatedAt).Take(_cache.Count - MaxMetadataCacheEntries))
                    _cache.TryRemove(pair.Key, out _);
            if (_networkCache.Count > MaxMetadataCacheEntries)
                foreach (var pair in _networkCache.OrderBy(item => item.Value.CreatedAt).Take(_networkCache.Count - MaxMetadataCacheEntries))
                    _networkCache.TryRemove(pair.Key, out _);
        }
    }

    private sealed record CacheEntry(
        DateTimeOffset CreatedAt,
        RadioStationMetadata Metadata);

    private sealed record NetworkCacheEntry(
        DateTimeOffset CreatedAt,
        RadioStationMetadata Metadata);
}