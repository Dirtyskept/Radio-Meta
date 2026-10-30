/* Radio Meta Player adapter (v1.3.107).
 * This module owns output-specific DOM/media-session behavior.
 */
define("RadioMetaDisplayPlayerAdapter_v1380", [], function() {
    "use strict";
    return function createRadioMetaDisplayPlayerAdapter(context) {
        var state = context.state;
        var cache = context.cache;
        var isVisible = context.isVisible;
        var requestImmediateTick = context.requestImmediateTick;

        function rememberOriginal(element) {
            if (state.originals.has(element)) {
                return;
            } state.originals.set(element, { html: element.innerHTML, style: element.getAttribute("style"), title: element.getAttribute("title") });
        }

        function updateLargePlayerTitle(track) {
            // Station transitions can precede fresh song/artist metadata. Override only the
            // big player's station line; do not mutate the shared cache or Media Session track.
            var stationOverride = state.largePlayerStationOverride;
            if (stationOverride && stationOverride.station) {
                track = track
                    ? Object.assign({}, track, { station: stationOverride.station })
                    : { song: stationOverride.station, artist: "", station: "" };
            }
            var selectors = ["h3.videoOsdParentTitle.videoOsdParentTitle-small", "h3.videoOsdParentTitle", "h1.videoOsdParentTitleLarge", "h1.videoOsdParentTitle", "#videoOsdParentTitleLarge"]; var station = String(track.station || "").trim(); var marker = track.song + track.artist + station; for (var i = 0; i < selectors.length; i++) {
                var candidates = Array.prototype.slice.call(document.querySelectorAll(selectors[i])).filter(isVisible);
                if (!candidates.length)
                    continue;
                var element = candidates[0];
                if (element.getAttribute("data-rnp-player-title") === marker && (element.textContent || "").trim() === marker)
                    return;
                rememberOriginal(element);
                while (element.firstChild)
                    element.removeChild(element.firstChild);
                function addLine(text, secondary) {
                    if (!text)
                        return; var line = document.createElement("span"); line.textContent = text; line.style.display = "block"; if (secondary) {
                            line.classList.add("secondaryText");
                            line.style.fontWeight = "normal";
                        } line.style.whiteSpace = "nowrap"; line.style.overflow = "hidden"; line.style.textOverflow = "ellipsis"; line.style.maxWidth = "100%"; element.appendChild(line);
                }
                addLine(track.song, false);
                addLine(track.artist, true);
                addLine(station, true);
                element.setAttribute("data-rnp-player-title", marker);
                element.title = [track.song, track.artist, station].filter(Boolean).join(" — ");
                element.style.whiteSpace = "normal";
                element.style.textOverflow = "clip";
                element.style.overflow = "visible";
                element.style.maxHeight = "none";
                element.style.height = "auto";
                return;
            }
        }

        function getVisibleMiniPlayerText(track) {
            var primary = Array.prototype.slice.call(document.querySelectorAll(".nowPlayingBarText")).filter(isVisible); if (primary.length)
                return primary[0]; var fallback = Array.prototype.slice.call(document.querySelectorAll(".nowplayingbar-textitems")).filter(isVisible); if (!fallback.length)
                return null; if (track) {
                    var marker = track.song + track.artist;
                    var station = normalizeMiniText(track.station);
                    var matched = fallback.find(function(element) { return element.getAttribute("data-rnp-player-title") === marker || normalizeMiniText(element.textContent) === station; });
                    if (matched)
                        return matched;
                } return fallback.length === 1 ? fallback[0] : null;
        }

        function updateMiniPlayerTitle(track) {
            var target = getVisibleMiniPlayerText(track); if (!target)
                return; var fallbackTarget = !!(target.matches && target.matches(".nowplayingbar-textitems") && !(target.closest && target.closest(".nowPlayingBarText"))); var marker = track.song + track.artist; if (normalizeMiniText(target.textContent) === normalizeMiniText(track.song + track.artist) && target.getAttribute("data-rnp-player-title") === marker)
                return; rememberOriginal(target); while (target.firstChild)
                target.removeChild(target.firstChild); function addLine(text, secondary) {
                    if (!text)
                        return; var row = document.createElement("div"); row.className = fallbackTarget ? "textItems" + (secondary ? " secondaryText" : "") : "textItems nowplayingbar-textitems" + (secondary ? " secondaryText" : ""); row.textContent = text; row.style.display = "block"; row.style.width = "100%"; row.style.maxWidth = "100%"; row.style.whiteSpace = "nowrap"; row.style.overflow = "hidden"; row.style.textOverflow = "ellipsis"; target.appendChild(row);
                } addLine(track.song, false); addLine(track.artist, true); target.setAttribute("data-rnp-player-title", marker); target.title = track.song + " — " + track.artist; target.style.whiteSpace = "normal"; target.style.textOverflow = "clip"; target.style.overflow = "hidden"; target.style.height = "auto"; target.style.maxHeight = fallbackTarget ? "none" : "100%"; target.style.minWidth = "0"; if (fallbackTarget) {
                    target.style.display = "block";
                    target.style.width = "100%";
                    target.style.maxWidth = "100%";
                }
        }

        function normalizeMiniText(value) { return String(value || "").replace(/\u00a0/g, " ").replace(/\s+/g, " ").trim(); }

        function getNativeStationTitleIfDifferent(track) {
            if (!track) return "";
            var oldMiniMarker = normalizeMiniText(String(track.song || "") + String(track.artist || ""));
            var oldMiniText = normalizeMiniText(String(track.song || "") + " " + String(track.artist || ""));
            var oldLargeMarker = normalizeMiniText(String(track.song || "") + String(track.artist || "") + String(track.station || ""));
            var oldLargeText = normalizeMiniText([track.song, track.artist, track.station].filter(Boolean).join(" "));
            var oldSong = normalizeMiniText(track.song);
            var oldArtist = normalizeMiniText(track.artist);
            var oldStation = normalizeMiniText(track.station);
            var candidates = [getVisibleMiniPlayerText(null), getVisibleLargePlayerTitle()];
            for (var i = 0; i < candidates.length; i++) {
                var element = candidates[i];
                if (!element) continue;
                var text = normalizeMiniText(element.textContent || "");
                if (!text || text.length > 120) continue;
                var marker = normalizeMiniText(element.getAttribute("data-rnp-player-title") || "");
                // Ignore our currently injected song/artist/station layout. A different native
                // title rendered by Emby during a session gap is likely the newly selected station.
                if (text === marker || text === oldMiniMarker || text === oldMiniText || text === oldLargeMarker || text === oldLargeText || text === oldSong || text === oldArtist || text === oldStation) continue;
                if (/^(loading|connecting|stopped|paused|now playing|playing|radio)$/i.test(text)) continue;
                return text;
            }
            return "";
        }

        function escapeHtmlText(value) {
            return String(value == null ? "" : value)
                .replace(/&/g, "&amp;")
                .replace(/</g, "&lt;")
                .replace(/>/g, "&gt;")
                .replace(/\"/g, "&quot;")
                .replace(/'/g, "&#39;");
        }

        function setStationRestoreBaseline(element, station) {
            if (!element || !station) return;
            if (!state.originals.has(element)) rememberOriginal(element);
            var saved = state.originals.get(element);
            if (saved) {
                // The selected .strm identity is now the correct native fallback. Do not
                // restore HTML/title captured from the previous station after this transition.
                saved.html = escapeHtmlText(station);
                saved.title = station;
            }
        }

        function showStationTransitionFallback(station) {
            station = String(station || "").trim();
            if (!station) return false;
            var shown = false;
            var mini = getVisibleMiniPlayerText(null);
            if (mini) {
                var miniText = normalizeMiniText(mini.textContent);
                var miniMarker = String(mini.getAttribute("data-rnp-player-title") || "");
                setStationRestoreBaseline(mini, station);
                if (miniText !== normalizeMiniText(station) || miniMarker !== station) {
                    var fallbackTarget = !!(mini.matches && mini.matches(".nowplayingbar-textitems") && !(mini.closest && mini.closest(".nowPlayingBarText")));
                    while (mini.firstChild) mini.removeChild(mini.firstChild);
                    var row = document.createElement("div");
                    row.className = fallbackTarget ? "textItems" : "textItems nowplayingbar-textitems";
                    row.textContent = station;
                    row.style.display = "block";
                    row.style.width = "100%";
                    row.style.maxWidth = "100%";
                    row.style.whiteSpace = "nowrap";
                    row.style.overflow = "hidden";
                    row.style.textOverflow = "ellipsis";
                    mini.appendChild(row);
                    mini.setAttribute("data-rnp-player-title", station);
                    mini.title = station;
                    mini.style.whiteSpace = "normal";
                    mini.style.textOverflow = "clip";
                    mini.style.overflow = "hidden";
                    mini.style.height = "auto";
                    mini.style.maxHeight = fallbackTarget ? "none" : "100%";
                    mini.style.minWidth = "0";
                    if (fallbackTarget) {
                        mini.style.display = "block";
                        mini.style.width = "100%";
                        mini.style.maxWidth = "100%";
                    }
                }
                shown = true;
            }

            // The coordinator sets largePlayerStationOverride to the selected .strm title.
            // updateLargePlayerTitle(null) renders that identity without a stale song/artist.
            var large = getVisibleLargePlayerTitle();
            if (large && state.largePlayerStationOverride && state.largePlayerStationOverride.station) {
                updateLargePlayerTitle(null);
                setStationRestoreBaseline(large, station);
                shown = true;
            }
            return shown;
        }

        function tryApplyCachedTrackToMiniPlayer() {
            if (state.stationTransition) return false;
            var track = cache.getTrack(); if (!track || !cache.isFresh(Date.now(), 15000))
                return false; var target = getVisibleMiniPlayerText(track); if (!target)
                return false; var currentText = normalizeMiniText(target.textContent); var stationText = normalizeMiniText(track.station); var marker = track.song + track.artist; if (currentText === stationText) {
                    updateMiniPlayerTitle(track);
                    return true;
                } if (target.getAttribute("data-rnp-player-title") === marker)
                return true; return false;
        }

        function getVisibleLargePlayerTitle() {
            var selectors = ["h3.videoOsdParentTitle.videoOsdParentTitle-small", "h3.videoOsdParentTitle", "h1.videoOsdParentTitleLarge", "h1.videoOsdParentTitle", "#videoOsdParentTitleLarge"]; for (var i = 0; i < selectors.length; i++) {
                var candidates = Array.prototype.slice.call(document.querySelectorAll(selectors[i])).filter(isVisible);
                if (candidates.length)
                    return candidates[0];
            } return null;
        }

        function tryApplyCachedTrackToLargePlayer() {
            if (state.stationTransition || state.largePlayerStationOverride) return false;
            var track = cache.getTrack(); if (!track || !cache.isFresh(Date.now(), 15000))
                return false; if (!getVisibleLargePlayerTitle())
                return false; updateLargePlayerTitle(track); return true;
        }

        function nodeTouchesLargePlayer(node) {
            if (!node)
                return false; if (node.nodeType === 3)
                node = node.parentElement; if (!node || node.nodeType !== 1)
                return false; var selector = "h3.videoOsdParentTitle, h1.videoOsdParentTitleLarge, h1.videoOsdParentTitle, #videoOsdParentTitleLarge"; if (node.matches && node.matches(selector))
                return true; if (node.closest && node.closest(selector))
                return true; return !!(node.querySelector && node.querySelector(selector));
        }

        function nodeTouchesMiniPlayer(node) {
            if (!node)
                return false; if (node.nodeType === 3)
                node = node.parentElement; if (!node || node.nodeType !== 1)
                return false; var selector = ".nowPlayingBarText, .nowplayingbar-textitems"; if (node.matches && node.matches(selector))
                return true; if (node.closest && node.closest(selector))
                return true; return !!(node.querySelector && node.querySelector(selector));
        }

        function closestInjectedTitle(node) {
            if (node && node.nodeType === 3)
                node = node.parentElement;
            if (!node || node.nodeType !== 1)
                return null;
            if (node.closest)
                return node.closest("[data-rnp-player-title]");
            for (var current = node; current && current.nodeType === 1; current = current.parentElement) {
                if (current.hasAttribute && current.hasAttribute("data-rnp-player-title"))
                    return current;
            }
            return null;
        }

        function isCurrentHelperTitleMutation(node) {
            var titleElement = closestInjectedTitle(node);
            if (!titleElement)
                return false;
            var expected = titleElement.getAttribute("data-rnp-player-title");
            return !!expected && String(titleElement.textContent || "").trim() === expected;
        }

        function isElementOrDescendantOf(node, selector) {
            if (node && node.nodeType === 3)
                node = node.parentElement;
            if (!node || node.nodeType !== 1)
                return false;
            return !!((node.matches && node.matches(selector)) || (node.closest && node.closest(selector)));
        }

        function nodeContainsPlayerTitle(node) {
            if (isElementOrDescendantOf(node, "h3.videoOsdParentTitle, h1.videoOsdParentTitleLarge, h1.videoOsdParentTitle, #videoOsdParentTitleLarge, .nowPlayingBarText, .nowplayingbar-textitems"))
                return true;
            if (!node || node.nodeType !== 1 || !node.querySelector)
                return false;
            return !!node.querySelector("h3.videoOsdParentTitle, h1.videoOsdParentTitleLarge, h1.videoOsdParentTitle, #videoOsdParentTitleLarge, .nowPlayingBarText, .nowplayingbar-textitems");
        }

        function nodeTouchesPlayerArtwork(node) {
            if (node && node.nodeType === 3)
                node = node.parentElement;
            if (!node || node.nodeType !== 1)
                return false;
            var artContainer = ".nowPlayingBarImage, .nowPlayingBarImageContainer, .nowplayingbar-image, .videoOsdParentImage, #videoOsdParentImage";
            var playerRoot = "#nowPlayingBar, .nowPlayingBar, .nowplayingbar, .videoOsd, .videoOsdParent, #videoOsd, #videoOsdPage";
            var isImage = String(node.tagName || "").toLowerCase() === "img";
            if (node.matches && node.matches(artContainer))
                return true;
            if (node.closest && node.closest(artContainer))
                return true;
            if (isImage && node.closest && node.closest(playerRoot))
                return true;
            if (node.querySelector && node.querySelector("img") && ((node.matches && node.matches(playerRoot)) || (node.closest && node.closest(playerRoot))))
                return true;
            return false;
        }

        function isArtworkChange(record) {
            if (!record)
                return false;
            if (record.type === "attributes") {
                var attributeName = String(record.attributeName || "").toLowerCase();
                if (["src", "data-src", "alt"].indexOf(attributeName) >= 0) {
                    var target = record.target;
                    return !!(target && (String(target.tagName || "").toLowerCase() === "img" || (target.matches && target.matches(".nowPlayingBarImage, .nowPlayingBarImageContainer, .nowplayingbar-image, .videoOsdParentImage, #videoOsdParentImage"))) && nodeTouchesPlayerArtwork(target));
                }
                if (attributeName === "style" && record.target && record.target.matches && record.target.matches(".nowPlayingBarImage, .nowPlayingBarImageContainer, .nowplayingbar-image, .videoOsdParentImage, #videoOsdParentImage"))
                    return true;
                return false;
            }
            if (record.type === "childList") {
                var changedNodes = Array.prototype.slice.call(record.addedNodes || []).concat(Array.prototype.slice.call(record.removedNodes || []));
                if (record.target && record.target.matches && record.target.matches(".nowPlayingBarImage, .nowPlayingBarImageContainer, .nowplayingbar-image, .videoOsdParentImage, #videoOsdParentImage"))
                    return true;
                return changedNodes.some(function(node) {
                    if (!node || node.nodeType !== 1)
                        return false;
                    var isImage = String(node.tagName || "").toLowerCase() === "img";
                    var isArtContainer = !!(node.matches && node.matches(".nowPlayingBarImage, .nowPlayingBarImageContainer, .nowplayingbar-image, .videoOsdParentImage, #videoOsdParentImage"));
                    return (isImage || isArtContainer) && nodeTouchesPlayerArtwork(node);
                });
            }
            return false;
        }

        function ensureMiniPlayerMutationObserver() {
            if (typeof window.MutationObserver !== "function" || !document.body)
                return; var root = document.body; if (state.miniObserver && state.miniObserverRoot === root)
                return; if (state.miniObserver) {
                    state.miniObserver.disconnect();
                    state.miniObserver = null;
                    state.miniObserverRoot = null;
                } state.miniObserver = new MutationObserver(function(records) {
                    var relevant = false;
                    var nativePlayerChange = false;
                    var artworkChanged = false;
                    records.forEach(function(record) {
                        var changedNodes = Array.prototype.slice.call(record.addedNodes || []).concat(Array.prototype.slice.call(record.removedNodes || []));
                        var titleTouched = isElementOrDescendantOf(record.target, "h3.videoOsdParentTitle, h1.videoOsdParentTitleLarge, h1.videoOsdParentTitle, #videoOsdParentTitleLarge, .nowPlayingBarText, .nowplayingbar-textitems") || changedNodes.some(nodeContainsPlayerTitle);
                        var artworkTouched = isArtworkChange(record);
                        if (titleTouched || artworkTouched) relevant = true;
                        if (artworkTouched) {
                            nativePlayerChange = true;
                            artworkChanged = true;
                        }
                        if (titleTouched && !isCurrentHelperTitleMutation(record.target) && (changedNodes.length === 0 || !changedNodes.every(isCurrentHelperTitleMutation))) {
                            // If Emby replaces our injected title text, its item/session has likely
                            // changed. A fresh session query should run now, not on the next poll.
                            nativePlayerChange = true;
                        }
                    });
                    if (!relevant)
                        return;
                    if (nativePlayerChange && typeof requestImmediateTick === "function") {
                        requestImmediateTick(artworkChanged ? "player-artwork-change" : "player-title-change");
                        return;
                    }
                    tryApplyCachedTrackToMiniPlayer();
                    tryApplyCachedTrackToLargePlayer();
                }); state.miniObserverRoot = root; state.miniObserver.observe(root, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["class", "style", "hidden", "src", "data-src", "alt", "title"] }); tryApplyCachedTrackToMiniPlayer(); tryApplyCachedTrackToLargePlayer();
        }

        function isLargePlayerTitleElement(element) {
            return !!(element && element.matches && element.matches("h3.videoOsdParentTitle, h1.videoOsdParentTitleLarge, h1.videoOsdParentTitle, #videoOsdParentTitleLarge"));
        }

        function restoreTitles(keepLargePlayer) {
            var restored = [];
            state.originals.forEach(function(saved, element) {
                if (keepLargePlayer && isLargePlayerTitleElement(element))
                    return;
                if (!element || !element.isConnected || !element.hasAttribute("data-rnp-player-title")) {
                    restored.push(element);
                    return;
                }
                var injected = element.getAttribute("data-rnp-player-title");
                if ((element.textContent || "").trim() === injected)
                    element.innerHTML = saved.html;
                if (saved.style === null)
                    element.removeAttribute("style");
                else
                    element.setAttribute("style", saved.style);
                if (saved.title === null)
                    element.removeAttribute("title");
                else
                    element.setAttribute("title", saved.title);
                element.removeAttribute("data-rnp-player-title");
                restored.push(element);
            });
            restored.forEach(function(element) { state.originals.delete(element); });
            if (!keepLargePlayer)
                state.originals.clear();
        }

        return {
            rememberOriginal: rememberOriginal,
            updateLargePlayerTitle: updateLargePlayerTitle,
            getVisibleMiniPlayerText: getVisibleMiniPlayerText,
            updateMiniPlayerTitle: updateMiniPlayerTitle,
            normalizeMiniText: normalizeMiniText,
            getNativeStationTitleIfDifferent: getNativeStationTitleIfDifferent,
            showStationTransitionFallback: showStationTransitionFallback,
            tryApplyCachedTrackToMiniPlayer: tryApplyCachedTrackToMiniPlayer,
            getVisibleLargePlayerTitle: getVisibleLargePlayerTitle,
            tryApplyCachedTrackToLargePlayer: tryApplyCachedTrackToLargePlayer,
            nodeTouchesLargePlayer: nodeTouchesLargePlayer,
            nodeTouchesMiniPlayer: nodeTouchesMiniPlayer,
            ensureMiniPlayerMutationObserver: ensureMiniPlayerMutationObserver,
            restoreTitles: restoreTitles
        };
    };
});
