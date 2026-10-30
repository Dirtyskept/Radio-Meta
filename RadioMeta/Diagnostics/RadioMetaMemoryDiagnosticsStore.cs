using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace RadioMeta;

/// <summary>A privacy-safe, process-wide memory sample. It deliberately stores no paths, URLs, or IDs.</summary>
public sealed class RadioMetaMemorySampleDto
{
    public DateTimeOffset TimestampUtc { get; set; }
    public long? ProcessWorkingSetBytes { get; set; }
    public long? ProcessPrivateMemoryBytes { get; set; }
    public long ManagedHeapBytes { get; set; }
    public long TotalAllocatedBytesSinceProcessStart { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int? ProcessThreadCount { get; set; }
    public int? ProcessHandleCount { get; set; }
}

/// <summary>Bounded memory history and a conservative growth warning for the diagnostic report.</summary>
public sealed class RadioMetaMemoryDiagnosticsSnapshotDto
{
    public bool Available { get; set; }
    public bool SamplingEnabled { get; set; }
    public int SamplingIntervalSeconds { get; set; }
    public int MaximumRetainedSamples { get; set; }
    public int RetainedSampleCount { get; set; }
    public DateTimeOffset? FirstSampleAtUtc { get; set; }
    public DateTimeOffset? LatestSampleAtUtc { get; set; }
    public double ObservedWindowMinutes { get; set; }
    public bool TrendAssessmentReady { get; set; }
    public bool PotentialGrowthDetected { get; set; }
    public string TrendAssessment { get; set; } = string.Empty;
    public long? CurrentWorkingSetBytes { get; set; }
    public long? CurrentPrivateMemoryBytes { get; set; }
    public long CurrentManagedHeapBytes { get; set; }
    public long CurrentTotalAllocatedBytesSinceProcessStart { get; set; }
    public long? WorkingSetChangeFromFirstBytes { get; set; }
    public long? PrivateMemoryChangeFromFirstBytes { get; set; }
    public long ManagedHeapChangeFromFirstBytes { get; set; }
    public long? PeakWorkingSetBytes { get; set; }
    public long? PeakPrivateMemoryBytes { get; set; }
    public long PeakManagedHeapBytes { get; set; }
    public int CurrentGen0Collections { get; set; }
    public int CurrentGen1Collections { get; set; }
    public int CurrentGen2Collections { get; set; }
    public int? CurrentProcessThreadCount { get; set; }
    public int? CurrentProcessHandleCount { get; set; }
    public List<RadioMetaMemorySampleDto> RecentSamples { get; set; } = new();
}

/// <summary>
/// Low-overhead process memory monitor for diagnostics. Samples every five minutes,
/// keeps at most seven days, and never forces garbage collection. Process memory is
/// shared by Emby and all loaded plugins, so growth is a warning signal, not proof
/// that Radio Meta alone owns the memory.
/// </summary>
public sealed class RadioMetaMemoryDiagnosticsStore
{
    public const int SamplingIntervalSeconds = 300;
    public const int MaximumRetainedSamples = 2016; // 7 days at five-minute intervals.
    private const int ReportSampleLimit = 48; // Keep detailed samples for the most recent four hours.
    private const int ReportTrendSampleStride = 12; // Also report one sample per hour across the retained week.
    private const int MinimumTrendSampleCount = 13;
    private const double MinimumTrendWindowMinutes = 60;
    private const long MiB = 1024L * 1024L;

    private readonly object _sync = new();
    private readonly List<RadioMetaMemorySampleDto> _samples = new();
    private Timer? _timer;
    private bool _samplingEnabled;

    public void Start()
    {
        lock (_sync)
        {
            if (_timer is not null)
            {
                return;
            }

            _samplingEnabled = true;
            _timer = new Timer(
                static state => ((RadioMetaMemoryDiagnosticsStore)state!).TryRecordSample(force: false),
                this,
                TimeSpan.FromSeconds(SamplingIntervalSeconds),
                TimeSpan.FromSeconds(SamplingIntervalSeconds));
        }

        // Capture an initial baseline as soon as the server entry point starts.
        TryRecordSample(force: true);
    }

    public void Stop()
    {
        Timer? timer;
        lock (_sync)
        {
            timer = _timer;
            _timer = null;
            _samplingEnabled = false;
        }

        try { timer?.Dispose(); } catch { }
    }

    public RadioMetaMemoryDiagnosticsSnapshotDto GetSnapshot()
    {
        // Health may be queried before the entry point has finished starting. Ensure
        // periodic sampling is running, then take a one-off current reading for this
        // report without adding extra entries to the bounded five-minute history.
        Start();

        lock (_sync)
        {
            var retainedSamples = _samples.ToArray();
            var first = retainedSamples.FirstOrDefault();
            RadioMetaMemorySampleDto? current;
            try { current = Capture(DateTimeOffset.UtcNow); }
            catch { current = retainedSamples.LastOrDefault(); }
            var reportSamples = retainedSamples.ToList();
            if (current is not null && (reportSamples.Count == 0 || current.TimestampUtc > reportSamples[^1].TimestampUtc))
            {
                reportSamples.Add(current);
            }
            var observedMinutes = first is null || current is null
                ? 0
                : Math.Max(0, (current.TimestampUtc - first.TimestampUtc).TotalMinutes);
            var ready = retainedSamples.Length >= MinimumTrendSampleCount && observedMinutes >= MinimumTrendWindowMinutes;

            var workingSetChange = Change(first?.ProcessWorkingSetBytes, current?.ProcessWorkingSetBytes);
            var privateChange = Change(first?.ProcessPrivateMemoryBytes, current?.ProcessPrivateMemoryBytes);
            var managedChange = current is null ? 0 : current.ManagedHeapBytes - (first?.ManagedHeapBytes ?? current.ManagedHeapBytes);
            var potentialGrowth = ready && HasPotentialGrowth(first, current);

            string assessment;
            if (!ready)
            {
                assessment = "Collecting baseline; growth assessment becomes meaningful after at least 60 minutes and 13 samples. This monitor never forces garbage collection.";
            }
            else if (potentialGrowth)
            {
                assessment = "WARNING: process memory rose beyond a conservative growth threshold across the retained window. This may be a leak or a normal workload increase; process-wide metrics cannot attribute it to Radio Meta. Review the sample timeline and repeat after the workload stabilizes.";
            }
            else
            {
                assessment = "No configured growth threshold was crossed across the retained window. This does not prove that no leak exists; process-wide metrics are shared by Emby and all plugins.";
            }

            return new RadioMetaMemoryDiagnosticsSnapshotDto
            {
                Available = current is not null,
                SamplingEnabled = _samplingEnabled,
                SamplingIntervalSeconds = SamplingIntervalSeconds,
                MaximumRetainedSamples = MaximumRetainedSamples,
                RetainedSampleCount = retainedSamples.Length,
                FirstSampleAtUtc = first?.TimestampUtc,
                LatestSampleAtUtc = current?.TimestampUtc,
                ObservedWindowMinutes = Math.Round(observedMinutes, 1),
                TrendAssessmentReady = ready,
                PotentialGrowthDetected = potentialGrowth,
                TrendAssessment = assessment,
                CurrentWorkingSetBytes = current?.ProcessWorkingSetBytes,
                CurrentPrivateMemoryBytes = current?.ProcessPrivateMemoryBytes,
                CurrentManagedHeapBytes = current?.ManagedHeapBytes ?? 0,
                CurrentTotalAllocatedBytesSinceProcessStart = current?.TotalAllocatedBytesSinceProcessStart ?? 0,
                WorkingSetChangeFromFirstBytes = workingSetChange,
                PrivateMemoryChangeFromFirstBytes = privateChange,
                ManagedHeapChangeFromFirstBytes = managedChange,
                PeakWorkingSetBytes = Max(reportSamples.Select(x => x.ProcessWorkingSetBytes)),
                PeakPrivateMemoryBytes = Max(reportSamples.Select(x => x.ProcessPrivateMemoryBytes)),
                PeakManagedHeapBytes = reportSamples.Count == 0 ? 0 : reportSamples.Max(x => x.ManagedHeapBytes),
                CurrentGen0Collections = current?.Gen0Collections ?? 0,
                CurrentGen1Collections = current?.Gen1Collections ?? 0,
                CurrentGen2Collections = current?.Gen2Collections ?? 0,
                CurrentProcessThreadCount = current?.ProcessThreadCount,
                CurrentProcessHandleCount = current?.ProcessHandleCount,
                RecentSamples = BuildReportSamples(reportSamples)
            };
        }
    }

    private static List<RadioMetaMemorySampleDto> BuildReportSamples(List<RadioMetaMemorySampleDto> samples)
    {
        // Include an hourly long-range trend and the most recent four hours at full
        // five-minute resolution, avoiding a huge 2,000-line diagnostic paste.
        var selected = new SortedDictionary<long, RadioMetaMemorySampleDto>();
        for (var i = 0; i < samples.Count; i += ReportTrendSampleStride)
        {
            var sample = samples[i];
            selected[sample.TimestampUtc.UtcDateTime.Ticks] = sample;
        }

        foreach (var sample in samples.TakeLast(ReportSampleLimit))
        {
            selected[sample.TimestampUtc.UtcDateTime.Ticks] = sample;
        }

        return selected.Values.Select(Clone).ToList();
    }

    private void TryRecordSample(bool force)
    {
        lock (_sync)
        {
            if (!_samplingEnabled)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (_samples.Count > 0)
            {
                var minimumSpacingSeconds = force ? 30 : SamplingIntervalSeconds;
                if ((now - _samples[^1].TimestampUtc).TotalSeconds < minimumSpacingSeconds)
                {
                    return;
                }
            }

            try
            {
                var sample = Capture(now);
                _samples.Add(sample);
                if (_samples.Count > MaximumRetainedSamples)
                {
                    _samples.RemoveRange(0, _samples.Count - MaximumRetainedSamples);
                }
            }
            catch
            {
                // Diagnostics must never interrupt playback or server maintenance.
            }
        }
    }

    private static RadioMetaMemorySampleDto Capture(DateTimeOffset now)
    {
        long? workingSet = null;
        long? privateMemory = null;
        int? threadCount = null;
        int? handleCount = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            workingSet = Math.Max(0, process.WorkingSet64);
            privateMemory = Math.Max(0, process.PrivateMemorySize64);
            try { threadCount = process.Threads.Count; } catch { }
            try { handleCount = process.HandleCount; } catch { }
        }
        catch
        {
            // GC-managed counters remain useful if OS process counters are unavailable.
        }

        return new RadioMetaMemorySampleDto
        {
            TimestampUtc = now,
            ProcessWorkingSetBytes = workingSet,
            ProcessPrivateMemoryBytes = privateMemory,
            ManagedHeapBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false)),
            TotalAllocatedBytesSinceProcessStart = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false)),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            ProcessThreadCount = threadCount,
            ProcessHandleCount = handleCount
        };
    }

    private static bool HasPotentialGrowth(RadioMetaMemorySampleDto? first, RadioMetaMemorySampleDto? current)
    {
        if (first is null || current is null) return false;

        var privateDelta = Change(first.ProcessPrivateMemoryBytes, current.ProcessPrivateMemoryBytes);
        if (privateDelta.HasValue && first.ProcessPrivateMemoryBytes is > 0)
        {
            var privateThreshold = Math.Max(128 * MiB, (long)(first.ProcessPrivateMemoryBytes.Value * 0.20));
            if (privateDelta.Value >= privateThreshold) return true;
        }

        var managedDelta = current.ManagedHeapBytes - first.ManagedHeapBytes;
        var managedThreshold = Math.Max(64 * MiB, (long)(first.ManagedHeapBytes * 0.25));
        return managedDelta >= managedThreshold;
    }

    private static long? Change(long? first, long? current) =>
        first.HasValue && current.HasValue ? current.Value - first.Value : null;

    private static long? Max(IEnumerable<long?> values)
    {
        var valid = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return valid.Length == 0 ? null : valid.Max();
    }

    private static RadioMetaMemorySampleDto Clone(RadioMetaMemorySampleDto sample) => new()
    {
        TimestampUtc = sample.TimestampUtc,
        ProcessWorkingSetBytes = sample.ProcessWorkingSetBytes,
        ProcessPrivateMemoryBytes = sample.ProcessPrivateMemoryBytes,
        ManagedHeapBytes = sample.ManagedHeapBytes,
        TotalAllocatedBytesSinceProcessStart = sample.TotalAllocatedBytesSinceProcessStart,
        Gen0Collections = sample.Gen0Collections,
        Gen1Collections = sample.Gen1Collections,
        Gen2Collections = sample.Gen2Collections,
        ProcessThreadCount = sample.ProcessThreadCount,
        ProcessHandleCount = sample.ProcessHandleCount
    };
}
