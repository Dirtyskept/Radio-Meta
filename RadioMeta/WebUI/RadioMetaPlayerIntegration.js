/* AMD resource loaded through Emby's plugin-resource URL helper and module loader. */
define([], function () {
/*
 * Radio Meta 1.3.128 DLL-embedded Emby player integration.
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

    var GLOBAL_KEY = "__radioMetaPlayerIntegration1328";
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
        activeTrack: null,
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
            if (detail === undefined) console.info("Radio Meta 1.3.128: " + message);
            else console.info("Radio Meta 1.3.128: " + message, detail);
        } catch (_) { }
    }

    function warn(message, error) {
        var text = String(error && error.message ? error.message : error || "Unknown error");
        if (text === state.lastError) return;
        state.lastError = text;
        console.warn("Radio Meta 1.3.128: " + message + " (" + text.slice(0, 180) + ")");
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

    function applyDisplayMetadata(item, track) {
        if (!item || !track) return;
        // These are the only two fields consumed by Emby's native mini-player
        // text renderer: Name and ArtistItems. Do not synthesize album/artist
        // alternatives or rewrite other metadata fields.
        item.Name = track.song;
        item.ArtistItems = [{ Name: track.artist }];

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

            var result = originalTrigger.apply(this, arguments);
            // Emby's subscribers render synchronously from displayitemupdated.
            // Keep the only DOM text update confined to the full-player station row.
            if (eventName === "displayitemupdated" && state.activeTrack) {
                scheduleFullPlayerStationPresentation();
            }
            return result;
        };

        events.trigger = wrappedTrigger;
        state.eventTriggerOriginal = originalTrigger;
        state.eventTriggerWrapper = wrappedTrigger;
    }

    function restoreEventBusOverlay() {
        var events = state.modules && state.modules.events;
        if (events && state.eventTriggerWrapper && events.trigger === state.eventTriggerWrapper) {
            events.trigger = state.eventTriggerOriginal;
        }
        state.eventTriggerOriginal = null;
        state.eventTriggerWrapper = null;
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
            restorePlayerDisplayPolish();
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
        publishNativePlayerState(player, modules.playbackManager, modules.events);
        scheduleFullPlayerStationPresentation();
        return true;
    }

    function restoreNativePlayerItem(modules) {
        if (!state.activeItem || !state.activeSnapshot) return;
        restorePlayerDisplayPolish();
        var oldItem = state.activeItem;
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

    function parseCurrentTrackFallback(session, currentTracks) {
        if (!session || !Array.isArray(currentTracks)) return null;
        var playState = session.PlayState || {};
        var candidates = [
            playState.PlaySessionId,
            session.PlaySessionId,
            session.Id
        ].filter(Boolean).map(String);

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
            var responses = await Promise.all([
                api.getSessions(),
                api.getJSON(api.getUrl("RadioMeta/CurrentTracks")).catch(function () { return null; })
            ]);
            var sessions = Array.isArray(responses[0]) ? responses[0] : [];
            var fallback = responses[1] && Array.isArray(responses[1].Items)
                ? responses[1].Items : [];

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
            var key = trackKey(track);
            var player = modules.currentPlayer.get();
            var item = player ? modules.playbackManager.currentItem(player) : null;
            var changedItem = item && item !== state.activeItem;

            if (key !== state.lastTrackKey || changedItem || !state.activeItem) {
                if (applyTrackToNativePlayer(track, modules)) {
                    state.lastTrackKey = key;
                    log("sent song and artist through Emby's native player statechange", {
                        song: track.song,
                        artist: track.artist
                    });
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

    function start() {
        if (state.started) return;
        state.started = true;
        tick().finally(scheduleNextTick);
        document.addEventListener("visibilitychange", function () {
            if (document.visibilityState === "visible") tick();
        });
        window.addEventListener("hashchange", function () {
            updateDashboard([], []);
            tick();
        });
        window.addEventListener("pagehide", restoreEventBusOverlay);
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

    return { version: "1.3.128" };
});
