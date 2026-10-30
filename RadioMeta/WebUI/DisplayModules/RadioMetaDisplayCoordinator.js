/* Radio Meta Integrated Display coordinator (v1.3.107).
 * Keeps established radio polling active through temporary session/metadata gaps.
 * Exposed through Emby's __plugin module loader; the DLL owns this file.
 */
define([
    "RadioMetaDisplayTrackCache_v1380",
    "RadioMetaDisplayMediaSessionAdapter_v1380",
    "RadioMetaDisplayPlayerAdapter_v1380",
    "RadioMetaDisplayDashboardAdapter_v1380"
], function(createTrackCache, createRadioMetaDisplayMediaSessionAdapter, createRadioMetaDisplayPlayerAdapter, createRadioMetaDisplayDashboardAdapter) {
    "use strict";
    ;
    (function() {
        "use strict";
        var GLOBAL_KEY = "__radioMetaIntegratedDisplay_v1323";
        if (window[GLOBAL_KEY]) {
            return;
        }
        var state = { timer: null, busy: false, originals: new Map(), radioPlaybackPending: false, radioPlaybackPendingSince: 0, radioPlaybackPendingCandidateKey: "", radioPlaybackPendingExpiredKey: "", radioPlaybackEstablishedCandidateKey: "", radioPlaybackEstablishedCandidateKeys: [], radioPlaybackEstablishedLastSeenAt: 0, radioPlaybackMissingSessionPolls: 0, nextScheduledDelayMs: 0, mediaSession: null, originalMediaMetadata: null, originalMediaMetadataCaptured: false, lastMediaMetadata: null, lastMediaMetadataKey: "", mediaSessionGuardSession: null, mediaSessionGuardInstalled: false, miniObserver: null, miniObserverRoot: null, dashboardOriginals: new Map(), dashboardRenderedNames: new Map(), dashboardElementTracks: new Map(), dashboardEmptySecondaryOriginals: new Map(), dashboardPendingStations: new Set(), dashboardPendingVisibility: new Map(), dashboardObserver: null, dashboardObserverRoot: null, lastPollStartedAt: 0, lastPollFinishedAt: 0, lastPollDurationMs: 0, lastPollError: "", lastPollStatus: "never", lastSessionCount: 0, lastHasPlaybackSession: false, lastHasNowPlayingItem: false, lastTrackMetadataValid: false, largePlayerStationOverride: null, stationTransition: null, lastStationCacheReuse: false, staleTransitionTrackRejections: 0, stationTransitionFallbackCount: 0, lastStationTransitionFallbackAt: 0, lastStationTransitionFallbackStation: "", lastStationTransitionFallbackSource: "", stationTransitionRepublishGeneration: 0, stationTransitionRepublishCount: 0, lastStationTransitionRepublishAt: 0, lastStationTransitionRepublishReason: "", currentTracksFallbackBusy: false, currentTracksFallbackLastAttemptAt: 0, currentTracksFallbackMisses: 0, currentTracksFallbackSuccessCount: 0, lastCurrentTracksFallbackAt: 0, lastCurrentTracksFallbackStation: "", lastCurrentTracksFallbackResult: "never", currentTracksFallbackStationKey: "", currentTracksFallbackTrackKey: "", immediateRefreshCount: 0, lastImmediateRefreshAt: 0, lastImmediateRefreshReason: "", immediateTickQueued: false, immediateRefreshRequested: false, pollCount: 0, outputAdapterStatus: { trackCache: "initializing", mediaSession: "initializing", player: "initializing", dashboard: "initializing" }, outputAdapterErrors: {} };
        var trackCache = createTrackCache();
        state.outputAdapterStatus.trackCache = "ready";
        var outputAdapters = { mediaSession: null, player: null, dashboard: null };
        function displayAdapterErrorText(error) {
            var message = error && error.message ? error.message : String(error || "Unknown error");
            return String(message).replace(/([?&](?:X-Emby-Token|api_key|access_token|token)=)[^&\s]+/gi, "$1[REDACTED]").replace(/https?:\/\/[^\s]+/gi, "[URL]").slice(0, 240);
        }
        function recordOutputAdapterError(name, operation, error) {
            var detail = String(operation || "operation") + ": " + displayAdapterErrorText(error);
            state.outputAdapterStatus[name] = operation === "initialization" ? "failed" : (state.outputAdapterStatus[name] === "failed" ? "failed" : "degraded");
            state.outputAdapterErrors[name] = detail;
        }
        function callOutputAdapter(name, method, args) {
            var adapter = outputAdapters[name];
            if (!adapter || typeof adapter[method] !== "function") {
                if (!state.outputAdapterErrors[name])
                    recordOutputAdapterError(name, method, new Error("Output adapter is unavailable."));
                return undefined;
            }
            try {
                return adapter[method].apply(null, args);
            }
            catch (error) {
                recordOutputAdapterError(name, method, error);
                return undefined;
            }
        }
        function createOutputAdapter(name, factory, context) {
            try {
                var adapter = factory(context);
                if (!adapter || typeof adapter !== "object")
                    throw new Error("Adapter factory returned no adapter object.");
                state.outputAdapterStatus[name] = "ready";
                delete state.outputAdapterErrors[name];
                return adapter;
            }
            catch (error) {
                recordOutputAdapterError(name, "initialization", error);
                return {};
            }
        }
        function initializeOutputAdapters() {
            var context = { state: state, cache: trackCache, isVisible: isVisible, parseRadioItem: parseRadioItem, normalizeMiniText: normalizeMiniText, requestImmediateTick: requestImmediateTick };
            outputAdapters.player = createOutputAdapter("player", createRadioMetaDisplayPlayerAdapter, context);
            outputAdapters.mediaSession = createOutputAdapter("mediaSession", createRadioMetaDisplayMediaSessionAdapter, context);
            outputAdapters.dashboard = createOutputAdapter("dashboard", createRadioMetaDisplayDashboardAdapter, context);
        }
        initializeOutputAdapters();
        window[GLOBAL_KEY] = true;
        window.__radioMetaDisplayDiagnostics_v1379 = function() { var now = Date.now(); return { helperRegistered: window[GLOBAL_KEY] === true, displayHelperVersion: "1.3.108", radioPlaybackPending: !!state.radioPlaybackPending, radioPlaybackEstablished: !!state.radioPlaybackEstablishedCandidateKey, radioPlaybackEstablishedLastSeenAgeMs: state.radioPlaybackEstablishedLastSeenAt ? Math.max(0, now - state.radioPlaybackEstablishedLastSeenAt) : null, radioPlaybackMissingSessionPolls: state.radioPlaybackMissingSessionPolls, nextScheduledDelayMs: state.nextScheduledDelayMs, radioPlaybackPendingElapsedMs: state.radioPlaybackPendingSince ? Math.max(0, now - state.radioPlaybackPendingSince) : 0, pendingCandidatePresent: !!state.radioPlaybackPendingCandidateKey, pendingExpired: !!state.radioPlaybackPendingExpiredKey, currentTrackCached: !!trackCache.getTrack(), currentTrackCacheAgeMs: trackCache.getSeenAt() ? Math.max(0, now - trackCache.getSeenAt()) : null, activeStationCacheKeyPresent: !!trackCache.getActiveStationKey(), stationCacheEntryCount: trackCache.getStationCacheCount(), activeTrackFromRecentStationCache: trackCache.isStationCacheReuse(), stationTransitionPending: !!state.stationTransition, lastStationCacheReuse: !!state.lastStationCacheReuse, staleTransitionTrackRejections: state.staleTransitionTrackRejections, stationTransitionFallbackCount: state.stationTransitionFallbackCount, lastStationTransitionFallbackAtUtc: state.lastStationTransitionFallbackAt ? new Date(state.lastStationTransitionFallbackAt).toISOString() : "", lastStationTransitionFallbackStation: state.lastStationTransitionFallbackStation, lastStationTransitionFallbackSource: state.lastStationTransitionFallbackSource, stationTransitionRepublishCount: state.stationTransitionRepublishCount, lastStationTransitionRepublishAtUtc: state.lastStationTransitionRepublishAt ? new Date(state.lastStationTransitionRepublishAt).toISOString() : "", lastStationTransitionRepublishReason: state.lastStationTransitionRepublishReason, currentTracksFallbackMisses: state.currentTracksFallbackMisses, currentTracksFallbackSuccessCount: state.currentTracksFallbackSuccessCount, lastCurrentTracksFallbackAtUtc: state.lastCurrentTracksFallbackAt ? new Date(state.lastCurrentTracksFallbackAt).toISOString() : "", lastCurrentTracksFallbackStation: state.lastCurrentTracksFallbackStation, lastCurrentTracksFallbackResult: state.lastCurrentTracksFallbackResult, immediateRefreshCount: state.immediateRefreshCount, lastImmediateRefreshAtUtc: state.lastImmediateRefreshAt ? new Date(state.lastImmediateRefreshAt).toISOString() : "", lastImmediateRefreshReason: state.lastImmediateRefreshReason, immediateRefreshQueued: !!state.immediateTickQueued || !!state.immediateRefreshRequested, outputAdapterStatus: Object.assign({}, state.outputAdapterStatus), outputAdapterErrors: Object.assign({}, state.outputAdapterErrors), pollCount: state.pollCount, lastPollStatus: state.lastPollStatus, lastPollStartedAtUtc: state.lastPollStartedAt ? new Date(state.lastPollStartedAt).toISOString() : "", lastPollFinishedAtUtc: state.lastPollFinishedAt ? new Date(state.lastPollFinishedAt).toISOString() : "", lastPollDurationMs: state.lastPollDurationMs, lastPollError: state.lastPollError, lastSessionCount: state.lastSessionCount, lastHasPlaybackSession: state.lastHasPlaybackSession, lastHasNowPlayingItem: state.lastHasNowPlayingItem, lastTrackMetadataValid: state.lastTrackMetadataValid }; };
        function isVisible(element) {
            if (!element || !element.isConnected) {
                return false;
            } var rects = element.getClientRects(); if (!rects.length || rects[0].width <= 0 || rects[0].height <= 0) {
                return false;
            } for (var node = element; node && node.nodeType === 1; node = node.parentElement) {
                var css = window.getComputedStyle(node);
                if (css.display === "none" || css.visibility === "hidden" || Number(css.opacity || 1) === 0) {
                    return false;
                }
            } return true;
        }
        function getRadioProviderValue(item, expectedKey) {
            var providerIds = item && item.ProviderIds; if (!providerIds || typeof providerIds !== "object")
                return ""; var expected = String(expectedKey || "").toLowerCase(); var keys = Object.keys(providerIds); for (var i = 0; i < keys.length; i++) {
                    if (keys[i].toLowerCase() === expected) {
                        var value = providerIds[keys[i]];
                        if (typeof value === "string" && value.trim())
                            return value.trim();
                    }
                } if (expected.indexOf("radiometa") === 0) {
                    var legacyExpected = expected.replace(/^radiometa/, "radionowplaying");
                    for (var j = 0; j < keys.length; j++) {
                        if (keys[j].toLowerCase() === legacyExpected) {
                            var legacyValue = providerIds[keys[j]];
                            return typeof legacyValue === "string" ? legacyValue.trim() : "";
                        }
                    }
                } return "";
        }
        function parseRadioItem(item) {
            if (!item)
                return null; var song = getRadioProviderValue(item, "RadioMetaSong"); var artist = getRadioProviderValue(item, "RadioMetaArtist"); var artists = Array.isArray(item.Artists) ? item.Artists.filter(function(name) { return name != null && String(name).trim(); }).map(function(name) { return String(name).trim(); }) : []; if (!artists.length && Array.isArray(item.ArtistItems)) {
                    artists = item.ArtistItems.map(function(a) { return a && a.Name ? String(a.Name).trim() : ""; }).filter(Boolean);
                } if (!artist)
                artist = artists.join(", ") || String(item.AlbumArtist || item.Artist || "").trim(); if (!song || !artist)
                return null; var station = getRadioProviderValue(item, "RadioMetaStation") || (typeof item.Album === "string" ? item.Album.trim() : "") || (typeof item.Name === "string" ? item.Name.trim() : ""); if (!station)
                return null; return { station: station, artist: artist, song: song };
        }
        async function getCurrentPlaybackSession(sessions) {
            var api = window.ApiClient; if (!api || typeof api.getSessions !== "function")
                return null; if (!Array.isArray(sessions)) {
                    try {
                        sessions = await api.getSessions();
                    }
                    catch (_) {
                        return null;
                    }
                } if (!Array.isArray(sessions))
                return null; var deviceId = ""; try {
                    deviceId = typeof api.deviceId === "function" ? api.deviceId() : "";
                }
            catch (_) {
                deviceId = "";
            } if (deviceId) {
                var own = sessions.filter(function(session) { return session && session.DeviceId && String(session.DeviceId) === String(deviceId); });
                if (own.length === 1)
                    return own[0];
                if (own.length > 1) {
                    var activeOwn = own.filter(function(session) { return !!session.NowPlayingItem; });
                    if (activeOwn.length === 1)
                        return activeOwn[0];
                    return null;
                }
            } var webSessions = sessions.filter(function(session) { return session && session.NowPlayingItem && String(session.Client || "").toLowerCase() === "emby web"; }); var visibleMini = getVisibleMiniPlayerText(trackCache.getTrack()); var visibleText = visibleMini ? normalizeMiniText(visibleMini.textContent) : ""; if (visibleText) {
                var matchingSessions = webSessions.filter(function(candidate) { var item = candidate.NowPlayingItem || {}; var track = parseRadioItem(item); var possibleValues = [item.Name, getRadioProviderValue(item, "RadioMetaStation"), track && track.station, track && track.song, track && (track.song + track.artist), track && (track.song + " " + track.artist), track && (track.song + " — " + track.artist)].filter(Boolean); return possibleValues.some(function(value) { return normalizeMiniText(value) === visibleText; }); });
                if (matchingSessions.length === 1)
                    return matchingSessions[0];
                return null;
            } return webSessions.length === 1 ? webSessions[0] : null;
        }
        function mediaTrackKey(track) { return trackCache.getKey(track); }
        function createMediaMetadata() { return callOutputAdapter("mediaSession", "createMediaMetadata", arguments); }
        function findNativeMetadataDescriptor() { return callOutputAdapter("mediaSession", "findNativeMetadataDescriptor", arguments); }
        function looksLikeStationFallback() { return callOutputAdapter("mediaSession", "looksLikeStationFallback", arguments); }
        function installMediaSessionMetadataGuard() { return callOutputAdapter("mediaSession", "installMediaSessionMetadataGuard", arguments); }
        function updateNativeMediaSessionMetadata() { return callOutputAdapter("mediaSession", "updateNativeMediaSessionMetadata", arguments); }
        function republishNativeMediaSessionMetadata() { return callOutputAdapter("mediaSession", "republishNativeMediaSessionMetadata", arguments); }
        function restoreNativeMediaSessionMetadata() { return callOutputAdapter("mediaSession", "restoreNativeMediaSessionMetadata", arguments); }
        function normalizeStationIdentity(value) {
            return normalizeMiniText(value).replace(/\.strm$/i, "").replace(/\s[-–]\s*\d{2,4}\s*k(?:bps)?$/i, "").toLowerCase();
        }
        function normalizeDisplayTrackIdentity(track) {
            if (!track) return "";
            return normalizeStationIdentity(track.station) + "\u0000" + normalizeMiniText(track.artist).toLowerCase() + "\u0000" + normalizeMiniText(track.song).toLowerCase();
        }
        async function getCurrentTrackFromServer(session, stationInfo, item, now) {
            var api = window.ApiClient;
            var sessionId = String(session && session.Id || "").trim();
            if (!sessionId || !api || typeof api.getJSON !== "function" || typeof api.getUrl !== "function") {
                state.lastCurrentTracksFallbackResult = "api-or-session-unavailable";
                return null;
            }
            var currentTime = Number(now) || Date.now();
            var missDelay = state.currentTracksFallbackMisses >= 8 ? 30000 : (state.currentTracksFallbackMisses >= 3 ? 12000 : 4000);
            if (state.currentTracksFallbackBusy || currentTime - state.currentTracksFallbackLastAttemptAt < missDelay) return null;
            state.currentTracksFallbackBusy = true;
            state.currentTracksFallbackLastAttemptAt = currentTime;
            var timeoutId = null;
            try {
                var response = await Promise.race([
                    api.getJSON(api.getUrl("RadioMeta/CurrentTracks")),
                    new Promise(function (_, reject) {
                        timeoutId = window.setTimeout(function () { reject(new Error("CurrentTracks request timed out")); }, 3500);
                    })
                ]);
                if (timeoutId) window.clearTimeout(timeoutId);
                var records = response && (response.Items || response.items);
                if (!Array.isArray(records)) {
                    state.currentTracksFallbackMisses++;
                    state.lastCurrentTracksFallbackResult = "invalid-response";
                    return null;
                }
                var record = records.find(function (candidate) {
                    return candidate && String(candidate.SessionId || candidate.sessionId || "").trim().toLowerCase() === sessionId.toLowerCase();
                });
                if (!record) {
                    state.currentTracksFallbackMisses++;
                    state.lastCurrentTracksFallbackResult = "session-not-found";
                    return null;
                }
                var station = String(record.StationName || record.stationName || "").trim();
                var expectedStation = String((stationInfo && stationInfo.station) || getRadioProviderValue(item, "RadioMetaStation") || (item && item.Name) || "").trim();
                if (expectedStation && station && normalizeStationIdentity(station) !== normalizeStationIdentity(expectedStation)) {
                    state.currentTracksFallbackMisses++;
                    state.lastCurrentTracksFallbackResult = "station-mismatch";
                    return null;
                }
                var serverUpdatedAt = String(record.UpdatedAt || record.updatedAt || "");
                var song = String(record.SongTitle || record.songTitle || "").trim();
                var artist = String(record.Artist || record.artist || "").trim();
                if (!song || !artist || !station) {
                    state.currentTracksFallbackMisses++;
                    state.lastCurrentTracksFallbackResult = "track-incomplete";
                    return null;
                }
                state.currentTracksFallbackMisses = 0;
                state.currentTracksFallbackSuccessCount++;
                state.lastCurrentTracksFallbackAt = Date.now();
                state.lastCurrentTracksFallbackStation = station;
                state.lastCurrentTracksFallbackResult = "accepted";
                return { station: station, song: song, artist: artist, source: "RadioMeta/CurrentTracks", serverUpdatedAt: serverUpdatedAt };
            }
            catch (error) {
                state.currentTracksFallbackMisses++;
                state.lastCurrentTracksFallbackResult = "request-failed";
                return null;
            }
            finally {
                if (timeoutId) window.clearTimeout(timeoutId);
                state.currentTracksFallbackBusy = false;
            }
        }
        function scheduleStationTransitionRepublish(track) {
            if (!track) return;
            var generation = ++state.stationTransitionRepublishGeneration;
            var expectedTrackKey = mediaTrackKey(track);
            [250, 700, 1500, 3000].forEach(function (delayMs) {
                window.setTimeout(function () {
                    if (generation !== state.stationTransitionRepublishGeneration) return;
                    var activeTrack = trackCache.getTrack();
                    if (!activeTrack || mediaTrackKey(activeTrack) !== expectedTrackKey) return;
                    if (republishNativeMediaSessionMetadata(activeTrack)) {
                        state.stationTransitionRepublishCount++;
                        state.lastStationTransitionRepublishAt = Date.now();
                        state.lastStationTransitionRepublishReason = "station-switch-retry-" + delayMs + "ms";
                    }
                }, delayMs);
            });
        }
        function isCurrentStationItem(item, track) {
            if (!item || !track)
                return false; var providerStation = getRadioProviderValue(item, "RadioMetaStation"); if (providerStation && normalizeMiniText(providerStation) === normalizeMiniText(track.station))
                return true; return normalizeMiniText(item.Name || "") === normalizeMiniText(track.station);
        }
        function getItemPathValues(item) {
            var paths = [];
            if (item && item.Path) paths.push(String(item.Path));
            if (item && Array.isArray(item.MediaSources)) item.MediaSources.forEach(function(source) {
                if (source && source.Path) paths.push(String(source.Path));
            });
            return paths;
        }
        function getUnannotatedLiveAudioCandidateKey(item) {
            if (!item) return "";
            var itemType = String(item.Type || "").toLowerCase();
            var mediaType = String(item.MediaType || "").toLowerCase();
            if (itemType !== "audio" && mediaType !== "audio") return "";
            var runtimeTicks = Number(item.RunTimeTicks || 0);
            // Regular library tracks normally have a known duration. Live streams and .strm
            // stations generally do not. Don't classify ordinary album tracks as stations.
            if (Number.isFinite(runtimeTicks) && runtimeTicks > 0) return "";
            var paths = getItemPathValues(item);
            var hasRemoteStreamUrl = paths.some(function(path) {
                return /^https?:\/\//i.test(path) && !/\.strm(?:$|[?#])/i.test(path);
            });
            var knownRadioContext = !!(state.radioPlaybackEstablishedCandidateKey || state.radioPlaybackPendingCandidateKey || state.stationTransition || state.largePlayerStationOverride);
            if (!hasRemoteStreamUrl && !knownRadioContext) return "";
            var hasTrackArtist = !!(String(item.AlbumArtist || item.Artist || "").trim()) ||
                (Array.isArray(item.Artists) && item.Artists.length > 0) || (Array.isArray(item.ArtistItems) && item.ArtistItems.length > 0);
            var album = String(item.Album || "").trim();
            if (!hasRemoteStreamUrl && (hasTrackArtist || album)) return "";
            var name = String(item.Name || album).trim();
            if (!name) return "";
            var id = String(item.Id || "").trim();
            return id ? "live-audio-id:" + id : "live-audio-name:" + normalizeMiniText(name);
        }
        function getRadioStationCandidateKey(item) {
            if (!item)
                return "";
            var paths = getItemPathValues(item);
            var streamPath = paths.filter(function(path) { return /\.strm(?:$|[?#])/i.test(path); })[0];
            if (streamPath) return "path:" + normalizeMiniText(streamPath);
            var name = String(item.Name || "").trim();
            if (/^.{1,100}?\s[-–]\s*\d{2,4}\s*k(?:bps)?$/i.test(name)) return "name:" + normalizeMiniText(name);
            var station = getRadioProviderValue(item, "RadioMetaStation");
            var liveAudioKey = getUnannotatedLiveAudioCandidateKey(item);
            // ProviderIds can briefly survive on the next Emby item during a handoff. When the
            // untimed live-audio item's visible Name disagrees with that carried-over station ID,
            // use the new item identity to initiate the transition instead of retaining the old
            // station cache. A normal item with no known live-radio context does not enter here.
            if (station && name && liveAudioKey && normalizeMiniText(name) !== normalizeMiniText(station))
                return "name:" + normalizeMiniText(name);
            if (station) return "station:" + normalizeMiniText(station);
            return liveAudioKey;
        }
        function isRadioStationCandidate(item) { return !!getRadioStationCandidateKey(item); }

        function getCurrentStationDisplayInfo(item) {
            if (!item) return null;
            var paths = getItemPathValues(item);
            var strmPath = paths.filter(function(path) { return /\.strm(?:$|[?#])/i.test(path); })[0] || "";
            var name = String(item.Name || "").trim();
            var providerStation = getRadioProviderValue(item, "RadioMetaStation");
            var looksLikeNamedStation = /^.{1,100}?\s[-–]\s*\d{2,4}\s*k(?:bps)?$/i.test(name);
            var unannotatedLiveKey = getUnannotatedLiveAudioCandidateKey(item);
            if (!strmPath && !providerStation && !looksLikeNamedStation && !unannotatedLiveKey) return null;
            // Stations without song metadata still have a useful Emby item title. Keep that
            // title as the player fallback rather than requiring RadioMetaSong/Artist ProviderIds.
            var station = name || providerStation || (typeof item.Album === "string" ? item.Album.trim() : "");
            if (!station) return null;
            var providerDisagreesWithVisibleStation = !!(providerStation && name &&
                normalizeMiniText(providerStation) !== normalizeMiniText(name) && unannotatedLiveKey);
            var key = strmPath ? "path:" + normalizeMiniText(strmPath) :
                (looksLikeNamedStation || providerDisagreesWithVisibleStation ? "name:" + normalizeMiniText(name) :
                (providerStation ? "station:" + normalizeMiniText(providerStation) : unannotatedLiveKey));
            return { key: key, station: station };
        }

        function getStationCacheKey(item, stationInfo, track) {
            if (stationInfo && stationInfo.key) return stationInfo.key;
            var candidateKey = item && getRadioStationCandidateKey(item);
            if (candidateKey) return candidateKey;
            if (track && track.station) return "station:" + normalizeMiniText(track.station);
            return "";
        }

        function isSameTrackContent(left, right) {
            return !!(left && right && normalizeMiniText(left.song) === normalizeMiniText(right.song) && normalizeMiniText(left.artist) === normalizeMiniText(right.artist));
        }

        function isStaleTrackFromPreviousStation(track, previousTrack, item, stationInfo) {
            if (!track || !stationInfo || !stationInfo.station) return false;
            var currentProviderStation = normalizeMiniText(getRadioProviderValue(item, "RadioMetaStation"));
            var currentItemStation = normalizeMiniText(stationInfo.station);
            // During a handoff, old ProviderIds can temporarily survive on the newly selected
            // .strm item. Reject that metadata until its station identity agrees with the selected
            // item's identity, even when the old station has moved to a different song meanwhile.
            if (currentProviderStation && currentProviderStation !== currentItemStation) return true;
            if (!previousTrack) return false;
            // If Emby has already published the new station identity, an identical song can be valid.
            if (currentProviderStation && currentProviderStation === currentItemStation) return false;
            return isSameTrackContent(track, previousTrack);
        }

        function isEstablishedRadioCandidateKey(candidateKey) {
            return !!(candidateKey && state.radioPlaybackEstablishedCandidateKeys && state.radioPlaybackEstablishedCandidateKeys.indexOf(candidateKey) >= 0);
        }

        function updateLargePlayerStationOverride(item, track, cachedTrack) {
            var info = getCurrentStationDisplayInfo(item);
            if (!info) {
                if (state.stationTransition && state.stationTransition.station) {
                    state.largePlayerStationOverride = { candidateKey: state.stationTransition.stationKey, station: state.stationTransition.station };
                    return;
                }
                state.largePlayerStationOverride = null;
                return;
            }
            // Once the accepted metadata explicitly names the current station, use it normally.
            if (track && normalizeMiniText(track.station) === normalizeMiniText(info.station)) {
                state.largePlayerStationOverride = null;
                return;
            }
            var referenceTrack = track || cachedTrack;
            if (referenceTrack && normalizeMiniText(referenceTrack.station) !== normalizeMiniText(info.station)) {
                state.largePlayerStationOverride = { candidateKey: info.key, station: info.station };
                return;
            }
            if (state.largePlayerStationOverride && state.largePlayerStationOverride.candidateKey === info.key) {
                state.largePlayerStationOverride.station = info.station;
                return;
            }
            state.largePlayerStationOverride = null;
        }
        function clearRadioPlaybackEstablished() {
            state.radioPlaybackEstablishedCandidateKey = "";
            state.radioPlaybackEstablishedCandidateKeys = [];
            state.radioPlaybackEstablishedLastSeenAt = 0;
            state.radioPlaybackMissingSessionPolls = 0;
        }
        function getEstablishedRadioCandidateKeys(item, track) {
            var keys = [];
            function addKey(value) {
                if (value && keys.indexOf(value) < 0) keys.push(value);
            }
            var station = getRadioProviderValue(item, "RadioMetaStation");
            if (station) addKey("station:" + normalizeMiniText(station));
            var paths = [];
            if (item && item.Path) paths.push(String(item.Path));
            if (item && Array.isArray(item.MediaSources)) item.MediaSources.forEach(function(source) { if (source && source.Path) paths.push(String(source.Path)); });
            var streamPath = paths.filter(function(path) { return /\.strm(?:$|[?#])/i.test(path); })[0];
            if (streamPath) addKey("path:" + normalizeMiniText(streamPath));
            var name = String(item && item.Name || "").trim();
            if (name && /^.{1,100}?\s[-–]\s*\d{2,4}\s*k(?:bps)?$/i.test(name)) addKey("name:" + normalizeMiniText(name));
            var liveAudioKey = getUnannotatedLiveAudioCandidateKey(item);
            if (liveAudioKey) addKey(liveAudioKey);
            var itemName = String(item && item.Name || "").trim();
            if (station && itemName && liveAudioKey && normalizeMiniText(itemName) !== normalizeMiniText(station))
                addKey("name:" + normalizeMiniText(itemName));
            if (track && track.station) addKey("station:" + normalizeMiniText(track.station));
            return keys;
        }
        function isEstablishedRadioCandidate(item) {
            if (!state.radioPlaybackEstablishedCandidateKeys || !state.radioPlaybackEstablishedCandidateKeys.length) return false;
            var keys = getEstablishedRadioCandidateKeys(item, null);
            return keys.some(function(key) { return state.radioPlaybackEstablishedCandidateKeys.indexOf(key) >= 0; });
        }
        function getEstablishedRadioCandidateKey(item, track) {
            var candidateKey = item && isRadioStationCandidate(item) ? getRadioStationCandidateKey(item) : "";
            if (candidateKey) return candidateKey;
            if (track && track.station) return "station:" + normalizeMiniText(track.station);
            return "";
        }
        function updateRadioPlaybackEstablished(session, item, track, now) {
            var paused = !!(session && session.PlayState && session.PlayState.IsPaused === true);
            if (track) {
                var acceptedKey = getEstablishedRadioCandidateKey(item, track);
                if (!acceptedKey) {
                    clearRadioPlaybackEstablished();
                    return;
                }
                state.radioPlaybackEstablishedCandidateKey = acceptedKey;
                state.radioPlaybackEstablishedCandidateKeys = getEstablishedRadioCandidateKeys(item, track);
                if (state.radioPlaybackEstablishedCandidateKeys.indexOf(acceptedKey) < 0) state.radioPlaybackEstablishedCandidateKeys.push(acceptedKey);
                state.radioPlaybackEstablishedLastSeenAt = now;
                state.radioPlaybackMissingSessionPolls = 0;
                return;
            }
            if (paused) {
                clearRadioPlaybackEstablished();
                return;
            }
            if (item) {
                var candidateKey = isRadioStationCandidate(item) ? getRadioStationCandidateKey(item) : "";
                if (candidateKey && isEstablishedRadioCandidateKey(candidateKey)) {
                    state.radioPlaybackEstablishedLastSeenAt = now;
                    state.radioPlaybackMissingSessionPolls = 0;
                    return;
                }
                // A different radio candidate or a non-radio item ends the previous established station.
                clearRadioPlaybackEstablished();
                return;
            }
            // No NowPlayingItem can be transient during background transitions. Keep the established
            // station active for a bounded number of successful session polls; API exceptions do not
            // count as evidence that playback stopped.
            if (state.radioPlaybackEstablishedCandidateKey) {
                state.radioPlaybackMissingSessionPolls += 1;
                if (state.radioPlaybackMissingSessionPolls >= 20) clearRadioPlaybackEstablished();
            }
        }
        function clearRadioPlaybackPending(clearExpired) {
            state.radioPlaybackPending = false;
            state.radioPlaybackPendingSince = 0;
            state.radioPlaybackPendingCandidateKey = "";
            if (clearExpired !== false) state.radioPlaybackPendingExpiredKey = "";
        }
        function updateRadioPlaybackPending(session, item, track, now) {
            if (track) {
                clearRadioPlaybackPending(true);
                return;
            }
            var paused = !!(session && session.PlayState && session.PlayState.IsPaused === true);
            var candidateKey = item && isRadioStationCandidate(item) ? getRadioStationCandidateKey(item) : "";
            if (paused || !candidateKey) {
                clearRadioPlaybackPending(true);
                return;
            }
            // A station that has previously produced valid metadata remains on the established-radio
            // path during temporary metadata gaps; the 60-second pending timeout is for new stations.
            if (candidateKey && isEstablishedRadioCandidateKey(candidateKey)) {
                clearRadioPlaybackPending(true);
                return;
            }
            if (state.radioPlaybackPending && state.radioPlaybackPendingCandidateKey !== candidateKey) {
                clearRadioPlaybackPending(false);
            }
            if (state.radioPlaybackPendingExpiredKey && state.radioPlaybackPendingExpiredKey !== candidateKey) {
                state.radioPlaybackPendingExpiredKey = "";
            }
            if (!state.radioPlaybackPending && state.radioPlaybackPendingExpiredKey !== candidateKey) {
                state.radioPlaybackPending = true;
                state.radioPlaybackPendingSince = now;
                state.radioPlaybackPendingCandidateKey = candidateKey;
            }
            if (state.radioPlaybackPending && state.radioPlaybackPendingCandidateKey === candidateKey && now - state.radioPlaybackPendingSince >= 60000) {
                state.radioPlaybackPending = false;
                state.radioPlaybackPendingSince = 0;
                state.radioPlaybackPendingCandidateKey = "";
                state.radioPlaybackPendingExpiredKey = candidateKey;
            }
        }
        
        function rememberOriginal() { return callOutputAdapter("player", "rememberOriginal", arguments); }
        function updateLargePlayerTitle() { return callOutputAdapter("player", "updateLargePlayerTitle", arguments); }
        function getVisibleMiniPlayerText() { return callOutputAdapter("player", "getVisibleMiniPlayerText", arguments); }
        function updateMiniPlayerTitle() { return callOutputAdapter("player", "updateMiniPlayerTitle", arguments); }
        function normalizeMiniText() { return callOutputAdapter("player", "normalizeMiniText", arguments); }
        function getNativeStationTitleIfDifferent() { return callOutputAdapter("player", "getNativeStationTitleIfDifferent", arguments); }
        function showStationTransitionFallback() { return callOutputAdapter("player", "showStationTransitionFallback", arguments); }
        function tryApplyCachedTrackToMiniPlayer() { return callOutputAdapter("player", "tryApplyCachedTrackToMiniPlayer", arguments); }
        function getVisibleLargePlayerTitle() { return callOutputAdapter("player", "getVisibleLargePlayerTitle", arguments); }
        function tryApplyCachedTrackToLargePlayer() { return callOutputAdapter("player", "tryApplyCachedTrackToLargePlayer", arguments); }
        function nodeTouchesLargePlayer() { return callOutputAdapter("player", "nodeTouchesLargePlayer", arguments); }
        function nodeTouchesMiniPlayer() { return callOutputAdapter("player", "nodeTouchesMiniPlayer", arguments); }
        function ensureMiniPlayerMutationObserver() { return callOutputAdapter("player", "ensureMiniPlayerMutationObserver", arguments); }
        function restoreTitles() { return callOutputAdapter("player", "restoreTitles", arguments); }
        function normalizeDashboardText() { return callOutputAdapter("dashboard", "normalizeDashboardText", arguments); }
        function isDashboardRoute() { return callOutputAdapter("dashboard", "isDashboardRoute", arguments); }
        function ensureDashboardCardStyle() { return callOutputAdapter("dashboard", "ensureDashboardCardStyle", arguments); }
        function getDashboardParts() { return callOutputAdapter("dashboard", "getDashboardParts", arguments); }
        function restorePendingDashboardVisibility() { return callOutputAdapter("dashboard", "restorePendingDashboardVisibility", arguments); }
        function hidePendingDashboardTitle() { return callOutputAdapter("dashboard", "hidePendingDashboardTitle", arguments); }
        function isLikelyStationTitle() { return callOutputAdapter("dashboard", "isLikelyStationTitle", arguments); }
        function forceDashboardPartStyle() { return callOutputAdapter("dashboard", "forceDashboardPartStyle", arguments); }
        function getNativeDashboardSecondaryMetrics() { return callOutputAdapter("dashboard", "getNativeDashboardSecondaryMetrics", arguments); }
        function applyNativeDashboardSecondaryMetrics() { return callOutputAdapter("dashboard", "applyNativeDashboardSecondaryMetrics", arguments); }
        function collapseEmptyDashboardSecondaryRows() { return callOutputAdapter("dashboard", "collapseEmptyDashboardSecondaryRows", arguments); }
        function restoreDashboardSecondaryRowsForTitle() { return callOutputAdapter("dashboard", "restoreDashboardSecondaryRowsForTitle", arguments); }
        function restoreAllDashboardSecondaryRows() { return callOutputAdapter("dashboard", "restoreAllDashboardSecondaryRows", arguments); }
        function renderDashboardTitleElement() { return callOutputAdapter("dashboard", "renderDashboardTitleElement", arguments); }
        function isDashboardNowPlayingTitle() { return callOutputAdapter("dashboard", "isDashboardNowPlayingTitle", arguments); }
        function markDashboardTitleElement() { return callOutputAdapter("dashboard", "markDashboardTitleElement", arguments); }
        function applyDashboardCardStyles() { return callOutputAdapter("dashboard", "applyDashboardCardStyles", arguments); }
        function queueDashboardCardStylePass() { return callOutputAdapter("dashboard", "queueDashboardCardStylePass", arguments); }
        function ensureDashboardMutationObserver() { return callOutputAdapter("dashboard", "ensureDashboardMutationObserver", arguments); }
        function restoreDashboardCardTitles() { return callOutputAdapter("dashboard", "restoreDashboardCardTitles", arguments); }
        function updateDashboardNowPlayingCards() { return callOutputAdapter("dashboard", "updateDashboardNowPlayingCards", arguments); }
        async function tick() {
            // Do not repaint the last cached song before verifying the active Emby item. On a
            // station switch the old cache can otherwise overwrite the new .strm title for a poll.
            if (state.busy)
                return; if (document.visibilityState === "hidden" && !state.originalMediaMetadataCaptured && !state.radioPlaybackPending && !state.radioPlaybackEstablishedCandidateKey && !state.stationTransition) {
                    state.lastPollStatus = "skipped-hidden-no-active-media-session";
                    state.lastPollFinishedAt = Date.now();
                    state.lastPollDurationMs = 0;
                    return;
                } state.busy = true; state.lastPollStartedAt = Date.now(); state.pollCount++; state.lastPollError = ""; state.lastPollStatus = "running"; try {
                    var api = window.ApiClient;
                    var sessions = api && typeof api.getSessions === "function" ? await api.getSessions() : [];
                    if (!Array.isArray(sessions))
                        sessions = [];
                    state.lastSessionCount = sessions.length;
                    updateDashboardNowPlayingCards(sessions);
                    var session = await getCurrentPlaybackSession(sessions);
                    var item = session && session.NowPlayingItem;
                    var playbackObservedAt = Date.now();
                    var inferredNativeStationTitle = "";
                    var stationInfo = item ? getCurrentStationDisplayInfo(item) : null;
                    if (!item && state.stationTransition && state.stationTransition.station) {
                        // Emby can briefly omit NowPlayingItem while switching streams. Keep the
                        // selected station identity through this gap rather than reusing old song data.
                        stationInfo = { key: state.stationTransition.stationKey, station: state.stationTransition.station };
                    } else if (!item && state.radioPlaybackEstablishedCandidateKey) {
                        inferredNativeStationTitle = String(getNativeStationTitleIfDifferent(trackCache.getTrack()) || "").trim();
                        if (inferredNativeStationTitle) stationInfo = { key: "native-title:" + normalizeMiniText(inferredNativeStationTitle), station: inferredNativeStationTitle };
                    }
                    var track = item && parseRadioItem(item);
                    var stationKey = getStationCacheKey(item, stationInfo, track);
                    var stationActivation = null;
                    if (item && stationKey) {
                        stationActivation = trackCache.activateStation(stationKey, playbackObservedAt, 3000);
                        if (stationActivation.changed) {
                            // Supersede queued metadata republishes from the previous station.
                            state.stationTransitionRepublishGeneration++;
                            state.currentTracksFallbackMisses = 0;
                            state.currentTracksFallbackLastAttemptAt = 0;
                            state.currentTracksFallbackStationKey = "";
                            state.currentTracksFallbackTrackKey = "";
                            state.stationTransition = {
                                stationKey: stationKey,
                                station: stationInfo && stationInfo.station ? stationInfo.station : String(item.Name || getRadioProviderValue(item, "RadioMetaStation") || "").trim(),
                                previousTrack: stationActivation.previousTrack || null,
                                since: playbackObservedAt,
                                fallbackAppliedAt: 0,
                                source: "session-item"
                            };
                            state.lastStationCacheReuse = !!stationActivation.reused;
                            if (stationInfo) state.largePlayerStationOverride = { candidateKey: stationInfo.key, station: stationInfo.station };
                        }
                    } else if (!item && inferredNativeStationTitle && stationKey) {
                        stationActivation = trackCache.activateStation(stationKey, playbackObservedAt, 0);
                        if (stationActivation.changed) {
                            state.stationTransitionRepublishGeneration++;
                            state.currentTracksFallbackMisses = 0;
                            state.currentTracksFallbackLastAttemptAt = 0;
                            state.currentTracksFallbackStationKey = "";
                            state.currentTracksFallbackTrackKey = "";
                            state.stationTransition = {
                                stationKey: stationKey,
                                station: inferredNativeStationTitle,
                                previousTrack: stationActivation.previousTrack || trackCache.getTrack() || null,
                                since: playbackObservedAt,
                                fallbackAppliedAt: 0,
                                source: "native-player-title"
                            };
                            state.largePlayerStationOverride = { candidateKey: stationKey, station: inferredNativeStationTitle };
                        }
                    } else if (item && !stationKey) {
                        trackCache.resetStation();
                        state.stationTransition = null;
                        state.largePlayerStationOverride = null;
                    }
                    var transitionState = state.stationTransition;
                    var stationTransitionTrackAccepted = false;
                    // Once server-canonical metadata was needed, do not let an old session-item
                    // title overwrite it on the next poll. Release this lock only when the item
                    // catches up to the exact station/song/artist tuple published by CurrentTracks.
                    if (track && state.currentTracksFallbackStationKey === stationKey && state.currentTracksFallbackTrackKey) {
                        if (normalizeDisplayTrackIdentity(track) !== state.currentTracksFallbackTrackKey) {
                            track = null;
                            state.staleTransitionTrackRejections++;
                            state.lastPollStatus = "currenttracks-fallback-protected-against-stale-session-metadata";
                        } else {
                            state.currentTracksFallbackStationKey = "";
                            state.currentTracksFallbackTrackKey = "";
                        }
                    }
                    if (track && transitionState && transitionState.stationKey === stationKey && isStaleTrackFromPreviousStation(track, transitionState.previousTrack, item, stationInfo)) {
                        track = null;
                        state.staleTransitionTrackRejections++;
                        state.lastPollStatus = "station-transition-stale-track-rejected";
                    } else if (track && transitionState && transitionState.stationKey === stationKey) {
                        state.stationTransition = null;
                        stationTransitionTrackAccepted = true;
                    }
                    // If the player item has stale/missing ProviderIds during a station switch,
                    // recover only from Radio Meta's authenticated, accepted per-session track.
                    // Exact Emby session matching plus station validation avoids cross-player or
                    // previous-station metadata; fallback polling is rate-limited and stops when
                    // the player item supplies a valid track again.
                    if (!track && session && stationKey && (state.stationTransition || state.radioPlaybackEstablishedCandidateKey || isRadioStationCandidate(item))) {
                        var serverTrack = await getCurrentTrackFromServer(session, stationInfo, item, playbackObservedAt);
                        if (serverTrack) {
                            track = serverTrack;
                            state.currentTracksFallbackStationKey = stationKey;
                            state.currentTracksFallbackTrackKey = normalizeDisplayTrackIdentity(serverTrack);
                            state.lastPollStatus = "accepted-currenttracks-fallback";
                            if (transitionState && transitionState.stationKey === stationKey) {
                                state.stationTransition = null;
                                stationTransitionTrackAccepted = true;
                            }
                        }
                    }
                    updateLargePlayerStationOverride(item, track, trackCache.getTrack() || (transitionState && transitionState.previousTrack) || (stationActivation && stationActivation.previousTrack));
                    state.lastHasPlaybackSession = !!session;
                    state.lastHasNowPlayingItem = !!item;
                    state.lastTrackMetadataValid = !!track;
                    updateRadioPlaybackPending(session, item, track, playbackObservedAt);
                    updateRadioPlaybackEstablished(session, item, track, playbackObservedAt);
                    if (!track) {
                        var cachedTrack = trackCache.getTrack();
                        // A station handoff must win over the established-station gap path. Clear
                        // the current-song cache, paint the selected .strm identity immediately,
                        // and wait for metadata from that station before showing song/artist again.
                        var transitionPendingForCurrentItem = !!(state.stationTransition &&
                            state.stationTransition.stationKey === stationKey);
                        if (transitionPendingForCurrentItem) {
                            trackCache.clear();
                            cachedTrack = null;
                            var transitionStation = String(state.stationTransition.station || (stationInfo && stationInfo.station) || (item && (item.Name || getRadioProviderValue(item, "RadioMetaStation"))) || "").trim();
                            if (transitionStation && document.visibilityState !== "hidden") {
                                if (!state.stationTransition.fallbackAppliedAt) restoreTitles(true);
                                if (!state.largePlayerStationOverride || state.largePlayerStationOverride.station !== transitionStation) {
                                    state.largePlayerStationOverride = { candidateKey: (stationInfo && stationInfo.key) || state.stationTransition.stationKey, station: transitionStation };
                                }
                                var fallbackRendered = showStationTransitionFallback(transitionStation);
                                if (fallbackRendered && !state.stationTransition.fallbackAppliedAt) {
                                    state.stationTransition.fallbackAppliedAt = Date.now();
                                    state.stationTransitionFallbackCount++;
                                    state.lastStationTransitionFallbackAt = state.stationTransition.fallbackAppliedAt;
                                    state.lastStationTransitionFallbackStation = transitionStation;
                                    state.lastStationTransitionFallbackSource = state.stationTransition.source || "session-item";
                                }
                            }
                            restoreNativeMediaSessionMetadata();
                            state.lastPollStatus = state.stationTransition.fallbackAppliedAt ? "station-transition-strm-title-shown" : "station-transition-metadata-pending";
                            return;
                        }
                        // A recent per-station entry must never be painted as current during a station handoff.
                        if (cachedTrack && trackCache.isStationCacheReuse() && state.stationTransition && state.stationTransition.stationKey === stationKey) {
                            trackCache.clear();
                            cachedTrack = null;
                        }
                        var currentCandidateKey = stationKey || (item && isRadioStationCandidate(item) ? getRadioStationCandidateKey(item) : "");
                        var pausedWithoutTrack = !!(session && session.PlayState && session.PlayState.IsPaused === true);
                        var sameEstablishedStation = !!(!pausedWithoutTrack && isEstablishedRadioCandidateKey(currentCandidateKey));
                        var establishedSessionGap = !!(!item && state.radioPlaybackEstablishedCandidateKey && state.radioPlaybackMissingSessionPolls > 0 && state.radioPlaybackMissingSessionPolls < 20);
                        if (sameEstablishedStation || establishedSessionGap) {
                            state.lastPollStatus = sameEstablishedStation ? "radio-established-metadata-gap-retained" : "radio-established-session-gap-retained";
                            if (cachedTrack && !trackCache.isFresh(Date.now(), 15000) && trackCache.isStationCacheReuse()) {
                                // A track reused from another recent station is only a brief transition
                                // fallback. Once its three-second window expires, stop displaying it.
                                trackCache.clear();
                                cachedTrack = null;
                                if (document.visibilityState !== "hidden") {
                                    if (state.largePlayerStationOverride) { updateLargePlayerTitle(null); restoreTitles(true); }
                                    else restoreTitles();
                                }
                                restoreNativeMediaSessionMetadata();
                            }
                            if (cachedTrack && state.originalMediaMetadataCaptured && !trackCache.isStationCacheReuse()) updateNativeMediaSessionMetadata(cachedTrack);
                            if (cachedTrack && trackCache.isFresh(Date.now(), 15000) && document.visibilityState !== "hidden") {
                                updateLargePlayerTitle(cachedTrack);
                                updateMiniPlayerTitle(cachedTrack);
                            }
                            return;
                        }
                        var cachedTrackIsFresh = !!(cachedTrack && trackCache.isFresh(Date.now(), 15000));
                        var cacheIsForCurrentStation = !!(cachedTrack && stationKey && trackCache.getActiveStationKey() === stationKey); var canKeepCachedTrack = cachedTrackIsFresh && (cacheIsForCurrentStation || !item || isCurrentStationItem(item, cachedTrack));
                        if (canKeepCachedTrack) {
                            state.lastPollStatus = state.radioPlaybackPending ? "radio-pending-cache-retained" : "cache-retained-no-valid-track";
                            if (state.originalMediaMetadataCaptured && !trackCache.isStationCacheReuse())
                                updateNativeMediaSessionMetadata(cachedTrack);
                            if (document.visibilityState !== "hidden") {
                                updateLargePlayerTitle(cachedTrack);
                                var miniTarget = getVisibleMiniPlayerText(cachedTrack);
                                var miniText = miniTarget ? normalizeMiniText(miniTarget.textContent) : "";
                                var cachedMarker = cachedTrack.song + cachedTrack.artist;
                                if (miniTarget && miniText === normalizeMiniText(cachedTrack.station)) {
                                    updateMiniPlayerTitle(cachedTrack);
                                }
                                else if (miniTarget && miniTarget.getAttribute("data-rnp-player-title") !== cachedMarker) { }
                            }
                            return;
                        }
                        state.lastPollStatus = state.largePlayerStationOverride ? "station-transition-metadata-pending" : (state.radioPlaybackPending ? "radio-pending-no-cache" : "no-valid-track");
                        if (document.visibilityState !== "hidden") {
                            if (state.largePlayerStationOverride) {
                                updateLargePlayerTitle(cachedTrackIsFresh ? cachedTrack : null);
                                // Restore other custom titles, but keep the big-player station line
                                // synchronized until the new station's metadata is accepted.
                                restoreTitles(true);
                            }
                            else {
                                restoreTitles();
                            }
                        }
                        trackCache.clear();
                        restoreNativeMediaSessionMetadata();
                        return;
                    }
                    var previousTrackKey = trackCache.getTrack() ? mediaTrackKey(trackCache.getTrack()) : "";
                    trackCache.setTrack(track, Date.now());
                    state.stationTransition = null;
                    state.lastPollStatus = track.source === "RadioMeta/CurrentTracks"
                        ? "currenttracks-fallback-track-updated"
                        : (previousTrackKey === mediaTrackKey(track) ? "track-confirmed" : "track-updated");
                    updateNativeMediaSessionMetadata(track);
                    if (stationTransitionTrackAccepted) scheduleStationTransitionRepublish(track);
                    if (document.visibilityState !== "hidden") {
                        updateLargePlayerTitle(track);
                        updateMiniPlayerTitle(track);
                    }
                }
            catch (error) {
                var errorName = error && error.name ? String(error.name) : "Error";
                var errorMessage = error && error.message ? String(error.message) : "Playback session lookup failed.";
                state.lastPollError = (errorName + ": " + errorMessage).replace(/([?&](?:X-Emby-Token|api_key|access_token|token)=)[^&\s]+/gi, "$1[REDACTED]").replace(/https?:\/\/[^\s]+/gi, "[URL]").slice(0, 240);
                state.lastPollStatus = "error";
            }
            finally {
                state.lastPollFinishedAt = Date.now();
                state.lastPollDurationMs = state.lastPollStartedAt ? Math.max(0, state.lastPollFinishedAt - state.lastPollStartedAt) : 0;
                state.busy = false;
                if (state.immediateRefreshRequested) {
                    state.immediateRefreshRequested = false;
                    requestImmediateTick("queued-after-active-poll");
                }
            }
        }
        function requestImmediateTick(reason) {
            state.immediateRefreshCount++;
            state.lastImmediateRefreshAt = Date.now();
            state.lastImmediateRefreshReason = String(reason || "player-dom-change").slice(0, 80);
            if (state.timer) {
                window.clearTimeout(state.timer);
                state.timer = null;
            }
            if (state.busy) {
                state.immediateRefreshRequested = true;
                return;
            }
            if (state.immediateTickQueued)
                return;
            state.immediateTickQueued = true;
            window.setTimeout(function() {
                state.immediateTickQueued = false;
                if (state.busy) {
                    state.immediateRefreshRequested = true;
                    return;
                }
                tick().finally(scheduleNextTick);
            }, 0);
        }
        function scheduleNextTick() {
            if (state.timer) {
                window.clearTimeout(state.timer);
            } var hidden = document.visibilityState === "hidden"; var mediaTrackActive = !!(state.originalMediaMetadataCaptured && trackCache.getTrack()); var radioPending = !!state.radioPlaybackPending; var establishedRadioActive = !!state.radioPlaybackEstablishedCandidateKey; var fastPolling = mediaTrackActive || radioPending || establishedRadioActive; var delay = hidden ? (fastPolling ? 2500 : 30000) : (fastPolling ? 2500 : 4000); state.nextScheduledDelayMs = delay; state.timer = window.setTimeout(async function() { state.timer = null; await tick(); scheduleNextTick(); }, delay);
        }
        function start() {
            ensureMiniPlayerMutationObserver(); if (isDashboardRoute()) {
                ensureDashboardMutationObserver();
                applyDashboardCardStyles(document.querySelector("#dashboardPage") || document.body);
            } tick().finally(scheduleNextTick); document.addEventListener("visibilitychange", function() {
                if (document.visibilityState === "visible" || state.originalMediaMetadataCaptured) {
                    tick().finally(function() { scheduleNextTick(); });
                }
            });
        }
        var attempts = 0;
        var waitForApi = window.setInterval(function() {
            attempts++; if (window.ApiClient && typeof window.ApiClient.getSessions === "function") {
                window.clearInterval(waitForApi);
                start();
            }
            else if (attempts >= 60) {
                window.clearInterval(waitForApi);
                console.warn("Radio Meta title helper: Emby ApiClient was not found.");
            }
        }, 1000);
    }());
    return { version: "1.3.108-station-transition-metadata-reinforcement" };
});
