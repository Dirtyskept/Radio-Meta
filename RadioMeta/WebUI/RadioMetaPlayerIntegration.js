/* AMD resource loaded through Emby's plugin-resource URL helper and module loader. */
define([], function () {
/*
 * Radio Meta 1.3.154 DLL-embedded Emby player integration.
 *
 * This integration does not patch Emby's nowplayingbar.js or any other player
 * module. The plugin DLL adds one reversible inline bootstrap to the Emby
 * dashboard index page. That bootstrap loads this module through Emby's own loader; the module uses Emby's PlaybackManager,
 * current-player proxy, and event bus to refresh the native player state and
 * decorate the native display-item event immediately before rendering.
 *
 * The mini-player is strictly data-driven: Radio Meta changes only the native
 * display item's Name and ArtistItems fields, then lets nowplayingbar.js render
 * its own markup, classes, colors, and layout. The integration never queries or
 * changes mini-player DOM nodes, classes, inline styles, or text.
 * The first two full-player rows are also native data rendering. Only the
 * full-player third/album row receives the station title, and the Dashboard
 * uses its existing native card rows for song, artist, and station. The targeted
 * full-player/Dashboard text updates remain separate from the mini-player path.
 * Original station artwork metadata remains attached to the .strm item. Emby's own image resolvers consume
 * PrimaryImageTag / ImageTags.Primary (and, as fallback, AlbumPrimaryImageTag).
 * Those values must remain attached to the .strm item so the station logo is
 * stable in the mini-player and full-player OSD. Text metadata is changed only
 * in the browser's in-memory display item; the stream URL and item ID remain
 * untouched. The original item values are restored when radio metadata stops
 * applying or the active item changes.
 */
(function () {
    "use strict";

    var GLOBAL_KEY = "__radioMetaPlayerIntegration1354";
    var SCRIPT_VERSION = "1.3.154";
    if (window[GLOBAL_KEY]) return;

    var state = {
        started: false,
        busy: false,
        timer: null,
        modules: null,
        lastTrackKey: "",
        activeItem: null,
        activeSnapshot: null,
        lastRadioSeenAt: 0,
        dashboardTracks: new Map(),
        dashboardObserver: null,
        dashboardOriginals: new WeakMap(),
        lastDashboardRoute: false,
        dashboardContainerFound: false,
        dashboardMatchedCards: 0,
        activeItemId: "",
        playerDisplayOriginals: new WeakMap(),
        playerDisplayElements: new Set(),
        fullPlayerObservers: [],
        fullPlayerObserverBusy: false,
        lastPresentationKey: "",
        displayItemCopies: new Set(),
        displayItemSnapshots: new WeakMap(),
        eventTriggerOriginal: null,
        eventTriggerWrapper: null,
        getPlaylistOriginal: null,
        playQueueCopies: new WeakMap(),
        lastDisplayUpdates: new Map(),
        scriptVersion: SCRIPT_VERSION,
        serverVersion: "",
        scriptOutOfDate: false,
        holdBigPlayerLines: false,
        lineHoldObservers: [],
        getPlaylistWrapper: null,
        activeTrack: null,
        artistLookups: new Map(),
        artistLookupsInFlight: new Set(),
        lastError: ""
    };

    window[GLOBAL_KEY] = state;

    // Emby's native nowplayingbar.js reads Name for the first line and
    // ArtistItems for the second line. Keep the mutation surface to those two
    // fields; do not rewrite OriginalTitle, SortName, Artists, AlbumArtist,
    // album data, or any artwork metadata.
    var ITEM_FIELDS = ["Name", "ArtistItems"];

    function log(message, detail) {
        try {
            if (detail === undefined) console.info("Radio Meta 1.3.154: " + message);
            else console.info("Radio Meta 1.3.154: " + message, detail);
        } catch (_) { }
    }

    function warn(message, error) {
        var text = String(error && error.message ? error.message : error || "Unknown error");
        if (text === state.lastError) return;
        state.lastError = text;
        console.warn("Radio Meta 1.3.154: " + message + " (" + text.slice(0, 180) + ")");
    }

    function moduleDefault(moduleValue) {
        return moduleValue && moduleValue.default ? moduleValue.default : moduleValue;
    }

    function loadEmbyModules() {
        if (state.modules) return Promise.resolve(state.modules);
        if (!window.Emby || typeof window.Emby.importModule !== "function") {
            return Promise.reject(new Error("Emby.importModule is not available yet"));
        }

        return Promise.all([
            Emby.importModule("./modules/playback/currentplayer.js"),
            Emby.importModule("./modules/common/playback/playbackmanager.js"),
            Emby.importModule("./modules/emby-apiclient/events.js")
        ]).then(function (modules) {
            var loaded = {
                currentPlayer: moduleDefault(modules[0]),
                playbackManager: moduleDefault(modules[1]),
                events: moduleDefault(modules[2])
            };

            if (!loaded.currentPlayer || typeof loaded.currentPlayer.get !== "function") {
                throw new Error("Emby current-player module did not expose get()");
            }
            if (!loaded.playbackManager || typeof loaded.playbackManager.currentItem !== "function" ||
                typeof loaded.playbackManager.getPlayerState !== "function") {
                throw new Error("Emby PlaybackManager methods were not available");
            }
            if (!loaded.events || typeof loaded.events.trigger !== "function") {
                throw new Error("Emby event bus did not expose trigger()");
            }
            state.modules = loaded;
            installDisplayItemEventOverlay(loaded.events);
            installPlayQueueOverlay(loaded.playbackManager);
            log("connected to Emby's native playback modules and display-item event path");
            return loaded;
        });
    }

    function getProviderValue(item, key) {
        var providerIds = item && item.ProviderIds;
        if (!providerIds || typeof providerIds !== "object") return "";
        var expected = String(key || "").toLowerCase();
        var keys = Object.keys(providerIds);

        for (var i = 0; i < keys.length; i++) {
            if (keys[i].toLowerCase() === expected) {
                var value = providerIds[keys[i]];
                if (typeof value === "string" && value.trim()) return value.trim();
            }
        }
        return "";
    }

    function normalizeText(value) {
        return String(value || "")
            .replace(/\u00a0/g, " ")
            .replace(/[\r\n]+/g, " ")
            .replace(/\s+/g, " ")
            .trim();
    }

    function isVisible(element) {
        if (!element) return false;
        try {
            var css = window.getComputedStyle ? window.getComputedStyle(element) : null;
            if (css && (css.display === "none" || css.visibility === "hidden" ||
                Number(css.opacity || 1) === 0)) return false;
            return !element.getClientRects || element.getClientRects().length > 0;
        } catch (_) {
            return true;
        }
    }

    function rememberPlayerDisplayElement(element) {
        if (!element || state.playerDisplayOriginals.has(element)) return;
        state.playerDisplayOriginals.set(element, {
            html: element.innerHTML,
            style: element.getAttribute("style"),
            title: element.getAttribute("title"),
            className: element.getAttribute("class")
        });
        state.playerDisplayElements.add(element);
    }

    function disconnectFullPlayerObservers() {
        state.fullPlayerObservers.forEach(function (entry) {
            try { entry.observer.disconnect(); } catch (_) { }
        });
        state.fullPlayerObservers = [];
    }

    function restorePlayerDisplayPolish() {
        disconnectFullPlayerObservers();
        state.playerDisplayElements.forEach(function (element) {
            var original = state.playerDisplayOriginals.get(element);
            if (!element || !original) return;
            element.innerHTML = original.html;
            if (original.style === null) element.removeAttribute("style");
            else element.setAttribute("style", original.style);
            if (original.title === null) element.removeAttribute("title");
            else element.setAttribute("title", original.title);
            if (original.className === null) element.removeAttribute("class");
            else element.setAttribute("class", original.className);
            element.removeAttribute("data-radiometa-player-title");
        });
        state.playerDisplayElements.clear();
        state.playerDisplayOriginals = new WeakMap();
        state.lastPresentationKey = "";
    }

    // ---- Big player: station switch ---------------------------------------
    // On a switch to a different station, the lines under the new .strm title
    // that still show the previous station's song, artist or station are given
    // an invisible placeholder (a non-breaking space keeps their height, so the
    // layout does not jump). The new song fills them the moment it is applied.
    // Only the visible big-player lines change: the display-item data used by
    // the mini-player, the lock screen and CarPlay is not touched here.
    var INVISIBLE_PLACEHOLDER = "\u00a0";
    var BIG_PLAYER_LINE_SELECTORS = [".videoOsdParentTitle", ".videoOsdTitle", ".videoOsdThirdTitle"];

    function clearBigPlayerAfterStationSwitch(previousTrack) {
        // Forget the saved copies taken while the previous station played, so
        // they are never pasted back over the new station, and stop the
        // observer that re-applies the previous station's name.
        disconnectFullPlayerObservers();
        state.playerDisplayElements.forEach(function (element) {
            if (element) element.removeAttribute("data-radiometa-player-title");
        });
        state.playerDisplayElements.clear();
        state.playerDisplayOriginals = new WeakMap();
        state.lastPresentationKey = "";

        if (!previousTrack || !document.querySelectorAll) return;
        var previousTexts = [previousTrack.song, previousTrack.artist, previousTrack.station]
            .map(normalizeText)
            .filter(Boolean);
        if (!previousTexts.length) return;

        Array.prototype.slice.call(document.querySelectorAll(".videoOsdText")).forEach(function (container) {
            if (!container || !container.querySelector) return;
            BIG_PLAYER_LINE_SELECTORS.forEach(function (selector) {
                var line = container.querySelector(selector);
                // Only lines still showing the previous station's text are
                // cleared, so the new .strm title can never be blanked.
                if (line && previousTexts.indexOf(normalizeText(line.textContent)) >= 0) {
                    line.textContent = INVISIBLE_PLACEHOLDER;
                }
            });
        });
    }

    // ---- Big player: hold the three-line layout -------------------------
    // Emby hides the artist line (.videoOsdTitle) and the album line
    // (.videoOsdThirdTitle) when they are empty. A station has neither until
    // its first song arrives, so the big player went from one line to three
    // and everything below it, the transport buttons too, moved down. While a
    // live audio station is shown, those two lines are kept with an invisible
    // placeholder, so the layout always has three lines and nothing moves.
    // Only the visible big-player lines change; the data read by the
    // mini-player, lock screen and CarPlay is not touched. Ordinary songs and
    // audiobooks have a running time, so they are never held.
    var HELD_LINE_SELECTORS = [".videoOsdTitle", ".videoOsdThirdTitle"];

    function isLiveAudioStation(item) {
        return !!item && item.MediaType === "Audio" && !(Number(item.RunTimeTicks) > 0);
    }

    function applyBigPlayerLineHold() {
        if (!state.holdBigPlayerLines || !document.querySelectorAll) return;
        Array.prototype.slice.call(document.querySelectorAll(".videoOsdText")).forEach(function (container) {
            if (!container || !container.querySelector) return;
            ensureLineHoldObserver(container);
            HELD_LINE_SELECTORS.forEach(function (selector) {
                var line = container.querySelector(selector);
                if (!line || !line.classList) return;
                // A hidden line may still hold an older album or station name,
                // so it is always blanked before it is shown. A line that is
                // already visible with the placeholder is left alone, which
                // also stops the observer from looping.
                if (line.classList.contains("hide") || line.textContent === "") {
                    line.textContent = INVISIBLE_PLACEHOLDER;
                    line.classList.remove("hide");
                }
            });
        });
    }

    function ensureLineHoldObserver(container) {
        if (typeof MutationObserver !== "function") return;
        if (state.lineHoldObservers.some(function (entry) { return entry.container === container; })) return;
        // Emby draws the big player shortly after the display update (after
        // an asynchronous user lookup), so watch for it hiding a line and put
        // the line back before the browser paints.
        var observer = new MutationObserver(function () {
            if (state.holdBigPlayerLines) applyBigPlayerLineHold();
        });
        observer.observe(container, {
            childList: true,
            characterData: true,
            subtree: true,
            attributes: true,
            attributeFilter: ["class"]
        });
        state.lineHoldObservers.push({ container: container, observer: observer });
    }

    function releaseBigPlayerLineHold() {
        state.holdBigPlayerLines = false;
        state.lineHoldObservers.forEach(function (entry) {
            try { entry.observer.disconnect(); } catch (_) { }
        });
        state.lineHoldObservers = [];
    }

    function updateBigPlayerLineHold(item) {
        if (isLiveAudioStation(item)) {
            state.holdBigPlayerLines = true;
            applyBigPlayerLineHold();
        } else if (state.holdBigPlayerLines) {
            // Emby redraws the lines itself for the new item.
            releaseBigPlayerLineHold();
        }
    }

    // ---- LIVE line instead of the progress slider (1.3.154) -------------
    // A live station has no running time, so Emby's players show an empty
    // slider and an elapsed time counting up. While one plays, the slider and
    // the time numbers are replaced by a thin line with "LIVE" in the middle,
    // as in the owner's EmbyAudio app (LiveBar): rounded lines at 25% of the
    // text colour, the word at 60%, semi-bold. Mini-player: on its top line
    // (3 px line, 10 px text). Big player: in the slider's place (4 px line,
    // 13 px text), the time numbers kept as empty space so nothing moves.
    // Display only: Emby's slider is hidden, not changed, and comes back as
    // soon as an item with a running time is shown. The lock screen and
    // CarPlay don't use these elements.
    var LIVE_BAR_CONTAINERS = [".nowPlayingBarPositionContainer", ".videoOsdPositionSliderContainer"];
    var LIVE_ATTRIBUTE = "data-radiometa-live";
    var LIVE_STYLE_ID = "radiometa-live-bar-style";
    var LIVE_STYLE = [
        ".radioMetaLiveBar{display:none}",
        "html[data-radiometa-live] .radioMetaLiveBar{display:flex;align-items:center;position:absolute;left:0;right:0;top:50%;transform:translateY(-50%);pointer-events:none;z-index:2}",
        "html[data-radiometa-live] .videoOsdPositionSliderContainer{position:relative}",
        "html[data-radiometa-live] .nowPlayingBarPositionContainer>:not(.radioMetaLiveBar),",
        "html[data-radiometa-live] .videoOsdPositionSliderContainer>:not(.radioMetaLiveBar),",
        "html[data-radiometa-live] .videoOsdPositionText,",
        "html[data-radiometa-live] .videoOsdDurationText,",
        "html[data-radiometa-live] .nowPlayingBarCurrentTime{visibility:hidden}",
        ".radioMetaLiveBar-line{flex:1 1 auto;min-width:0;border-radius:999px;background:currentColor;opacity:.25}",
        ".radioMetaLiveBar-text{flex:none;font-weight:600;line-height:1;opacity:.6}",
        // Mini-player: the 12 px LIVE strip is centred on the bar's top line, where the progress line runs.
        ".nowPlayingBarPositionContainer .radioMetaLiveBar{height:12px;gap:6px}",
        // TV layout: the mini-player sits in the header and its progress line
        // runs along the bottom edge, so the strip sits just inside that edge.
        "html[data-radiometa-live] .headerNowPlaying .nowPlayingBarPositionContainer .radioMetaLiveBar{top:auto;bottom:0;transform:none}",
        ".nowPlayingBarPositionContainer .radioMetaLiveBar-line{height:3px}",
        ".nowPlayingBarPositionContainer .radioMetaLiveBar-text{font-size:10px}",
        ".videoOsdPositionSliderContainer .radioMetaLiveBar{height:30px;gap:8px}",
        ".videoOsdPositionSliderContainer .radioMetaLiveBar-line{height:4px}",
        ".videoOsdPositionSliderContainer .radioMetaLiveBar-text{font-size:13px}"
    ].join("");

    function ensureLiveBarStyle() {
        if (!document.getElementById || document.getElementById(LIVE_STYLE_ID)) return;
        var parent = document.head || document.documentElement;
        if (!parent || !document.createElement) return;
        var style = document.createElement("style");
        style.id = LIVE_STYLE_ID;
        style.textContent = LIVE_STYLE;
        parent.appendChild(style);
    }

    function ensureLiveBars() {
        if (!document.querySelectorAll || !document.createElement) return;
        LIVE_BAR_CONTAINERS.forEach(function (selector) {
            Array.prototype.slice.call(document.querySelectorAll(selector)).forEach(function (container) {
                if (!container || !container.querySelector || container.querySelector(".radioMetaLiveBar")) return;
                var bar = document.createElement("div");
                bar.className = "radioMetaLiveBar";
                bar.setAttribute("aria-hidden", "true");
                ["radioMetaLiveBar-line", "radioMetaLiveBar-text", "radioMetaLiveBar-line"].forEach(function (part) {
                    var element = document.createElement("span");
                    element.className = part;
                    if (part === "radioMetaLiveBar-text") element.textContent = "LIVE";
                    bar.appendChild(element);
                });
                container.appendChild(bar);
            });
        });
    }

    // Emby only sends displayitemupdated after fetching the new item, so on a
    // stop or a switch the LIVE line would otherwise stay up for a moment over
    // the next song or video. Playback signals settle it at once: a stop with
    // nothing next clears it; a start or state change with an item decides it.
    function updateLiveBarFromPlaybackEvent(eventName, args) {
        if (!Array.isArray(args)) return;
        if (eventName === "playbackstop") {
            var next = args.some(function (arg) { return !!arg && typeof arg === "object" && !!arg.NextMediaType; });
            if (!next) updateLiveBar(null);
            return;
        }
        for (var i = 0; i < args.length; i++) {
            var arg = args[i];
            if (arg && typeof arg === "object" && arg.NowPlayingItem && typeof arg.NowPlayingItem === "object") {
                updateLiveBar(arg.NowPlayingItem);
                return;
            }
        }
    }

    function updateLiveBar(item) {
        var root = document.documentElement;
        if (!root || !root.setAttribute) return;
        if (isLiveAudioStation(item)) {
            ensureLiveBarStyle();
            ensureLiveBars();
            if (root.getAttribute(LIVE_ATTRIBUTE) !== "true") root.setAttribute(LIVE_ATTRIBUTE, "true");
        } else if (root.getAttribute(LIVE_ATTRIBUTE) !== null) {
            root.removeAttribute(LIVE_ATTRIBUTE);
        }
    }

    function syncLargePlayerStationSlot(container, track, marker) {
        if (!container || !track || !track.station || !container.querySelector) return false;
        var target = container.querySelector(".videoOsdThirdTitle");
        if (!target) return false;

        var presentationMarker = marker + "\u0000station";
        var needsUpdate = normalizeText(target.textContent) !== normalizeText(track.station) ||
            (target.classList && target.classList.contains("hide")) ||
            target.getAttribute("data-radiometa-player-title") !== presentationMarker;
        if (!needsUpdate) return false;

        rememberPlayerDisplayElement(target);
        target.textContent = track.station;
        if (target.classList) target.classList.remove("hide");
        target.setAttribute("data-radiometa-player-title", presentationMarker);
        target.title = track.station;
        return true;
    }

    function ensureFullPlayerObserver(container) {
        if (!container || typeof MutationObserver !== "function") return;
        var existing = state.fullPlayerObservers.find(function (entry) {
            return entry.container === container;
        });
        if (existing) return;

        var observer = new MutationObserver(function () {
            if (state.fullPlayerObserverBusy || !state.activeTrack) return;
            var currentTrack = state.activeTrack;
            var marker = currentTrack.song + "\u0000" + currentTrack.artist + "\u0000" + (currentTrack.station || "");
            var target = container.querySelector && container.querySelector(".videoOsdThirdTitle");
            if (!target) return;
            if (normalizeText(target.textContent) === normalizeText(currentTrack.station) &&
                !(target.classList && target.classList.contains("hide")) &&
                target.getAttribute("data-radiometa-player-title") === marker + "\u0000station") {
                return;
            }

            state.fullPlayerObserverBusy = true;
            try { syncLargePlayerStationSlot(container, currentTrack, marker); }
            finally { state.fullPlayerObserverBusy = false; }
        });

        observer.observe(container, {
            childList: true,
            characterData: true,
            subtree: true,
            attributes: true,
            attributeFilter: ["class"]
        });
        state.fullPlayerObservers.push({ container: container, observer: observer });
    }

    function updateLargePlayerPresentation(track, marker) {
        if (!track || !track.station || !document.querySelectorAll) return;

        // Emby's native OSD renders the song and artist from Name and
        // ArtistItems. Its third row is the native album slot. Radio Meta puts
        // the original .strm station title in that third row, leaving the
        // first two native OSD rows and all their native typography untouched.
        var containers = Array.prototype.slice.call(document.querySelectorAll(".videoOsdText"));
        containers.forEach(function (container) {
            ensureFullPlayerObserver(container);
            if (isVisible(container)) syncLargePlayerStationSlot(container, track, marker);
        });
    }

    function applyFullPlayerStationPresentation(track) {
        if (!track || !track.song || !track.artist || !document.querySelectorAll) return;
        var marker = track.song + "\u0000" + track.artist + "\u0000" + (track.station || "");

        // Mini-player song and artist rendering is data-only. This function
        // touches only the full-player's native third/album station row.
        updateLargePlayerPresentation(track, marker);
        state.lastPresentationKey = marker;
    }

    function scheduleFullPlayerStationPresentation() {
        var track = state.activeTrack;
        if (!track) return;
        var callback = function () {
            if (state.activeTrack === track) {
                try { applyFullPlayerStationPresentation(track); }
                catch (error) { warn("could not align the native player title, artist, and station line", error); }
            }
        };
        if (typeof window.requestAnimationFrame === "function") window.requestAnimationFrame(callback);
        else window.setTimeout(callback, 0);
    }

    function parseSessionTrack(session) {
        var item = session && session.NowPlayingItem;
        if (!item) return null;

        var song = getProviderValue(item, "RadioMetaSong");
        var artist = getProviderValue(item, "RadioMetaArtist");
        if (!song || !artist) return null;

        // Radio Meta only owns audio station sessions. Do not re-label video
        // or ordinary library playback even if stale ProviderIds are present.
        if (item.MediaType && item.MediaType !== "Audio") return null;
        if (item.Type && item.Type !== "Audio" && item.MediaType !== "Audio") return null;

        return {
            song: song,
            artist: artist,
            station: normalizeText(item.Name),
            sessionId: String(session.Id || ""),
            deviceId: String(session.DeviceId || "")
        };
    }

    function trackKey(track) {
        return track.song + "\u0000" + track.artist;
    }

    function takeItemSnapshot(item) {
        var snapshot = {};
        ITEM_FIELDS.forEach(function (field) {
            snapshot[field] = {
                existed: Object.prototype.hasOwnProperty.call(item, field),
                value: item[field]
            };
        });
        return snapshot;
    }

    function restoreItemSnapshot(item, snapshot) {
        if (!item || !snapshot) return;
        ITEM_FIELDS.forEach(function (field) {
            var saved = snapshot[field];
            if (!saved) return;
            if (saved.existed) item[field] = saved.value;
            else {
                try { delete item[field]; } catch (_) { item[field] = undefined; }
            }
        });
    }

    // ---- Library artist lookup -------------------------------------------
    // Emby renders each ArtistItems entry as a link to that item's page. An entry
    // without an Id produced a link to "undefined" ("Unrecognized Guid format").
    // Resolve the artist name to a real library artist so the link opens the
    // artist page. Results are cached per name: hits for 30 minutes, misses for
    // 10 minutes, so a newly added artist is picked up without polling Emby.
    var ARTIST_HIT_TTL_MS = 30 * 60 * 1000;
    var ARTIST_MISS_TTL_MS = 10 * 60 * 1000;
    var ARTIST_LOOKUP_TIMEOUT_MS = 2500;
    var ARTIST_CACHE_LIMIT = 200;

    function artistMatchKey(value) {
        var text = normalizeText(value).toLowerCase();
        try { text = text.normalize("NFD").replace(/[\u0300-\u036f]/g, ""); } catch (_) { }
        return text.replace(/\s+/g, " ").trim();
    }

    // Names to try, most specific first: the full credit, then the primary
    // artist before "feat."/"ft."/"featuring"/"with". "&", "and" and "," are not
    // split because many real artist names contain them.
    function artistNameCandidates(artist) {
        var full = normalizeText(artist);
        var names = full ? [full] : [];
        var primary = full.split(/\s+(?:feat\.?|ft\.?|featuring|with)\s+|\s*[(\[](?:feat\.?|ft\.?|featuring)\s+/i)[0];
        primary = normalizeText(primary);
        if (primary && artistMatchKey(primary) !== artistMatchKey(full)) names.push(primary);
        return names;
    }

    function withTimeout(promise, ms) {
        return new Promise(function (resolve) {
            var done = false;
            var timer = window.setTimeout(function () { if (!done) { done = true; resolve(null); } }, ms);
            Promise.resolve(promise).then(function (value) {
                if (done) return;
                done = true; window.clearTimeout(timer); resolve(value);
            }, function () {
                if (done) return;
                done = true; window.clearTimeout(timer); resolve(null);
            });
        });
    }

    function findArtistInLibrary(api, name) {
        var params = {
            SearchTerm: name,
            Limit: 20,
            Recursive: true,
            EnableImages: false,
            EnableUserData: false
        };
        try {
            var userId = typeof api.getCurrentUserId === "function" ? api.getCurrentUserId() : "";
            if (userId) params.UserId = userId;
        } catch (_) { }

        var wanted = artistMatchKey(name);
        return api.getJSON(api.getUrl("Artists", params)).then(function (result) {
            var items = result && Array.isArray(result.Items) ? result.Items : [];
            for (var i = 0; i < items.length; i++) {
                var candidate = items[i];
                if (candidate && candidate.Id && artistMatchKey(candidate.Name) === wanted) {
                    return { id: String(candidate.Id), name: String(candidate.Name || name) };
                }
            }
            return null;
        });
    }

    // Resolves to { id, name } for a library artist, or null. Never rejects.
    function resolveLibraryArtist(api, artist) {
        var key = artistMatchKey(artist);
        if (!key || !api || typeof api.getJSON !== "function" || typeof api.getUrl !== "function") {
            return Promise.resolve(null);
        }

        var now = Date.now();
        var cached = state.artistLookups.get(key);
        if (cached && now < cached.expiresAt) return Promise.resolve(cached.artist);

        var names = artistNameCandidates(artist);
        var lookup = names.reduce(function (previous, name) {
            return previous.then(function (found) {
                return found || findArtistInLibrary(api, name);
            });
        }, Promise.resolve(null));

        return withTimeout(lookup, ARTIST_LOOKUP_TIMEOUT_MS).then(function (found) {
            if (state.artistLookups.size >= ARTIST_CACHE_LIMIT) state.artistLookups.clear();
            state.artistLookups.set(key, {
                artist: found,
                expiresAt: Date.now() + (found ? ARTIST_HIT_TTL_MS : ARTIST_MISS_TTL_MS)
            });
            return found;
        }, function () { return null; });
    }

    // Returns the cached library artist ({ id, name }), null for a cached miss,
    // or undefined when the name has not been looked up (or the entry expired).
    function peekLibraryArtist(artist) {
        var cached = state.artistLookups.get(artistMatchKey(artist));
        if (!cached || Date.now() >= cached.expiresAt) return undefined;
        return cached.artist;
    }

    // Starts a lookup in the background and never blocks the poll. The display
    // must not wait for it: during a station switch, a blocking lookup let the
    // previous station's song be written onto the newly selected station. The
    // result lands in the cache, and the next poll (within 2.5 s) adds the link.
    function startLibraryArtistLookup(api, artist) {
        var key = artistMatchKey(artist);
        if (!key || state.artistLookupsInFlight.has(key)) return;
        state.artistLookupsInFlight.add(key);
        resolveLibraryArtist(api, artist).then(function () {
            state.artistLookupsInFlight.delete(key);
        }, function () {
            state.artistLookupsInFlight.delete(key);
        });
    }

    // The ArtistItems entry Emby renders on the artist line. A library artist
    // links to its own page. An artist that is not in the library keeps the same
    // text, but its link targets the playing station item, so a click opens the
    // station instead of failing with "Unrecognized Guid format".
    function buildArtistItem(item, track) {
        if (track.artistId) {
            return { Name: track.artist, Id: track.artistId, Type: "MusicArtist" };
        }
        var fallback = { Name: track.artist };
        if (item && item.Id) {
            fallback.Id = item.Id;
            if (item.Type) fallback.Type = item.Type;
            if (item.ServerId) fallback.ServerId = item.ServerId;
        }
        return fallback;
    }

    function applyDisplayMetadata(item, track) {
        if (!item || !track) return;
        // These are the only two fields consumed by Emby's native mini-player
        // text renderer: Name and ArtistItems. Do not synthesize album/artist
        // alternatives or rewrite other metadata fields.
        item.Name = track.song;
        item.ArtistItems = [buildArtistItem(item, track)];

        // Preserve the .strm item's native station artwork metadata. Emby's
        // mini-player reads PrimaryImageTag or ImageTags.Primary and builds its
        // image URL from PrimaryImageItemId/Id. Its album-art fallback uses
        // AlbumId + AlbumPrimaryImageTag. Clearing any of these values causes
        // exactly the observed flash-then-blank behavior: the original image
        // appears on initial render, then disappears after Radio Meta emits
        // the decorated display item. Do not rewrite image tags, image IDs,
        // aspect ratio, blur hashes, or album-art fallback fields here.
    }

    function decorateDisplayItemCopy(item, track) {
        if (!item || !state.activeItemId || String(item.Id || "") !== state.activeItemId) return;
        if (!state.displayItemSnapshots.has(item)) {
            state.displayItemSnapshots.set(item, takeItemSnapshot(item));
        }
        state.displayItemCopies.add(item);
        applyDisplayMetadata(item, track);
    }

    function updateKnownDisplayItemCopies(track) {
        state.displayItemCopies.forEach(function (item) {
            if (item && String(item.Id || "") === state.activeItemId) {
                applyDisplayMetadata(item, track);
            }
        });
    }

    function restoreDisplayItemCopies() {
        state.displayItemCopies.forEach(function (item) {
            restoreItemSnapshot(item, state.displayItemSnapshots.get(item));
        });
        state.displayItemCopies.clear();
        state.displayItemSnapshots = new WeakMap();
    }

    function installDisplayItemEventOverlay(events) {
        if (!events || typeof events.trigger !== "function" || state.eventTriggerWrapper) return;

        // Emby's mini-player and full-player OSD publish this event immediately
        // before rendering. Decorate only the display-item data object that the
        // native renderer will read; the mini-player DOM is never queried or changed.
        var originalTrigger = events.trigger;
        var wrappedTrigger = function (target, eventName, args) {
            try {
                if (eventName === "displayitemupdated" && Array.isArray(args) &&
                    state.activeTrack && state.activeItemId) {
                    var originalItem = args[0];
                    var displayItem = args[1];
                    if (originalItem && displayItem &&
                        String(originalItem.Id || "") === state.activeItemId &&
                        String(displayItem.Id || originalItem.Id || "") === state.activeItemId) {
                        decorateDisplayItemCopy(displayItem, state.activeTrack);
                    }
                }
            } catch (error) {
                warn("could not decorate Emby's native display item", error);
            }

            // A display update for a different item than the station being
            // labelled is a station switch; remember the previous track so its
            // leftovers can be cleared after Emby renders the new item.
            var switchedFromTrack = null;
            try {
                if (eventName === "displayitemupdated" && Array.isArray(args) &&
                    state.activeTrack && state.activeItemId && args[0] &&
                    String(args[0].Id || "") && String(args[0].Id || "") !== state.activeItemId) {
                    switchedFromTrack = state.activeTrack;
                }
            } catch (_) { switchedFromTrack = null; }

            var result = originalTrigger.apply(this, arguments);

            // Remember each player renderer's latest display update, whatever item
            // it is for, so a new song can be drawn by repeating it (see
            // redrawPlayersQuietly). Matched to the playing item by its Id.
            if (eventName === "displayitemupdated" && Array.isArray(args) && args[0] &&
                target && typeof target === "object") {
                state.lastDisplayUpdates.set(target, args);
            }

            if (eventName === "displayitemupdated" && Array.isArray(args)) {
                try { updateBigPlayerLineHold(args[1] || args[0]); }
                catch (error) { warn("could not hold the big player's line layout", error); }
            }
            if (eventName === "displayitemupdated" && Array.isArray(args)) {
                try { updateLiveBar(args[1] || args[0]); }
                catch (error) { warn("could not show the LIVE line", error); }
            } else if (eventName === "playbackstop" || eventName === "playbackstart" || eventName === "statechange") {
                try { updateLiveBarFromPlaybackEvent(eventName, args); }
                catch (error) { warn("could not update the LIVE line", error); }
            }

            if (switchedFromTrack) {
                try { clearBigPlayerAfterStationSwitch(switchedFromTrack); }
                catch (error) { warn("could not clear the big player after a station switch", error); }
            }
            // Emby's subscribers render synchronously from displayitemupdated.
            // Keep the only DOM text update confined to the full-player station row.
            // Not after a station switch: the active track is still the previous
            // station's until the new song is applied, so re-applying it here
            // would write the old station name under the new .strm title.
            if (eventName === "displayitemupdated" && state.activeTrack && !switchedFromTrack) {
                scheduleFullPlayerStationPresentation();
            }
            return result;
        };

        events.trigger = wrappedTrigger;
        state.eventTriggerOriginal = originalTrigger;
        state.eventTriggerWrapper = wrappedTrigger;
    }

    // The play queue list in the big player (and "save play queue", "play next"
    // and the queue reported to the server) reads its entries through
    // PlaybackManager.getPlaylist(), which returns the same item objects Radio
    // Meta renames. Hand the list a copy of the playing station with its
    // original station name, so the list never changes when the song does and
    // Emby has nothing to redraw. The real item stays renamed, so the player,
    // mini-player, lock screen and CarPlay are unchanged. Entries are matched by
    // PlaylistItemId, so clicking, removing and dragging work as before.
    // Emby's list keeps the item objects it was given and later redraws single
    // rows from them without asking again (when a row scrolls into view, or when
    // the server reports playback data). So the list must never be given an
    // object Radio Meta will rename. Each such item gets one copy, kept for the
    // rest of the page, so the list sees the same object on every refresh (with
    // music Emby always gets the same objects and redraws without a blink).
    // Copies are made for:
    //  - Radio Meta's active station, from its saved station data;
    //  - the audio item that is playing now, even before Radio Meta has applied
    //    its first song. After a station change Emby refreshes the list before
    //    the song arrives; handing it Emby's own object then let the later
    //    rename leak into a single-row redraw (station name -> song -> station
    //    name, with a flicker).
    function stationCopyForPlayQueue(item) {
        var cached = state.playQueueCopies.get(item);
        if (cached) return cached;
        var copy = Object.assign({}, item);
        if (item === state.activeItem && state.activeSnapshot) restoreItemSnapshot(copy, state.activeSnapshot);
        state.playQueueCopies.set(item, copy);
        return copy;
    }

    function currentlyPlayingAudioItem() {
        try {
            var modules = state.modules;
            var player = modules && modules.currentPlayer && modules.currentPlayer.get();
            var item = player && player.isLocalPlayer ? modules.playbackManager.currentItem(player) : null;
            return item && item.MediaType === "Audio" ? item : null;
        } catch (_) {
            return null;
        }
    }

    function withStationNamesInPlayQueue(result) {
        try {
            if (!result || !Array.isArray(result.Items)) return result;
            var active = state.activeItem && state.activeSnapshot ? state.activeItem : null;
            var playing = currentlyPlayingAudioItem();
            var hasActive = !!active && result.Items.indexOf(active) >= 0;
            var hasPlaying = !!playing && result.Items.indexOf(playing) >= 0;
            if (!hasActive && !hasPlaying) return result;
            var copy = Object.assign({}, result);
            copy.Items = result.Items.map(function (item) {
                return (item === active || item === playing) ? stationCopyForPlayQueue(item) : item;
            });
            return copy;
        } catch (error) {
            warn("could not keep the station name in the play queue", error);
            return result;
        }
    }

    // Radio Meta's two wrappers (the display event above and getPlaylist below)
    // live only in this page. Browsers can fire pagehide and keep the page
    // running (back/forward cache, a frozen or restored tab), and before 1.3.153
    // Radio Meta removed both wrappers then and never put them back, so the
    // players showed the station title for the rest of the page. On each poll,
    // put back a wrapper that is missing (Emby's original is in place again).
    // Returns true when one was put back.
    function ensurePlayerHooks(modules) {
        var restored = false;
        var events = modules && modules.events;
        var playbackManager = modules && modules.playbackManager;
        if (events && typeof events.trigger === "function" &&
            (!state.eventTriggerWrapper || events.trigger === state.eventTriggerOriginal)) {
            state.eventTriggerWrapper = null;
            state.eventTriggerOriginal = null;
            installDisplayItemEventOverlay(events);
            restored = !!state.eventTriggerWrapper;
        }
        if (playbackManager && typeof playbackManager.getPlaylist === "function" &&
            (!state.getPlaylistWrapper || playbackManager.getPlaylist === state.getPlaylistOriginal)) {
            state.getPlaylistWrapper = null;
            state.getPlaylistOriginal = null;
            installPlayQueueOverlay(playbackManager);
            restored = !!state.getPlaylistWrapper || restored;
        }
        return restored;
    }

    function installPlayQueueOverlay(playbackManager) {
        if (!playbackManager || typeof playbackManager.getPlaylist !== "function" || state.getPlaylistWrapper) return;
        var originalGetPlaylist = playbackManager.getPlaylist;
        var wrappedGetPlaylist = function () {
            var result = originalGetPlaylist.apply(this, arguments);
            if (result && typeof result.then === "function") {
                return result.then(withStationNamesInPlayQueue);
            }
            return withStationNamesInPlayQueue(result);
        };
        playbackManager.getPlaylist = wrappedGetPlaylist;
        state.getPlaylistOriginal = originalGetPlaylist;
        state.getPlaylistWrapper = wrappedGetPlaylist;
    }

    // When the background library-artist lookup finishes for the song already
    // showing, only the artist link changes. Sending Emby's full statechange for
    // that made the big player refresh and scroll its whole play-queue list a
    // second time (in videoosd.js the list refresh runs only in onStateChanged).
    // Instead, repeat the last display update each player renderer (mini-player
    // and big player) received for this station: they redraw their text, now
    // with the link, and the list is left alone. The item itself is updated as
    // usual, so the lock screen and CarPlay read the same name and artist.
    // Returns false (the caller then uses the normal full update) if no display
    // update for this station has been seen yet.
    function updateArtistLinkQuietly(track, modules) {
        var player = modules.currentPlayer.get();
        if (!player || !player.isLocalPlayer) return false;
        var item = modules.playbackManager.currentItem(player);
        if (!item || item !== state.activeItem) return false;
        if (!knownDisplayUpdatesFor(item).length) return false;

        state.activeTrack = track;
        applyDisplayMetadata(item, track);
        updateKnownDisplayItemCopies(track);
        return redrawPlayersQuietly(item, modules);
    }

    function knownDisplayUpdatesFor(item) {
        var itemId = String((item && item.Id) || "");
        var updates = [];
        if (!itemId) return updates;
        state.lastDisplayUpdates.forEach(function (args, target) {
            if (Array.isArray(args) && args[0] && String(args[0].Id || "") === itemId) {
                updates.push({ target: target, args: args });
            }
        });
        return updates;
    }

    // Repeats the last display update each player renderer (mini-player and big
    // player) received for this item. They redraw their text from the decorated
    // copy; Emby's play-queue list is not refreshed, because in videoosd.js that
    // happens only on statechange. Returns false when none is known yet.
    //
    // The repeated update carries a copy of Emby's state flagged
    // IsInitialRequest. In the big player's displayitemupdated handler that flag
    // only skips repainting the poster/logo, header logo, backdrop and tab title;
    // the song, artist and station lines are still drawn. Those pictures belong
    // to the station, which hasn't changed, so repainting them only made the logo
    // flicker. The mini-player ignores the flag. Emby's own display updates (a
    // station change, playback start) are never flagged by Radio Meta, so a new
    // station's logo and backdrop appear as usual.
    function redrawPlayersQuietly(item, modules) {
        var updates = knownDisplayUpdatesFor(item);
        if (!updates.length) return false;
        updates.forEach(function (update) {
            var args = update.args.slice(0);
            args[2] = Object.assign({}, args[2] || {}, { IsInitialRequest: true });
            try { modules.events.trigger(update.target, "displayitemupdated", args); }
            catch (error) { warn("could not redraw a player quietly", error); }
        });
        return true;
    }

    // True while Emby's big player (videoosd) is on screen in this browser.
    function isBigPlayerOpen() {
        if (!document.querySelectorAll) return false;
        return Array.prototype.slice.call(document.querySelectorAll(".videoOsdText")).some(function (element) {
            try {
                return !!element && typeof element.getClientRects === "function" && element.getClientRects().length > 0;
            } catch (_) {
                return false;
            }
        });
    }

    function publishNativePlayerState(player, playbackManager, events) {
        // Emby's own nowplayingbar listens to statechange on the raw player and
        // expects the freshly-built PlaybackManager state as the second arg.
        var freshState = playbackManager.getPlayerState(player);
        if (!freshState || !freshState.NowPlayingItem) {
            throw new Error("Emby did not return a NowPlayingItem for the active player");
        }
        events.trigger(player, "statechange", [freshState]);
    }

    function applyTrackToNativePlayer(track, modules) {
        var player = modules.currentPlayer.get();
        if (!player || !player.isLocalPlayer) return false;

        var item = modules.playbackManager.currentItem(player);
        if (!item) return false;
        if (item.MediaType && item.MediaType !== "Audio") return false;
        if (item.Type && item.Type !== "Audio" && item.MediaType !== "Audio") return false;

        if (state.activeItem && state.activeItem !== item) {
            if (String(item.Id || "") !== state.activeItemId) clearBigPlayerAfterStationSwitch(state.activeTrack);
            else restorePlayerDisplayPolish();
            restoreItemSnapshot(state.activeItem, state.activeSnapshot);
            restoreDisplayItemCopies();
            state.activeItem = null;
            state.activeSnapshot = null;
            state.activeItemId = "";
            state.activeTrack = null;
        }

        if (!state.activeItem) {
            state.activeItem = item;
            state.activeSnapshot = takeItemSnapshot(item);
        }

        // Change only the two text fields consumed by Emby's native mini-player:
        // Name and ArtistItems. The .strm item ID, playback URL, media source,
        // album fields, artwork fields, CSS, and mini-player DOM remain untouched.
        state.activeItemId = String(item.Id || "");
        state.activeTrack = track;
        applyDisplayMetadata(item, track);

        // Emby's OSD controllers cache display items by the .strm item's ID.
        // The displayitemupdated event bridge above decorates that cached copy
        // immediately before Emby's native mini/full-player renderers consume it.
        updateKnownDisplayItemCopies(track);

        // While the big player is open, a full statechange makes Emby refresh and
        // scroll its whole play-queue list, which flickers in a long list. Then
        // the players are redrawn by repeating their display update instead. The
        // lock screen and CarPlay read the renamed item on Emby's next time
        // update. With the big player closed, nothing changes: the statechange
        // is sent exactly as before.
        var redrawnQuietly = isBigPlayerOpen() && redrawPlayersQuietly(item, modules);
        if (!redrawnQuietly) {
            publishNativePlayerState(player, modules.playbackManager, modules.events);
        }
        scheduleFullPlayerStationPresentation();
        return redrawnQuietly ? "quiet" : "statechange";
    }

    function restoreNativePlayerItem(modules) {
        if (!state.activeItem || !state.activeSnapshot) return;
        var oldItem = state.activeItem;
        var playingItem = null;
        try {
            var currentLocalPlayer = modules.currentPlayer.get();
            playingItem = currentLocalPlayer ? modules.playbackManager.currentItem(currentLocalPlayer) : null;
        } catch (_) { playingItem = null; }
        // If a different station is now playing, clear rather than restore the
        // big player, so the previous station's content is not pasted back.
        if (playingItem && String(playingItem.Id || "") && String(playingItem.Id || "") !== state.activeItemId) {
            clearBigPlayerAfterStationSwitch(state.activeTrack);
        } else {
            restorePlayerDisplayPolish();
        }
        restoreItemSnapshot(oldItem, state.activeSnapshot);
        restoreDisplayItemCopies();
        state.activeItem = null;
        state.activeSnapshot = null;
        state.activeItemId = "";
        state.activeTrack = null;
        state.lastTrackKey = "";

        try {
            var player = modules.currentPlayer.get();
            if (player && player.isLocalPlayer && modules.playbackManager.currentItem(player) === oldItem) {
                publishNativePlayerState(player, modules.playbackManager, modules.events);
            }
        } catch (error) {
            warn("could not refresh the restored native player item", error);
        }
    }

    function getSessionDeviceId(api) {
        try {
            return typeof api.deviceId === "function" ? String(api.deviceId() || "") : "";
        } catch (_) {
            return "";
        }
    }

    function findLocalSession(sessions, api) {
        var deviceId = getSessionDeviceId(api);
        if (deviceId) {
            return sessions.find(function (session) {
                return session && String(session.DeviceId || "") === deviceId &&
                    session.NowPlayingItem;
            }) || null;
        }

        // If the client cannot identify its device, only use a single active
        // session. Never guess between multiple active devices.
        var candidates = sessions.filter(function (session) {
            return session && session.NowPlayingItem;
        });
        return candidates.length === 1 ? candidates[0] : null;
    }

    function fallbackSessionCandidates(session) {
        var playState = (session && session.PlayState) || {};
        return [
            playState.PlaySessionId,
            session && session.PlaySessionId,
            session && session.Id
        ].filter(Boolean).map(String);
    }

    // True when a playing session's ProviderIds lack a track, so the server
    // fallback is needed.
    function needsServerFallback(sessions) {
        return sessions.some(function (session) {
            return !!(session && session.NowPlayingItem && !parseSessionTrack(session));
        });
    }

    function parseCurrentTrackFallback(session, currentTracks) {
        if (!session || !Array.isArray(currentTracks)) return null;
        var candidates = fallbackSessionCandidates(session);

        var current = currentTracks.find(function (track) {
            return track && candidates.indexOf(String(track.SessionId || "")) >= 0 &&
                track.SongTitle && track.Artist;
        });
        if (!current) return null;

        return {
            song: String(current.SongTitle),
            artist: String(current.Artist),
            station: normalizeText(current.StationName),
            sessionId: String(session.Id || ""),
            deviceId: String(session.DeviceId || "")
        };
    }

    function getDashboardSecondaryRows(titleElement) {
        var parent = titleElement && titleElement.parentElement;
        if (!parent || !parent.children) return [];
        return Array.prototype.filter.call(parent.children, function (row) {
            return row !== titleElement && row.classList && row.classList.contains("cardText-secondary");
        });
    }

    function saveDashboardRow(row, original) {
        if (!row || original.secondaryRows.some(function (entry) { return entry.element === row; })) return;
        original.secondaryRows.push({
            element: row,
            html: row.innerHTML,
            style: row.getAttribute("style"),
            part: row.getAttribute("data-radiometa-part"),
            stationKey: row.getAttribute("data-radiometa-station-key")
        });
    }

    function restoreDashboardRow(saved) {
        var row = saved && saved.element;
        if (!row || row.isConnected === false) return;
        row.innerHTML = saved.html;
        if (saved.style === null) row.removeAttribute("style");
        else row.setAttribute("style", saved.style);
        if (saved.part === null) row.removeAttribute("data-radiometa-part");
        else row.setAttribute("data-radiometa-part", saved.part);
        if (saved.stationKey === null) row.removeAttribute("data-radiometa-station-key");
        else row.setAttribute("data-radiometa-station-key", saved.stationKey);
    }

    function restoreDashboardElement(element) {
        if (!element || element.getAttribute("data-radiometa-player-integration") !== "1") return;
        var original = state.dashboardOriginals.get(element);
        if (original && typeof original === "object") {
            element.innerHTML = original.html;
            if (original.style === null) element.removeAttribute("style");
            else element.setAttribute("style", original.style);
            if (original.part === null) element.removeAttribute("data-radiometa-part");
            else element.setAttribute("data-radiometa-part", original.part);
            original.secondaryRows.forEach(restoreDashboardRow);
            if (original.createdArtistRow && original.createdArtistRow.parentNode) {
                original.createdArtistRow.parentNode.removeChild(original.createdArtistRow);
            }
            if (original.createdStationRow && original.createdStationRow.parentNode) {
                original.createdStationRow.parentNode.removeChild(original.createdStationRow);
            }
        } else if (typeof original === "string") {
            element.innerHTML = original;
        }
        element.removeAttribute("data-radiometa-player-integration");
        element.removeAttribute("data-radiometa-station-key");
        state.dashboardOriginals.delete(element);
    }

    function renderDashboardElement(element, track, stationKey) {
        var original = state.dashboardOriginals.get(element);
        if (!original) {
            original = {
                html: element.innerHTML || "",
                style: element.getAttribute("style"),
                part: element.getAttribute("data-radiometa-part"),
                secondaryRows: []
            };
            state.dashboardOriginals.set(element, original);
        }

        var secondaryRows = getDashboardSecondaryRows(element);
        var artistRow = secondaryRows[0] || null;
        var stationRow = secondaryRows[1] || null;
        if (!artistRow && element.parentNode && typeof document.createElement === "function") {
            artistRow = document.createElement("div");
            artistRow.className = "cardText cardText-secondary innerFooter-cardText";
            if (element.nextSibling) element.parentNode.insertBefore(artistRow, element.nextSibling);
            else element.parentNode.appendChild(artistRow);
            original.createdArtistRow = artistRow;
        }
        if (!stationRow && element.parentNode && typeof document.createElement === "function") {
            stationRow = document.createElement("div");
            stationRow.className = "cardText cardText-secondary innerFooter-cardText";
            if (artistRow && artistRow.nextSibling) element.parentNode.insertBefore(stationRow, artistRow.nextSibling);
            else if (artistRow) element.parentNode.appendChild(stationRow);
            else if (element.nextSibling) element.parentNode.insertBefore(stationRow, element.nextSibling);
            else element.parentNode.appendChild(stationRow);
            original.createdStationRow = stationRow;
        }

        // Emby's ActiveSession card provides three native text rows:
        // 1) item title, 2) artist, 3) elapsed/runtime text. A live .strm has
        // no useful runtime, so use that existing third row for the station
        // title. Keeping all three native rows also preserves the vertical
        // alignment/height Emby uses for ordinary MP3 items with a time counter.
        // Only replace text; do not apply custom margins, fonts, colors, or offsets.
        var songIsCurrent = normalizeText(element.textContent) === normalizeText(track.song) &&
            element.getAttribute("data-radiometa-part") === "song" &&
            element.getAttribute("data-radiometa-station-key") === stationKey;
        var artistIsCurrent = artistRow &&
            normalizeText(artistRow.textContent) === normalizeText(track.artist) &&
            artistRow.getAttribute("data-radiometa-part") === "artist" &&
            artistRow.getAttribute("data-radiometa-station-key") === stationKey;
        var stationText = normalizeText(track.station);
        var stationIsCurrent = stationRow && stationText &&
            normalizeText(stationRow.textContent) === stationText &&
            stationRow.getAttribute("data-radiometa-part") === "station" &&
            stationRow.getAttribute("data-radiometa-station-key") === stationKey;

        if (!songIsCurrent) element.textContent = track.song;
        element.setAttribute("data-radiometa-part", "song");
        element.setAttribute("data-radiometa-player-integration", "1");
        element.setAttribute("data-radiometa-station-key", stationKey);

        if (artistRow) {
            saveDashboardRow(artistRow, original);
            if (!artistIsCurrent) artistRow.textContent = track.artist;
            artistRow.setAttribute("data-radiometa-part", "artist");
            artistRow.setAttribute("data-radiometa-station-key", stationKey);
        }

        if (stationRow) {
            saveDashboardRow(stationRow, original);
            if (stationText && !stationIsCurrent) stationRow.textContent = stationText;
            stationRow.setAttribute("data-radiometa-part", "station");
            stationRow.setAttribute("data-radiometa-station-key", stationKey);
        }

        // If a future Emby card template adds rows beyond its native third
        // line, hide only additional empty placeholders. The third row is now
        // the station title and must remain visible.
        secondaryRows.slice(2).forEach(function (row) {
            if (normalizeText(row.textContent)) return;
            saveDashboardRow(row, original);
            if (row.style.getPropertyValue("display") !== "none") {
                row.style.setProperty("display", "none", "important");
                row.style.setProperty("height", "0", "important");
                row.style.setProperty("min-height", "0", "important");
                row.style.setProperty("margin", "0", "important");
                row.style.setProperty("padding", "0", "important");
            }
        });
    }

    function getDashboardNowPlayingContainer() {
        // Emby 4.10.x dashboard/dashboard.html does not define #dashboardPage.
        // Its actual active-session card container is .nowPlayingSessions.
        return document.querySelector(".nowPlayingSessions");
    }

    function applyDashboardTracks() {
        var dashboard = getDashboardNowPlayingContainer();
        if (!dashboard) {
            state.dashboardMatchedCards = 0;
            return 0;
        }

        var matchedCards = 0;
        var nodes = dashboard.querySelectorAll(".cardText.cardText-first");
        Array.prototype.forEach.call(nodes, function (element) {
            var markedKey = element.getAttribute("data-radiometa-station-key");
            var markedArtistRow = element.parentElement && element.parentElement.querySelector
                ? element.parentElement.querySelector('[data-radiometa-part="artist"]') : null;
            var hasRadioParts = element.getAttribute("data-radiometa-part") === "song" && !!markedArtistRow;

            // If Emby reused the title node but replaced its native subtitle
            // row, restore our saved card markup and match the new station name.
            if (markedKey && !hasRadioParts) {
                restoreDashboardElement(element);
                markedKey = "";
            }

            var stationKey = markedKey || normalizeText(element.textContent).toLowerCase();
            var track = state.dashboardTracks.get(stationKey);
            if (track) {
                renderDashboardElement(element, track, stationKey);
                matchedCards++;
            } else if (markedKey) {
                restoreDashboardElement(element);
            }
        });
        state.dashboardMatchedCards = matchedCards;
        return matchedCards;
    }

    function updateDashboard(sessions, currentTracks) {
        var nextTracks = new Map();
        sessions.forEach(function (session) {
            var track = parseSessionTrack(session) || parseCurrentTrackFallback(session, currentTracks);
            var item = session && session.NowPlayingItem;
            var stationKey = normalizeText(item && item.Name).toLowerCase();
            if (track && stationKey) nextTracks.set(stationKey, track);
        });

        state.dashboardTracks = nextTracks;
        // Use the real Dashboard Now Playing container as the route signal.
        // The 4.10.x dashboard template has .nowPlayingSessions, not #dashboardPage.
        var dashboard = getDashboardNowPlayingContainer();
        var inDashboard = !!dashboard;
        state.dashboardContainerFound = inDashboard;

        if (!inDashboard) {
            if (state.lastDashboardRoute) {
                var marked = document.querySelectorAll('[data-radiometa-player-integration="1"]');
                Array.prototype.forEach.call(marked, restoreDashboardElement);
            }
            if (state.dashboardObserver) {
                state.dashboardObserver.disconnect();
                state.dashboardObserver = null;
            }
            state.lastDashboardRoute = false;
            return;
        }

        state.lastDashboardRoute = true;
        if (!state.dashboardObserver && document.body && typeof MutationObserver === "function") {
            state.dashboardObserver = new MutationObserver(function () {
                applyDashboardTracks();
            });
            state.dashboardObserver.observe(document.body, {
                childList: true,
                subtree: true,
                characterData: true
            });
        }
        applyDashboardTracks();
    }

    async function tick() {
        if (state.busy) return;
        state.busy = true;

        try {
            var api = window.ApiClient;
            if (!api || typeof api.getSessions !== "function") {
                throw new Error("Emby ApiClient is not ready");
            }

            var modules = await loadEmbyModules();
            // A player page built after the last display update gets its LIVE line here.
            if (document.documentElement && document.documentElement.getAttribute &&
                document.documentElement.getAttribute(LIVE_ATTRIBUTE) !== null) {
                try { ensureLiveBars(); } catch (error) { warn("could not add the LIVE line", error); }
            }
            if (ensurePlayerHooks(modules)) {
                // Draw the current song again now that the players can get it.
                state.lastTrackKey = "";
                log("put back its hook on Emby's player display, which was missing; redrawing the players with the current song");
            }
            var sessionList = await api.getSessions();
            var sessions = Array.isArray(sessionList) ? sessionList : [];
            // Ask the server only when a playing session lacks Radio Meta ProviderIds.
            // In normal playback that is never, so no extra request delays the poll.
            // MyCurrentTracks returns only the signed-in user's own sessions.
            var fallbackResponse = needsServerFallback(sessions)
                ? await api.getJSON(api.getUrl("RadioMeta/MyCurrentTracks"))
                    .catch(function () { return null; })
                : null;
            var fallback = fallbackResponse && Array.isArray(fallbackResponse.Items)
                ? fallbackResponse.Items : [];

            updateDashboard(sessions, fallback);

            var session = findLocalSession(sessions, api);
            var track = session ? parseSessionTrack(session) : null;
            if (session && !track) track = parseCurrentTrackFallback(session, fallback);

            if (!session || !track) {
                // Retain the last song briefly during short session API gaps.
                if (state.activeItem && Date.now() - state.lastRadioSeenAt > 10000) {
                    restoreNativePlayerItem(modules);
                    log("restored the original Emby item after radio metadata ended");
                }
                return;
            }

            state.lastRadioSeenAt = Date.now();
            // Never await here: the session snapshot must be applied immediately.
            var libraryArtist = peekLibraryArtist(track.artist);
            if (libraryArtist === undefined) startLibraryArtistLookup(api, track.artist);
            track.artistId = libraryArtist ? libraryArtist.id : "";
            var key = trackKey(track) + "\u0000" + track.artistId;
            var player = modules.currentPlayer.get();
            var item = player ? modules.playbackManager.currentItem(player) : null;
            var changedItem = item && item !== state.activeItem;

            if (key !== state.lastTrackKey || changedItem || !state.activeItem) {
                // Same song on the same station, and the only news is the library
                // artist link: add it without a full player refresh.
                var onlyLinkAdded = !changedItem && state.activeItem && state.activeTrack &&
                    track.artistId && !state.activeTrack.artistId &&
                    trackKey(track) === trackKey(state.activeTrack);
                if (onlyLinkAdded && updateArtistLinkQuietly(track, modules)) {
                    state.lastTrackKey = key;
                    log("added the library artist link without a full player refresh", {
                        song: track.song,
                        artist: track.artist
                    });
                } else {
                    var applied = applyTrackToNativePlayer(track, modules);
                    if (applied) {
                        state.lastTrackKey = key;
                        log(applied === "quiet"
                            ? "big player open: redrew the players with the new song without a full player refresh"
                            : "sent song and artist through Emby's native player statechange", {
                            song: track.song,
                            artist: track.artist,
                            artistLinked: !!track.artistId
                        });
                    }
                }
            }
        } catch (error) {
            warn("player integration tick did not complete", error);
        } finally {
            state.busy = false;
        }
    }

    function scheduleNextTick() {
        if (state.timer) window.clearTimeout(state.timer);
        var delay = document.visibilityState === "hidden" ? 6000 : 2500;
        state.timer = window.setTimeout(function () {
            state.timer = null;
            tick().finally(scheduleNextTick);
        }, delay);
    }

    // After a Radio Meta update, a browser can keep running its cached copy of
    // this script until the page is hard-reloaded. Once per page load, ask the
    // server which version it runs; if it differs, show Emby's own short toast
    // asking for Ctrl+F5, and note it for Diagnostics. Nothing reloads by itself
    // and Emby's files are not changed.
    function compareVersion(a, b) {
        var pa = String(a || "").split("."), pb = String(b || "").split(".");
        for (var i = 0; i < 3; i++) {
            var x = parseInt(pa[i], 10) || 0, y = parseInt(pb[i], 10) || 0;
            if (x !== y) return x < y ? -1 : 1;
        }
        return 0;
    }

    function checkPlayerScriptIsCurrent() {
        var api = window.ApiClient;
        if (!api || typeof api.getJSON !== "function" || typeof api.getUrl !== "function") return;
        api.getJSON(api.getUrl("RadioMeta/MyCurrentTracks", { VersionCheck: true })).then(function (response) {
            var serverVersion = response && typeof response.PluginVersion === "string" ? response.PluginVersion.trim() : "";
            if (!serverVersion) return;
            state.serverVersion = serverVersion;
            if (compareVersion(SCRIPT_VERSION, serverVersion) >= 0) return;
            state.scriptOutOfDate = true;
            console.warn("Radio Meta " + SCRIPT_VERSION + ": this browser is running the cached player script " + SCRIPT_VERSION +
                 " but the server runs Radio Meta " + serverVersion + "; press Ctrl+F5 to load the new version.");
            return window.Emby.importModule("./modules/toast/toast.js").then(function (module) {
                var showToast = module && module.default ? module.default : module;
                if (typeof showToast === "function") {
                    showToast({
                        text: "Radio Meta was updated",
                        secondaryText: "Press Ctrl+F5 (on a phone, close and reopen the tab) to load the new player."
                    });
                }
            });
        }).catch(function () { /* A failed check changes nothing. */ });
    }

    function start() {
        if (state.started) return;
        state.started = true;
        tick().finally(scheduleNextTick);
        window.setTimeout(checkPlayerScriptIsCurrent, 3000);
        document.addEventListener("visibilitychange", function () {
            if (document.visibilityState === "visible") tick();
        });
        window.addEventListener("hashchange", function () {
            updateDashboard([], []);
            tick();
        });
        window.addEventListener("pagehide", releaseBigPlayerLineHold);
        window.addEventListener("pagehide", function () { state.lastDisplayUpdates.clear(); });
        log("DLL-embedded integration started; native player display-item events are bridged without modifying Emby player source files");
    }

    var attempts = 0;
    var waitForEmby = window.setInterval(function () {
        attempts++;
        if (window.ApiClient && typeof window.ApiClient.getSessions === "function" &&
            window.Emby && typeof window.Emby.importModule === "function") {
            window.clearInterval(waitForEmby);
            start();
        } else if (attempts >= 60) {
            window.clearInterval(waitForEmby);
            warn("Emby startup APIs were not available; reload the web app after confirming the plugin is enabled");
        }
    }, 500);
}());

    return { version: "1.3.154" };
});
