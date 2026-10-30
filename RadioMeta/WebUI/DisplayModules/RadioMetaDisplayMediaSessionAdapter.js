/* Radio Meta Media Session adapter (v1.3.80).
 * This module owns output-specific DOM/media-session behavior.
 */
define("RadioMetaDisplayMediaSessionAdapter_v1380", [], function() {
    "use strict";
    return function createRadioMetaDisplayMediaSessionAdapter(context) {
        var state = context.state;
        var cache = context.cache;
        var normalizeMiniText = context.normalizeMiniText;

        function createMediaMetadata(track) {
            var values = { title: track.song, artist: track.artist }; var artwork = state.originalMediaMetadata && state.originalMediaMetadata.artwork; if (artwork && typeof artwork.length === "number" && artwork.length) {
                var images = Array.prototype.map.call(artwork, function(image) {
                    if (!image || !image.src)
                        return null; var result = { src: image.src }; if (image.sizes)
                        result.sizes = image.sizes; if (image.type)
                        result.type = image.type; return result;
                }).filter(Boolean);
                if (images.length)
                    values.artwork = images;
            } return new window.MediaMetadata(values);
        }

        function findNativeMetadataDescriptor(mediaSession) {
            var own = Object.getOwnPropertyDescriptor(mediaSession, "metadata"); if (own)
                return own; var prototype = Object.getPrototypeOf(mediaSession); while (prototype) {
                    var descriptor = Object.getOwnPropertyDescriptor(prototype, "metadata");
                    if (descriptor && typeof descriptor.get === "function" && typeof descriptor.set === "function") {
                        return descriptor;
                    }
                    prototype = Object.getPrototypeOf(prototype);
                } return null;
        }

        function looksLikeStationFallback(value, track) {
            if (!value)
                return true; var title = typeof value.title === "string" ? value.title.trim() : ""; var artist = typeof value.artist === "string" ? value.artist.trim() : ""; if (!title || normalizeMiniText(title) === normalizeMiniText(track.station) || /\.strm(?:$|[?#])/i.test(title)) {
                    return true;
                } return !artist;
        }

        function installMediaSessionMetadataGuard(mediaSession) {
            if (!mediaSession || typeof window.MediaMetadata !== "function")
                return false; if (state.mediaSessionGuardInstalled && state.mediaSessionGuardSession === mediaSession)
                return true; try {
                    var ownDescriptor = Object.getOwnPropertyDescriptor(mediaSession, "metadata");
                    if ((ownDescriptor && ownDescriptor.configurable === false) || (!ownDescriptor && !Object.isExtensible(mediaSession)))
                        return false;
                    var nativeDescriptor = findNativeMetadataDescriptor(mediaSession);
                    if (!nativeDescriptor || typeof nativeDescriptor.get !== "function" || typeof nativeDescriptor.set !== "function")
                        return false;
                    Object.defineProperty(mediaSession, "metadata", {
                        configurable: true, enumerable: !!nativeDescriptor.enumerable, get: function() { return nativeDescriptor.get.call(this); }, set: function(value) {
                            var activeTrack = cache.getTrack(); var stationCacheReuse = cache.isStationCacheReuse(); if (activeTrack && !stationCacheReuse && looksLikeStationFallback(value, activeTrack)) {
                                var replacement = createMediaMetadata(activeTrack);
                                nativeDescriptor.set.call(this, replacement);
                                state.lastMediaMetadata = nativeDescriptor.get.call(this);
                                state.lastMediaMetadataKey = cache.getKey(activeTrack);
                                return;
                            } nativeDescriptor.set.call(this, value); if (activeTrack && !stationCacheReuse && value && value.title === activeTrack.song && value.artist === activeTrack.artist) {
                                state.lastMediaMetadata = nativeDescriptor.get.call(this);
                                state.lastMediaMetadataKey = cache.getKey(activeTrack);
                            }
                        }
                    });
                    state.mediaSessionGuardSession = mediaSession;
                    state.mediaSessionGuardInstalled = true;
                    return true;
                }
            catch (_) {
                return false;
            }
        }

        function updateNativeMediaSessionMetadata(track) {
            try {
                if (!("mediaSession" in navigator) || !navigator.mediaSession || typeof window.MediaMetadata !== "function")
                    return;
                var mediaSession = navigator.mediaSession;
                installMediaSessionMetadataGuard(mediaSession);
                if (!state.originalMediaMetadataCaptured || state.mediaSession !== mediaSession) {
                    state.mediaSession = mediaSession;
                    state.originalMediaMetadata = mediaSession.metadata || null;
                    state.originalMediaMetadataCaptured = true;
                    state.lastMediaMetadata = null;
                    state.lastMediaMetadataKey = "";
                }
                var key = cache.getKey(track);
                if (state.lastMediaMetadataKey === key && mediaSession.metadata === state.lastMediaMetadata)
                    return;
                mediaSession.metadata = createMediaMetadata(track);
                state.lastMediaMetadata = mediaSession.metadata;
                state.lastMediaMetadataKey = key;
            }
            catch (_) { }
        }

        // Re-publish a known accepted track with a fresh MediaMetadata object. This is
        // deliberately separate from updateNativeMediaSessionMetadata(), which skips
        // identical values for normal polling efficiency. A station handoff can reset
        // the player/media-session bridge, so the coordinator may retry this briefly.
        function republishNativeMediaSessionMetadata(track) {
            try {
                if (!track || !("mediaSession" in navigator) || !navigator.mediaSession || typeof window.MediaMetadata !== "function")
                    return false;
                var mediaSession = navigator.mediaSession;
                installMediaSessionMetadataGuard(mediaSession);
                if (!state.originalMediaMetadataCaptured || state.mediaSession !== mediaSession) {
                    state.mediaSession = mediaSession;
                    state.originalMediaMetadata = mediaSession.metadata || null;
                    state.originalMediaMetadataCaptured = true;
                }
                mediaSession.metadata = createMediaMetadata(track);
                state.lastMediaMetadata = mediaSession.metadata;
                state.lastMediaMetadataKey = cache.getKey(track);
                return true;
            }
            catch (_) { return false; }
        }

        function restoreNativeMediaSessionMetadata() {
            if (!state.originalMediaMetadataCaptured)
                return; try {
                    if (state.mediaSession && state.mediaSession.metadata === state.lastMediaMetadata) {
                        state.mediaSession.metadata = state.originalMediaMetadata;
                    }
                }
            catch (_) { } state.mediaSession = null; state.originalMediaMetadata = null; state.originalMediaMetadataCaptured = false; state.lastMediaMetadata = null; state.lastMediaMetadataKey = "";
        }

        return {
            createMediaMetadata: createMediaMetadata,
            findNativeMetadataDescriptor: findNativeMetadataDescriptor,
            looksLikeStationFallback: looksLikeStationFallback,
            installMediaSessionMetadataGuard: installMediaSessionMetadataGuard,
            updateNativeMediaSessionMetadata: updateNativeMediaSessionMetadata,
            republishNativeMediaSessionMetadata: republishNativeMediaSessionMetadata,
            restoreNativeMediaSessionMetadata: restoreNativeMediaSessionMetadata
        };
    };
});
