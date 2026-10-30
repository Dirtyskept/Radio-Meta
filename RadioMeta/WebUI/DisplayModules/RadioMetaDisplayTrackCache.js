/* Radio Meta display track-cache module (v1.3.107).
 * Owns the shared active display track plus a short-lived per-station reuse cache.
 * The mini-player and big player still consume one active track. On a station switch,
 * an older station entry is eligible for reuse only when it was observed very recently.
 */
define("RadioMetaDisplayTrackCache_v1380", [], function() {
    "use strict";
    return function createRadioMetaDisplayTrackCache() {
        var currentTrack = null;
        var currentTrackSeenAt = 0;
        var activeStationKey = "";
        var currentTrackIsStationReuse = false;
        var stationTracks = new Map();
        var maxStationEntries = 12;
        var stationReuseLimitMs = 3000;

        function trackKey(track) {
            if (!track) return "";
            return String(track.station || "") + "\u0000" + String(track.artist || "") + "\u0000" + String(track.song || "");
        }
        function getTrack() { return currentTrack; }
        function getSeenAt() { return currentTrackSeenAt; }
        function getActiveStationKey() { return activeStationKey; }
        function getStationCacheCount() { return stationTracks.size; }
        function isStationCacheReuse() { return currentTrackIsStationReuse; }
        function rememberStationTrack(key, track, seenAt) {
            if (!key || !track || !(Number(seenAt) > 0)) return;
            stationTracks.set(String(key), { track: track, seenAt: Number(seenAt) });
            if (stationTracks.size > maxStationEntries) {
                var oldestKey = "";
                var oldestSeenAt = Infinity;
                stationTracks.forEach(function(entry, entryKey) {
                    if (entry.seenAt < oldestSeenAt) { oldestSeenAt = entry.seenAt; oldestKey = entryKey; }
                });
                if (oldestKey) stationTracks.delete(oldestKey);
            }
        }
        function setTrack(track, seenAt) {
            if (!track) { clear(); return; }
            currentTrack = track;
            currentTrackSeenAt = Number(seenAt) || Date.now();
            currentTrackIsStationReuse = false;
            if (activeStationKey) rememberStationTrack(activeStationKey, currentTrack, currentTrackSeenAt);
        }
        function activateStation(stationKey, now, maxReuseAgeMs) {
            var key = String(stationKey || "").trim();
            var currentTime = Number(now) || Date.now();
            var maxReuseAge = Number(maxReuseAgeMs);
            if (!Number.isFinite(maxReuseAge) || maxReuseAge < 0) maxReuseAge = stationReuseLimitMs;
            maxReuseAge = Math.min(maxReuseAge, stationReuseLimitMs);

            var previousKey = activeStationKey;
            var previousTrack = currentTrack;
            var changed = key !== activeStationKey;
            if (currentTrack && activeStationKey) {
                rememberStationTrack(activeStationKey, currentTrack, currentTrackSeenAt);
            }
            if (changed) {
                activeStationKey = key;
                currentTrack = null;
                currentTrackSeenAt = 0;
                currentTrackIsStationReuse = false;
            }
            if (!key) {
                currentTrack = null;
                currentTrackSeenAt = 0;
                currentTrackIsStationReuse = false;
                return { changed: changed, previousStationKey: previousKey, previousTrack: previousTrack, track: null, reused: false };
            }
            var reused = false;
            if (!currentTrack) {
                var cached = stationTracks.get(key);
                if (cached) {
                    var age = currentTime - cached.seenAt;
                    if (age >= 0 && age <= maxReuseAge) {
                        currentTrack = cached.track;
                        currentTrackSeenAt = cached.seenAt;
                        currentTrackIsStationReuse = true;
                        reused = true;
                    } else {
                        stationTracks.delete(key);
                    }
                }
            }
            return { changed: changed, previousStationKey: previousKey, previousTrack: previousTrack, track: currentTrack, reused: reused };
        }
        function clear() {
            currentTrack = null;
            currentTrackSeenAt = 0;
            currentTrackIsStationReuse = false;
        }
        function resetStation() {
            clear();
            activeStationKey = "";
        }
        function isFresh(now, maxAgeMs) {
            var current = Number(now) || Date.now();
            var maxAge = Number(maxAgeMs);
            if (currentTrackIsStationReuse) maxAge = Math.min(maxAge, stationReuseLimitMs);
            return !!currentTrack && currentTrackSeenAt > 0 && Number.isFinite(maxAge) && current - currentTrackSeenAt >= 0 && current - currentTrackSeenAt <= maxAge;
        }
        return {
            getTrack: getTrack,
            getSeenAt: getSeenAt,
            setTrack: setTrack,
            clear: clear,
            resetStation: resetStation,
            activateStation: activateStation,
            getActiveStationKey: getActiveStationKey,
            getStationCacheCount: getStationCacheCount,
            isStationCacheReuse: isStationCacheReuse,
            isFresh: isFresh,
            getKey: trackKey
        };
    };
});
