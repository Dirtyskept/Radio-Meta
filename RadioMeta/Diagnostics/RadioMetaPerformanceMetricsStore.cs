using System;
using System.Collections.Generic;
using System.Linq;

namespace RadioMeta;

/// <summary>One sanitized, in-memory performance measurement from a metadata lookup.</summary>
public sealed class RadioMetaPerformanceSampleDto
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public double? ShoutcastDurationMs { get; set; }
    public bool ShoutcastAttempted { get; set; }
    public bool ShoutcastCancelled { get; set; }
    public double? IcyDurationMs { get; set; }
    public int IcyIntervalsAttempted { get; set; }
    public bool IcyAttempted { get; set; }
    public bool IcyCancelled { get; set; }
    public bool CacheHit { get; set; }
    public bool ProcessStarted { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string ResultSource { get; set; } = string.Empty;
}

/// <summary>Rolling performance totals and recent samples for the diagnostics page.</summary>
public sealed class RadioMetaPerformanceSnapshotDto
{
    public int RetainedSampleCount { get; set; }
    public int NetworkLookupCount { get; set; }
    public int UncachedNetworkLookupCount { get; set; }
    public int NetworkCacheHitCount { get; set; }
    public double AverageNetworkLookupMs { get; set; }
    public double MaxNetworkLookupMs { get; set; }
    public int DirectIcyReadCount { get; set; }
    public double AverageIcyDurationMs { get; set; }
    public double AverageIcyIntervalsAttempted { get; set; }
    public int FfprobeAttemptCount { get; set; }
    public int FfprobeStartedCount { get; set; }
    public int FfprobeNonSuccessCount { get; set; }
    public double AverageFfprobeDurationMs { get; set; }
    public double MaxFfprobeDurationMs { get; set; }
    public List<RadioMetaPerformanceSampleDto> RecentSamples { get; set; } = new();
}

/// <summary>
/// Keeps a bounded performance sample window in memory only. It does not write
/// metrics into history/configuration and does not retain URLs, paths or IDs.
/// </summary>
public sealed class RadioMetaPerformanceMetricsStore
{
    private const int MaximumSamples = 120;
    private const int RecentSampleLimit = 24;
    private readonly object _sync = new();
    private readonly List<RadioMetaPerformanceSampleDto> _samples = new();

    public void RecordNetworkLookup(
        string? stationName,
        double durationMs,
        double shoutcastDurationMs,
        double? icyDurationMs,
        int icyIntervalsAttempted,
        bool icyAttempted,
        bool cacheHit,
        string? outcome,
        string? resultSource,
        bool shoutcastAttempted = true,
        bool shoutcastCancelled = false,
        bool icyCancelled = false)
    {
        Add(new RadioMetaPerformanceSampleDto
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            StationName = SafeStation(stationName),
            Operation = "Network lookup",
            DurationMs = RoundMs(durationMs),
            ShoutcastDurationMs = shoutcastAttempted ? RoundMs(shoutcastDurationMs) : null,
            ShoutcastAttempted = shoutcastAttempted,
            ShoutcastCancelled = shoutcastAttempted && shoutcastCancelled,
            IcyDurationMs = icyAttempted && icyDurationMs.HasValue ? RoundMs(icyDurationMs.Value) : null,
            IcyIntervalsAttempted = Math.Clamp(icyIntervalsAttempted, 0, RadioMetadataReader.DirectIcyMaxIntervals),
            IcyAttempted = icyAttempted,
            IcyCancelled = icyAttempted && icyCancelled,
            CacheHit = cacheHit,
            Outcome = SafeLabel(outcome),
            ResultSource = SafeLabel(resultSource)
        });
    }

    public void RecordFfprobeAttempt(string? stationName, double durationMs, bool processStarted, string? outcome)
    {
        Add(new RadioMetaPerformanceSampleDto
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            StationName = SafeStation(stationName),
            Operation = "FFprobe process",
            DurationMs = RoundMs(durationMs),
            ProcessStarted = processStarted,
            Outcome = SafeLabel(outcome),
            ResultSource = "FFprobe"
        });
    }

    public RadioMetaPerformanceSnapshotDto GetSnapshot()
    {
        lock (_sync)
        {
            var samples = _samples.ToArray();
            var network = samples.Where(s => s.Operation == "Network lookup").ToArray();
            var uncachedNetwork = network.Where(s => !s.CacheHit).ToArray();
            var icyAttempts = network.Where(s => s.IcyAttempted && s.IcyDurationMs.HasValue).ToArray();
            var icyCompleted = icyAttempts.Where(s => !s.IcyCancelled).ToArray();
            var ffprobe = samples.Where(s => s.Operation == "FFprobe process").ToArray();
            var startedFfprobe = ffprobe.Where(s => s.ProcessStarted).ToArray();

            return new RadioMetaPerformanceSnapshotDto
            {
                RetainedSampleCount = samples.Length,
                NetworkLookupCount = network.Length,
                UncachedNetworkLookupCount = uncachedNetwork.Length,
                NetworkCacheHitCount = network.Count(s => s.CacheHit),
                AverageNetworkLookupMs = Average(uncachedNetwork.Select(s => s.DurationMs)),
                MaxNetworkLookupMs = Maximum(uncachedNetwork.Select(s => s.DurationMs)),
                DirectIcyReadCount = icyAttempts.Length,
                AverageIcyDurationMs = Average(icyCompleted.Select(s => s.IcyDurationMs!.Value)),
                AverageIcyIntervalsAttempted = Average(icyCompleted.Select(s => (double)s.IcyIntervalsAttempted)),
                FfprobeAttemptCount = ffprobe.Length,
                FfprobeStartedCount = startedFfprobe.Length,
                FfprobeNonSuccessCount = ffprobe.Count(s => !string.Equals(s.Outcome, "success", StringComparison.OrdinalIgnoreCase)),
                AverageFfprobeDurationMs = Average(startedFfprobe.Select(s => s.DurationMs)),
                MaxFfprobeDurationMs = Maximum(startedFfprobe.Select(s => s.DurationMs)),
                RecentSamples = samples.TakeLast(RecentSampleLimit).Reverse().Select(Clone).ToList()
            };
        }
    }

    private void Add(RadioMetaPerformanceSampleDto sample)
    {
        lock (_sync)
        {
            _samples.Add(sample);
            if (_samples.Count > MaximumSamples)
            {
                _samples.RemoveRange(0, _samples.Count - MaximumSamples);
            }
        }
    }

    private static RadioMetaPerformanceSampleDto Clone(RadioMetaPerformanceSampleDto sample) => new()
    {
        TimestampUtc = sample.TimestampUtc,
        StationName = sample.StationName,
        Operation = sample.Operation,
        DurationMs = sample.DurationMs,
        ShoutcastDurationMs = sample.ShoutcastDurationMs,
        ShoutcastAttempted = sample.ShoutcastAttempted,
        ShoutcastCancelled = sample.ShoutcastCancelled,
        IcyDurationMs = sample.IcyDurationMs,
        IcyIntervalsAttempted = sample.IcyIntervalsAttempted,
        IcyAttempted = sample.IcyAttempted,
        IcyCancelled = sample.IcyCancelled,
        CacheHit = sample.CacheHit,
        ProcessStarted = sample.ProcessStarted,
        Outcome = sample.Outcome,
        ResultSource = sample.ResultSource
    };

    private static double Average(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? 0 : RoundMs(array.Average());
    }

    private static double Maximum(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? 0 : RoundMs(array.Max());
    }

    private static double RoundMs(double value) => Math.Round(Math.Max(0, value), 1);
    private static string SafeStation(string? value) => SafeLabel(value, 100);
    private static string SafeLabel(string? value, int limit = 32)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var safe = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
        return safe.Length > limit ? safe[..limit] : safe;
    }
}
