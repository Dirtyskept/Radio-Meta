using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace RadioMeta;

internal enum RadioMetaMetadataRoutingMode
{
    ParallelLearning,
    ParallelRecheck,
    PreferredFirst
}

internal sealed record RadioMetaMetadataRoutePlan(
    RadioMetaMetadataRoutingMode Mode,
    string? PreferredSource,
    int LookupsUntilParallelRecheck,
    string Description)
{
    public bool RunsInParallel => Mode != RadioMetaMetadataRoutingMode.PreferredFirst;
}

/// <summary>
/// Learns which existing metadata source is fastest per station. Durable state
/// is checkpointed by RadioMetaAdaptiveRoutingStateStore; station keys never
/// appear in the diagnostic event stream.
/// </summary>
internal sealed class RadioMetaAdaptiveMetadataRouting
{
    internal const int ConsecutiveLossesBeforePreferredFirst = 4;
    internal const int SequentialLookupsBetweenParallelRechecks = 10;
    internal const int MaximumParallelRecheckInterval = 80;
    internal const int PreferredSourceMissesBeforeReset = 2;
    private const int MaximumTrackedStations = RadioMetaAdaptiveRoutingStateStore.MaximumRouteStations;

    private readonly ConcurrentDictionary<string, StationState> _stations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly RadioMetaAdaptiveRoutingStateStore? _stateStore;

    public RadioMetaAdaptiveMetadataRouting(RadioMetaAdaptiveRoutingStateStore? stateStore = null)
    {
        _stateStore = stateStore;
    }

    public RadioMetaMetadataRoutePlan BeginLookup(string stationKey)
    {
        var state = GetState(stationKey);
        lock (state.Sync)
        {
            if (string.IsNullOrWhiteSpace(state.PreferredSource))
            {
                Persist(state, force: false);
                return new RadioMetaMetadataRoutePlan(
                    RadioMetaMetadataRoutingMode.ParallelLearning,
                    null,
                    0,
                    "No stable source winner is known; testing Shoutcast and direct ICY in parallel.");
            }

            state.LookupsSinceParallelRecheck++;
            if (state.LookupsSinceParallelRecheck >= state.ParallelRecheckInterval)
            {
                state.LookupsSinceParallelRecheck = 0;
                Persist(state, force: true);
                return new RadioMetaMetadataRoutePlan(
                    RadioMetaMetadataRoutingMode.ParallelRecheck,
                    state.PreferredSource,
                    0,
                    "Running a parallel recheck of the current preferred source (" + state.PreferredSource + ") after " +
                    state.ParallelRecheckInterval + " uncached lookup(s).");
            }

            var remaining = state.ParallelRecheckInterval - state.LookupsSinceParallelRecheck;
            Persist(state, force: false);
            return new RadioMetaMetadataRoutePlan(
                RadioMetaMetadataRoutingMode.PreferredFirst,
                state.PreferredSource,
                remaining,
                "Trying " + state.PreferredSource + " first; the other source is fallback. Parallel recheck in " + remaining + " uncached lookup(s).");
        }
    }

    /// <summary>A cancelled slower request counts as a race loss, not a measured duration.</summary>
    public string RecordParallelOutcome(string stationKey, string winnerSource, string loserSource, bool wasPeriodicRecheck = false)
    {
        if (!IsKnownSource(winnerSource) || !IsKnownSource(loserSource) ||
            string.Equals(winnerSource, loserSource, StringComparison.OrdinalIgnoreCase))
        {
            return "Parallel result was not eligible for adaptive source learning.";
        }

        var state = GetState(stationKey);
        lock (state.Sync)
        {
            var preferenceBeforeRace = state.PreferredSource;
            var winnerWasPreferred = !string.IsNullOrWhiteSpace(preferenceBeforeRace) &&
                string.Equals(preferenceBeforeRace, winnerSource, StringComparison.OrdinalIgnoreCase);
            string scheduleFeedback = string.Empty;

            if (wasPeriodicRecheck && !string.IsNullOrWhiteSpace(preferenceBeforeRace))
            {
                if (winnerWasPreferred)
                {
                    state.ConsecutiveSuccessfulRechecks++;
                    if (state.ConsecutiveSuccessfulRechecks >= 2)
                    {
                        if (state.ParallelRecheckInterval < MaximumParallelRecheckInterval)
                        {
                            var previousInterval = state.ParallelRecheckInterval;
                            state.ParallelRecheckInterval = Math.Min(MaximumParallelRecheckInterval, state.ParallelRecheckInterval * 2);
                            scheduleFeedback = " Two consecutive confirming rechecks extended the next parallel check from " +
                                               previousInterval + " to " + state.ParallelRecheckInterval + " lookups.";
                        }
                        // Keep the successful-recheck counter bounded after the 80-lookup ceiling.
                        // At the ceiling, continue rechecking every 80 lookups indefinitely.
                        state.ConsecutiveSuccessfulRechecks = 0;
                    }
                }
                else
                {
                    state.ConsecutiveSuccessfulRechecks = 0;
                    if (state.ParallelRecheckInterval != SequentialLookupsBetweenParallelRechecks)
                    {
                        state.ParallelRecheckInterval = SequentialLookupsBetweenParallelRechecks;
                        scheduleFeedback = " The preferred source lost this recheck, so the check interval returned to " +
                                           SequentialLookupsBetweenParallelRechecks + " lookups.";
                    }
                }
            }

            if (IsShoutcast(winnerSource))
            {
                state.ConsecutiveShoutcastLosses = 0;
                state.ConsecutiveIcyLosses = Math.Min(ConsecutiveLossesBeforePreferredFirst, state.ConsecutiveIcyLosses + 1);
            }
            else
            {
                state.ConsecutiveIcyLosses = 0;
                state.ConsecutiveShoutcastLosses = Math.Min(ConsecutiveLossesBeforePreferredFirst, state.ConsecutiveShoutcastLosses + 1);
            }

            if (winnerWasPreferred) state.PreferredSourceMisses = 0;

            var loserStreak = IsShoutcast(loserSource)
                ? state.ConsecutiveShoutcastLosses
                : state.ConsecutiveIcyLosses;

            if (loserStreak >= ConsecutiveLossesBeforePreferredFirst)
            {
                var previousPreferred = state.PreferredSource;
                var preferenceChanged = !string.Equals(previousPreferred, winnerSource, StringComparison.OrdinalIgnoreCase);
                state.PreferredSource = winnerSource;
                state.PreferredSourceMisses = 0;
                state.LookupsSinceParallelRecheck = 0;
                state.ConsecutiveShoutcastLosses = 0;
                state.ConsecutiveIcyLosses = 0;
                if (preferenceChanged)
                {
                    state.ParallelRecheckInterval = SequentialLookupsBetweenParallelRechecks;
                    state.ConsecutiveSuccessfulRechecks = 0;
                }

                var message = string.Equals(previousPreferred, winnerSource, StringComparison.OrdinalIgnoreCase)
                    ? "Adaptive routing confirms " + winnerSource + " as the preferred source after " +
                      ConsecutiveLossesBeforePreferredFirst + " consecutive parallel tests against " + loserSource + "."
                    : "Adaptive routing changed preference from " + (string.IsNullOrWhiteSpace(previousPreferred) ? "unlearned" : previousPreferred) +
                      " to " + winnerSource + " after " + loserSource + " lost " + ConsecutiveLossesBeforePreferredFirst +
                      " consecutive parallel speed tests. " + winnerSource + " will run first, with " + loserSource + " as fallback.";
                message += scheduleFeedback;
                SetOutcome(state, message);
                Persist(state, force: true);
                return message;
            }

            var feedback = "Parallel speed test: " + winnerSource + " returned the first usable title; " + loserSource +
                           " consecutive loss streak=" + loserStreak + "/" + ConsecutiveLossesBeforePreferredFirst +
                           "; preferred source=" + (state.PreferredSource ?? "not learned") + "." + scheduleFeedback;
            SetOutcome(state, feedback);
            Persist(state, force: wasPeriodicRecheck && (scheduleFeedback.Length > 0 || winnerWasPreferred));
            return feedback;
        }
    }

    public string RecordParallelNoWinner(string stationKey, bool wasPeriodicRecheck = false)
    {
        var state = GetState(stationKey);
        lock (state.Sync)
        {
            state.ConsecutiveShoutcastLosses = 0;
            state.ConsecutiveIcyLosses = 0;
            if (wasPeriodicRecheck)
            {
                state.ConsecutiveSuccessfulRechecks = 0;
                state.ParallelRecheckInterval = SequentialLookupsBetweenParallelRechecks;
            }
            var feedback = "Parallel lookup produced no usable title; adaptive source preference was not changed.";
            SetOutcome(state, feedback);
            Persist(state, force: wasPeriodicRecheck);
            return feedback;
        }
    }

    /// <summary>Records a preferred-first lookup and resumes parallel learning after repeat misses.</summary>
    public string? RecordSequentialOutcome(string stationKey, string preferredSource, string? selectedSource)
    {
        var state = GetState(stationKey);
        lock (state.Sync)
        {
            if (string.Equals(preferredSource, selectedSource, StringComparison.OrdinalIgnoreCase))
            {
                state.PreferredSourceMisses = 0;
                SetOutcome(state, "Preferred source " + preferredSource + " returned a usable title.");
                Persist(state, force: false);
                return null;
            }

            state.PreferredSourceMisses++;
            if (state.PreferredSourceMisses >= PreferredSourceMissesBeforeReset)
            {
                var failedPreference = state.PreferredSource;
                state.PreferredSource = null;
                state.PreferredSourceMisses = 0;
                state.LookupsSinceParallelRecheck = 0;
                state.ConsecutiveShoutcastLosses = 0;
                state.ConsecutiveIcyLosses = 0;
                state.ParallelRecheckInterval = SequentialLookupsBetweenParallelRechecks;
                state.ConsecutiveSuccessfulRechecks = 0;
                var message = "Adaptive routing reset to parallel learning because preferred source " + preferredSource +
                              " failed to produce usable metadata in " + PreferredSourceMissesBeforeReset +
                              " consecutive lookups. Last selected source=" + (selectedSource ?? "none") +
                              "; previous preference=" + (failedPreference ?? "unknown") + ".";
                SetOutcome(state, message);
                Persist(state, force: true);
                return message;
            }

            var missed = "Preferred source " + preferredSource + " did not produce usable metadata; selected fallback=" +
                         (selectedSource ?? "none") + ". One more consecutive preferred-source miss will return this station to parallel learning.";
            SetOutcome(state, missed);
            Persist(state, force: false);
            return missed;
        }
    }

    public void Reset()
    {
        _stations.Clear();
        _stateStore?.ClearStationRoutingStates();
    }

    private StationState GetState(string stationKey)
    {
        var key = string.IsNullOrWhiteSpace(stationKey) ? "<unknown-station>" : stationKey;
        var state = _stations.GetOrAdd(key, stationKeyValue =>
        {
            var loaded = new StationState { StationKey = stationKeyValue };
            if (_stateStore is not null && _stateStore.TryLoadStationRoutingState(stationKeyValue, out var saved))
            {
                loaded.PreferredSource = saved.PreferredSource;
                loaded.ConsecutiveShoutcastLosses = saved.ConsecutiveShoutcastLosses;
                loaded.ConsecutiveIcyLosses = saved.ConsecutiveIcyLosses;
                loaded.PreferredSourceMisses = saved.PreferredSourceMisses;
                loaded.LookupsSinceParallelRecheck = saved.LookupsSinceParallelRecheck;
                loaded.ParallelRecheckInterval = Math.Clamp(saved.ParallelRecheckInterval, SequentialLookupsBetweenParallelRechecks, MaximumParallelRecheckInterval);
                loaded.ConsecutiveSuccessfulRechecks = saved.ConsecutiveSuccessfulRechecks;
                loaded.LastOutcome = saved.LastOutcome;
                loaded.LastOutcomeUtc = saved.LastOutcomeUtc;
                loaded.LastAccessUtcTicks = saved.LastAccessUtc.UtcDateTime.Ticks;
            }
            return loaded;
        });

        Interlocked.Exchange(ref state.LastAccessUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
        if (_stations.Count > MaximumTrackedStations)
        {
            var cutoff = DateTime.UtcNow.AddHours(-1).Ticks;
            foreach (var pair in _stations)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(pair.Key, key) && Interlocked.Read(ref pair.Value.LastAccessUtcTicks) < cutoff)
                {
                    _stations.TryRemove(pair.Key, out _);
                }
            }

            if (_stations.Count > MaximumTrackedStations)
            {
                foreach (var pair in _stations.OrderBy(item => Interlocked.Read(ref item.Value.LastAccessUtcTicks)).Take(_stations.Count - MaximumTrackedStations))
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals(pair.Key, key)) _stations.TryRemove(pair.Key, out _);
                }
            }
        }

        return state;
    }

    private void Persist(StationState state, bool force)
    {
        if (_stateStore is null) return;
        _stateStore.SaveStationRoutingState(new RadioMetaAdaptiveRoutingStationSnapshot
        {
            StationKey = state.StationKey,
            PreferredSource = state.PreferredSource,
            ConsecutiveShoutcastLosses = state.ConsecutiveShoutcastLosses,
            ConsecutiveIcyLosses = state.ConsecutiveIcyLosses,
            PreferredSourceMisses = state.PreferredSourceMisses,
            LookupsSinceParallelRecheck = state.LookupsSinceParallelRecheck,
            ParallelRecheckInterval = state.ParallelRecheckInterval,
            ConsecutiveSuccessfulRechecks = state.ConsecutiveSuccessfulRechecks,
            LastOutcome = state.LastOutcome,
            LastOutcomeUtc = state.LastOutcomeUtc,
            LastAccessUtc = new DateTimeOffset(Interlocked.Read(ref state.LastAccessUtcTicks), TimeSpan.Zero)
        }, force);
    }

    private static void SetOutcome(StationState state, string outcome)
    {
        state.LastOutcome = outcome.Length <= 240 ? outcome : outcome[..240];
        state.LastOutcomeUtc = DateTimeOffset.UtcNow;
    }

    private static bool IsKnownSource(string source) => IsShoutcast(source) ||
        string.Equals(source, "ICY", StringComparison.OrdinalIgnoreCase);

    private static bool IsShoutcast(string source) =>
        string.Equals(source, "Shoutcast", StringComparison.OrdinalIgnoreCase);

    private sealed class StationState
    {
        public object Sync { get; } = new();
        public string StationKey { get; set; } = string.Empty;
        public string? PreferredSource { get; set; }
        public int ConsecutiveShoutcastLosses { get; set; }
        public int ConsecutiveIcyLosses { get; set; }
        public int PreferredSourceMisses { get; set; }
        public int LookupsSinceParallelRecheck { get; set; }
        public int ParallelRecheckInterval { get; set; } = SequentialLookupsBetweenParallelRechecks;
        public int ConsecutiveSuccessfulRechecks { get; set; }
        public string LastOutcome { get; set; } = string.Empty;
        public DateTimeOffset? LastOutcomeUtc { get; set; }
        public long LastAccessUtcTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
    }
}
