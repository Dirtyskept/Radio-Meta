/* Radio Meta Dashboard adapter (v1.3.80).
 * This module owns output-specific DOM/media-session behavior.
 */
define("RadioMetaDisplayDashboardAdapter_v1380", [], function() {
    "use strict";
    return function createRadioMetaDisplayDashboardAdapter(context) {
        var state = context.state;
        var parseRadioItem = context.parseRadioItem;

        function normalizeDashboardText(value) { return String(value || "").replace(/\u00a0/g, " ").split(/\r\n|\n|\r/).map(function(part) { return part.trim(); }).filter(Boolean).join(" ").replace(/\s+/g, " ").trim(); }

        function isDashboardRoute() { return /!\/dashboard(?:[/?]|$)/i.test(String(window.location.hash || "")); }

        function ensureDashboardCardStyle() {
            ["rnp-dashboard-song-artist-station-style-v1315", "rnp-dashboard-song-artist-station-style-v1316", "rnp-dashboard-song-artist-station-style-v1317", "rnp-dashboard-song-artist-station-style-v1318", "rnp-dashboard-song-artist-station-style-v1319", "rnp-dashboard-song-artist-station-style-v1320", "rnp-dashboard-song-artist-station-style-v1321"].forEach(function(staleId) {
                var staleStyle = document.getElementById(staleId); if (staleStyle)
                    staleStyle.remove();
            }); var id = "rnp-dashboard-song-artist-station-style-v1323"; var existing = document.getElementById(id); if (existing)
                return; var style = document.createElement("style"); style.id = id; style.textContent = ['.cardText.cardText-first[data-rnp-dashboard-title="song-artist-station"] {', '  white-space: normal !important; text-overflow: clip !important; overflow: visible !important;', '  height: auto !important; min-height: 0 !important; margin-bottom: 0 !important; padding-bottom: 0 !important;', '  line-height: 1.25 !important;', '}', '.cardText.cardText-first[data-rnp-dashboard-title="song-artist-station"] > [data-rnp-dashboard-part] {', '  display: block !important; margin: 0 !important; padding: 0 !important; min-height: 0 !important;', '  box-sizing: border-box !important;', '}', '.cardText.cardText-first[data-rnp-dashboard-title="song-artist-station"] > [data-rnp-dashboard-part="song"] {', '  font-weight: inherit !important; white-space: normal !important;', '}', '.cardText.cardText-first[data-rnp-dashboard-title="song-artist-station"] > [data-rnp-dashboard-part="artist"],', '.cardText.cardText-first[data-rnp-dashboard-title="song-artist-station"] > [data-rnp-dashboard-part="station"] {', '  font-weight: normal !important; white-space: normal !important;', '}'].join("\n"); document.head.appendChild(style);
        }

        function getDashboardParts(element) {
            var song = null, artist = null, station = null; if (!element || !element.children)
                return { song: null, artist: null, station: null }; for (var i = 0; i < element.children.length; i++) {
                    var child = element.children[i];
                    var part = child.getAttribute && child.getAttribute("data-rnp-dashboard-part");
                    if (part === "song")
                        song = child;
                    if (part === "artist")
                        artist = child;
                    if (part === "station")
                        station = child;
                } return { song: song, artist: artist, station: station };
        }

        function restorePendingDashboardVisibility(element) {
            if (!element)
                return; var saved = state.dashboardPendingVisibility.get(element); if (saved) {
                    if (saved.value) {
                        element.style.setProperty("visibility", saved.value, saved.priority || "");
                    }
                    else {
                        element.style.removeProperty("visibility");
                    }
                    state.dashboardPendingVisibility.delete(element);
                } element.removeAttribute("data-rnp-dashboard-pending");
        }

        function hidePendingDashboardTitle(element) {
            if (!element)
                return; if (!state.dashboardPendingVisibility.has(element)) {
                    state.dashboardPendingVisibility.set(element, { value: element.style.getPropertyValue("visibility"), priority: element.style.getPropertyPriority("visibility"), since: Date.now() });
                } element.setAttribute("data-rnp-dashboard-pending", "true"); element.style.setProperty("visibility", "hidden", "important");
        }

        function isLikelyStationTitle(text) {
            var normalized = normalizeDashboardText(text); if (!normalized)
                return false; if (state.dashboardPendingStations.has(normalized))
                return true; return /^.{1,100}?\s[-–]\s*\d{2,4}\s*k(?:bps)?(?:\s|$)/i.test(normalized);
        }

        function forceDashboardPartStyle(element, part) {
            if (!element || !element.style)
                return; element.style.setProperty("display", "block", "important"); if (part === "song") {
                    element.classList.remove("secondaryText");
                    element.style.removeProperty("font-weight");
                }
            else {
                element.classList.add("secondaryText");
                element.style.setProperty("font-weight", "normal", "important");
            } element.style.setProperty("white-space", "normal", "important");
        }

        function getNativeDashboardSecondaryMetrics(titleElement) {
            var parent = titleElement && titleElement.parentElement; if (!parent)
                return null; var rows = Array.prototype.filter.call(parent.children || [], function(row) { return !!(row && row.classList && row.classList.contains("cardText-secondary")); }); if (!rows.length)
                return null; var reference = rows.find(function(row) { return !!normalizeDashboardText(row.textContent || ""); }) || rows[0]; if (!reference)
                return null; var computed = window.getComputedStyle(reference); return { fontSize: computed.fontSize, lineHeight: computed.lineHeight, letterSpacing: computed.letterSpacing };
        }

        function applyNativeDashboardSecondaryMetrics(element, metrics) {
            if (!element || !element.style || !metrics)
                return; if (metrics.fontSize && metrics.fontSize !== "0px") {
                    element.style.setProperty("font-size", metrics.fontSize, "important");
                } if (metrics.lineHeight && metrics.lineHeight !== "normal") {
                    element.style.setProperty("line-height", metrics.lineHeight, "important");
                } if (metrics.letterSpacing) {
                    element.style.setProperty("letter-spacing", metrics.letterSpacing, "important");
                }
        }

        function collapseEmptyDashboardSecondaryRows(titleElement) {
            if (!titleElement || !titleElement.parentElement)
                return; var parent = titleElement.parentElement; Array.prototype.forEach.call(parent.children, function(row) {
                    if (!row || row === titleElement || !row.classList || !row.classList.contains("cardText-secondary"))
                        return; if (normalizeDashboardText(row.textContent || "")) {
                            var populatedSaved = state.dashboardEmptySecondaryOriginals.get(row);
                            if (populatedSaved) {
                                if (populatedSaved.style === null)
                                    row.removeAttribute("style");
                                else
                                    row.setAttribute("style", populatedSaved.style);
                                if (populatedSaved.marker === null)
                                    row.removeAttribute("data-rnp-dashboard-empty-slot");
                                else
                                    row.setAttribute("data-rnp-dashboard-empty-slot", populatedSaved.marker);
                                state.dashboardEmptySecondaryOriginals.delete(row);
                            }
                            return;
                        } if (!state.dashboardEmptySecondaryOriginals.has(row)) {
                            state.dashboardEmptySecondaryOriginals.set(row, { style: row.hasAttribute("style") ? row.getAttribute("style") : null, marker: row.hasAttribute("data-rnp-dashboard-empty-slot") ? row.getAttribute("data-rnp-dashboard-empty-slot") : null });
                        } row.setAttribute("data-rnp-dashboard-empty-slot", "hidden"); row.style.setProperty("display", "none", "important"); row.style.setProperty("height", "0", "important"); row.style.setProperty("min-height", "0", "important"); row.style.setProperty("margin", "0", "important"); row.style.setProperty("padding", "0", "important");
                });
        }

        function restoreDashboardSecondaryRowsForTitle(titleElement) {
            if (!titleElement || !titleElement.parentElement)
                return; var parent = titleElement.parentElement; Array.prototype.forEach.call(parent.children, function(row) {
                    if (!row || !state.dashboardEmptySecondaryOriginals.has(row))
                        return; var saved = state.dashboardEmptySecondaryOriginals.get(row); if (saved.style === null)
                        row.removeAttribute("style");
                    else
                        row.setAttribute("style", saved.style); if (saved.marker === null)
                        row.removeAttribute("data-rnp-dashboard-empty-slot");
                    else
                        row.setAttribute("data-rnp-dashboard-empty-slot", saved.marker); state.dashboardEmptySecondaryOriginals.delete(row);
                });
        }

        function restoreAllDashboardSecondaryRows() {
            state.dashboardEmptySecondaryOriginals.forEach(function(saved, row) {
                if (!row || !row.isConnected)
                    return; if (saved.style === null)
                    row.removeAttribute("style");
                else
                    row.setAttribute("style", saved.style); if (saved.marker === null)
                    row.removeAttribute("data-rnp-dashboard-empty-slot");
                else
                    row.setAttribute("data-rnp-dashboard-empty-slot", saved.marker);
            }); state.dashboardEmptySecondaryOriginals.clear();
        }

        function renderDashboardTitleElement(element, track, trackKey) {
            restorePendingDashboardVisibility(element); var parts = getDashboardParts(element); var stationText = String(track.station || "").trim(); var nativeSecondaryMetrics = getNativeDashboardSecondaryMetrics(element); var alreadyCorrect = parts.song && parts.artist && parts.station && parts.song.textContent === track.song && parts.artist.textContent === track.artist && parts.station.textContent === stationText; element.setAttribute("data-rnp-dashboard-title", "song-artist-station"); element.setAttribute("data-rnp-dashboard-key", trackKey); if (!state.dashboardOriginals.has(element)) {
                state.dashboardOriginals.set(element, { html: element.innerHTML, style: element.getAttribute("style") });
            } state.dashboardElementTracks.set(element, { track: track, key: trackKey }); if (!alreadyCorrect) {
                while (element.firstChild)
                    element.removeChild(element.firstChild);
                function addPart(part, text) {
                    if (!text)
                        return; var node = document.createElement("span"); node.setAttribute("data-rnp-dashboard-part", part); node.textContent = text; forceDashboardPartStyle(node, part); if (part === "artist" || part === "station") {
                            applyNativeDashboardSecondaryMetrics(node, nativeSecondaryMetrics);
                        } element.appendChild(node);
                }
                addPart("song", track.song);
                addPart("artist", track.artist);
                addPart("station", stationText);
            }
            else {
                forceDashboardPartStyle(parts.song, "song");
                forceDashboardPartStyle(parts.artist, "artist");
                forceDashboardPartStyle(parts.station, "station");
                applyNativeDashboardSecondaryMetrics(parts.artist, nativeSecondaryMetrics);
                applyNativeDashboardSecondaryMetrics(parts.station, nativeSecondaryMetrics);
            } element.style.setProperty("white-space", "normal", "important"); element.style.setProperty("text-overflow", "clip", "important"); element.style.setProperty("overflow", "visible", "important"); element.style.removeProperty("font-weight"); collapseEmptyDashboardSecondaryRows(element); return true;
        }

        function isDashboardNowPlayingTitle(element) {
            if (!element || !element.matches || !element.matches(".cardText.cardText-first")) {
                return false;
            } if (element.getAttribute("data-rnp-dashboard-title") === "song-artist-station") {
                return true;
            } var currentText = normalizeDashboardText(element.innerText || element.textContent || ""); if (!currentText)
                return false; return state.dashboardRenderedNames.has(currentText);
        }

        function markDashboardTitleElement(element) {
            if (!isDashboardNowPlayingTitle(element))
                return false; if (!element || !element.isConnected || !element.matches || !element.matches(".cardText.cardText-first"))
                return false; var rawValue = element.innerText || element.textContent || ""; var currentText = normalizeDashboardText(rawValue); if (!currentText)
                return false; var entry = state.dashboardRenderedNames.get(currentText); var track = entry && entry.track; var trackKey = currentText; if (!track && element.getAttribute("data-rnp-dashboard-title") === "song-artist-station") {
                    var previous = state.dashboardElementTracks.get(element);
                    var previousParts = getDashboardParts(element);
                    if (previous && previousParts.song && previousParts.artist && previousParts.station && previousParts.song.textContent === previous.track.song && previousParts.artist.textContent === previous.track.artist && previousParts.station.textContent === String(previous.track.station || "")) {
                        track = previous.track;
                        trackKey = previous.key;
                    }
                } if (track && track.song && track.artist && track.station) {
                    return renderDashboardTitleElement(element, track, trackKey);
                } if (isLikelyStationTitle(currentText)) {
                    var pending = state.dashboardPendingVisibility.get(element);
                    if (!pending || Date.now() - pending.since < 8000) {
                        hidePendingDashboardTitle(element);
                        return true;
                    }
                    restorePendingDashboardVisibility(element);
                    return false;
                } var pendingState = state.dashboardPendingVisibility.get(element); if (pendingState) {
                    if (Date.now() - pendingState.since < 8000) {
                        hidePendingDashboardTitle(element);
                        return true;
                    }
                    restorePendingDashboardVisibility(element);
                } if (element.hasAttribute("data-rnp-dashboard-title")) {
                    restoreDashboardSecondaryRowsForTitle(element);
                    var saved = state.dashboardOriginals.get(element);
                    if (saved) {
                        if (saved.style === null)
                            element.removeAttribute("style");
                        else
                            element.setAttribute("style", saved.style);
                    }
                    element.removeAttribute("data-rnp-dashboard-title");
                    element.removeAttribute("data-rnp-dashboard-key");
                    state.dashboardElementTracks.delete(element);
                    state.dashboardOriginals.delete(element);
                } return false;
        }

        function applyDashboardCardStyles(root) {
            if (!isDashboardRoute())
                return; ensureDashboardCardStyle(); root = root || document.querySelector("#dashboardPage") || document.body; if (!root)
                return; if (root.matches && root.matches(".cardText.cardText-first")) {
                    markDashboardTitleElement(root);
                } var nodes = root.querySelectorAll ? root.querySelectorAll(".cardText.cardText-first") : []; Array.prototype.forEach.call(nodes, markDashboardTitleElement); state.dashboardOriginals.forEach(function(_, element) {
                    if (!element || !element.isConnected) {
                        state.dashboardOriginals.delete(element);
                        state.dashboardElementTracks.delete(element);
                    }
                }); state.dashboardEmptySecondaryOriginals.forEach(function(_, row) {
                    if (!row || !row.isConnected)
                        state.dashboardEmptySecondaryOriginals.delete(row);
                });
        }

        function queueDashboardCardStylePass() {
            if (!isDashboardRoute())
                return; applyDashboardCardStyles(document.querySelector("#dashboardPage") || document.body);
        }

        function ensureDashboardMutationObserver() {
            if (!isDashboardRoute() || typeof window.MutationObserver !== "function")
                return; var root = document.body; if (!root)
                return; if (state.dashboardObserver && state.dashboardObserverRoot === root)
                return; if (state.dashboardObserver) {
                    state.dashboardObserver.disconnect();
                    state.dashboardObserver = null;
                    state.dashboardObserverRoot = null;
                } state.dashboardObserver = new MutationObserver(function() { queueDashboardCardStylePass(); }); state.dashboardObserverRoot = root; state.dashboardObserver.observe(root, { childList: true, subtree: true, characterData: true });
        }

        function restoreDashboardCardTitles() {
            if (state.dashboardObserver) {
                state.dashboardObserver.disconnect();
                state.dashboardObserver = null;
                state.dashboardObserverRoot = null;
            } state.dashboardPendingVisibility.forEach(function(_, element) { restorePendingDashboardVisibility(element); }); state.dashboardPendingVisibility.clear(); restoreAllDashboardSecondaryRows(); state.dashboardPendingStations.clear(); state.dashboardOriginals.forEach(function(saved, element) {
                if (!element || !element.isConnected)
                    return; if (element.getAttribute("data-rnp-dashboard-title") === "song-artist-station") {
                        if (saved && typeof saved.html === "string")
                            element.innerHTML = saved.html;
                        if (saved && saved.style === null)
                            element.removeAttribute("style");
                        else if (saved)
                            element.setAttribute("style", saved.style);
                        element.removeAttribute("data-rnp-dashboard-title");
                        element.removeAttribute("data-rnp-dashboard-key");
                    }
            }); state.dashboardOriginals.clear(); state.dashboardElementTracks.clear(); state.dashboardRenderedNames.clear();
        }

        function updateDashboardNowPlayingCards(sessions) {
            if (!isDashboardRoute()) {
                if (state.dashboardOriginals.size || state.dashboardObserver) {
                    restoreDashboardCardTitles();
                }
                return;
            } var now = Date.now(); state.dashboardPendingStations.clear(); (Array.isArray(sessions) ? sessions : []).forEach(function(session) {
                if (!session || !session.NowPlayingItem)
                    return; var item = session.NowPlayingItem; var track = parseRadioItem(item); var rawKey = normalizeDashboardText(typeof item.Name === "string" ? item.Name : (track && track.station || "")); if (!rawKey && track)
                    rawKey = normalizeDashboardText(track.station || track.song); if (!rawKey)
                    return; if (track && track.song && track.artist) {
                        state.dashboardRenderedNames.set(rawKey, { track: track, seenAt: now, });
                        return;
                    } var paths = []; if (item.Path)
                    paths.push(String(item.Path)); if (Array.isArray(item.MediaSources)) {
                        item.MediaSources.forEach(function(source) {
                            if (source && source.Path)
                                paths.push(String(source.Path));
                        });
                    } var isStrmPath = paths.some(function(path) { return /\.strm(?:$|[?#])/i.test(path); }); var looksLikeBitrateStation = /^.{1,100}?\s[-–]\s*\d{2,4}\s*k(?:bps)?$/i.test(rawKey); if (isStrmPath || looksLikeBitrateStation) {
                        state.dashboardPendingStations.add(rawKey);
                    }
            }); state.dashboardRenderedNames.forEach(function(entry, key) {
                if (!entry || now - entry.seenAt > 60000) {
                    state.dashboardRenderedNames.delete(key);
                }
            }); while (state.dashboardRenderedNames.size > 40) {
                var oldestKey = null;
                var oldestSeen = Infinity;
                state.dashboardRenderedNames.forEach(function(entry, key) {
                    if (entry.seenAt < oldestSeen) {
                        oldestSeen = entry.seenAt;
                        oldestKey = key;
                    }
                });
                if (oldestKey === null)
                    break;
                state.dashboardRenderedNames.delete(oldestKey);
            } if (!state.dashboardRenderedNames.size && !state.dashboardPendingStations.size && !state.dashboardPendingVisibility.size) {
                return;
            } ensureDashboardMutationObserver(); applyDashboardCardStyles(document.querySelector("#dashboardPage") || document.body);
        }

        return {
            normalizeDashboardText: normalizeDashboardText,
            isDashboardRoute: isDashboardRoute,
            ensureDashboardCardStyle: ensureDashboardCardStyle,
            getDashboardParts: getDashboardParts,
            restorePendingDashboardVisibility: restorePendingDashboardVisibility,
            hidePendingDashboardTitle: hidePendingDashboardTitle,
            isLikelyStationTitle: isLikelyStationTitle,
            forceDashboardPartStyle: forceDashboardPartStyle,
            getNativeDashboardSecondaryMetrics: getNativeDashboardSecondaryMetrics,
            applyNativeDashboardSecondaryMetrics: applyNativeDashboardSecondaryMetrics,
            collapseEmptyDashboardSecondaryRows: collapseEmptyDashboardSecondaryRows,
            restoreDashboardSecondaryRowsForTitle: restoreDashboardSecondaryRowsForTitle,
            restoreAllDashboardSecondaryRows: restoreAllDashboardSecondaryRows,
            renderDashboardTitleElement: renderDashboardTitleElement,
            isDashboardNowPlayingTitle: isDashboardNowPlayingTitle,
            markDashboardTitleElement: markDashboardTitleElement,
            applyDashboardCardStyles: applyDashboardCardStyles,
            queueDashboardCardStylePass: queueDashboardCardStylePass,
            ensureDashboardMutationObserver: ensureDashboardMutationObserver,
            restoreDashboardCardTitles: restoreDashboardCardTitles,
            updateDashboardNowPlayingCards: updateDashboardNowPlayingCards
        };
    };
});
