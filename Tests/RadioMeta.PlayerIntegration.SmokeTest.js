/*
 * Optional Node.js smoke test for the Radio Meta 1.3.155 embedded player bridge.
 * Simulates the Emby module/event contracts and the native display slots used
 * by Emby 4.10.x. It does not replace a live Emby/browser test.
 *
 * Run from the project root:
 *   node .\Tests\RadioMeta.PlayerIntegration.SmokeTest.js
 */
"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const assert = require("assert");

const scriptPath = path.join(__dirname, "..", "RadioMeta", "WebUI", "RadioMetaPlayerIntegration.js");
const source = fs.readFileSync(scriptPath, "utf8");
assert(!source.includes(".nowPlayingBarText"), "Player integration must not query the mini-player container");
assert(!source.includes(".nowplayingbar-textitems"), "Player integration must not query native mini-player text rows");
assert(!source.includes("restoreMiniPlayerNativeArtistStyle"), "Mini-player style rewriting must not return");
const listeners = new WeakMap();

const events = {
    on(target, name, callback) {
        if (!listeners.has(target)) listeners.set(target, {});
        (listeners.get(target)[name] ||= []).push(callback);
    },
    off() { },
    trigger(target, name, args) {
        const callbacks = ((listeners.get(target) || {})[name] || []).slice();
        callbacks.forEach(callback => callback.apply(target, [{ type: name }, ...(args || [])]));
    }
};

class FakeElement {
    constructor(tagName = "div", className = "") {
        this.tagName = tagName.toUpperCase();
        this.children = [];
        this.parentElement = null;
        this.parentNode = null;
        this.attributes = {};
        this.className = className;
        this._text = "";
        this.isConnected = true;
        const styleValues = {};
        this.style = {
            setProperty(name, value) { styleValues[name] = String(value); },
            removeProperty(name) { delete styleValues[name]; },
            getPropertyValue(name) { return styleValues[name] || ""; },
            getPropertyPriority() { return ""; },
            get display() { return styleValues.display || ""; },
            set display(value) { styleValues.display = String(value); }
        };
        this.classList = {
            add: name => {
                const names = new Set(this.className.split(/\s+/).filter(Boolean));
                names.add(name);
                this.className = Array.from(names).join(" ");
            },
            remove: name => {
                this.className = this.className.split(/\s+/).filter(x => x && x !== name).join(" ");
            },
            contains: name => this.className.split(/\s+/).includes(name)
        };
    }
    get firstChild() { return this.children[0] || null; }
    get nextSibling() {
        if (!this.parentElement) return null;
        const siblings = this.parentElement.children;
        return siblings[siblings.indexOf(this) + 1] || null;
    }
    get innerHTML() { return this.children.length ? this.children.map(x => x.innerHTML || x.textContent).join("") : this._text; }
    set innerHTML(value) { this._text = String(value || ""); this.children.forEach(x => { x.parentElement = null; x.parentNode = null; }); this.children = []; }
    get textContent() { return this.children.length ? this.children.map(x => x.textContent).join("") : this._text; }
    set textContent(value) { this._text = String(value || ""); this.children.forEach(x => { x.parentElement = null; x.parentNode = null; }); this.children = []; }
    appendChild(child) {
        if (child.parentElement) child.parentElement.removeChild(child);
        this.children.push(child);
        child.parentElement = this;
        child.parentNode = this;
        return child;
    }
    insertBefore(child, before) {
        if (child.parentElement) child.parentElement.removeChild(child);
        const index = before ? this.children.indexOf(before) : -1;
        if (index < 0) this.children.push(child); else this.children.splice(index, 0, child);
        child.parentElement = this;
        child.parentNode = this;
        return child;
    }
    removeChild(child) {
        this.children = this.children.filter(x => x !== child);
        child.parentElement = null;
        child.parentNode = null;
        child.isConnected = false;
        return child;
    }
    setAttribute(name, value) {
        this.attributes[name] = String(value);
        if (name === "class") this.className = String(value);
    }
    getAttribute(name) {
        if (name === "class") return this.className || null;
        return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null;
    }
    removeAttribute(name) {
        delete this.attributes[name];
        if (name === "class") this.className = "";
    }
    getClientRects() { return [1]; }
    matches(selector) {
        if (selector === ".cardText.cardText-first") return this.classList.contains("cardText") && this.classList.contains("cardText-first");
        if (selector === ".cardText-secondary") return this.classList.contains("cardText-secondary");
        if (selector === ".videoOsdText") return this.classList.contains("videoOsdText");
        if (selector === ".videoOsdThirdTitle") return this.classList.contains("videoOsdThirdTitle");
        if (selector === ".videoOsdParentTitle") return this.classList.contains("videoOsdParentTitle");
        if (selector === ".videoOsdTitle") return this.classList.contains("videoOsdTitle");
        return false;
    }
    querySelectorAll(selector) {
        const results = [];
        const visit = node => {
            node.children.forEach(child => {
                if (selector === "*" ||
                    (selector === ".cardText.cardText-first" && child.matches(selector)) ||
                    (selector === ".videoOsdThirdTitle" && child.matches(selector)) ||
                    (selector === ".videoOsdParentTitle" && child.matches(selector)) ||
                    (selector === ".videoOsdTitle" && child.matches(selector)) ||
                    (selector === ".nowplayingbar-textitems" && child.classList.contains("nowplayingbar-textitems")) ||
                    (selector === '[data-radiometa-part="artist"]' && child.getAttribute("data-radiometa-part") === "artist") ||
                    (selector === ".radioMetaLiveBar" && child.classList.contains("radioMetaLiveBar")) ||
                    (selector === ".radioMetaLiveCardFooter" && child.classList.contains("radioMetaLiveCardFooter")) ||
                    (selector === ".cardProgressBarContainer" && child.classList.contains("cardProgressBarContainer"))) {
                    results.push(child);
                }
                visit(child);
            });
        };
        visit(this);
        return results;
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    closest(selector) {
        const name = selector.charAt(0) === "." ? selector.slice(1) : null;
        for (let node = this; node; node = node.parentElement) {
            if (name && node.classList.contains(name)) return node;
        }
        return null;
    }
}

class FakeMutationObserver {
    constructor(callback) {
        this.callback = callback;
        this.target = null;
        this.options = null;
        this.disconnected = false;
    }
    observe(target, options) {
        this.target = target;
        this.options = options;
    }
    disconnect() {
        this.disconnected = true;
    }
    trigger() {
        if (!this.disconnected) this.callback([], this);
    }
}

const item = {
    Id: "radio-station-item-1",
    Name: "Test Radio Station",
    OriginalTitle: "Original station title metadata",
    SortName: "Original station sort name",
    Artists: ["Original station artist list"],
    AlbumArtist: "Original station album artist",
    MediaType: "Audio",
    Type: "Audio",
    ProviderIds: {},
    ImageTags: { Primary: "station-primary-tag" },
    PrimaryImageTag: "station-primary-tag",
    PrimaryImageItemId: "radio-station-item-1",
    PrimaryImageAspectRatio: 1,
    ImageBlurHashes: { Primary: "station-blur-hash" },
    Album: "Original station album metadata",
    AlbumId: "radio-station-album-1",
    AlbumPrimaryImageTag: "station-album-art-tag"
};
// The item playing locally; the big-player switch test changes it.
let localItem = item;
const player = {
    isLocalPlayer: true,
    currentItem() { return localItem; },
    streamInfo: { item }
};

let nativeDisplay = null;
// Emby's OSD controller is one long-lived object per player view.
const miniPlayerOsd = { name: "simulated-mini-player-osd" };
let stateChangeCount = 0;
let miniRenderedArtistId = null;
let miniRenderedName = null;
const renderedStates = [];
events.on(miniPlayerOsd, "displayitemupdated", function (_event, _item, displayItem, renderState) {
    renderedStates.push(renderState);
    const artist = displayItem && displayItem.ArtistItems && displayItem.ArtistItems[0];
    miniRenderedArtistId = artist ? artist.Id : null;
    miniRenderedName = displayItem ? displayItem.Name : null;
});
events.on(player, "statechange", function (_event, playerState) {
    stateChangeCount++;
    // In the pagehide test the display copy is built like Emby's own, from the
    // station's original data, so only Radio Meta's display hook can put the
    // song into it.
    const displayItem = testPageHide
        ? Object.assign({}, playerState.NowPlayingItem, { Name: "Test Radio Station", ArtistItems: [] })
        : Object.assign({}, playerState.NowPlayingItem);
    events.trigger(miniPlayerOsd, "displayitemupdated", [
        playerState.NowPlayingItem,
        displayItem,
        playerState
    ]);
    nativeDisplay = {
        title: displayItem.Name,
        artist: displayItem.ArtistItems && displayItem.ArtistItems[0] && displayItem.ArtistItems[0].Name,
        primaryImageTag: displayItem.PrimaryImageTag,
        imageTagsPrimary: displayItem.ImageTags && displayItem.ImageTags.Primary,
        primaryImageItemId: displayItem.PrimaryImageItemId,
        albumPrimaryImageTag: displayItem.AlbumPrimaryImageTag
    };
});

const currentPlayer = { get() { return player; } };
// Emby's getPlaylist is a prototype method that returns the play queue's own
// item objects, so the fake does the same.
const queuedOtherItem = { Id: "other-queue-item", Name: "Another queued station", PlaylistItemId: "playlistItem1" };
item.PlaylistItemId = "playlistItem2";
const playbackManagerPrototype = {
    getPlaylist() {
        return Promise.resolve({ Items: [queuedOtherItem, localItem], TotalRecordCount: 2 });
    }
};
const playbackManager = Object.assign(Object.create(playbackManagerPrototype), {
    currentItem(currentPlayerValue) { return currentPlayerValue.currentItem(); },
    getPlayerState(currentPlayerValue) {
        return {
            NowPlayingItem: Object.assign({}, currentPlayerValue.currentItem()),
            MediaSource: {},
            PlayState: {}
        };
    }
});

// RADIOMETA_TEST_SERVER_FALLBACK=1: the Emby session lacks Radio Meta
// ProviderIds, so the player must read the track from the per-user
// /RadioMeta/MyCurrentTracks route (never the admin-only CurrentTracks).
const useServerFallback = process.env.RADIOMETA_TEST_SERVER_FALLBACK === "1";
// RADIOMETA_TEST_BIG_PLAYER_SWITCH=1: after the first station shows its song,
// the local player switches to a second station. Like Emby, the simulated big
// player redraws the title line from the new item but leaves the artist line
// alone when the item has no artist. The old song, artist and station must be
// cleared to an invisible placeholder at once, never pasted back, and replaced
// by the new station's song as soon as it is applied.
const testBigPlayerSwitch = process.env.RADIOMETA_TEST_BIG_PLAYER_SWITCH === "1";
const sessions = [{
    Id: "play-session-1",
    DeviceId: "radio-meta-test-device",
    NowPlayingItem: {
        Id: item.Id,
        Name: "Test Radio Station",
        MediaType: "Audio",
        Type: "Audio",
        ProviderIds: useServerFallback ? {} : {
            RadioMetaSong: "Test Song Title",
            RadioMetaArtist: "Test Artist"
        }
    }
}];

const miniPlayerText = new FakeElement("div", "nowPlayingBarText");
const miniPlayerSong = new FakeElement("div", "textItems nowplayingbar-textitems");
const miniPlayerArtist = new FakeElement("div", "textItems nowplayingbar-textitems secondaryText");
miniPlayerSong.textContent = "Test Radio Station";
miniPlayerArtist.textContent = "Test Artist";
miniPlayerArtist.style.setProperty("color", "grey");
miniPlayerArtist.style.setProperty("font-weight", "normal");
miniPlayerText.appendChild(miniPlayerSong);
miniPlayerText.appendChild(miniPlayerArtist);
const miniPlayerOriginalSongClass = miniPlayerSong.className;
const miniPlayerOriginalArtistClass = miniPlayerArtist.className;
const miniPlayerOriginalArtistColor = miniPlayerArtist.style.getPropertyValue("color");
const miniPlayerOriginalArtistWeight = miniPlayerArtist.style.getPropertyValue("font-weight");
const fullPlayerTitle = new FakeElement("h3", "videoOsdParentTitle videoOsdParentTitle-small");
const fullPlayerArtist = new FakeElement("h3", "videoOsdTitle");
const fullPlayerStation = new FakeElement("h3", "videoOsdThirdTitle hide");
const fullPlayerSecondary = new FakeElement("div", "videoOsdSecondaryText secondaryText");
fullPlayerSecondary.appendChild(fullPlayerArtist);
fullPlayerSecondary.appendChild(fullPlayerStation);
const fullPlayerContainer = new FakeElement("div", "videoOsdText");
fullPlayerContainer.appendChild(fullPlayerTitle);
fullPlayerContainer.appendChild(fullPlayerSecondary);

const dashboardRoot = new FakeElement("div", "nowPlayingSessions");
const dashboardCardFooter = new FakeElement("div", "cardFooterContent");
const dashboardTitle = new FakeElement("div", "cardText cardText-first");
dashboardTitle.textContent = "Test Radio Station";
dashboardTitle.setAttribute("style", "padding-top:.22em");
const dashboardArtist = new FakeElement("div", "cardText cardText-secondary");
dashboardArtist.textContent = "\u00a0";
dashboardArtist.setAttribute("style", "margin-bottom:0");
const dashboardRuntime = new FakeElement("div", "cardText cardText-secondary");
dashboardRuntime.textContent = "\u00a0";
dashboardCardFooter.appendChild(dashboardTitle);
dashboardCardFooter.appendChild(dashboardArtist);
dashboardCardFooter.appendChild(dashboardRuntime);
// Emby's session card: .cardBox holds the footer; a live station has no
// progress bar, so Radio Meta adds its LIVE line to the card box (1.3.155).
const dashboardCardBox = new FakeElement("div", "cardBox cardBox-sideFooter");
dashboardCardBox.appendChild(dashboardCardFooter);
dashboardRoot.appendChild(dashboardCardBox);

// RADIOMETA_TEST_LIBRARY_ARTIST=1 simulates an Emby library that contains the
// playing artist; otherwise the library has no matching artist.
const libraryHasArtist = process.env.RADIOMETA_TEST_LIBRARY_ARTIST === "1";
// RADIOMETA_TEST_SLOW_ARTIST_LOOKUP=1 makes the artist lookup never answer. The
// song must still be shown on the first poll: the display must not wait for the
// lookup, or a station switch during the wait shows the old station's song.
const artistLookupHangs = process.env.RADIOMETA_TEST_SLOW_ARTIST_LOOKUP === "1";
const pendingTimers = [];
// RADIOMETA_TEST_BIG_PLAYER_OPEN / _CLOSED: a second song arrives with the big
// player open or closed (choice B: no full statechange only while it is open).
const testBigPlayerOpen = process.env.RADIOMETA_TEST_BIG_PLAYER_OPEN === "1";
const testBigPlayerClosed = process.env.RADIOMETA_TEST_BIG_PLAYER_CLOSED === "1";
// RADIOMETA_TEST_OUTDATED_SCRIPT=1: the server runs a newer Radio Meta than this
// cached player script, so one Emby toast asks for Ctrl+F5.
const testOutdatedScript = process.env.RADIOMETA_TEST_OUTDATED_SCRIPT === "1";
// RADIOMETA_TEST_PAGEHIDE=1: the browser fires pagehide but keeps the page (as
// Edge did). Radio Meta must keep its display hook, put it back if it goes
// missing anyway, and the players must keep getting each new song.
const testPageHide = process.env.RADIOMETA_TEST_PAGEHIDE === "1";
// RADIOMETA_TEST_LIVE_BAR=1: a live station shows the LIVE line in place of the
// progress slider (mini-player and big player); a song with a running time
// brings Emby's slider back.
const testLiveBar = process.env.RADIOMETA_TEST_LIVE_BAR === "1";
// RADIOMETA_TEST_DASHBOARD_LIVE=1: the Dashboard card's LIVE line over several
// polls: no duplicates, removed when Emby draws a progress bar, back when it
// goes, and removed when Radio Meta stops labelling the card.
const testDashboardLive = process.env.RADIOMETA_TEST_DASHBOARD_LIVE === "1";
let dashboardHidden = false;
const htmlRoot = new FakeElement("html");
const headElement = new FakeElement("head");
const miniPositionContainer = new FakeElement("div", "nowPlayingBarPositionContainer sliderContainer");
const bigPositionContainer = new FakeElement("div", "sliderContainer videoOsdPositionSliderContainer");
miniPositionContainer.appendChild(new FakeElement("input", "nowPlayingBarPositionSlider"));
bigPositionContainer.appendChild(new FakeElement("input", "videoOsdPositionSlider"));
const windowListeners = {};
const scriptVersionMatch = /var SCRIPT_VERSION = "([^"]+)"/.exec(fs.readFileSync(scriptPath, "utf8"));
const SCRIPT_VERSION_IN_SOURCE = scriptVersionMatch ? scriptVersionMatch[1] : "";
const versionChecks = [];
const toasts = [];
const LIBRARY_ARTIST_ID = "4242";
const artistLookups = [];
const trackRequests = [];
// The server's current-track records, as /RadioMeta/MyCurrentTracks returns them.
// Like the real server, the station-switch test clears them on a switch.
let serverTracks = [{
    SessionId: "play-session-1",
    StationName: "Test Radio Station",
    Artist: "Test Artist",
    SongTitle: "Test Song Title"
}];
const apiClient = {
    getSessions() { return Promise.resolve(sessions); },
    getJSON(url) {
        if (url && url.indexOf("/Artists") === 0) {
            artistLookups.push(url);
            if (artistLookupHangs) return new Promise(() => { });
            return Promise.resolve({
                Items: libraryHasArtist
                    ? [{ Name: "Some Other Artist", Id: "1" }, { Name: "TEST ARTIST", Id: LIBRARY_ARTIST_ID, Type: "MusicArtist" }]
                    : [{ Name: "Test Artist Tribute Band", Id: "2" }]
            });
        }
        // The once-per-page version check is not a track request.
        if (url && url.indexOf("/RadioMeta/MyCurrentTracks") === 0 && url.indexOf("VersionCheck=true") > 0) {
            versionChecks.push(url);
            return Promise.resolve({ Items: [], PluginVersion: testOutdatedScript ? "9.9.9" : SCRIPT_VERSION_IN_SOURCE });
        }
        if (url && url.indexOf("/RadioMeta/") === 0) {
            trackRequests.push(url.split("?")[0]);
            if (url.indexOf("/RadioMeta/MyCurrentTracks") === 0) {
                return Promise.resolve({ Items: serverTracks.slice() });
            }
        }
        return Promise.resolve({ Items: [] });
    },
    getUrl(value, params) {
        const query = params ? "?" + Object.keys(params).map(k => k + "=" + encodeURIComponent(params[k])).join("&") : "";
        return "/" + value + query;
    },
    getCurrentUserId() { return "test-user"; },
    deviceId() { return "radio-meta-test-device"; }
};

const moduleMap = {
    "./modules/playback/currentplayer.js": currentPlayer,
    "./modules/common/playback/playbackmanager.js": playbackManager,
    "./modules/emby-apiclient/events.js": events,
    "./modules/toast/toast.js": function (options) { toasts.push(options); }
};
const emby = {
    importModule(name) {
        if (!moduleMap[name]) return Promise.reject(new Error("Unexpected module: " + name));
        return Promise.resolve({ default: moduleMap[name] });
    }
};

let dashboardSelectorReads = 0;
let miniPlayerSelectorReads = 0;
const documentStub = {
    visibilityState: "visible",
    body: null,
    documentElement: htmlRoot,
    head: headElement,
    getElementById(id) { return headElement.children.find(child => child.id === id) || null; },
    addEventListener() { },
    querySelector(selector) {
        if (selector === ".nowPlayingSessions") {
            dashboardSelectorReads++;
            if (dashboardHidden) return null;
            return dashboardRoot;
        }
        return null;
    },
    querySelectorAll(selector) {
        if (selector === ".videoOsdText") return [fullPlayerContainer];
        if (selector === '[data-radiometa-player-integration="1"]') {
            return dashboardTitle.getAttribute("data-radiometa-player-integration") === "1" ? [dashboardTitle] : [];
        }
        if (selector === ".nowPlayingBarPositionContainer") return [miniPositionContainer];
        if (selector === ".videoOsdPositionSliderContainer") return [bigPositionContainer];
        if (selector.indexOf("nowPlayingBar") >= 0 || selector.indexOf("nowplayingbar-textitems") >= 0) {
            miniPlayerSelectorReads++;
            return [miniPlayerText];
        }
        return [];
    },
    createElement(tagName) { return new FakeElement(tagName); }
};
const windowStub = {
    ApiClient: apiClient,
    Emby: emby,
    location: { hash: "#!/dashboard" },
    setInterval(callback) { callback(); return 1; },
    clearInterval() { },
    // Timers are queued, not run, so the test controls when the next poll happens.
    setTimeout(callback) { pendingTimers.push(callback); return pendingTimers.length; },
    clearTimeout() { },
    requestAnimationFrame(callback) { callback(); return 1; },
    addEventListener(name, callback) { (windowListeners[name] ||= []).push(callback); },
    console
};

const context = {
    define(dependencies, factory) {
        if (!Array.isArray(dependencies) || typeof factory !== "function") {
            throw new Error("Unexpected AMD resource definition");
        }
        return factory();
    },
    window: windowStub,
    Emby: emby,
    ApiClient: apiClient,
    document: documentStub,
    console,
    Promise,
    Map,
    Set,
    WeakMap,
    Array,
    Object,
    String,
    Number,
    Date,
    Error,
    MutationObserver: FakeMutationObserver
};

const embyOriginalTrigger = events.trigger;
vm.runInNewContext(source, context, { filename: scriptPath });

// Run the queued timers once (the next poll), then let its promises settle.
function runNextPoll(done) {
    const timers = pendingTimers.splice(0, pendingTimers.length);
    timers.forEach(callback => { try { callback(); } catch (_) { } });
    setTimeout(done, 40);
}

setTimeout(() => {
    if (testBigPlayerOpen || testBigPlayerClosed) { runBigPlayerOpenTest(); return; }
    if (testOutdatedScript) { runNextPoll(checkOutdatedScript); return; }
    if (testPageHide) { runPageHideTest(); return; }
    if (testLiveBar) { runLiveBarTest(); return; }
    if (testDashboardLive) { runDashboardLiveTest(); return; }
    // The first poll shows the song at once and starts the artist lookup. With a
    // library artist, the link is added on the next poll from the cache.
    if (libraryHasArtist) runNextPoll(checkResults);
    else checkResults();
}, 40);

function checkResults() {
    const integration = windowStub.__radioMetaPlayerIntegration1355;
    const activeTrack = integration && integration.activeTrack;
    const result = {
        activeTrack: activeTrack && { song: activeTrack.song, artist: activeTrack.artist, station: activeTrack.station },
        localItemName: item.Name,
        localItemArtist: item.ArtistItems && item.ArtistItems[0] && item.ArtistItems[0].Name,
        preservedArtwork: {
            primaryImageTag: item.PrimaryImageTag,
            imageTagsPrimary: item.ImageTags && item.ImageTags.Primary,
            primaryImageItemId: item.PrimaryImageItemId,
            primaryImageAspectRatio: item.PrimaryImageAspectRatio,
            imageBlurHash: item.ImageBlurHashes && item.ImageBlurHashes.Primary,
            albumPrimaryImageTag: item.AlbumPrimaryImageTag
        },
        nativeDisplayArtwork: nativeDisplay && {
            primaryImageTag: nativeDisplay.primaryImageTag,
            imageTagsPrimary: nativeDisplay.imageTagsPrimary,
            primaryImageItemId: nativeDisplay.primaryImageItemId,
            albumPrimaryImageTag: nativeDisplay.albumPrimaryImageTag
        },
        dashboardContainerFound: integration && integration.dashboardContainerFound,
        dashboardMatchedCards: integration && integration.dashboardMatchedCards,
        dashboardSelectorReads,
        dashboardSong: dashboardTitle.textContent,
        dashboardArtist: dashboardArtist.textContent,
        dashboardStation: dashboardRuntime.textContent,
        dashboardArtistUsesNativeSecondaryRow: dashboardArtist.classList.contains("cardText-secondary"),
        dashboardStationUsesNativeThirdRow: dashboardRuntime.classList.contains("cardText-secondary"),
        dashboardNativeRowStylesPreserved: dashboardTitle.getAttribute("style") === "padding-top:.22em" &&
            dashboardArtist.getAttribute("style") === "margin-bottom:0",
        dashboardTitleHasNoNestedCustomLines: dashboardTitle.children.length === 0,
        dashboardStationRowVisible: dashboardRuntime.style.display !== "none",
        dashboardStationPart: dashboardRuntime.getAttribute("data-radiometa-part"),
        miniPlayerDomUntouched: miniPlayerSelectorReads === 0 &&
            miniPlayerText.children.length === 2 &&
            miniPlayerSong.className === miniPlayerOriginalSongClass &&
            miniPlayerArtist.className === miniPlayerOriginalArtistClass &&
            miniPlayerArtist.style.getPropertyValue("color") === miniPlayerOriginalArtistColor &&
            miniPlayerArtist.style.getPropertyValue("font-weight") === miniPlayerOriginalArtistWeight,
        onlyNativeMiniPlayerFieldsChanged: item.Name === "Test Song Title" &&
            item.ArtistItems && item.ArtistItems[0].Name === "Test Artist" &&
            item.OriginalTitle === "Original station title metadata" &&
            item.SortName === "Original station sort name" &&
            item.Artists[0] === "Original station artist list" &&
            item.AlbumArtist === "Original station album artist" &&
            item.Album === "Original station album metadata" &&
            item.AlbumId === "radio-station-album-1",
        fullPlayerObserverCount: integration && integration.fullPlayerObservers && integration.fullPlayerObservers.length,
        fullPlayerNativeTitleUntouched: fullPlayerTitle.children.length === 0,
        fullPlayerNativeArtistUntouched: fullPlayerArtist.textContent === "",
        fullPlayerStationAlbumSlot: fullPlayerStation.textContent,
        fullPlayerStationSlotVisible: !fullPlayerStation.classList.contains("hide")
    };

    // Simulate Emby's own OSD rerender writing a stale album value (the
    // duplicated artist observed by the user). The DLL-owned observer should
    // restore the station name in the native third/album slot.
    fullPlayerStation.textContent = "Test Artist";
    fullPlayerStation.classList.add("hide");
    (integration.fullPlayerObservers || []).forEach(entry => entry.observer.trigger());
    result.fullPlayerStationAfterNativeRerender = fullPlayerStation.textContent;
    result.fullPlayerStationRestoredAfterNativeRerender =
        fullPlayerStation.textContent === "Test Radio Station" && !fullPlayerStation.classList.contains("hide");

    const artistItem = item.ArtistItems && item.ArtistItems[0];
    result.artistLink = artistItem && { id: artistItem.Id, type: artistItem.Type };
    result.artistLookups = artistLookups.length;
    result.artistLinkCorrect = libraryHasArtist
        // In the library: link to the library artist.
        ? !!artistItem && artistItem.Id === LIBRARY_ARTIST_ID && artistItem.Type === "MusicArtist"
        // Not in the library (a near-miss name must not match): link to the
        // playing station item, never to an undefined ID.
        : !!artistItem && artistItem.Id === "radio-station-item-1";

    console.log(JSON.stringify(result, null, 2));

    result.trackRequests = trackRequests;
    const trackRequestsCorrect = useServerFallback
        // The fallback must use the per-user route only.
        ? trackRequests.length >= 1 && trackRequests.every(u => u === "/RadioMeta/MyCurrentTracks")
        // With ProviderIds present, no server track request should be made at all.
        : trackRequests.length === 0;
    if (!trackRequestsCorrect) {
        console.error("FAIL: unexpected server track requests: " + JSON.stringify(trackRequests));
        process.exitCode = 1;
        return;
    }

    if (!result.artistLinkCorrect || artistLookups.length < 1) {
        console.error("FAIL: artist link did not match the " + (libraryHasArtist ? "library-artist" : "not-in-library") + " expectation.");
        process.exitCode = 1;
        return;
    }

    if (!activeTrack || activeTrack.song !== "Test Song Title" ||
        activeTrack.artist !== "Test Artist" || activeTrack.station !== "Test Radio Station" ||
        item.Name !== "Test Song Title" || !item.ArtistItems || item.ArtistItems[0].Name !== "Test Artist" ||
        !nativeDisplay || nativeDisplay.title !== "Test Song Title" || nativeDisplay.artist !== "Test Artist" ||
        nativeDisplay.primaryImageTag !== "station-primary-tag" ||
        nativeDisplay.imageTagsPrimary !== "station-primary-tag" ||
        nativeDisplay.primaryImageItemId !== "radio-station-item-1" ||
        nativeDisplay.albumPrimaryImageTag !== "station-album-art-tag" ||
        item.PrimaryImageTag !== "station-primary-tag" ||
        !item.ImageTags || item.ImageTags.Primary !== "station-primary-tag" ||
        item.PrimaryImageItemId !== "radio-station-item-1" ||
        item.AlbumPrimaryImageTag !== "station-album-art-tag" ||
        !integration.dashboardContainerFound || integration.dashboardMatchedCards !== 1 ||
        dashboardSelectorReads < 1 || dashboardTitle.textContent !== "Test Song Title" ||
        dashboardArtist.textContent !== "Test Artist" || !dashboardArtist.classList.contains("cardText-secondary") ||
        dashboardRuntime.textContent !== "Test Radio Station" || !dashboardRuntime.classList.contains("cardText-secondary") ||
        dashboardRuntime.style.display === "none" || dashboardRuntime.getAttribute("data-radiometa-part") !== "station" ||
        dashboardTitle.getAttribute("style") !== "padding-top:.22em" ||
        dashboardArtist.getAttribute("style") !== "margin-bottom:0" || dashboardTitle.children.length !== 0 ||
        miniPlayerSelectorReads !== 0 ||
        miniPlayerText.children.length !== 2 ||
        miniPlayerSong.className !== miniPlayerOriginalSongClass ||
        miniPlayerArtist.className !== miniPlayerOriginalArtistClass ||
        miniPlayerArtist.style.getPropertyValue("color") !== miniPlayerOriginalArtistColor ||
        miniPlayerArtist.style.getPropertyValue("font-weight") !== miniPlayerOriginalArtistWeight ||
        item.OriginalTitle !== "Original station title metadata" ||
        item.SortName !== "Original station sort name" ||
        item.Artists[0] !== "Original station artist list" ||
        item.AlbumArtist !== "Original station album artist" ||
        item.Album !== "Original station album metadata" ||
        item.AlbumId !== "radio-station-album-1" ||
        fullPlayerTitle.children.length !== 0 || fullPlayerStation.textContent !== "Test Radio Station" ||
        fullPlayerStation.classList.contains("hide") || !result.fullPlayerStationRestoredAfterNativeRerender) {
        console.error("FAIL: native Emby display-slot simulation did not match the Radio Meta display specification.");
        process.exitCode = 1;
        return;
    }

    if (testBigPlayerSwitch) {
        runBigPlayerSwitchTest(integration);
        return;
    }

    if (libraryHasArtist) {
        // The link is added by repeating the display update, not by a second
        // full statechange (which made Emby refresh its whole play-queue list).
        if (stateChangeCount !== 1 || miniRenderedArtistId !== LIBRARY_ARTIST_ID) {
            console.error("FAIL: artist link: expected 1 statechange and the rendered artist Id " + LIBRARY_ARTIST_ID +
                ", got " + stateChangeCount + " statechange(s) and Id " + miniRenderedArtistId + ".");
            process.exitCode = 1;
            return;
        }
        console.log("PASS: the library artist link reaches the rendered player without a second full statechange.");
        if (versionChecks.length !== 1 || toasts.length !== 0) {
            console.error("FAIL: version check: expected one check and no toast when versions match, got " + versionChecks.length + " check(s) and " + toasts.length + " toast(s).");
            process.exitCode = 1;
            return;
        }
        console.log("PASS: when the browser's player script matches the server, the version check shows no message.");
    }
    console.log(libraryHasArtist
        ? "PASS: artist found in the Emby library links to its artist page (Id " + LIBRARY_ARTIST_ID + ")."
        : "PASS: artist not in the library keeps its text and links to the station item instead of an undefined ID.");
    if (artistLookupHangs) console.log("PASS: the song was shown on the first poll without waiting for the artist lookup.");
    console.log(useServerFallback
        ? "PASS: with no session ProviderIds, the track came from /RadioMeta/MyCurrentTracks only."
        : "PASS: with session ProviderIds present, no server track request was made.");
    console.log("PASS: Mini-player text DOM is never queried or modified (only the LIVE line is added to its progress area); Emby renders native markup/styles from Name and ArtistItems only.");
    console.log("PASS: Emby's native full-player title and artist slots are untouched; station occupies only the third/album row.");
    console.log("PASS: DLL observer restores station text if Emby rerenders a stale value into the third/album row.");
    const dashboardLiveFooters = dashboardCardBox.children.filter(child => child.classList.contains("radioMetaLiveCardFooter"));
    const dashboardLiveBar = dashboardLiveFooters[0] && dashboardLiveFooters[0].children[0];
    if (dashboardLiveFooters.length !== 1 || !dashboardLiveBar || dashboardLiveBar.textContent !== "LIVE" ||
        !dashboardLiveBar.classList.contains("radioMetaLiveBar-small")) {
        console.error("FAIL: the Dashboard card of a live station should get one small LIVE line (found " + dashboardLiveFooters.length + ").");
        process.exitCode = 1;
        return;
    }
    console.log("PASS: the Dashboard card of a live station gets one LIVE line in the mini-player's size, added to the card box (position not checked here).");
    console.log("PASS: Dashboard song, artist, and station title use Emby's three native ActiveSession rows; original inline styles are preserved and the live-stream station line fills the unused runtime row.");
    console.log("PASS: original .strm image tags, image IDs, aspect ratio, blur hashes, and album artwork metadata are retained.");
    checkPlayQueueKeepsStationName(integration);
}

// The big player's play queue list must show the station name, not the song,
// so the list never changes (and never flickers) when the song does. The real
// item keeps the song for the player, mini-player, lock screen and CarPlay.
function checkPlayQueueKeepsStationName(integration) {
    const songName = integration.activeTrack && integration.activeTrack.song;
    Promise.resolve(playbackManager.getPlaylist({}, player)).then(function (result) {
        const listed = result && result.Items ? result.Items[1] : null;
        const failures = [];
        if (!listed || listed.Name !== "Test Radio Station") failures.push("the queue entry should show the station name, got " + (listed && listed.Name));
        if (listed && listed.ArtistItems !== undefined) failures.push("the queue entry should not carry the song's artist");
        if (listed === item) failures.push("the queue entry should be a copy, not the renamed item");
        if (listed && listed.PlaylistItemId !== "playlistItem2") failures.push("the queue entry must keep its PlaylistItemId");
        if (result.Items[0] !== queuedOtherItem) failures.push("other queue entries must be passed through unchanged");
        if (!songName || item.Name !== songName) failures.push("the real item must still carry the song for the player and lock screen");
        return Promise.resolve(playbackManager.getPlaylist({}, player)).then(function (again) {
            if (!again || again.Items[1] !== listed) failures.push("the queue must get the same station copy on every refresh, like music");
            return failures;
        });
    }).then(function (failures) {
        if (failures.length) {
            console.error("FAIL: play queue: " + failures.join("; ") + ".");
            process.exitCode = 1;
            return;
        }
        console.log("PASS: the big player's play queue shows the station name while the player shows the song, so the list never changes or flickers.");
        console.log("PASS: the play queue gets the same station copy on every refresh, as with music.");
    }).catch(function (error) {
        console.error("FAIL: play queue check threw: " + (error && error.message));
        process.exitCode = 1;
    });
}

function runBigPlayerSwitchTest(integration) {
    const NBSP = "\u00a0";
    const failures = [];
    const expect = (condition, message) => { if (!condition) failures.push(message); };
    const secondItem = {
        Id: "radio-station-item-2",
        Name: "Second Radio Station",
        MediaType: "Audio",
        Type: "Audio",
        ProviderIds: {},
        ImageTags: { Primary: "second-station-tag" },
        PrimaryImageTag: "second-station-tag",
        PrimaryImageItemId: "radio-station-item-2"
    };

    // Emby has drawn the first station's song and artist in the big player.
    fullPlayerTitle.textContent = "Test Song Title";
    fullPlayerArtist.textContent = "Test Artist";
    const lockScreenDataBefore = JSON.stringify({ name: item.Name, artist: item.ArtistItems && item.ArtistItems[0] && item.ArtistItems[0].Name });

    // Simulated Emby big player, as in videoosd.js: always redraws the title
    // line; empties and hides the artist line when there is no artist; hides
    // the album line (leaving its old text) when there is no album.
    const bigPlayerTarget = { name: "simulated-big-player-osd" };
    events.on(bigPlayerTarget, "displayitemupdated", function (_event, _originalItem, displayItem) {
        fullPlayerTitle.textContent = displayItem.Name;
        if (displayItem.ArtistItems && displayItem.ArtistItems.length) {
            fullPlayerArtist.textContent = displayItem.ArtistItems.map(a => a.Name).join(", ");
            fullPlayerArtist.classList.remove("hide");
        } else {
            fullPlayerArtist.textContent = "";
            fullPlayerArtist.classList.add("hide");
        }
        if (!(displayItem.Album && displayItem.AlbumId)) fullPlayerStation.classList.add("hide");
    });

    // The switch: the local player moves on and Emby announces the new item.
    // The server ends the old station's session record, as it does on a switch.
    serverTracks = [];
    localItem = secondItem;
    events.trigger(bigPlayerTarget, "displayitemupdated", [secondItem, Object.assign({}, secondItem), {}]);
    // Emby refreshes its play-queue list right after the switch, before Radio
    // Meta has applied the new station's song. The list keeps the row objects it
    // gets and may later redraw one row from them without asking again.
    let rowStoredByList = null;
    Promise.resolve(playbackManager.getPlaylist({}, player)).then(function (result) {
        rowStoredByList = result && result.Items ? result.Items[1] : null;
    });
    expect(fullPlayerTitle.textContent === "Second Radio Station", "switch: new .strm title not shown (" + fullPlayerTitle.textContent + ")");
    expect(fullPlayerArtist.textContent === NBSP, "switch: old artist left under the new title (" + fullPlayerArtist.textContent + ")");
    expect(fullPlayerStation.textContent === NBSP, "switch: old station line left under the new title (" + fullPlayerStation.textContent + ")");
    expect((integration.fullPlayerObservers || []).length === 0, "switch: observer still re-applying the old station");
    // The layout is held at three lines while the new station waits for its song.
    expect(!fullPlayerArtist.classList.contains("hide") && !fullPlayerStation.classList.contains("hide"),
        "hold: Emby's empty artist/station lines were left hidden, so the layout jumps");
    // Emby draws again a moment later (after its async user lookup) and hides
    // the empty artist line; the hold puts it back before the browser paints.
    fullPlayerArtist.textContent = "";
    fullPlayerArtist.classList.add("hide");
    fullPlayerStation.textContent = "Old album name";
    fullPlayerStation.classList.add("hide");
    (integration.lineHoldObservers || []).forEach(entry => entry.observer.trigger());
    expect(!fullPlayerArtist.classList.contains("hide") && fullPlayerArtist.textContent === NBSP,
        "hold: a later Emby redraw hid the artist line again");
    expect(!fullPlayerStation.classList.contains("hide") && fullPlayerStation.textContent === NBSP,
        "hold: a hidden line was shown with its old text instead of the placeholder");
    expect(JSON.stringify({ name: item.Name, artist: item.ArtistItems && item.ArtistItems[0] && item.ArtistItems[0].Name }) === lockScreenDataBefore,
        "switch: display-item data (used by lock screen/CarPlay) was changed by the big-player clear");

    // Emby's own rerender of the station row must not bring the old name back.
    fullPlayerStation.textContent = "Test Radio Station";
    (integration.fullPlayerObservers || []).forEach(entry => entry.observer.trigger());
    fullPlayerStation.textContent = NBSP;

    // Next poll: the session shows the new station, not yet with a song.
    sessions[0].NowPlayingItem = Object.assign({}, secondItem, { ProviderIds: {} });
    runNextPoll(() => {
        expect(fullPlayerArtist.textContent === NBSP && fullPlayerStation.textContent === NBSP,
            "poll: old content pasted back (" + fullPlayerArtist.textContent + " / " + fullPlayerStation.textContent + ")");

        // Force the end-of-radio restore path for the old item.
        integration.lastRadioSeenAt = 0;
        runNextPoll(() => {
            expect(item.Name === "Test Radio Station", "restore: first station's own data not restored (" + item.Name + ")");
            expect(fullPlayerArtist.textContent === NBSP && fullPlayerStation.textContent === NBSP,
                "restore: old content pasted back (" + fullPlayerArtist.textContent + " / " + fullPlayerStation.textContent + ")");

            // The new station's song arrives.
            sessions[0].NowPlayingItem = Object.assign({}, secondItem, {
                ProviderIds: { RadioMetaSong: "Second Song", RadioMetaArtist: "Second Artist" }
            });
            serverTracks = [{ SessionId: "play-session-1", StationName: "Second Radio Station", Artist: "Second Artist", SongTitle: "Second Song" }];
            runNextPoll(() => {
                expect(secondItem.Name === "Second Song", "new song: not applied (" + secondItem.Name + ")");
                expect(secondItem.ArtistItems && secondItem.ArtistItems[0].Name === "Second Artist", "new song: artist not applied");
                expect(fullPlayerStation.textContent === "Second Radio Station", "new song: station line not filled (" + fullPlayerStation.textContent + ")");
                // The row the list stored before the song arrived must still show
                // the station: Radio Meta's rename of the playing item must not
                // reach it (that caused station name -> song -> station name).
                expect(!!rowStoredByList && rowStoredByList !== secondItem,
                    "list row: the list was given Emby's own playing object, which Radio Meta later renames");
                expect(!!rowStoredByList && rowStoredByList.Name === "Second Radio Station",
                    "list row: a single-row redraw would show '" + (rowStoredByList && rowStoredByList.Name) + "' instead of the station");

                // An ordinary song (it has a running time) is never held:
                // Emby's own hidden empty lines stay hidden.
                const ordinarySong = { Id: "music-item-1", Name: "Ordinary Song", MediaType: "Audio", Type: "Audio", RunTimeTicks: 2000000000 };
                localItem = ordinarySong;
                events.trigger(bigPlayerTarget, "displayitemupdated", [ordinarySong, Object.assign({}, ordinarySong), {}]);
                (integration.lineHoldObservers || []).forEach(entry => entry.observer.trigger());
                expect(fullPlayerArtist.classList.contains("hide"), "ordinary song: its empty artist line was un-hidden");
                expect(integration.holdBigPlayerLines === false && (integration.lineHoldObservers || []).length === 0,
                    "ordinary song: the line hold was not released");
                if (failures.length) {
                    failures.forEach(f => console.error("FAIL: " + f));
                    process.exitCode = 1;
                    return;
                }
                console.log("PASS: on a switch, the old song, artist and station under the new .strm title are cleared to an invisible placeholder at once.");
                console.log("PASS: while a station waits for its song, the big player keeps all three lines (invisible placeholders), so nothing below it moves; ordinary songs are not held.");
                console.log("PASS: the previous station's saved big-player content is never pasted back, and its station name is not re-applied.");
                console.log("PASS: display-item data used by the mini-player, lock screen and CarPlay is not changed by the clear.");
                console.log("PASS: the new station's song fills the big player on the first poll it arrives.");
                console.log("PASS: a play-queue row stored before the new song arrives keeps the station name after the song is applied.");
            });
        });
    });
}


// Choice B: while the big player is open, a new song is drawn by repeating each
// renderer's display update, without the full statechange that makes Emby
// refresh and scroll its whole play-queue list. With it closed, the statechange
// is sent exactly as before (lock screen and CarPlay timing unchanged).
function runBigPlayerOpenTest() {
    const failures = [];
    function expect(condition, message) { if (!condition) failures.push(message); }
    if (testBigPlayerClosed) fullPlayerContainer.getClientRects = function () { return []; };
    const before = stateChangeCount;
    expect(before === 1, "first song: expected the normal statechange, got " + before);
    sessions[0].NowPlayingItem.ProviderIds = { RadioMetaSong: "Next Song", RadioMetaArtist: "Next Artist" };
    runNextPoll(function () {
        const sent = stateChangeCount - before;
        expect(miniRenderedName === "Next Song", "the mini-player renderer did not get the new song (" + miniRenderedName + ")");
        expect(item.Name === "Next Song", "the playing item (read by the lock screen and CarPlay) was not renamed");
        if (testBigPlayerOpen) {
            expect(sent === 0, "big player open: a full statechange was still sent (" + sent + ")");
            // Emby's own update (from the first song's statechange) is not flagged;
            // Radio Meta's repeated one is, so the big player keeps its logo, and
            // Emby's state object itself is left unchanged.
            const emby = renderedStates[0], repeated = renderedStates[renderedStates.length - 1];
            expect(renderedStates.length >= 2, "expected Emby's update and Radio Meta's repeat, got " + renderedStates.length);
            expect(!(emby && emby.IsInitialRequest), "Emby's own display update was flagged (or its state was changed)");
            expect(!!(repeated && repeated.IsInitialRequest === true), "Radio Meta's repeated update was not flagged, so the big player would repaint its logo");
            expect(repeated !== emby, "Radio Meta must pass a copy of Emby's state, not change it");
        } else {
            expect(sent === 1, "big player closed: the normal statechange was not sent (" + sent + ")");
        }
        if (failures.length) {
            console.error("FAIL: " + failures.join("; ") + ".");
            process.exitCode = 1;
            return;
        }
        console.log(testBigPlayerOpen
            ? "PASS: with the big player open, a new song is drawn without a full statechange, so the play-queue list is not refreshed; the repeat is flagged so the logo isn't repainted, and Emby's own state is untouched."
            : "PASS: with the big player closed, a new song still sends the normal statechange (lock screen and CarPlay unchanged).");
    });
}

function checkOutdatedScript() {
    const integration = windowStub.__radioMetaPlayerIntegration1355;
    setTimeout(function () {
        const failures = [];
        if (versionChecks.length !== 1) failures.push("expected one version check, got " + versionChecks.length);
        if (toasts.length !== 1 || !/Ctrl\+F5/.test(String(toasts[0] && toasts[0].secondaryText))) failures.push("expected one Ctrl+F5 toast, got " + JSON.stringify(toasts));
        if (!integration || integration.scriptOutOfDate !== true || integration.serverVersion !== "9.9.9") failures.push("the out-of-date state was not recorded for Diagnostics");
        if (trackRequests.length) failures.push("the version check was counted as a track request");
        if (failures.length) {
            console.error("FAIL: update message: " + failures.join("; ") + ".");
            process.exitCode = 1;
            return;
        }
        console.log("PASS: a cached player script older than the server shows one Emby toast asking for Ctrl+F5, and records it for Diagnostics.");
    }, 40);
}

function runPageHideTest() {
    const integration = windowStub.__radioMetaPlayerIntegration1355;
    const failures = [];
    function expect(condition, message) { if (!condition) failures.push(message); }
    function hookInPlace() {
        return !!integration.eventTriggerWrapper && events.trigger === integration.eventTriggerWrapper &&
            integration.eventTriggerOriginal === embyOriginalTrigger;
    }
    function playQueueHookInPlace() {
        return !!integration.getPlaylistWrapper && playbackManager.getPlaylist === integration.getPlaylistWrapper;
    }
    fullPlayerContainer.getClientRects = function () { return []; };
    expect(miniRenderedName === "Test Song Title", "first song: the mini-player did not get the song (" + miniRenderedName + ")");

    // 1. pagehide while the page stays open: both hooks stay.
    (windowListeners.pagehide || []).forEach(function (callback) { callback({ type: "pagehide", persisted: true }); });
    expect(hookInPlace(), "pagehide removed the display hook");
    expect(playQueueHookInPlace(), "pagehide removed the play-queue hook");

    // 2. The hooks go missing anyway (Emby's originals are back, as in 1.3.153
    //    after pagehide): the next poll puts them back and redraws the song.
    events.trigger = embyOriginalTrigger;
    playbackManager.getPlaylist = integration.getPlaylistOriginal;
    miniRenderedName = null;
    runNextPoll(function () {
        expect(hookInPlace(), "the missing display hook was not put back (or was wrapped twice)");
        expect(playQueueHookInPlace(), "the missing play-queue hook was not put back");
        expect(miniRenderedName === "Test Song Title", "after the hook was put back, the players were not redrawn with the current song (" + miniRenderedName + ")");

        // 3. The next song reaches the players.
        sessions[0].NowPlayingItem.ProviderIds = { RadioMetaSong: "Next Song", RadioMetaArtist: "Next Artist" };
        runNextPoll(function () {
            expect(miniRenderedName === "Next Song", "the next song did not reach the mini-player (" + miniRenderedName + ")");
            expect(item.Name === "Next Song", "the playing item (lock screen and CarPlay) was not renamed");
            if (failures.length) {
                console.error("FAIL: pagehide: " + failures.join("; ") + ".");
                process.exitCode = 1;
                return;
            }
            console.log("PASS: pagehide keeps Radio Meta's player hooks; if they go missing anyway, the next poll puts them back once and redraws the current song, and the next song still reaches the players.");
        });
    });
}

function runLiveBarTest() {
    const failures = [];
    function expect(condition, message) { if (!condition) failures.push(message); }
    function bars(container) { return container.children.filter(child => child.classList.contains("radioMetaLiveBar")); }

    // The station in this test has no running time, so it is live.
    events.trigger(miniPlayerOsd, "displayitemupdated", [item, Object.assign({}, item), {}]);
    expect(htmlRoot.getAttribute("data-radiometa-live") === "true", "a live station did not mark the page as live");
    expect(bars(miniPositionContainer).length === 1, "the mini-player has no LIVE line");
    expect(bars(bigPositionContainer).length === 1, "the big player has no LIVE line");
    const miniBar = bars(miniPositionContainer)[0];
    expect(!!miniBar && miniBar.textContent === "LIVE", "the LIVE line does not read LIVE (" + (miniBar && miniBar.textContent) + ")");
    expect(!!miniBar && miniBar.children.length === 3, "the LIVE line is not line - LIVE - line");
    expect(!!miniBar && miniBar.getAttribute("aria-hidden") === "true", "the LIVE line is not hidden from screen readers");
    expect(headElement.children.filter(child => String(child.id).indexOf("radiometa-live-bar-style-") === 0).length === 1, "the LIVE line style was not added once");
    expect(miniBar.classList.contains("radioMetaLiveBar-small"), "the mini-player LIVE line is not the small size");
    const bigBar = bars(bigPositionContainer)[0];
    expect(!!bigBar && bigBar.classList.contains("radioMetaLiveBar-large"), "the big player LIVE line is not the large size");
    expect(miniPositionContainer.children[0].classList.contains("nowPlayingBarPositionSlider"), "Emby's mini-player slider was removed");

    // A second update must not add a second line or style.
    events.trigger(miniPlayerOsd, "displayitemupdated", [item, Object.assign({}, item), {}]);
    expect(bars(miniPositionContainer).length === 1 && bars(bigPositionContainer).length === 1, "the LIVE line was added twice");
    expect(headElement.children.filter(child => String(child.id).indexOf("radiometa-live-bar-style-") === 0).length === 1, "the LIVE line style was added twice");

    // A song with a running time brings Emby's slider back.
    const ordinarySong = { Id: "music-item-live-test", Name: "Ordinary Song", MediaType: "Audio", Type: "Audio", RunTimeTicks: 2000000000 };
    events.trigger(miniPlayerOsd, "displayitemupdated", [ordinarySong, Object.assign({}, ordinarySong), {}]);
    expect(htmlRoot.getAttribute("data-radiometa-live") === null, "a song with a running time still shows the LIVE line");

    // And a station again shows it again, reusing the same element, also when
    // the update comes from the big player.
    const bigPlayerOsd = {};
    events.trigger(bigPlayerOsd, "displayitemupdated", [item, Object.assign({}, item), {}]);
    expect(htmlRoot.getAttribute("data-radiometa-live") === "true" && bars(miniPositionContainer).length === 1 && bars(bigPositionContainer).length === 1, "the LIVE line did not come back for the station");

    // Emby's "no item" update (playback ended) clears it.
    events.trigger(miniPlayerOsd, "displayitemupdated", [null, null, {}]);
    expect(htmlRoot.getAttribute("data-radiometa-live") === null, "a display update with no item still shows the LIVE line");

    // A stop with nothing next clears it at once, without waiting for a display update.
    events.trigger(miniPlayerOsd, "displayitemupdated", [item, Object.assign({}, item), {}]);
    const fakePlayer = {};
    events.trigger(fakePlayer, "playbackstop", [{ NowPlayingItem: item }]);
    expect(htmlRoot.getAttribute("data-radiometa-live") === null, "stopping a station did not clear the LIVE line");

    // A stop with a next item leaves it to the next item's start signal, so a
    // station-to-station switch doesn't flash Emby's slider.
    events.trigger(miniPlayerOsd, "displayitemupdated", [item, Object.assign({}, item), {}]);
    events.trigger(fakePlayer, "playbackstop", [{ NowPlayingItem: item, NextMediaType: "Audio" }]);
    expect(htmlRoot.getAttribute("data-radiometa-live") === "true", "a stop with a next item cleared the LIVE line too early");
    events.trigger(fakePlayer, "playbackstart", [{ NowPlayingItem: ordinarySong }]);
    expect(htmlRoot.getAttribute("data-radiometa-live") === null, "starting a song after a station did not clear the LIVE line at once");
    events.trigger(fakePlayer, "statechange", [{ NowPlayingItem: item }]);
    expect(htmlRoot.getAttribute("data-radiometa-live") === "true", "a state change to a station did not show the LIVE line");

    if (failures.length) {
        console.error("FAIL: LIVE line: " + failures.join("; ") + ".");
        process.exitCode = 1;
        return;
    }
    console.log("PASS: a live station adds one LIVE line (line - LIVE - line) to the mini-player and big player slider areas and marks the page live, without removing Emby's slider; a song, an empty display update, or a stop with nothing next clears the mark, and a stop with a next item waits for that item's start. (CSS is not applied in this test.)");
}

function runDashboardLiveTest() {
    const failures = [];
    const footers = () => dashboardCardBox.children.filter(child => child.classList.contains("radioMetaLiveCardFooter"));
    if (footers().length !== 1) failures.push("first poll: expected one LIVE line, found " + footers().length);
    runNextPoll(() => {
        if (footers().length !== 1) failures.push("second poll: expected one LIVE line, found " + footers().length);
        const progress = new FakeElement("div", "innerCardFooter");
        progress.appendChild(new FakeElement("div", "itemProgressBar cardProgressBarContainer"));
        dashboardCardBox.appendChild(progress);
        runNextPoll(() => {
            if (footers().length !== 0) failures.push("with Emby's progress bar the LIVE line should go");
            dashboardCardBox.removeChild(progress);
            runNextPoll(() => {
                if (footers().length !== 1) failures.push("without the progress bar the LIVE line should come back");
                dashboardHidden = true;
                runNextPoll(() => {
                    if (footers().length !== 0) failures.push("leaving the Dashboard should remove the LIVE line");
                    if (failures.length) {
                        console.error("FAIL: Dashboard LIVE line: " + failures.join("; ") + ".");
                        process.exitCode = 1;
                        return;
                    }
                    console.log("PASS: the Dashboard card keeps one LIVE line across polls, drops it when Emby draws a progress bar, gets it back, and loses it when Radio Meta stops labelling the card. (Position is not checked here.)");
                });
            });
        });
    });
}
