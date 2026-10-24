define([], function () {
    'use strict';

    function ensureIntegratedDisplayHelperForDashboard() {
        var helperKey = '__radioMetaIntegratedDisplay_v1323';
        var loaderStateKey = '__radioMetaDisplayLoaderState_v1366';
        var pendingKey = '__radioMetaDashboardHelperImportPending';
        if (window[helperKey] === true) {
            window[loaderStateKey] = 'loaded';
            try { delete window.__radioMetaDisplayLoaderError; } catch (ignore) {}
            return;
        }
        if (window[pendingKey]) return;

        var pathname = String(window.location && window.location.pathname || '/');
        var lowerPath = pathname.toLowerCase();
        var webPosition = lowerPath.lastIndexOf('/web/');
        var basePath = webPosition >= 0 ? pathname.slice(0, webPosition) : '';
        if (webPosition < 0 && /\/web$/i.test(pathname)) basePath = pathname.slice(0, -4);
        var helperPath = basePath + '/web/ConfigurationPage?name=RadioMetaIntegratedDisplay.js';
        var amdRequire = window.require;
        var emby = window.Emby;

        if (!(typeof amdRequire === 'function' && typeof window.define === 'function' && window.define.amd) &&
            !(emby && typeof emby.importModule === 'function')) {
            return; // The optional site-wide WebLoader can retry when Emby finishes startup.
        }

        window[pendingKey] = true;
        window[loaderStateKey] = 'loading';
        var finished = false;
        function finish(error) {
            if (finished) return;
            finished = true;
            window[pendingKey] = false;
            if (window[helperKey] === true) {
                window[loaderStateKey] = 'loaded';
                try { delete window.__radioMetaDisplayLoaderError; } catch (ignore) {}
                try { delete window.__radioMetaDashboardHelperLoadError; } catch (ignore) {}
                return;
            }

            var message = error && error.message ? error.message : (error || 'Module request completed without registering the helper.');
            message = String(message).replace(/([?&](?:X-Emby-Token|api_key|token)=)[^&\s]+/gi, '$1[REDACTED]');
            window[loaderStateKey] = 'failed';
            window.__radioMetaDisplayLoaderError = message.slice(0, 300);
            window.__radioMetaDashboardHelperLoadError = window.__radioMetaDisplayLoaderError;
        }

        try {
            var result;
            if (typeof amdRequire === 'function' && typeof window.define === 'function' && window.define.amd) {
                // Same AMD module route verified manually in Emby 4.10.2.0.
                result = amdRequire([helperPath], function () { finish(); });
            } else {
                result = emby.importModule('ConfigurationPage?name=RadioMetaIntegratedDisplay.js');
            }
            if (result && typeof result.then === 'function') {
                Promise.resolve(result).then(function () { finish(); }, function (error) { finish(error); });
            } else if (!(typeof amdRequire === 'function' && typeof window.define === 'function' && window.define.amd)) {
                finish();
            }
        } catch (error) {
            finish(error);
        }
    }

    return function (view) {
        ensureIntegratedDisplayHelperForDashboard();
        var refreshTimer = null;
        var requestInProgress = false;
        var historyReloadPending = false;
        var historyActionInProgress = false;
        var hasLoadedOnce = false;
        var historyPanelVisible = false;
        var historyItems = [];
        var activeHistoryEditId = '';
        var ignoreRules = [];
        var ignoreRequestInProgress = false;
        var ignoreSaveInProgress = false;
        var stationModeRules = [];
        var stationModeRequestInProgress = false;
        var stationModeSaveInProgress = false;
        var pathSettingsRequestInProgress = false;
        var pathSettingsSaveInProgress = false;
        var titleCleanupRules = [];
        var titleCleanupRequestInProgress = false;
        var titleCleanupSaveInProgress = false;
        var titleCorrections = [];
        var titleCorrectionsRequestInProgress = false;
        var titleCorrectionsSaveInProgress = false;
        var titleCorrectionsLoaded = false;
        var titleCleanupRulesLoaded = false;

        function getValue(object, pascalName, camelName) {
            if (!object) {
                return '';
            }

            return object[pascalName] !== undefined
                ? object[pascalName]
                : (object[camelName] !== undefined
                    ? object[camelName]
                    : '');
        }

        function addText(parent, className, value, tagName) {
            var element = document.createElement(tagName || 'div');
            element.className = className;
            element.textContent = value || '';
            parent.appendChild(element);
        }

        function formatPlayedAt(value) {
            if (!value) {
                return '';
            }

            var date = new Date(value);
            if (isNaN(date.getTime())) {
                return String(value);
            }

            return date.toLocaleString();
        }

        function showError(status, error) {
            var message = error && error.message
                ? error.message
                : String(error || 'Unknown error');

            status.textContent = 'Unable to load play history: ' + message;
            console.error('Radio Meta history:', error);
        }

        function makeTrashIcon(button) {
            var ns = 'http://www.w3.org/2000/svg';
            var svg = document.createElementNS(ns, 'svg');
            svg.setAttribute('viewBox', '0 0 24 24');
            svg.setAttribute('aria-hidden', 'true');
            svg.setAttribute('focusable', 'false');
            svg.setAttribute('fill', 'none');
            svg.setAttribute('stroke', 'currentColor');
            svg.setAttribute('stroke-width', '2');
            svg.setAttribute('stroke-linecap', 'round');
            svg.setAttribute('stroke-linejoin', 'round');

            var paths = [
                'M3 6h18',
                'M8 6V4h8v2',
                'M19 6l-1 14H6L5 6',
                'M10 10v6',
                'M14 10v6'
            ];
            paths.forEach(function (pathData) {
                var path = document.createElementNS(ns, 'path');
                path.setAttribute('d', pathData);
                svg.appendChild(path);
            });
            button.appendChild(svg);
        }

        function makePencilIcon(button) {
            var ns = 'http://www.w3.org/2000/svg';
            var svg = document.createElementNS(ns, 'svg');
            svg.setAttribute('viewBox', '0 0 24 24');
            svg.setAttribute('aria-hidden', 'true');
            svg.setAttribute('focusable', 'false');
            svg.setAttribute('fill', 'none');
            svg.setAttribute('stroke', 'currentColor');
            svg.setAttribute('stroke-width', '2');
            svg.setAttribute('stroke-linecap', 'round');
            svg.setAttribute('stroke-linejoin', 'round');
            ['M12 20h9', 'M16.5 3.5a2.12 2.12 0 0 1 3 3L8 18l-4 1 1-4Z'].forEach(function (pathData) {
                var path = document.createElementNS(ns, 'path');
                path.setAttribute('d', pathData);
                svg.appendChild(path);
            });
            button.appendChild(svg);
        }


        var stationScanInProgress = false;
        var stationFolderPanelVisible = false;

        // Uses the same native Emby operation as Dashboard > Scan library files
        // for one library root, with Recursive=true. The server resolves the
        // appropriate library ID from the configured Station Folder; no IDs or
        // credentials are hard-coded here.
        function requestTargetedLibraryRefresh(operationData) {
            var targetFound = getValue(operationData, 'LibraryScanTargetFound', 'libraryScanTargetFound') === true;
            var itemId = String(getValue(operationData, 'LibraryScanItemId', 'libraryScanItemId') || '').trim();
            var libraryName = String(getValue(operationData, 'LibraryScanLibraryName', 'libraryScanLibraryName') || '').trim();
            var libraryLocation = String(getValue(operationData, 'LibraryScanLocation', 'libraryScanLocation') || '').trim();
            var resolutionMessage = String(getValue(operationData, 'LibraryScanMessage', 'libraryScanMessage') || '').trim();

            if (!targetFound || !itemId) {
                return Promise.resolve({
                    queued: false,
                    message: (resolutionMessage ? resolutionMessage + ' ' : '') +
                        'Emby did not receive a library scan request. Use the Emby Scan library files command after confirming the Station Folder belongs to a library.'
                });
            }

            var url = ApiClient.getUrl('Items/' + encodeURIComponent(itemId) + '/Refresh');
            var refreshOptions = 'Recursive=true&ImageRefreshMode=Default&MetadataRefreshMode=Default&ReplaceAllImages=false&ReplaceAllMetadata=false';
            url += (url.indexOf('?') >= 0 ? '&' : '?') + refreshOptions;

            return ApiClient.ajax({ type: 'POST', url: url }).then(function () {
                return {
                    queued: true,
                    message: 'Emby accepted a recursive library refresh for "' + (libraryName || 'the matching library') + '"' +
                        (libraryLocation ? ' (' + libraryLocation + ')' : '') + '. The scan runs in the background.'
                };
            }).catch(function (error) {
                // Do not display or log the URL or arbitrary error text: Emby
                // clients may include authentication query parameters in URLs.
                var detail = error && (error.statusText || error.status)
                    ? String(error.statusText || error.status)
                    : 'the request failed';
                return {
                    queued: false,
                    message: 'The station file operation succeeded, but Emby did not accept the targeted library refresh (' + detail + '). Use the Emby Scan library files command if the change does not appear.'
                };
            });
        }

        function scanStationFolder(completionNote) {
            function withCompletionNote(message) {
                return completionNote ? message + ' ' + completionNote : message;
            }
            var button = view.querySelector('#radioStationScanButton');
            var status = view.querySelector('#radioStationScanStatus');
            var container = view.querySelector('#radioStationScanResults');
            if (stationScanInProgress || !button || !status || !container) return;

            stationScanInProgress = true;
            status.textContent = withCompletionNote('Scanning configured Station Folder...');
            container.replaceChildren();

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/Stations/Scan'),
                data: JSON.stringify({ IncludeSubfolders: false }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (data) {
                // Some fetch-backed clients resolve to a Fetch Response rather
                // than the decoded body. Read that body before inspecting fields.
                if (data && typeof data.json === 'function') {
                    return data.json();
                }
                return data;
            }).then(function (data) {
                // Emby's fetch helper can expose JSON as a DTO, a JSON string,
                // or an XHR-like object. Normalize those forms before reading
                // the scan response, then walk known envelopes (not just one
                // level) until the actual scan DTO is found.
                function parseJson(value) {
                    if (typeof value !== 'string') return value;
                    try { return JSON.parse(value); } catch (ignore) { return value; }
                }
                var payload = parseJson(data);
                if (payload && typeof payload === 'object') {
                    if (payload.responseJSON !== undefined) payload = parseJson(payload.responseJSON);
                    else if (payload.responseText !== undefined) payload = parseJson(payload.responseText);
                    else if (payload.body !== undefined && typeof payload.body === 'string') payload = parseJson(payload.body);
                }
                var envelopeNames = ['Response', 'response', 'Result', 'result', 'd', 'Data', 'data', 'Body', 'body'];
                function isScanDto(value) {
                    return !!value && typeof value === 'object' &&
                        (value.Stations !== undefined || value.stations !== undefined ||
                         value.ScannedFiles !== undefined || value.scannedFiles !== undefined ||
                         value.ConfiguredFolder !== undefined || value.configuredFolder !== undefined ||
                         value.Folder !== undefined || value.folder !== undefined ||
                         value.Error !== undefined || value.error !== undefined);
                }
                // Do not stop at the first object-valued envelope: some Emby
                // client wrappers expose an empty `data` object alongside the
                // actual DTO under `Response`/`responseJSON`. Prefer a candidate
                // that is itself the scan DTO; otherwise continue only through
                // a wrapper that contains another recognized envelope.
                for (var depth = 0; depth < 6 && !isScanDto(payload); depth++) {
                    var next = null;
                    for (var i = 0; payload && typeof payload === 'object' && i < envelopeNames.length; i++) {
                        var candidate = parseJson(payload[envelopeNames[i]]);
                        if (!candidate || typeof candidate !== 'object') continue;
                        if (isScanDto(candidate)) {
                            next = candidate;
                            break;
                        }
                        var hasNestedEnvelope = envelopeNames.some(function (name) {
                            var nested = candidate[name];
                            return nested !== undefined && nested !== null &&
                                (typeof nested === 'object' || typeof nested === 'string');
                        });
                        if (!next && hasNestedEnvelope) next = candidate;
                    }
                    if (!next) break;
                    payload = next;
                }
                if (!isScanDto(payload)) {
                    var shape = payload && typeof payload === 'object' ? Object.keys(payload).join(', ') : typeof payload;
                    var detail = payload && typeof payload === 'object' && payload.constructor ? payload.constructor.name : typeof payload;
                    status.textContent = withCompletionNote('Station scan returned an unexpected response format (' + shape + '; ' + detail + '). The server scan itself may have succeeded; no station files were changed.');
                    console.error('Radio Meta station scan response format:', { original: data, normalized: payload });
                    return;
                }
                var error = getValue(payload, 'Error', 'error');
                if (error) {
                    var configuredErrorPath = getValue(payload, 'ConfiguredFolder', 'configuredFolder');
                    var resolvedErrorPath = getValue(payload, 'Folder', 'folder');
                    status.textContent = withCompletionNote(error +
                        (configuredErrorPath ? ' Configured path: ' + configuredErrorPath + '.' : '') +
                        (resolvedErrorPath ? ' Resolved path: ' + resolvedErrorPath + '.' : ''));
                    return;
                }
                var stations = getValue(payload, 'Stations', 'stations') || [];
                var scannedFiles = Number(getValue(payload, 'ScannedFiles', 'scannedFiles')) || 0;
                var folder = getValue(payload, 'Folder', 'folder') || '(server returned no resolved folder)';
                var configuredFolder = getValue(payload, 'ConfiguredFolder', 'configuredFolder') || '';
                var directoriesExamined = Number(getValue(payload, 'DirectoriesExamined', 'directoriesExamined')) || 0;
                var directoryErrors = Number(getValue(payload, 'DirectoryErrors', 'directoryErrors')) || 0;
                if (!stations.length) {
                    status.textContent = withCompletionNote('Scan returned zero stations. Configured path: ' + (configuredFolder || '(not returned)') + '; resolved path: ' + folder + '; directories examined: ' + directoriesExamined + '; .strm files found: ' + scannedFiles + '; directory errors: ' + directoryErrors + '. If the paths are missing, refresh the plugin page and verify the installed DLL and embedded resources match.');
                    return;
                }

                stations.forEach(function (station) {
                    var row = document.createElement('div');
                    row.className = 'radio-station-scan-row';
                    var artworkDataUri = getValue(station, 'ArtworkDataUri', 'artworkDataUri') || '';
                    if (artworkDataUri) {
                        var logo = document.createElement('img');
                        logo.className = 'radio-station-scan-logo';
                        logo.alt = '';
                        logo.loading = 'lazy';
                        logo.src = artworkDataUri;
                        row.appendChild(logo);
                    } else {
                        var placeholder = document.createElement('div');
                        placeholder.className = 'radio-station-scan-logo-placeholder';
                        placeholder.setAttribute('aria-hidden', 'true');
                        placeholder.textContent = '♫';
                        row.appendChild(placeholder);
                    }
                    var content = document.createElement('div');
                    content.className = 'radio-station-scan-content';
                    var name = document.createElement('div');
                    name.className = 'radio-station-scan-name';
                    name.textContent = getValue(station, 'Name', 'name') || '(unnamed station)';
                    content.appendChild(name);

                    var streamStatus = getValue(station, 'StreamStatus', 'streamStatus') || 'Unknown';
                    var artworkStatus = getValue(station, 'ArtworkStatus', 'artworkStatus') || 'Unknown';
                    var meta = document.createElement('div');
                    meta.className = 'radio-station-scan-meta';
                    meta.textContent = 'Stream: ' + streamStatus + ' · Artwork: ' + artworkStatus;
                    content.appendChild(meta);

                    var streamUrl = getValue(station, 'StreamUrl', 'streamUrl') || '';
                    var url = document.createElement('div');
                    url.className = 'radio-station-scan-url';
                    url.textContent = streamUrl || '(no stream URL could be read)';
                    content.appendChild(url);

                    row.appendChild(content);
                    var editButton = document.createElement('button');
                    editButton.type = 'button';
                    editButton.className = 'emby-button raised radio-station-edit';
                    editButton.setAttribute('aria-label', 'Edit ' + (getValue(station, 'Name', 'name') || 'station'));
                    editButton.setAttribute('title', 'Edit station');
                    makePencilIcon(editButton);
                    editButton.addEventListener('click', function () {
                        if (row.querySelector('.radio-station-edit-form')) return;
                        var currentName = String(getValue(station, 'Name', 'name') || '');
                        var currentUrl = String(getValue(station, 'StreamUrl', 'streamUrl') || '');
                        var currentArtworkPath = String(getValue(station, 'ArtworkPath', 'artworkPath') || '');
                        var filePath = String(getValue(station, 'FilePath', 'filePath') || getValue(station, 'Id', 'id') || '');
                        var editForm = document.createElement('div');
                        editForm.className = 'radio-station-edit-form';
                        editForm.style.cssText = 'display:flex;flex-direction:column;gap:8px;margin-top:10px;';
                        var nameLabel = document.createElement('label');
                        nameLabel.textContent = 'Station name';
                        var nameInput = document.createElement('input');
                        nameInput.type = 'text'; nameInput.maxLength = 160; nameInput.className = 'emby-input'; nameInput.value = currentName;
                        nameLabel.appendChild(nameInput);
                        var urlLabel = document.createElement('label');
                        urlLabel.textContent = 'Stream URL';
                        var urlInput = document.createElement('input');
                        urlInput.type = 'url'; urlInput.maxLength = 2048; urlInput.className = 'emby-input'; urlInput.value = currentUrl;
                        urlLabel.appendChild(urlInput);
                        var artworkLabel = document.createElement('label');
                        artworkLabel.textContent = 'Station logo URL (optional; leave blank to keep current logo)';
                        var artworkInput = document.createElement('input');
                        artworkInput.type = 'url'; artworkInput.maxLength = 2048; artworkInput.className = 'emby-input'; artworkInput.placeholder = 'https://example.com/logo.png';
                        artworkLabel.appendChild(artworkInput);
                        var help = document.createElement('div');
                        help.className = 'radio-path-help';
                        help.textContent = 'Saving updates the .strm file. A supplied JPG/PNG logo URL downloads a replacement; leaving it blank keeps the current logo and renames it with the station if needed.';
                        var actions = document.createElement('div');
                        actions.style.cssText = 'display:flex;gap:8px;flex-wrap:wrap;';
                        var saveButton = document.createElement('button');
                        saveButton.type = 'button'; saveButton.className = 'emby-button raised button-submit'; saveButton.textContent = 'Save changes';
                        var cancelButton = document.createElement('button');
                        cancelButton.type = 'button'; cancelButton.className = 'emby-button'; cancelButton.textContent = 'Cancel';
                        var deleteButton = document.createElement('button');
                        deleteButton.type = 'button'; deleteButton.className = 'emby-button'; deleteButton.textContent = 'Delete station';
                        actions.appendChild(saveButton); actions.appendChild(cancelButton); actions.appendChild(deleteButton);
                        editForm.appendChild(nameLabel); editForm.appendChild(urlLabel); editForm.appendChild(artworkLabel); editForm.appendChild(help); editForm.appendChild(actions);
                        content.appendChild(editForm);
                        editButton.disabled = true;
                        nameInput.focus();
                        cancelButton.addEventListener('click', function () {
                            editForm.remove(); editButton.disabled = false;
                        });
                        deleteButton.addEventListener('click', function () {
                            var stationName = currentName || 'this station';
                            if (!window.confirm('Delete ' + stationName + '.strm and its matching JPG/JPEG/PNG logo? This cannot be undone.')) return;
                            deleteButton.disabled = true; saveButton.disabled = true; cancelButton.disabled = true;
                            status.textContent = 'Deleting ' + stationName + '...';
                            ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('RadioMeta/Stations/Delete'), data: JSON.stringify({ FilePath: filePath }), contentType: 'application/json', dataType: 'json' })
                                .then(function (data) { if (data && typeof data.json === 'function') return data.json(); return data; })
                                .then(function (data) {
                                    if (typeof data === 'string') { try { data = JSON.parse(data); } catch (ignore) {} }
                                    if (data && (data.Success === true || data.success === true)) {
                                        var deleteWarning = String(getValue(data, 'Warning', 'warning') || '');
                                        requestTargetedLibraryRefresh(data).then(function (scanResult) {
                                            var notes = [deleteWarning, scanResult.message].filter(Boolean).join(' ');
                                            status.textContent = 'Deleted station and matching logo. ' + notes + ' Refreshing station list...';
                                            scanStationFolder(notes);
                                        });
                                    } else {
                                        window.alert((data && (data.Error || data.error)) || 'The server did not confirm deletion.');
                                        deleteButton.disabled = false; saveButton.disabled = false; cancelButton.disabled = false;
                                    }
                                })
                                .catch(function (error) { window.alert('Station delete failed: ' + (error && error.message ? error.message : String(error))); deleteButton.disabled = false; saveButton.disabled = false; cancelButton.disabled = false; });
                        });
                        saveButton.addEventListener('click', function () {
                            var editedName = nameInput.value.trim();
                            var editedUrl = urlInput.value.trim();
                            if (!editedName) { nameInput.focus(); window.alert('Station name cannot be blank.'); return; }
                            if (!editedUrl) { urlInput.focus(); window.alert('Stream URL cannot be blank.'); return; }
                            saveButton.disabled = true; cancelButton.disabled = true;
                            status.textContent = 'Saving changes to ' + currentName + '...';
                            ApiClient.ajax({
                                type: 'POST',
                                url: ApiClient.getUrl('RadioMeta/Stations/Edit'),
                                data: JSON.stringify({ FilePath: filePath, Name: editedName, StreamUrl: editedUrl, ArtworkUrl: artworkInput.value.trim() }),
                                contentType: 'application/json',
                                dataType: 'json'
                            }).then(function (data) {
                                if (data && typeof data.json === 'function') return data.json();
                                return data;
                            }).then(function (data) {
                                if (typeof data === 'string') { try { data = JSON.parse(data); } catch (ignore) {} }
                                if (data && (data.Success === true || data.success === true)) {
                                    status.textContent = 'Updated station "' + (data.Name || data.name || editedName) + '". Refreshing station list...';
                                    scanStationFolder();
                                } else {
                                    window.alert((data && (data.Error || data.error)) || 'The server did not confirm the station update.');
                                    saveButton.disabled = false; cancelButton.disabled = false;
                                }
                            }).catch(function (error) {
                                window.alert('Station edit failed: ' + (error && error.message ? error.message : String(error)));
                                saveButton.disabled = false; cancelButton.disabled = false;
                            });
                        });
                    });
                    row.appendChild(editButton);
                    container.appendChild(row);
                });
                status.textContent = withCompletionNote('Scan complete: ' + stations.length + ' unique .strm file(s) listed; ' +
                    scannedFiles + ' station file(s) examined in ' + folder +
                    '. Configured path: ' + (configuredFolder || '(not returned)') + '; directories examined: ' + directoriesExamined + '; directory errors: ' + directoryErrors + '. Existing files were not modified.');
            }).catch(function (error) {
                status.textContent = withCompletionNote('Station scan failed: ' +
                    (error && error.message ? error.message : String(error))); 
                console.error('Radio Meta station scan:', error);
            }).finally(function () {
                stationScanInProgress = false;
            });
        }

        function updateStationFolderVisibilityButton() {
            var button = view.querySelector('#radioStationScanButton');
            if (!button) return;
            var label = button.querySelector('span');
            if (label) label.textContent = stationFolderPanelVisible ? 'Hide Folder' : 'Show Folder';
            button.setAttribute('aria-expanded', stationFolderPanelVisible ? 'true' : 'false');
        }

        function toggleStationFolderVisibility() {
            var panel = view.querySelector('#radioStationFolderPanel');
            if (!panel) return;

            if (stationFolderPanelVisible) {
                stationFolderPanelVisible = false;
                panel.style.display = 'none';
                updateStationFolderVisibilityButton();
                return;
            }

            stationFolderPanelVisible = true;
            panel.style.display = '';
            updateStationFolderVisibilityButton();
            // Opening the folder view refreshes the list; hiding it only
            // collapses the panel and never changes or deletes station files.
            scanStationFolder();
        }

        function updateHistoryVisibilityButton() {
            // The left-hand history button is the single show/hide control.
            var button = view.querySelector('#radioHistoryScan');
            if (!button) return;
            var label = button.querySelector('span');
            if (label) label.textContent = historyPanelVisible ? 'Hide History' : 'Show History';
            button.setAttribute('aria-expanded', historyPanelVisible ? 'true' : 'false');
        }

        function toggleHistoryVisibility() {
            var panel = view.querySelector('#radioHistoryPanel');
            if (!panel) return;

            if (historyPanelVisible) {
                historyPanelVisible = false;
                panel.style.display = 'none';
                updateHistoryVisibilityButton();
                return;
            }

            historyPanelVisible = true;
            panel.style.display = '';
            updateHistoryVisibilityButton();
            // Showing history requests the saved entries; there is no separate
            // Refresh button. Hiding the panel never deletes or alters entries.
            loadHistory();
        }

        function loadHistory() {
            if (!historyPanelVisible) return;
            var status = view.querySelector('#radioStatus');
            var container = view.querySelector('#radioHistory');

            if (!status || !container) {
                return;
            }
            // Keep the row editor and its draft stable while the user is editing.
            if (activeHistoryEditId) {
                return;
            }
            if (requestInProgress) {
                historyReloadPending = true;
                return;
            }

            requestInProgress = true;
            if (!hasLoadedOnce) {
                status.textContent = 'Loading played-item history...';
            }

            ApiClient.getJSON(ApiClient.getUrl('RadioMeta/History'))
                .then(function (data) {
                    var error = getValue(data, 'Error', 'error');
                    if (error) {
                        status.textContent = error;
                        return;
                    }

                    var items = getValue(data, 'Items', 'items') || [];
                    historyItems = items;
                    var configuredLimit = Number(getValue(data, 'Limit', 'limit')) || 200;
                    container.replaceChildren();
                    var clearButton = view.querySelector('#radioHistoryClear');
                    if (clearButton) {
                        clearButton.disabled = !items.length || historyActionInProgress;
                    }

                    if (!items.length) {
                        status.textContent = 'No songs have been recorded yet. Play a radio station; newly detected songs will appear here.';
                        hasLoadedOnce = true;
                        return;
                    }

                    var table = document.createElement('table');
                    table.className = 'radio-history-table';
                    table.setAttribute('aria-label', 'Radio history');

                    var thead = document.createElement('thead');
                    var headerRow = document.createElement('tr');
                    ['#', 'Station', 'Song Title', 'Artist', 'Date/Time', 'Actions'].forEach(function (label) {
                        var th = document.createElement('th');
                        th.scope = 'col';
                        th.textContent = label;
                        headerRow.appendChild(th);
                    });
                    thead.appendChild(headerRow);
                    table.appendChild(thead);

                    var tbody = document.createElement('tbody');
                    items.forEach(function (item, itemIndex) {
                        var row = document.createElement('tr');
                        row.className = 'radio-history-row' + (itemIndex % 2 === 1 ? ' radio-history-row-alternate' : '');

                        var artist = getValue(item, 'Artist', 'artist');
                        var songTitle = getValue(item, 'SongTitle', 'songTitle');
                        var stationName = getValue(item, 'StationName', 'stationName');
                        var playedAt = getValue(item, 'PlayedAt', 'playedAt');
                        var id = String(getValue(item, 'Id', 'id') || '');
                        row.setAttribute('data-history-row-id', id);
                        row.setAttribute('data-current-title', songTitle || '');

                        addText(row, 'radio-history-number', String(itemIndex + 1), 'td');
                        addText(row, 'radio-history-station', stationName || '—', 'td');
                        addText(row, 'radio-history-song', songTitle || (artist ? 'Unknown title' : 'Unknown track'), 'td');
                        addText(row, 'radio-history-artist', artist || '—', 'td');
                        addText(row, 'radio-history-played-at', formatPlayedAt(playedAt) || '—', 'td');

                        var actions = document.createElement('td');
                        actions.className = 'radio-history-actions';
                        if (id) {
                            var editButton = document.createElement('button');
                            editButton.type = 'button';
                            editButton.className = 'emby-button raised radio-history-edit';
                            editButton.setAttribute('data-history-edit-id', id);
                            editButton.setAttribute('aria-label', 'Edit title for ' + (songTitle || 'Unknown track'));
                            editButton.setAttribute('title', 'Edit title');
                            editButton.disabled = historyActionInProgress;
                            makePencilIcon(editButton);
                            actions.appendChild(editButton);

                            var deleteButton = document.createElement('button');
                            deleteButton.type = 'button';
                            deleteButton.className = 'emby-button raised button-delete radio-history-delete';
                            deleteButton.setAttribute('data-history-delete-id', id);
                            deleteButton.setAttribute('aria-label', 'Delete history entry ' + (songTitle || 'Unknown track'));
                            deleteButton.setAttribute('title', 'Delete entry');
                            deleteButton.disabled = historyActionInProgress;
                            makeTrashIcon(deleteButton);
                            actions.appendChild(deleteButton);
                        }
                        row.appendChild(actions);
                        tbody.appendChild(row);
                    });
                    table.appendChild(tbody);
                    container.appendChild(table);

                    status.textContent = items.length >= configuredLimit
                        ? 'Showing the ' + configuredLimit + ' most recent played items, newest first.'
                        : items.length + (items.length === 1
                            ? ' played item recorded, newest first.'
                            : ' played items recorded, newest first.');
                    hasLoadedOnce = true;
                }).catch(function (error) {
                    showError(status, error);
                }).finally(function () {
                    requestInProgress = false;
                    if (historyReloadPending) {
                        historyReloadPending = false;
                        loadHistory();
                    }
                });
        }

        function findHistoryItem(id) {
            return historyItems.find(function (item) {
                return String(getValue(item, 'Id', 'id') || '') === String(id || '');
            }) || null;
        }

        function findHistoryEditorRow(id) {
            return Array.from(view.querySelectorAll('tr[data-history-editor-for]')).find(function (row) {
                return row.getAttribute('data-history-editor-for') === String(id || '');
            }) || null;
        }

        function getPreserveLabelsForStation(stationName) {
            var station = String(stationName || '').trim();
            if (!station) return '';
            var labels = [];
            titleCleanupRules.forEach(function (rule) {
                var ruleStation = getCleanupStation(rule);
                var kind = getCleanupType(rule);
                if (ruleStation.toLowerCase() === station.toLowerCase() &&
                    ['TrailingBracketCode4', 'TrailingSquareBracketAny', 'TrailingParenthesisAny'].indexOf(kind) >= 0) {
                    getCleanupPreserveLabels(rule).split(/[,;\r\n]+/).forEach(function (label) {
                        var trimmed = label.trim();
                        if (trimmed && !labels.some(function (existing) { return existing.toLowerCase() === trimmed.toLowerCase(); })) {
                            labels.push(trimmed);
                        }
                    });
                }
            });
            return labels.join(', ');
        }

        function inferStationCleanup(originalTitle, correctedTitle, preserveLabels) {
            var original = String(originalTitle || '').trim();
            var corrected = String(correctedTitle || '').trim();
            var customPreserved = String(preserveLabels || '').split(/[,;\r\n]+/).map(function (label) {
                return label.trim().replace(/^[\[\(]+|[\]\)]+$/g, '').toLowerCase();
            }).filter(Boolean);
            if (!original || !corrected || corrected.length >= original.length ||
                original.toLowerCase().indexOf(corrected.toLowerCase()) !== 0) {
                return { ok: false, message: 'A station-wide rule can only be inferred when the correction removes a trailing suffix.' };
            }

            var rawSuffix = original.substring(corrected.length);
            var suffix = rawSuffix.trim();
            if (!suffix || !(/^\s/.test(rawSuffix) || /^[\[(\-_|/]/.test(rawSuffix))) {
                return { ok: false, message: 'The difference does not look like a separate trailing suffix. Choose This song only or edit a trailing suffix.' };
            }

            var inner;
            if (/^\[[^\[\]]+\]$/.test(suffix)) {
                inner = suffix.substring(1, suffix.length - 1).trim().toLowerCase();
                if (['live', 'edit', 'demo', 'mono', 'full', 'remastered', 'radio edit', 'acoustic', 'explicit', 'instrumental', 'studio'].indexOf(inner) >= 0) {
                    return { ok: false, message: 'This is a common meaningful label and is preserved by default. Choose This song only if you intentionally want to remove it.' };
                }
                if (customPreserved.indexOf(inner) >= 0) {
                    return { ok: false, message: 'This suffix is in your labels-to-preserve list. Remove it from that list if you intend to clean it.' };
                }
                return { ok: true, ruleType: 'TrailingSquareBracketAny', description: 'Remove trailing [bracketed] suffixes on this station; built-in and custom labels are preserved.' };
            }
            if (/^\([^()]+\)$/.test(suffix)) {
                inner = suffix.substring(1, suffix.length - 1).trim().toLowerCase();
                if (['live', 'edit', 'demo', 'mono', 'full', 'remastered', 'radio edit', 'acoustic', 'explicit', 'instrumental', 'studio'].indexOf(inner) >= 0) {
                    return { ok: false, message: 'This is a common meaningful label and is preserved by default. Choose This song only if you intentionally want to remove it.' };
                }
                if (customPreserved.indexOf(inner) >= 0) {
                    return { ok: false, message: 'This suffix is in your labels-to-preserve list. Remove it from that list if you intend to clean it.' };
                }
                return { ok: true, ruleType: 'TrailingParenthesisAny', description: 'Remove trailing (parenthetical) suffixes on this station; built-in and custom labels are preserved.' };
            }
            return { ok: true, ruleType: 'ExactSuffix', suffix: suffix, description: 'Remove the exact suffix "' + suffix + '" from future titles on this station.' };
        }

        function updateHistoryEditPreview(editorRow) {
            if (!editorRow) return;
            var correctedInput = editorRow.querySelector('[data-history-edit-corrected]');
            var scopeInput = editorRow.querySelector('input[name="radioHistoryEditScope"]:checked');
            var preserveInput = editorRow.querySelector('[data-history-edit-preserve]');
            var preview = editorRow.querySelector('.radio-history-edit-preview');
            var saveButton = editorRow.querySelector('.radio-history-edit-save');
            if (!correctedInput || !scopeInput || !preview || !saveButton) return;

            var original = editorRow.getAttribute('data-original-title') || '';
            var current = editorRow.getAttribute('data-current-title') || original;
            var corrected = correctedInput.value.trim();
            var preserveLabels = preserveInput ? preserveInput.value.trim() : '';
            if (preserveInput) {
                preserveInput.disabled = scopeInput.value !== 'Station';
            }
            if (!corrected) {
                preview.textContent = 'Enter the corrected song title.';
                saveButton.disabled = true;
                return;
            }
            if (scopeInput.value === 'Station') {
                var inferred = inferStationCleanup(original, corrected, preserveLabels);
                preview.textContent = inferred.ok
                    ? 'Station rule preview: ' + inferred.description + (preserveLabels ? ' Labels to preserve: ' + preserveLabels + '.' : '')
                    : inferred.message;
                saveButton.disabled = historyActionInProgress || !inferred.ok;
            } else {
                preview.textContent = corrected === current
                    ? 'No title change has been made yet.'
                    : 'Save this exact correction for this station and incoming title; future exact matches will use it.';
                saveButton.disabled = historyActionInProgress || corrected === current;
            }
        }

        function closeHistoryEditor(id) {
            var targetId = id || activeHistoryEditId;
            var editorRow = findHistoryEditorRow(targetId);
            if (editorRow && editorRow.parentNode) editorRow.parentNode.removeChild(editorRow);
            if (!id || activeHistoryEditId === id) activeHistoryEditId = '';
        }

        function openHistoryEditor(id) {
            if (historyActionInProgress || !id) return;
            if (activeHistoryEditId === id) {
                closeHistoryEditor(id);
                return;
            }
            if (activeHistoryEditId) closeHistoryEditor(activeHistoryEditId);

            var item = findHistoryItem(id);
            var row = Array.from(view.querySelectorAll('tr[data-history-row-id]')).find(function (candidate) {
                return candidate.getAttribute('data-history-row-id') === String(id);
            });
            if (!item || !row || !row.parentNode) return;

            var currentTitle = String(getValue(item, 'SongTitle', 'songTitle') || '');
            var originalTitle = String(getValue(item, 'OriginalSongTitle', 'originalSongTitle') || currentTitle);
            if (!originalTitle) originalTitle = currentTitle;
            var editorRow = document.createElement('tr');
            editorRow.className = 'radio-history-edit-row';
            editorRow.setAttribute('data-history-editor-for', id);
            editorRow.setAttribute('data-original-title', originalTitle);
            editorRow.setAttribute('data-current-title', currentTitle);
            var cell = document.createElement('td');
            cell.colSpan = 6;
            var editor = document.createElement('div');
            editor.className = 'radio-history-inline-editor';

            var fields = document.createElement('div');
            fields.className = 'radio-history-edit-fields';
            var originalField = document.createElement('div');
            originalField.className = 'radio-history-edit-field';
            addText(originalField, '', 'Original incoming title', 'label');
            var originalValue = document.createElement('div');
            originalValue.className = 'radio-history-edit-original';
            originalValue.textContent = originalTitle;
            originalField.appendChild(originalValue);
            fields.appendChild(originalField);

            var correctedField = document.createElement('div');
            correctedField.className = 'radio-history-edit-field';
            var correctedLabel = document.createElement('label');
            correctedLabel.textContent = 'Title shown in history';
            var correctedInput = document.createElement('input');
            correctedInput.type = 'text';
            correctedInput.className = 'emby-input';
            correctedInput.maxLength = 300;
            correctedInput.value = currentTitle;
            correctedInput.setAttribute('data-history-edit-corrected', 'true');
            correctedLabel.appendChild(correctedInput);
            correctedField.appendChild(correctedLabel);
            fields.appendChild(correctedField);
            editor.appendChild(fields);

            addText(editor, '', 'Apply correction to:', 'strong');
            var scope = document.createElement('div');
            scope.className = 'radio-history-edit-scope';
            [
                { value: 'SongOnly', label: 'This song only', checked: true },
                { value: 'Station', label: 'Entire station', checked: false }
            ].forEach(function (option) {
                var label = document.createElement('label');
                var radio = document.createElement('input');
                radio.type = 'radio';
                radio.name = 'radioHistoryEditScope';
                radio.value = option.value;
                radio.checked = option.checked;
                radio.addEventListener('change', function () { updateHistoryEditPreview(editorRow); });
                label.appendChild(radio);
                label.appendChild(document.createTextNode(option.label));
                scope.appendChild(label);
            });
            editor.appendChild(scope);

            var preserveField = document.createElement('div');
            preserveField.className = 'radio-history-edit-preserve';
            var preserveLabel = document.createElement('label');
            preserveLabel.textContent = 'Additional labels to preserve (for entire station)';
            var preserveInput = document.createElement('input');
            preserveInput.type = 'text';
            preserveInput.className = 'emby-input';
            preserveInput.maxLength = 500;
            preserveInput.placeholder = 'e.g. Live, Radio Edit, Remastered';
            preserveInput.setAttribute('data-history-edit-preserve', 'true');
            preserveInput.value = getPreserveLabelsForStation(String(getValue(item, 'StationName', 'stationName') || ''));
            preserveLabel.appendChild(preserveInput);
            preserveField.appendChild(preserveLabel);
            addText(preserveField, 'radio-history-edit-preserve-help', 'Common labels are already preserved. Add extra labels separated by commas or semicolons. Used only for Entire station.', 'div');
            editor.appendChild(preserveField);

            var preview = document.createElement('div');
            preview.className = 'radio-history-edit-preview';
            preview.setAttribute('role', 'status');
            preview.setAttribute('aria-live', 'polite');
            editor.appendChild(preview);

            var buttons = document.createElement('div');
            buttons.className = 'radio-history-edit-actions';
            var cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.className = 'emby-button radio-history-edit-cancel';
            cancel.setAttribute('data-history-edit-cancel-id', id);
            cancel.textContent = 'Cancel';
            var save = document.createElement('button');
            save.type = 'button';
            save.className = 'emby-button raised button-submit radio-history-edit-save';
            save.setAttribute('data-history-edit-save-id', id);
            save.textContent = 'Save correction';
            buttons.appendChild(cancel);
            buttons.appendChild(save);
            editor.appendChild(buttons);
            cell.appendChild(editor);
            editorRow.appendChild(cell);
            row.parentNode.insertBefore(editorRow, row.nextSibling);
            activeHistoryEditId = String(id);
            correctedInput.addEventListener('input', function () { updateHistoryEditPreview(editorRow); });
            preserveInput.addEventListener('input', function () { updateHistoryEditPreview(editorRow); });
            updateHistoryEditPreview(editorRow);
            correctedInput.focus();
            correctedInput.select();
        }

        function saveHistoryTitleCorrection(id) {
            if (historyActionInProgress || !id) return;
            var editorRow = findHistoryEditorRow(id);
            if (!editorRow) return;
            var correctedInput = editorRow.querySelector('[data-history-edit-corrected]');
            var scopeInput = editorRow.querySelector('input[name="radioHistoryEditScope"]:checked');
            var preserveInput = editorRow.querySelector('[data-history-edit-preserve]');
            var status = view.querySelector('#radioStatus');
            var correctedTitle = correctedInput ? correctedInput.value.trim() : '';
            var preserveLabels = preserveInput ? preserveInput.value.trim() : '';
            var scope = scopeInput ? scopeInput.value : 'SongOnly';
            if (!correctedTitle) {
                if (status) status.textContent = 'Enter a corrected title before saving.';
                if (correctedInput) correctedInput.focus();
                return;
            }
            var item = findHistoryItem(id);
            if (!item) {
                if (status) status.textContent = 'History entry not found. Refresh the history and try again.';
                return;
            }
            if (scope === 'Station') {
                var inferred = inferStationCleanup(
                    String(getValue(item, 'OriginalSongTitle', 'originalSongTitle') || getValue(item, 'SongTitle', 'songTitle') || ''),
                    correctedTitle,
                    preserveLabels);
                if (!inferred.ok) {
                    updateHistoryEditPreview(editorRow);
                    return;
                }
            }

            historyActionInProgress = true;
            setHistoryButtonsDisabled(true);
            var saveButton = editorRow.querySelector('.radio-history-edit-save');
            var cancelButton = editorRow.querySelector('.radio-history-edit-cancel');
            if (saveButton) saveButton.disabled = true;
            if (cancelButton) cancelButton.disabled = true;
            if (status) status.textContent = 'Saving corrected title...';

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/History/Edit'),
                data: JSON.stringify({ Id: id, CorrectedTitle: correctedTitle, Scope: scope, PreserveLabels: preserveLabels }),
                contentType: 'application/json'
            }).then(function (data) {
                var error = getValue(data, 'Error', 'error');
                if (error) throw new Error(error);

                item.SongTitle = correctedTitle;
                var songCell = Array.from(view.querySelectorAll('tr[data-history-row-id]')).find(function (row) {
                    return row.getAttribute('data-history-row-id') === String(id);
                });
                if (songCell && songCell.children[2]) songCell.children[2].textContent = correctedTitle;
                closeHistoryEditor(String(id));
                if (status) status.textContent = getValue(data, 'Message', 'message') || 'Song title correction saved.';
                if (scope === 'Station') {
                    loadTitleCleanupRules();
                } else {
                    loadTitleCorrections();
                }
            }).catch(function (error) {
                var message = error && error.message ? error.message : String(error);
                if (status) status.textContent = 'Unable to save title correction: ' + message;
                console.error('Radio Meta history title correction:', error);
            }).finally(function () {
                historyActionInProgress = false;
                setHistoryButtonsDisabled(false);
                if (activeHistoryEditId) {
                    var activeEditor = findHistoryEditorRow(activeHistoryEditId);
                    if (activeEditor) updateHistoryEditPreview(activeEditor);
                }
            });
        }

        function getModeStation(rule) {
            return String(getValue(rule, 'StationName', 'stationName') || '').trim();
        }

        function getStationMode(rule) {
            var mode = String(getValue(rule, 'Mode', 'mode') || 'Automatic').trim().toLowerCase();
            if (mode === 'stationonly') return 'StationOnly';
            if (mode === 'carplay') return 'Carplay';
            return 'Automatic';
        }

        function getStationModeDelaySeconds(rule) {
            var value = Number(getValue(rule, 'MetadataDelaySeconds', 'metadataDelaySeconds'));
            return Number.isFinite(value) ? Math.max(0, Math.min(30, Math.floor(value))) : 0;
        }

        function getStationModeTargetDevice(rule) {
            return String(getValue(rule, 'MetadataDelayDeviceName', 'metadataDelayDeviceName') || '').trim();
        }

        function renderStationModeRules() {
            var container = view.querySelector('#radioStationModeRules');
            if (!container) {
                return;
            }

            container.replaceChildren();
            var rules = stationModeRules.slice().sort(function (a, b) {
                return getModeStation(a).localeCompare(getModeStation(b));
            });

            if (!rules.length) {
                addText(container, 'radio-ignore-empty',
                    'No station/device rules saved. Unmatched playback uses Automatic mode with no metadata delay.');
                return;
            }

            rules.forEach(function (rule) {
                var station = getModeStation(rule);
                var mode = getStationMode(rule);
                var delaySeconds = getStationModeDelaySeconds(rule);
                var targetDevice = getStationModeTargetDevice(rule);
                var row = document.createElement('div');
                row.className = 'radio-station-mode-row';

                var details = document.createElement('div');
                details.className = 'radio-station-mode-details';
                addText(details, 'radio-station-mode-name', station, 'strong');
                addText(details, 'radio-station-mode-scope', 'Station / Device: ' + station);
                var modeDescription = mode === 'StationOnly'
                    ? 'Station only — skips track metadata lookups.'
                    : mode === 'Carplay'
                        ? 'Carplay — clears old song metadata on station changes.'
                        : 'Automatic — uses normal track metadata lookups.';
                var delayDescription = delaySeconds === 0
                    ? 'Metadata delay: off.'
                    : 'Metadata delay: ' + delaySeconds + ' second(s). A changed candidate restarts the timer.';
                var legacyTargetDescription = targetDevice
                    ? ' Existing station rule is limited to ' + targetDevice + '.'
                    : '';
                addText(details, 'radio-station-mode-description', modeDescription + ' ' + delayDescription + legacyTargetDescription);
                row.appendChild(details);

                var remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'emby-button raised radio-station-mode-remove';
                remove.textContent = 'Remove rule';
                remove.setAttribute('data-station-mode-remove', station);
                remove.setAttribute('aria-label', 'Remove metadata mode rule for ' + station);
                remove.disabled = stationModeSaveInProgress;
                row.appendChild(remove);
                container.appendChild(row);
            });
        }

        function loadStationModes() {
            var status = view.querySelector('#radioStationModeStatus');
            if (!status || stationModeRequestInProgress) {
                return;
            }

            stationModeRequestInProgress = true;
            status.textContent = 'Loading station/device metadata settings...';

            ApiClient.getJSON(ApiClient.getUrl('RadioMeta/StationModes'))
                .then(function (data) {
                    var error = getValue(data, 'Error', 'error');
                    if (error) {
                        status.textContent = error;
                        return;
                    }

                    stationModeRules = getValue(data, 'Rules', 'rules') || [];
                    renderStationModeRules();
                    status.textContent = stationModeRules.length
                        ? stationModeRules.length + ' saved station/device rule(s). Unmatched playback uses Automatic mode with no metadata delay.'
                        : 'No saved station/device rules. Unmatched playback uses Automatic mode with no metadata delay by default.';
                })
                .catch(function (error) {
                    status.textContent = 'Unable to load station/device metadata settings: ' +
                        (error && error.message ? error.message : String(error));
                    console.error('Radio Meta station metadata settings:', error);
                })
                .finally(function () {
                    stationModeRequestInProgress = false;
                });
        }

        function saveStationModeRules(nextRules, station, mode, delaySeconds, targetDevice, successMessage, clearStationInput) {
            var status = view.querySelector('#radioStationModeStatus');
            var addButton = view.querySelector('#radioStationModeAdd');
            if (!status || stationModeSaveInProgress) {
                return;
            }

            var previousRules = stationModeRules;
            stationModeRules = nextRules;
            stationModeSaveInProgress = true;
            if (addButton) {
                addButton.disabled = true;
            }
            renderStationModeRules();
            status.textContent = 'Saving metadata rule for ' + station + '...';

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/StationModes'),
                data: JSON.stringify({ Rules: nextRules }),
                contentType: 'application/json'
            }).then(function (data) {
                var error = getValue(data, 'Error', 'error');
                if (error) {
                    stationModeRules = previousRules;
                    status.textContent = error;
                    return;
                }

                stationModeRules = getValue(data, 'Rules', 'rules') || nextRules;
                renderStationModeRules();
                var savedMessage = successMessage || (mode === 'StationOnly'
                    ? 'Station only saved for ' + station + '. Track metadata lookups are skipped.'
                    : mode === 'Carplay'
                        ? 'Carplay saved for ' + station + '. Normal metadata lookup is enabled; the selected delay controls when updated song metadata is published.'
                        : 'Automatic saved for ' + station + '. Normal metadata lookup is enabled.');
                status.textContent = savedMessage +
                    (delaySeconds > 0
                        ? ' Metadata delay: ' + delaySeconds + ' second(s). The identifier is matched against station titles and Emby device names.'
                        : ' Metadata delay is off.');
                if (clearStationInput) {
                    var input = view.querySelector('#radioStationModeName');
                    var modeSelect = view.querySelector('#radioStationModeValue');
                    var delaySelect = view.querySelector('#radioStationModeDelay');
                    if (input) {
                        input.value = '';
                    }
                    if (modeSelect) modeSelect.value = 'Automatic';
                    if (delaySelect) delaySelect.value = '0';
                }
            }).catch(function (error) {
                stationModeRules = previousRules;
                renderStationModeRules();
                status.textContent = 'Unable to save station metadata settings: ' +
                    (error && error.message ? error.message : String(error));
                console.error('Radio Meta station metadata save:', error);
            }).finally(function () {
                stationModeSaveInProgress = false;
                if (addButton) {
                    addButton.disabled = false;
                }
                renderStationModeRules();
            });
        }

        function addStationModeRule() {
            var input = view.querySelector('#radioStationModeName');
            var modeSelect = view.querySelector('#radioStationModeValue');
            var delaySelect = view.querySelector('#radioStationModeDelay');
            var status = view.querySelector('#radioStationModeStatus');
            if (!input || !modeSelect || !delaySelect || !status) {
                return;
            }

            var station = input.value.trim();
            var mode = modeSelect.value === 'StationOnly' ? 'StationOnly' : (modeSelect.value === 'Carplay' ? 'Carplay' : 'Automatic');
            var delaySeconds = Math.max(0, Math.min(30, Number(delaySelect.value) || 0));
            var existingTargetDevice = stationModeRules.find(function (rule) {
                return getModeStation(rule).toLowerCase() === station.toLowerCase();
            });
            // Preserve the optional target device from older saved rules until that rule is removed.
            var targetDevice = existingTargetDevice ? getStationModeTargetDevice(existingTargetDevice) : '';
            if (!station) {
                status.textContent = 'Enter a .strm station title or an exact Emby device name.';
                input.focus();
                return;
            }

            var existing = stationModeRules.find(function (rule) {
                return getModeStation(rule).toLowerCase() === station.toLowerCase();
            });
            if (existing && getStationMode(existing) === mode &&
                getStationModeDelaySeconds(existing) === delaySeconds &&
                getStationModeTargetDevice(existing).toLowerCase() === targetDevice.toLowerCase()) {
                status.textContent = 'That station/device rule already has these settings saved.';
                return;
            }
            if (!existing && stationModeRules.length >= 100) {
                status.textContent = 'You have reached the limit of 100 station/device rules.';
                return;
            }

            var nextRules = stationModeRules.filter(function (rule) {
                return getModeStation(rule).toLowerCase() !== station.toLowerCase();
            });
            nextRules.push({
                StationName: station,
                Mode: mode,
                MetadataDelaySeconds: delaySeconds,
                MetadataDelayDeviceName: targetDevice
            });
            saveStationModeRules(nextRules, station, mode, delaySeconds, targetDevice,
                'Station/device rule saved to the plugin XML.', true);
        }

        function removeStationModeRule(station) {
            if (stationModeSaveInProgress) {
                return;
            }
            var normalizedStation = String(station || '').trim();
            var nextRules = stationModeRules.filter(function (rule) {
                return getModeStation(rule).toLowerCase() !== normalizedStation.toLowerCase();
            });
            if (nextRules.length === stationModeRules.length) {
                return;
            }
            saveStationModeRules(nextRules, normalizedStation, 'Automatic', 0, '',
                'Saved rule removed. Matching playback uses Automatic mode with no metadata delay unless another rule applies.', false);
        }

        function getRulePhrase(rule) {
            return String(getValue(rule, 'Phrase', 'phrase') || '').trim();
        }

        function getRuleStation(rule) {
            return String(getValue(rule, 'StationName', 'stationName') || '').trim();
        }

        function renderIgnoreRules() {
            var container = view.querySelector('#radioIgnoreRules');
            if (!container) {
                return;
            }

            container.replaceChildren();

            if (!ignoreRules.length) {
                addText(container, 'radio-ignore-empty', 'No ignored phrases configured.');
                return;
            }

            ignoreRules.forEach(function (rule, index) {
                var phrase = getRulePhrase(rule);
                var station = getRuleStation(rule);
                var row = document.createElement('div');
                row.className = 'radio-ignore-rule';

                var text = document.createElement('div');
                text.className = 'radio-ignore-rule-text';
                addText(text, 'radio-ignore-rule-phrase', phrase, 'strong');
                addText(
                    text,
                    'radio-ignore-rule-scope',
                    station ? 'Station: ' + station : 'Applies to all stations'
                );
                row.appendChild(text);

                var remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'emby-button raised radio-ignore-remove';
                remove.textContent = 'Remove';
                remove.setAttribute('data-ignore-index', String(index));
                remove.setAttribute('aria-label', 'Remove ignore phrase ' + phrase);
                row.appendChild(remove);
                container.appendChild(row);
            });
        }

        function loadIgnorePhrases() {
            var status = view.querySelector('#radioIgnoreStatus');
            if (!status || ignoreRequestInProgress) {
                return;
            }

            ignoreRequestInProgress = true;
            status.textContent = 'Loading ignore phrase settings...';

            ApiClient.getJSON(ApiClient.getUrl('RadioMeta/IgnorePhrases'))
                .then(function (data) {
                    var error = getValue(data, 'Error', 'error');
                    if (error) {
                        status.textContent = error;
                        return;
                    }

                    ignoreRules = getValue(data, 'Rules', 'rules') || [];
                    renderIgnoreRules();
                    status.textContent = ignoreRules.length
                        ? ignoreRules.length + ' ignore phrase rule(s) configured.'
                        : 'No ignore phrase rules configured.';
                })
                .catch(function (error) {
                    status.textContent = 'Unable to load ignore phrase settings: ' +
                        (error && error.message ? error.message : String(error));
                    console.error('Radio Meta ignore phrases:', error);
                })
                .finally(function () {
                    ignoreRequestInProgress = false;
                });
        }

        function saveIgnoreRules(nextRules, successMessage, clearInputs) {
            var status = view.querySelector('#radioIgnoreStatus');
            var addButton = view.querySelector('#radioIgnoreAdd');
            if (!status || ignoreSaveInProgress) {
                return;
            }

            ignoreSaveInProgress = true;
            if (addButton) {
                addButton.disabled = true;
            }
            view.querySelectorAll('.radio-ignore-remove').forEach(function (button) {
                button.disabled = true;
            });
            status.textContent = 'Saving ignore phrase settings...';

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/IgnorePhrases'),
                data: JSON.stringify({ Rules: nextRules }),
                contentType: 'application/json'
            }).then(function (data) {
                var error = getValue(data, 'Error', 'error');
                if (error) {
                    status.textContent = error;
                    return;
                }

                ignoreRules = getValue(data, 'Rules', 'rules') || nextRules;
                renderIgnoreRules();
                status.textContent = successMessage ||
                    getValue(data, 'Message', 'message') || 'Ignore phrase settings saved.';

                if (clearInputs) {
                    var phraseInput = view.querySelector('#radioIgnorePhrase');
                    var stationInput = view.querySelector('#radioIgnoreStation');
                    if (phraseInput) {
                        phraseInput.value = '';
                    }
                    if (stationInput) {
                        stationInput.value = '';
                    }
                }
            }).catch(function (error) {
                status.textContent = 'Unable to save ignore phrase settings: ' +
                    (error && error.message ? error.message : String(error));
                console.error('Radio Meta ignore phrase save:', error);
            }).finally(function () {
                ignoreSaveInProgress = false;
                if (addButton) {
                    addButton.disabled = false;
                }
                view.querySelectorAll('.radio-ignore-remove').forEach(function (button) {
                    button.disabled = false;
                });
            });
        }

        function addIgnorePhrase() {
            var phraseInput = view.querySelector('#radioIgnorePhrase');
            var stationInput = view.querySelector('#radioIgnoreStation');
            var status = view.querySelector('#radioIgnoreStatus');
            if (!phraseInput || !stationInput || !status) {
                return;
            }

            var phrase = phraseInput.value.trim();
            var stationName = stationInput.value.trim();
            if (!phrase) {
                status.textContent = 'Enter a phrase before adding a rule.';
                phraseInput.focus();
                return;
            }

            var duplicate = ignoreRules.some(function (rule) {
                return getRulePhrase(rule).toLowerCase() === phrase.toLowerCase() &&
                    getRuleStation(rule).toLowerCase() === stationName.toLowerCase();
            });
            if (duplicate) {
                status.textContent = 'That phrase rule already exists for the selected station scope.';
                return;
            }

            if (ignoreRules.length >= 100) {
                status.textContent = 'You have reached the limit of 100 ignore phrase rules.';
                return;
            }

            saveIgnoreRules(ignoreRules.concat([{
                StationName: stationName,
                Phrase: phrase
            }]), 'Ignore phrase added and saved.', true);
        }

        function getCleanupStation(rule) {
            return String(getValue(rule, 'StationName', 'stationName') || '').trim();
        }

        function getCleanupType(rule) {
            var type = String(getValue(rule, 'RuleType', 'ruleType') || 'TrailingBracketCode4').trim();
            var lower = type.toLowerCase();
            if (lower === 'exactsuffix') return 'ExactSuffix';
            if (lower === 'trailingsquarebracketany') return 'TrailingSquareBracketAny';
            if (lower === 'trailingparenthesisany') return 'TrailingParenthesisAny';
            return 'TrailingBracketCode4';
        }

        function getCleanupSuffix(rule) {
            return String(getValue(rule, 'Suffix', 'suffix') || '').trim();
        }

        function getCleanupPreserveLabels(rule) {
            return String(getValue(rule, 'PreserveLabels', 'preserveLabels') || '').trim();
        }

        function updateSavedTitleFixesStatus(message) {
            var status = view.querySelector('#radioTitleFixesStatus');
            if (!status) return;
            if (message) {
                status.textContent = message;
                return;
            }
            if (!titleCorrectionsLoaded || !titleCleanupRulesLoaded) {
                status.textContent = 'Loading saved title fixes...';
                return;
            }
            var exactCount = titleCorrections.length;
            var cleanupCount = titleCleanupRules.length;
            if (exactCount === 0 && cleanupCount === 0) {
                status.textContent = 'No saved title fixes configured yet.';
            } else {
                status.textContent = exactCount + ' exact song correction(s) and ' + cleanupCount + ' reusable cleanup rule(s) saved.';
            }
        }

        function renderSavedTitleFixes() {
            var container = view.querySelector('#radioTitleFixes');
            if (!container) return;
            container.replaceChildren();

            // The status line above the list is the single empty-state message.
            // Keep the list itself empty so we don't show duplicate empty notices.
            if (!titleCorrections.length && !titleCleanupRules.length) return;

            titleCorrections.forEach(function (correction, index) {
                var row = document.createElement('div');
                row.className = 'radio-title-correction';
                var details = document.createElement('div');
                details.className = 'radio-title-correction-text';
                addText(details, 'radio-title-cleanup-rule-name', 'This song only · ' + (getValue(correction, 'StationName', 'stationName') || 'Unknown station'), 'strong');
                addText(details, 'radio-title-correction-scope',
                    String(getValue(correction, 'OriginalTitle', 'originalTitle') || '') + '  →  ' +
                    String(getValue(correction, 'CorrectedTitle', 'correctedTitle') || ''));
                row.appendChild(details);
                var remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'emby-button raised radio-title-correction-remove';
                remove.textContent = 'Remove';
                remove.setAttribute('data-title-correction-index', String(index));
                remove.setAttribute('aria-label', 'Remove exact title correction ' + (index + 1));
                row.appendChild(remove);
                container.appendChild(row);
            });

            titleCleanupRules.forEach(function (rule, index) {
                var station = getCleanupStation(rule);
                var type = getCleanupType(rule);
                var suffix = getCleanupSuffix(rule);
                var preserve = getCleanupPreserveLabels(rule);
                var ruleLabel = type === 'ExactSuffix'
                    ? 'Remove exact suffix: ' + suffix
                    : type === 'TrailingSquareBracketAny'
                        ? 'Remove trailing [bracketed] suffix'
                        : type === 'TrailingParenthesisAny'
                            ? 'Remove trailing (parenthetical) suffix'
                            : 'Remove trailing 4-character bracket code';
                var row = document.createElement('div');
                row.className = 'radio-title-cleanup-rule';
                var details = document.createElement('div');
                details.className = 'radio-title-cleanup-rule-text';
                addText(details, 'radio-title-cleanup-rule-name', (station || 'All stations') + ' · ' + ruleLabel, 'strong');
                var detailText = [];
                if (preserve && type !== 'ExactSuffix') detailText.push('Additional labels to preserve: ' + preserve);
                addText(details, 'radio-title-cleanup-rule-scope', detailText.join(' · '));
                row.appendChild(details);
                var remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'emby-button raised radio-title-cleanup-remove';
                remove.textContent = 'Remove';
                remove.setAttribute('data-title-cleanup-index', String(index));
                remove.setAttribute('aria-label', 'Remove saved title cleanup rule ' + (index + 1));
                row.appendChild(remove);
                container.appendChild(row);
            });
        }

        function loadTitleCorrections() {
            if (titleCorrectionsRequestInProgress) return;
            titleCorrectionsRequestInProgress = true;
            ApiClient.getJSON(ApiClient.getUrl('RadioMeta/TitleCorrections'))
                .then(function (data) {
                    var error = getValue(data, 'Error', 'error');
                    if (error) throw new Error(error);
                    titleCorrections = getValue(data, 'Corrections', 'corrections') || [];
                    titleCorrectionsLoaded = true;
                    renderSavedTitleFixes();
                    updateSavedTitleFixesStatus();
                })
                .catch(function (error) {
                    titleCorrectionsLoaded = true;
                    updateSavedTitleFixesStatus('Unable to load exact title corrections: ' + (error && error.message ? error.message : String(error)));
                    console.error('Radio Meta title correction load:', error);
                })
                .finally(function () { titleCorrectionsRequestInProgress = false; });
        }

        function saveTitleCorrections(nextCorrections) {
            var status = view.querySelector('#radioTitleFixesStatus');
            if (!status || titleCorrectionsSaveInProgress) return;
            var previous = titleCorrections;
            titleCorrectionsSaveInProgress = true;
            view.querySelectorAll('.radio-title-correction-remove').forEach(function (button) { button.disabled = true; });
            status.textContent = 'Saving exact title corrections to the plugin XML...';
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/TitleCorrections'),
                data: JSON.stringify({ Corrections: nextCorrections }),
                contentType: 'application/json'
            }).then(function (data) {
                var error = getValue(data, 'Error', 'error');
                if (error) throw new Error(error);
                titleCorrections = getValue(data, 'Corrections', 'corrections') || nextCorrections;
                renderSavedTitleFixes();
                updateSavedTitleFixesStatus(getValue(data, 'Message', 'message') || 'Exact title corrections saved.');
            }).catch(function (error) {
                titleCorrections = previous;
                renderSavedTitleFixes();
                updateSavedTitleFixesStatus('Unable to save exact title corrections: ' + (error && error.message ? error.message : String(error)));
                console.error('Radio Meta exact title correction save:', error);
            }).finally(function () {
                titleCorrectionsSaveInProgress = false;
                view.querySelectorAll('.radio-title-correction-remove').forEach(function (button) { button.disabled = false; });
            });
        }

        function loadTitleCleanupRules() {
            if (titleCleanupRequestInProgress) return;
            titleCleanupRequestInProgress = true;
            ApiClient.getJSON(ApiClient.getUrl('RadioMeta/TitleCleanup'))
                .then(function (data) {
                    var error = getValue(data, 'Error', 'error');
                    if (error) throw new Error(error);
                    titleCleanupRules = getValue(data, 'Rules', 'rules') || [];
                    titleCleanupRulesLoaded = true;
                    renderSavedTitleFixes();
                    updateSavedTitleFixesStatus();
                })
                .catch(function (error) {
                    titleCleanupRulesLoaded = true;
                    updateSavedTitleFixesStatus('Unable to load reusable title cleanup rules: ' + (error && error.message ? error.message : String(error)));
                    console.error('Radio Meta title cleanup load:', error);
                })
                .finally(function () { titleCleanupRequestInProgress = false; });
        }

        function saveTitleCleanupRules(nextRules, successMessage) {
            var status = view.querySelector('#radioTitleFixesStatus');
            if (!status || titleCleanupSaveInProgress) return;
            var previousRules = titleCleanupRules;
            titleCleanupSaveInProgress = true;
            view.querySelectorAll('.radio-title-cleanup-remove').forEach(function (button) { button.disabled = true; });
            status.textContent = 'Saving reusable title cleanup rules to the plugin XML...';

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/TitleCleanup'),
                data: JSON.stringify({ Rules: nextRules }),
                contentType: 'application/json'
            }).then(function (data) {
                var error = getValue(data, 'Error', 'error');
                if (error) throw new Error(error);
                titleCleanupRules = getValue(data, 'Rules', 'rules') || nextRules;
                renderSavedTitleFixes();
                updateSavedTitleFixesStatus(successMessage || getValue(data, 'Message', 'message') || 'Title cleanup rules saved.');
            }).catch(function (error) {
                titleCleanupRules = previousRules;
                renderSavedTitleFixes();
                updateSavedTitleFixesStatus('Unable to save title cleanup rules: ' + (error && error.message ? error.message : String(error)));
                console.error('Radio Meta title cleanup save:', error);
            }).finally(function () {
                titleCleanupSaveInProgress = false;
                view.querySelectorAll('.radio-title-cleanup-remove').forEach(function (button) { button.disabled = false; });
            });
        }

        function removeTitleCorrection(indexValue) {
            if (titleCorrectionsSaveInProgress) return;
            var index = Number(indexValue);
            if (!Number.isInteger(index) || index < 0 || index >= titleCorrections.length) return;
            var correction = titleCorrections[index];
            var original = String(getValue(correction, 'OriginalTitle', 'originalTitle') || '');
            if (!window.confirm('Remove this exact correction for "' + original + '"? This stops the rule from being applied to future metadata. Existing history titles are not automatically reverted.')) return;
            var next = titleCorrections.filter(function (_correction, correctionIndex) { return correctionIndex !== index; });
            saveTitleCorrections(next);
        }

        function removeTitleCleanupRule(indexValue) {
            if (titleCleanupSaveInProgress) return;
            var index = Number(indexValue);
            if (!Number.isInteger(index) || index < 0 || index >= titleCleanupRules.length) return;
            if (!window.confirm('Remove this reusable cleanup rule? Future metadata will no longer use it. Existing history titles are not automatically reverted.')) return;
            var nextRules = titleCleanupRules.filter(function (_rule, ruleIndex) { return ruleIndex !== index; });
            saveTitleCleanupRules(nextRules, 'Reusable title cleanup rule removed and saved.');
        }

        function setHistoryButtonsDisabled(disabled) {
            var clearButton = view.querySelector('#radioHistoryClear');
            var historyContainer = view.querySelector('#radioHistory');
            if (clearButton) {
                clearButton.disabled = disabled || !historyContainer || !historyContainer.querySelector('.radio-history-row');
            }
            view.querySelectorAll('.radio-history-delete, .radio-history-edit, .radio-history-edit-save, .radio-history-edit-cancel').forEach(function (button) {
                button.disabled = disabled;
            });
        }

        function deleteHistoryEntry(id) {
            if (historyActionInProgress || !id) {
                return;
            }
            if (activeHistoryEditId) closeHistoryEditor(activeHistoryEditId);
            if (!window.confirm('Delete this history entry? This cannot be undone.')) {
                return;
            }

            historyActionInProgress = true;
            setHistoryButtonsDisabled(true);
            var status = view.querySelector('#radioStatus');
            if (status) {
                status.textContent = 'Deleting history entry...';
            }

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/History/Delete'),
                data: JSON.stringify({ Id: id }),
                contentType: 'application/json'
            }).then(function (data) {
                var error = getValue(data, 'Error', 'error');
                if (error) {
                    if (status) status.textContent = error;
                    return;
                }
                if (status) status.textContent = getValue(data, 'Message', 'message') || 'History entry deleted.';
                historyReloadPending = true;
            }).catch(function (error) {
                if (status) {
                    status.textContent = 'Unable to delete history entry: ' +
                        (error && error.message ? error.message : String(error));
                }
                console.error('Radio Meta history deletion:', error);
            }).finally(function () {
                historyActionInProgress = false;
                setHistoryButtonsDisabled(false);
                if (historyReloadPending) {
                    historyReloadPending = false;
                }
                loadHistory();
            });
        }

        function clearHistory() {
            if (historyActionInProgress) {
                return;
            }
            if (activeHistoryEditId) closeHistoryEditor(activeHistoryEditId);
            var container = view.querySelector('#radioHistory');
            if (!container || !container.querySelector('.radio-history-row')) {
                return;
            }
            if (!window.confirm('Clear ALL Radio Meta history? This permanently deletes every recorded entry and cannot be undone.')) {
                return;
            }

            historyActionInProgress = true;
            setHistoryButtonsDisabled(true);
            var status = view.querySelector('#radioStatus');
            if (status) {
                status.textContent = 'Clearing all radio play history...';
            }

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/History/Clear'),
                data: JSON.stringify({}),
                contentType: 'application/json'
            }).then(function (data) {
                var error = getValue(data, 'Error', 'error');
                if (error) {
                    if (status) status.textContent = error;
                    return;
                }
                if (status) status.textContent = getValue(data, 'Message', 'message') || 'Radio play history cleared.';
                historyReloadPending = true;
            }).catch(function (error) {
                if (status) {
                    status.textContent = 'Unable to clear radio play history: ' +
                        (error && error.message ? error.message : String(error));
                }
                console.error('Radio Meta history clear:', error);
            }).finally(function () {
                historyActionInProgress = false;
                setHistoryButtonsDisabled(false);
                if (historyReloadPending) {
                    historyReloadPending = false;
                }
                loadHistory();
            });
        }

        function updateStationFolderShortcut(folder) {
            var display = view.querySelector('#radioStationFolderDisplay');
            var shortcut = view.querySelector('#radioStationFolderShortcut');
            if (!display) return;

            var configuredFolder = String(folder || '').trim();
            var fullDisplayText = configuredFolder || 'Automatic detection';
            // Limit the visible folder label to 40 characters, including the ellipsis.
            var displayText = fullDisplayText.length > 40
                ? fullDisplayText.slice(0, 39) + '\u2026'
                : fullDisplayText;
            display.textContent = displayText;
            display.title = configuredFolder || 'No Station Folder configured; active audio .strm paths are detected automatically.';
            if (shortcut) {
                shortcut.setAttribute('aria-label', configuredFolder
                    ? 'Configured station folder: ' + configuredFolder
                    : 'Station folder not configured; automatic detection is enabled');
            }
        }

        function focusStationFolderSetting() {
            var section = view.querySelector('#radioPathSettingsSection');
            var input = view.querySelector('#radioStationFolder');
            if (section && typeof section.scrollIntoView === 'function') {
                section.scrollIntoView({ block: 'center', behavior: 'auto' });
            }
            if (input) {
                input.focus();
                if (typeof input.select === 'function') input.select();
            }
        }

        function loadPathSettings() {
            var status = view.querySelector('#radioPathSettingsStatus');
            if (!status || pathSettingsRequestInProgress || pathSettingsSaveInProgress) {
                return;
            }

            pathSettingsRequestInProgress = true;
            status.textContent = 'Loading path settings...';
            ApiClient.getJSON(ApiClient.getUrl('RadioMeta/PathSettings'))
                .then(function (data) {
                    var error = getValue(data, 'Error', 'error');
                    if (error) {
                        status.textContent = error;
                        return;
                    }

                    var stationFolderInput = view.querySelector('#radioStationFolder');
                    var ffprobePathInput = view.querySelector('#radioFfprobePath');
                    if (stationFolderInput) {
                        stationFolderInput.value = String(getValue(data, 'StationFolder', 'stationFolder') || '');
                    }
                    updateStationFolderShortcut(getValue(data, 'StationFolder', 'stationFolder'));
                    if (ffprobePathInput) {
                        ffprobePathInput.value = String(getValue(data, 'FfprobePath', 'ffprobePath') || '');
                    }

                    var automatic = !!getValue(data, 'AutomaticStationDetection', 'automaticStationDetection');
                    var folderAvailable = !!getValue(data, 'StationFolderAvailable', 'stationFolderAvailable');
                    var ffprobeConfigured = String(getValue(data, 'FfprobePath', 'ffprobePath') || '').trim().length > 0;
                    var ffprobeAvailable = !!getValue(data, 'FfprobeAvailable', 'ffprobeAvailable');
                    var messages = [];

                    if (automatic) {
                        messages.push('Automatic station detection is enabled.');
                    } else if (folderAvailable) {
                        messages.push('Station folder found.');
                    } else {
                        messages.push('Station folder not found. Check the path or leave it blank for automatic detection.');
                    }

                    if (ffprobeAvailable) {
                        messages.push('FFprobe found.');
                    } else if (ffprobeConfigured) {
                        messages.push('FFprobe was not found at that path. Check it or leave blank to search PATH.');
                    } else {
                        messages.push('FFprobe path is blank; searching Windows PATH.');
                    }

                    status.textContent = messages.join(' ');
                })
                .catch(function (error) {
                    status.textContent = 'Unable to load path settings. Please check the connection and retry.';
                    updateStationFolderShortcut('');
                    var shortcutDisplay = view.querySelector('#radioStationFolderDisplay');
                    if (shortcutDisplay) {
                        shortcutDisplay.textContent = 'Unable to load';
                        shortcutDisplay.title = error && error.message ? error.message : String(error);
                    }
                    console.error('Radio Meta path settings load:', error);
                })
                .finally(function () {
                    pathSettingsRequestInProgress = false;
                });
        }

        function savePathSettings() {
            var status = view.querySelector('#radioPathSettingsStatus');
            var saveButton = view.querySelector('#radioPathSettingsSave');
            var stationFolderInput = view.querySelector('#radioStationFolder');
            var ffprobePathInput = view.querySelector('#radioFfprobePath');
            if (!status || !stationFolderInput || !ffprobePathInput || pathSettingsSaveInProgress) {
                return;
            }

            var stationFolder = stationFolderInput.value.trim();
            var ffprobePath = ffprobePathInput.value.trim();
            if (stationFolder.length > 1024 || ffprobePath.length > 1024) {
                status.textContent = 'Paths must be 1024 characters or fewer.';
                return;
            }

            pathSettingsSaveInProgress = true;
            if (saveButton) {
                saveButton.disabled = true;
            }
            stationFolderInput.disabled = true;
            ffprobePathInput.disabled = true;
            status.textContent = 'Saving path settings to the plugin XML...';

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/PathSettings'),
                data: JSON.stringify({
                    StationFolder: stationFolder,
                    FfprobePath: ffprobePath
                }),
                contentType: 'application/json'
            }).then(function (data) {
                var error = getValue(data, 'Error', 'error');
                if (error) {
                    status.textContent = error;
                    return;
                }

                stationFolderInput.value = String(getValue(data, 'StationFolder', 'stationFolder') || '');
                ffprobePathInput.value = String(getValue(data, 'FfprobePath', 'ffprobePath') || '');
                updateStationFolderShortcut(stationFolderInput.value);
                var message = String(getValue(data, 'Message', 'message') || 'Path settings saved to the plugin XML.');
                status.textContent = message + ' Changes apply on the next playback progress update.';
            }).catch(function (error) {
                status.textContent = 'Unable to save path settings: ' +
                    (error && error.message ? error.message : String(error));
                console.error('Radio Meta path settings save:', error);
            }).finally(function () {
                pathSettingsSaveInProgress = false;
                if (saveButton) {
                    saveButton.disabled = false;
                }
                stationFolderInput.disabled = false;
                ffprobePathInput.disabled = false;
            });
        }

        function onViewClick(event) {
            var target = event.target;
            if (!target || !target.closest) {
                return;
            }

            var historyEdit = target.closest('button[data-history-edit-id]');
            if (historyEdit) {
                openHistoryEditor(historyEdit.getAttribute('data-history-edit-id'));
                return;
            }

            var historyEditCancel = target.closest('button[data-history-edit-cancel-id]');
            if (historyEditCancel) {
                closeHistoryEditor(historyEditCancel.getAttribute('data-history-edit-cancel-id'));
                return;
            }

            var historyEditSave = target.closest('button[data-history-edit-save-id]');
            if (historyEditSave) {
                saveHistoryTitleCorrection(historyEditSave.getAttribute('data-history-edit-save-id'));
                return;
            }

            var historyDelete = target.closest('button[data-history-delete-id]');
            if (historyDelete) {
                deleteHistoryEntry(historyDelete.getAttribute('data-history-delete-id'));
                return;
            }

            var stationRemove = target.closest('button[data-station-mode-remove]');
            if (stationRemove) {
                removeStationModeRule(stationRemove.getAttribute('data-station-mode-remove'));
                return;
            }

            var exactCorrectionRemove = target.closest('button[data-title-correction-index]');
            if (exactCorrectionRemove) {
                removeTitleCorrection(exactCorrectionRemove.getAttribute('data-title-correction-index'));
                return;
            }

            var cleanupRemove = target.closest('button[data-title-cleanup-index]');
            if (cleanupRemove) {
                removeTitleCleanupRule(cleanupRemove.getAttribute('data-title-cleanup-index'));
                return;
            }

            var ignoreRemove = target.closest('button[data-ignore-index]');
            if (!ignoreRemove) {
                return;
            }

            var indexValue = ignoreRemove.getAttribute('data-ignore-index');
            var index = Number(indexValue);
            if (!Number.isInteger(index) || index < 0 || index >= ignoreRules.length) {
                return;
            }

            var nextRules = ignoreRules.filter(function (_rule, ruleIndex) {
                return ruleIndex !== index;
            });
            saveIgnoreRules(nextRules, 'Ignore phrase removed and saved.', false);
        }

        function onViewShow() {
            if (historyPanelVisible) loadHistory();
            loadStationModes();
            loadIgnorePhrases();
            loadTitleCleanupRules();
            loadTitleCorrections();
            loadPathSettings();

            if (refreshTimer === null) {
                refreshTimer = window.setInterval(function () {
                    if (historyPanelVisible) loadHistory();
                }, 20000);
            }
        }

        function onViewHide() {
            if (refreshTimer !== null) {
                window.clearInterval(refreshTimer);
                refreshTimer = null;
            }
        }

        function createStationFile() {
            var nameInput = view.querySelector('#radioStationCreateName');
            var urlInput = view.querySelector('#radioStationCreateUrl');
            var artworkInput = view.querySelector('#radioStationCreateArtworkUrl');
            var button = view.querySelector('#radioStationCreateButton');
            var status = view.querySelector('#radioStationCreateStatus');
            if (!nameInput || !urlInput || !artworkInput || !button || !status || button.disabled) return;
            var name = nameInput.value.trim();
            var streamUrl = urlInput.value.trim();
            var artworkUrl = artworkInput.value.trim();
            if (!name || !streamUrl) { status.textContent = 'Enter both a station name and a stream URL.'; return; }
            button.disabled = true;
            status.textContent = 'Creating station file...';
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/Stations/Create'),
                data: JSON.stringify({ Name: name, StreamUrl: streamUrl, ArtworkUrl: artworkUrl }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (data) {
                if (data && typeof data.json === 'function') return data.json();
                return data;
            }).then(function (data) {
                if (typeof data === 'string') { try { data = JSON.parse(data); } catch (ignore) {} }
                if (data && (data.Success === true || data.success === true)) {
                    var filePath = data.FilePath || data.filePath || '';
                    var artworkPath = data.ArtworkPath || data.artworkPath || '';
                    var logoWarning = String(getValue(data, 'Warning', 'warning') || '');
                    var createdMessage = 'Created ' + (data.Name || data.name || name) + '.strm' +
                        (filePath ? ': ' + filePath : '') +
                        (artworkPath ? '. Logo saved: ' + artworkPath : (artworkUrl ? '. Logo URL was supplied.' : '. No logo supplied.')) +
                        (logoWarning ? ' ' + logoWarning : '');
                    status.textContent = createdMessage + ' Resolving targeted Emby library refresh...';
                    nameInput.value = '';
                    urlInput.value = '';
                    artworkInput.value = '';
                    requestTargetedLibraryRefresh(data).then(function (scanResult) {
                        status.textContent = createdMessage + ' ' + scanResult.message;
                        scanStationFolder(scanResult.message);
                    });
                } else {
                    status.textContent = (data && (data.Error || data.error)) || 'The server did not confirm that the station file was created.';
                }
            }).catch(function (error) {
                status.textContent = 'Station creation failed: ' + (error && error.message ? error.message : String(error));
                console.error('Radio Meta station creation:', error);
            }).finally(function () { button.disabled = false; });
        }


        // On-demand diagnostic report. Read-only and designed to omit secrets.
        function diagnosticValue(object, pascalName, camelName) {
            if (!object) return '';
            if (object[pascalName] !== undefined) return object[pascalName];
            if (object[camelName] !== undefined) return object[camelName];
            return '';
        }

        function diagnosticText(value) {
            return String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
        }

        function diagnosticVisible(element) {
            if (!element || !element.isConnected) return false;
            var rect = element.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return false;
            for (var node = element; node && node.nodeType === 1; node = node.parentElement) {
                var style = window.getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity || 1) === 0) return false;
            }
            return true;
        }

        function diagnosticProviderValue(item, wantedName) {
            var providerIds = diagnosticValue(item, 'ProviderIds', 'providerIds');
            if (!providerIds || typeof providerIds !== 'object') return '';
            var wanted = String(wantedName || '').toLowerCase();
            var keys = Object.keys(providerIds);
            for (var i = 0; i < keys.length; i++) {
                if (keys[i].toLowerCase() === wanted) {
                    var value = providerIds[keys[i]];
                    if (typeof value === 'string' && value.trim()) return diagnosticText(value);
                }
            }
            return '';
        }

        function diagnosticNormalize(value) {
            return diagnosticText(value).toLowerCase();
        }

        function diagnosticContainsTrack(text, track, includeStation) {
            var normalized = diagnosticNormalize(text);
            if (!normalized || !track || !track.song || !track.artist) return false;
            if (normalized.indexOf(diagnosticNormalize(track.song)) < 0 ||
                normalized.indexOf(diagnosticNormalize(track.artist)) < 0) return false;
            if (includeStation && track.station &&
                normalized.indexOf(diagnosticNormalize(track.station)) < 0) return false;
            return true;
        }

        function diagnosticGetJson(path, timeoutMs) {
            return new Promise(function (resolve, reject) {
                var finished = false;
                var timeout = window.setTimeout(function () {
                    if (finished) return;
                    finished = true;
                    reject(new Error('Request timed out'));
                }, timeoutMs || 9000);
                try {
                    ApiClient.getJSON(ApiClient.getUrl(path)).then(function (result) {
                        if (finished) return;
                        finished = true;
                        window.clearTimeout(timeout);
                        resolve(result);
                    }).catch(function (error) {
                        if (finished) return;
                        finished = true;
                        window.clearTimeout(timeout);
                        reject(error || new Error('Request failed'));
                    });
                } catch (error) {
                    if (finished) return;
                    finished = true;
                    window.clearTimeout(timeout);
                    reject(error || new Error('Request failed'));
                }
            });
        }

        // Probe both known display-resource URLs automatically. The old __plugin
        // path is diagnostic-only; ConfigurationPage is the supported embedded-resource route.
        function diagnosticProbeDisplayResources(timeoutMs) {
            function probe(path) {
                return new Promise(function (resolve) {
                    var finished = false;
                    var timer = window.setTimeout(function () {
                        if (finished) return;
                        finished = true;
                        resolve({ path: path, ok: false, status: 0, contentType: '', bytes: 0, looksLikeModule: false, error: 'Request timed out' });
                    }, timeoutMs || 6000);
                    try {
                        window.fetch(path, { method: 'GET', credentials: 'same-origin', cache: 'no-store' })
                            .then(function (response) {
                                return response.text().then(function (body) {
                                    if (finished) return;
                                    finished = true;
                                    window.clearTimeout(timer);
                                    var contentType = response.headers.get('content-type') || '';
                                    resolve({
                                        path: path,
                                        ok: response.ok,
                                        status: response.status,
                                        contentType: contentType,
                                        bytes: body.length,
                                        looksLikeModule: /javascript|ecmascript/i.test(contentType) && /define\s*\(/.test(body),
                                        error: ''
                                    });
                                });
                            })
                            .catch(function (error) {
                                if (finished) return;
                                finished = true;
                                window.clearTimeout(timer);
                                resolve({ path: path, ok: false, status: 0, contentType: '', bytes: 0, looksLikeModule: false, error: String(error && error.message ? error.message : error || 'Request failed') });
                            });
                    } catch (error) {
                        if (finished) return;
                        finished = true;
                        window.clearTimeout(timer);
                        resolve({ path: path, ok: false, status: 0, contentType: '', bytes: 0, looksLikeModule: false, error: String(error && error.message ? error.message : error || 'Request failed') });
                    }
                });
            }
            return Promise.all([
                probe('/web/__plugin/RadioMetaIntegratedDisplay.js'),
                probe('/web/ConfigurationPage?name=RadioMetaIntegratedDisplay.js')
            ]).then(function (results) {
                return { legacy: results[0], embedded: results[1] };
            });
        }

        function diagnosticGetSessions(timeoutMs) {
            return new Promise(function (resolve, reject) {
                var finished = false;
                var timeout = window.setTimeout(function () {
                    if (finished) return;
                    finished = true;
                    reject(new Error('Request timed out'));
                }, timeoutMs || 9000);
                try {
                    if (!ApiClient || typeof ApiClient.getSessions !== 'function') {
                        throw new Error('Emby session API is not available in this page');
                    }
                    ApiClient.getSessions().then(function (result) {
                        if (finished) return;
                        finished = true;
                        window.clearTimeout(timeout);
                        resolve(Array.isArray(result) ? result : []);
                    }).catch(function (error) {
                        if (finished) return;
                        finished = true;
                        window.clearTimeout(timeout);
                        reject(error || new Error('Session request failed'));
                    });
                } catch (error) {
                    if (finished) return;
                    finished = true;
                    window.clearTimeout(timeout);
                    reject(error || new Error('Session request failed'));
                }
            });
        }

        function diagnosticAddCheck(checks, status, name, detail) {
            checks.push({ status: status, name: name, detail: String(detail || '') });
        }

        function diagnosticSetCopyButtonVisible(visible) {
            var button = view.querySelector('#radioDiagnosticsCopy');
            if (button) button.style.display = visible ? '' : 'none';
        }

        function diagnosticRender(checks, headerLines, recentEvents, performance) {
            recentEvents = Array.isArray(recentEvents) ? recentEvents : [];
            performance = performance && typeof performance === 'object' ? performance : null;
            var results = view.querySelector('#radioDiagnosticsResults');
            var summary = view.querySelector('#radioDiagnosticsSummary');
            var checkList = view.querySelector('#radioDiagnosticsChecks');
            var eventsSection = view.querySelector('#radioDiagnosticsEvents');
            var eventList = view.querySelector('#radioDiagnosticsEventList');
            var performanceSection = view.querySelector('#radioDiagnosticsPerformance');
            var performanceSummary = view.querySelector('#radioDiagnosticsPerformanceSummary');
            var performanceList = view.querySelector('#radioDiagnosticsPerformanceList');
            var report = view.querySelector('#radioDiagnosticsReport');
            var status = view.querySelector('#radioDiagnosticsStatus');
            var runButton = view.querySelector('#radioDiagnosticsRun');
            var copyButton = view.querySelector('#radioDiagnosticsCopy');
            if (!results || !summary || !checkList || !report || !status) return;

            var passCount = checks.filter(function (item) { return item.status === 'PASS'; }).length;
            var warnCount = checks.filter(function (item) { return item.status === 'WARN'; }).length;
            var failCount = checks.filter(function (item) { return item.status === 'FAIL'; }).length;
            var infoCount = checks.filter(function (item) { return item.status === 'INFO'; }).length;

            summary.textContent = failCount
                ? 'Diagnostics completed: ' + failCount + ' FAIL, ' + warnCount + ' WARN, ' + passCount + ' PASS, ' + infoCount + ' INFO. Start with the FAIL items.'
                : warnCount
                    ? 'Diagnostics completed: ' + warnCount + ' WARN, ' + passCount + ' PASS, no FAIL items, ' + infoCount + ' INFO. Review WARN items.'
                    : infoCount
                        ? 'Diagnostics completed: ' + passCount + ' checks passed; ' + infoCount + ' additional checks could not be verified in this page.'
                        : 'Diagnostics completed: all ' + passCount + ' checks passed.';

            while (checkList.firstChild) checkList.removeChild(checkList.firstChild);
            checks.forEach(function (item) {
                var row = document.createElement('div');
                row.className = 'radio-diagnostic-check';
                var state = document.createElement('span');
                state.className = 'radio-diagnostic-status ' + item.status.toLowerCase();
                state.textContent = item.status;
                var name = document.createElement('strong');
                name.textContent = item.name;
                var detail = document.createElement('span');
                detail.className = 'radio-diagnostic-detail';
                detail.textContent = item.detail;
                row.appendChild(state);
                row.appendChild(name);
                row.appendChild(detail);
                checkList.appendChild(row);
            });

            if (performanceSection && performanceSummary && performanceList) {
                while (performanceSummary.firstChild) performanceSummary.removeChild(performanceSummary.firstChild);
                while (performanceList.firstChild) performanceList.removeChild(performanceList.firstChild);
                var perfSamples = performance && Array.isArray(diagnosticValue(performance, 'RecentSamples', 'recentSamples'))
                    ? diagnosticValue(performance, 'RecentSamples', 'recentSamples') : [];
                if (performance) {
                    performanceSection.style.display = '';
                    var metricValues = [
                        ['Network lookups / uncached', (diagnosticValue(performance, 'NetworkLookupCount', 'networkLookupCount') || 0) + ' / ' + (diagnosticValue(performance, 'UncachedNetworkLookupCount', 'uncachedNetworkLookupCount') || 0)],
                        ['Avg / max uncached lookup', Number(diagnosticValue(performance, 'AverageNetworkLookupMs', 'averageNetworkLookupMs') || 0).toFixed(1) + ' / ' + Number(diagnosticValue(performance, 'MaxNetworkLookupMs', 'maxNetworkLookupMs') || 0).toFixed(1) + ' ms'],
                        ['Direct ICY reads', diagnosticValue(performance, 'DirectIcyReadCount', 'directIcyReadCount') || 0],
                        ['Avg ICY time / intervals', Number(diagnosticValue(performance, 'AverageIcyDurationMs', 'averageIcyDurationMs') || 0).toFixed(1) + ' ms / ' + Number(diagnosticValue(performance, 'AverageIcyIntervalsAttempted', 'averageIcyIntervalsAttempted') || 0).toFixed(1)],
                        ['FFprobe launches', (diagnosticValue(performance, 'FfprobeStartedCount', 'ffprobeStartedCount') || 0) + ' of ' + (diagnosticValue(performance, 'FfprobeAttemptCount', 'ffprobeAttemptCount') || 0) + ' attempts'],
                        ['FFprobe non-success outcomes', diagnosticValue(performance, 'FfprobeNonSuccessCount', 'ffprobeNonSuccessCount') || 0],
                        ['Avg / max FFprobe', Number(diagnosticValue(performance, 'AverageFfprobeDurationMs', 'averageFfprobeDurationMs') || 0).toFixed(1) + ' / ' + Number(diagnosticValue(performance, 'MaxFfprobeDurationMs', 'maxFfprobeDurationMs') || 0).toFixed(1) + ' ms']
                    ];
                    metricValues.forEach(function (metric) {
                        var card = document.createElement('div');
                        card.className = 'radio-diagnostic-performance-metric';
                        var label = document.createElement('span');
                        label.className = 'radio-diagnostic-performance-label';
                        label.textContent = metric[0];
                        var value = document.createElement('strong');
                        value.textContent = String(metric[1]);
                        card.appendChild(label);
                        card.appendChild(value);
                        performanceSummary.appendChild(card);
                    });
                    if (perfSamples.length) {
                        perfSamples.slice(0, 24).forEach(function (sample) {
                            var operation = String(diagnosticValue(sample, 'Operation', 'operation') || 'Operation');
                            var station = String(diagnosticValue(sample, 'StationName', 'stationName') || 'Unknown station');
                            var outcome = String(diagnosticValue(sample, 'Outcome', 'outcome') || 'unknown');
                            var timestamp = String(diagnosticValue(sample, 'TimestampUtc', 'timestampUtc') || '');
                            var row = document.createElement('article');
                            row.className = 'radio-diagnostic-performance-row' + (outcome === 'success' || outcome === 'title-found' ? ' performance-success' : outcome === 'timeout' || outcome === 'exception' || outcome.indexOf('error') >= 0 ? ' performance-warning' : '');
                            var heading = document.createElement('div');
                            heading.className = 'radio-diagnostic-performance-row-heading';
                            var title = document.createElement('strong');
                            title.textContent = operation + ' · ' + station;
                            var time = document.createElement('time');
                            time.textContent = timestamp ? timestamp.replace('T', ' ').replace(/\.\d+\+00:00$/, 'Z').replace('+00:00', 'Z') : 'Time unavailable';
                            heading.appendChild(title);
                            heading.appendChild(time);
                            row.appendChild(heading);
                            var details = document.createElement('div');
                            details.className = 'radio-diagnostic-performance-details';
                            var detailParts = ['duration=' + Number(diagnosticValue(sample, 'DurationMs', 'durationMs') || 0).toFixed(1) + ' ms', 'outcome=' + outcome];
                            if (operation === 'Network lookup') {
                                detailParts.push('Shoutcast=' + Number(diagnosticValue(sample, 'ShoutcastDurationMs', 'shoutcastDurationMs') || 0).toFixed(1) + ' ms');
                                if (diagnosticValue(sample, 'IcyAttempted', 'icyAttempted') === true) {
                                    detailParts.push('ICY=' + Number(diagnosticValue(sample, 'IcyDurationMs', 'icyDurationMs') || 0).toFixed(1) + ' ms');
                                    detailParts.push('intervals=' + Number(diagnosticValue(sample, 'IcyIntervalsAttempted', 'icyIntervalsAttempted') || 0));
                                } else {
                                    detailParts.push('ICY=not needed');
                                }
                                if (diagnosticValue(sample, 'CacheHit', 'cacheHit') === true) detailParts.push('cache hit');
                                var resultSource = String(diagnosticValue(sample, 'ResultSource', 'resultSource') || '');
                                if (resultSource) detailParts.push('result=' + resultSource);
                            } else if (operation === 'FFprobe process') {
                                detailParts.push('process started=' + (diagnosticValue(sample, 'ProcessStarted', 'processStarted') === true ? 'yes' : 'no'));
                            }
                            details.textContent = detailParts.join(' · ');
                            row.appendChild(details);
                            performanceList.appendChild(row);
                        });
                    } else {
                        var noSamples = document.createElement('p');
                        noSamples.className = 'radio-diagnostics-no-performance';
                        noSamples.textContent = 'No performance samples yet. Start a station and run diagnostics after a lookup occurs.';
                        performanceList.appendChild(noSamples);
                    }
                } else {
                    performanceSection.style.display = 'none';
                }
            }

            if (eventList && eventsSection) {
                while (eventList.firstChild) eventList.removeChild(eventList.firstChild);
                if (recentEvents.length) {
                    eventsSection.style.display = '';
                    recentEvents.slice(0, 40).forEach(function (entry) {
                        var item = function (pascal, camel) { return diagnosticValue(entry, pascal, camel); };
                        var eventStatus = String(item('Status', 'status') || 'info').toLowerCase().replace(/[^a-z0-9_-]/g, '');
                        var station = String(item('StationName', 'stationName') || 'Unknown station');
                        var source = String(item('Source', 'source') || 'Metadata reader');
                        var title = String(item('Title', 'title') || item('RawMetadata', 'rawMetadata') || '');
                        var artist = String(item('Artist', 'artist') || '');
                        var details = String(item('Details', 'details') || '');
                        var timestamp = String(item('TimestampUtc', 'timestampUtc') || '');
                        var row = document.createElement('article');
                        row.className = 'radio-diagnostic-event event-' + eventStatus;
                        var meta = document.createElement('div');
                        meta.className = 'radio-diagnostic-event-meta';
                        var statusText = document.createElement('span');
                        statusText.className = 'radio-diagnostic-event-status';
                        statusText.textContent = eventStatus || 'info';
                        var context = document.createElement('span');
                        context.textContent = station + ' · ' + source;
                        var time = document.createElement('time');
                        time.textContent = timestamp ? timestamp.replace('T', ' ').replace(/\.\d+\+00:00$/, 'Z').replace('+00:00', 'Z') : 'Time unavailable';
                        meta.appendChild(statusText);
                        meta.appendChild(context);
                        meta.appendChild(time);
                        row.appendChild(meta);
                        if (title || artist) {
                            var titleNode = document.createElement('div');
                            titleNode.className = 'radio-diagnostic-event-title';
                            titleNode.textContent = (artist ? artist + ' — ' : '') + title;
                            row.appendChild(titleNode);
                        }
                        if (details) {
                            var detailNode = document.createElement('div');
                            detailNode.className = 'radio-diagnostic-event-details';
                            detailNode.textContent = details;
                            row.appendChild(detailNode);
                        }
                        eventList.appendChild(row);
                    });
                } else {
                    eventsSection.style.display = '';
                    var noEvents = document.createElement('p');
                    noEvents.className = 'radio-diagnostic-no-events';
                    noEvents.textContent = 'No recent metadata-reader events are recorded. Start a radio station, wait for an update, then run diagnostics again.';
                    eventList.appendChild(noEvents);
                }
            }

            var reportLines = headerLines.slice();
            reportLines.push('');
            reportLines.push('SUMMARY: ' + passCount + ' PASS, ' + warnCount + ' WARN, ' + failCount + ' FAIL, ' + infoCount + ' INFO');
            reportLines.push('');
            checks.forEach(function (item) {
                reportLines.push('[' + item.status + '] ' + item.name + ': ' + item.detail);
            });
            reportLines.push('');
            reportLines.push('PERFORMANCE MEASUREMENTS (rolling in-memory window; up to 120 most-recent samples; resets on restart):');
            if (performance) {
                reportLines.push('  Retained samples: ' + (diagnosticValue(performance, 'RetainedSampleCount', 'retainedSampleCount') || 0));
                reportLines.push('  Network lookups: ' + (diagnosticValue(performance, 'NetworkLookupCount', 'networkLookupCount') || 0) +
                    '; cache hits: ' + (diagnosticValue(performance, 'NetworkCacheHitCount', 'networkCacheHitCount') || 0) +
                    '; uncached lookups: ' + (diagnosticValue(performance, 'UncachedNetworkLookupCount', 'uncachedNetworkLookupCount') || 0) +
                    '; average/max uncached: ' + Number(diagnosticValue(performance, 'AverageNetworkLookupMs', 'averageNetworkLookupMs') || 0).toFixed(1) + '/' + Number(diagnosticValue(performance, 'MaxNetworkLookupMs', 'maxNetworkLookupMs') || 0).toFixed(1) + ' ms');
                reportLines.push('  Direct ICY reads: ' + (diagnosticValue(performance, 'DirectIcyReadCount', 'directIcyReadCount') || 0) +
                    '; average duration: ' + Number(diagnosticValue(performance, 'AverageIcyDurationMs', 'averageIcyDurationMs') || 0).toFixed(1) + ' ms' +
                    '; average intervals attempted: ' + Number(diagnosticValue(performance, 'AverageIcyIntervalsAttempted', 'averageIcyIntervalsAttempted') || 0).toFixed(1));
                reportLines.push('  FFprobe attempts/process launches/non-success outcomes: ' + (diagnosticValue(performance, 'FfprobeAttemptCount', 'ffprobeAttemptCount') || 0) + '/' +
                    (diagnosticValue(performance, 'FfprobeStartedCount', 'ffprobeStartedCount') || 0) + '/' + (diagnosticValue(performance, 'FfprobeNonSuccessCount', 'ffprobeNonSuccessCount') || 0) +
                    '; average/max launched-process time: ' + Number(diagnosticValue(performance, 'AverageFfprobeDurationMs', 'averageFfprobeDurationMs') || 0).toFixed(1) + '/' + Number(diagnosticValue(performance, 'MaxFfprobeDurationMs', 'maxFfprobeDurationMs') || 0).toFixed(1) + ' ms');
                reportLines.push('  Recent samples (newest first):');
                (diagnosticValue(performance, 'RecentSamples', 'recentSamples') || []).slice(0, 24).forEach(function (sample) {
                    var op = String(diagnosticValue(sample, 'Operation', 'operation') || 'Operation');
                    var station = String(diagnosticValue(sample, 'StationName', 'stationName') || 'Unknown station');
                    var time = String(diagnosticValue(sample, 'TimestampUtc', 'timestampUtc') || 'unknown time');
                    var duration = Number(diagnosticValue(sample, 'DurationMs', 'durationMs') || 0).toFixed(1);
                    var outcome = String(diagnosticValue(sample, 'Outcome', 'outcome') || 'unknown');
                    var extras = [];
                    if (op === 'Network lookup') {
                        extras.push('Shoutcast=' + Number(diagnosticValue(sample, 'ShoutcastDurationMs', 'shoutcastDurationMs') || 0).toFixed(1) + 'ms');
                        if (diagnosticValue(sample, 'IcyAttempted', 'icyAttempted') === true) {
                            extras.push('ICY=' + Number(diagnosticValue(sample, 'IcyDurationMs', 'icyDurationMs') || 0).toFixed(1) + 'ms');
                            extras.push('intervals=' + Number(diagnosticValue(sample, 'IcyIntervalsAttempted', 'icyIntervalsAttempted') || 0));
                        } else extras.push('ICY not needed');
                        if (diagnosticValue(sample, 'CacheHit', 'cacheHit') === true) extras.push('cache hit');
                        var source = String(diagnosticValue(sample, 'ResultSource', 'resultSource') || '');
                        if (source) extras.push('result=' + source);
                    } else if (op === 'FFprobe process') {
                        extras.push('process started=' + (diagnosticValue(sample, 'ProcessStarted', 'processStarted') === true ? 'yes' : 'no'));
                    }
                    reportLines.push('    [' + time + '] ' + op + ' | ' + station + ' | ' + duration + 'ms | ' + outcome + (extras.length ? ' | ' + extras.join('; ') : ''));
                });
            } else {
                reportLines.push('  Performance measurements are unavailable in this plugin build.');
            }
            reportLines.push('');
            reportLines.push('RECENT METADATA ACTIVITY (newest first; titles are marked for visibility):');
            if (recentEvents.length) {
                recentEvents.slice(0, 40).forEach(function (entry) {
                    var status = String(diagnosticValue(entry, 'Status', 'status') || 'info').toUpperCase();
                    var station = String(diagnosticValue(entry, 'StationName', 'stationName') || 'Unknown station');
                    var source = String(diagnosticValue(entry, 'Source', 'source') || 'Metadata reader');
                    var title = String(diagnosticValue(entry, 'Title', 'title') || diagnosticValue(entry, 'RawMetadata', 'rawMetadata') || '');
                    var artist = String(diagnosticValue(entry, 'Artist', 'artist') || '');
                    var detail = String(diagnosticValue(entry, 'Details', 'details') || '');
                    var timestamp = String(diagnosticValue(entry, 'TimestampUtc', 'timestampUtc') || 'unknown time');
                    var displayedTitle = (artist ? artist + ' — ' : '') + title;
                    reportLines.push('[' + timestamp + '] [' + status + '] ' + station + ' / ' + source +
                        (displayedTitle ? '  >>> TITLE: ' + displayedTitle + ' <<<' : '') +
                        (detail ? ' — ' + detail : ''));
                });
            } else {
                reportLines.push('No recent metadata-reader events were recorded.');
            }
            reportLines.push('');
            reportLines.push('Privacy: report excludes stream URLs, query tokens, access tokens, filesystem paths, session IDs, and device IDs.');
            report.textContent = reportLines.join('\n');
            results.style.display = '';
            diagnosticSetCopyButtonVisible(true);
            status.textContent = failCount
                ? 'Diagnostics finished. One or more checks need attention.'
                : warnCount
                    ? 'Diagnostics finished. Some items could not be verified in this page.'
                    : 'Diagnostics finished successfully.';
            if (runButton) {
                runButton.disabled = false;
                runButton.innerHTML = '<span>Run diagnostics again</span>';
            }
            if (copyButton) copyButton.disabled = false;
        }

        function runRadioMetaDiagnostics() {
            var runButton = view.querySelector('#radioDiagnosticsRun');
            var statusElement = view.querySelector('#radioDiagnosticsStatus');
            var resultsElement = view.querySelector('#radioDiagnosticsResults');
            if (!runButton || !statusElement || runButton.disabled) return;

            runButton.disabled = true;
            runButton.innerHTML = '<span>Running diagnostics...</span>';
            statusElement.textContent = 'Checking plugin, metadata, Emby sessions, and visible player elements...';
            if (resultsElement) resultsElement.style.display = 'none';
            diagnosticSetCopyButtonVisible(false);

            var checks = [];
            var startedAt = new Date();
            var helperKey = '__radioMetaIntegratedDisplay_v1323';
            var loaderKey = '__radioMetaDisplayLoaderState_v1366';
            var helperLoaded = window[helperKey] === true;
            var loaderState = String(window[loaderKey] || 'not reported');
            var loaderErrorPresent = !!window.__radioMetaDisplayLoaderError;
            var pagePath = String(window.location && window.location.pathname || '/');
            var userAgent = String(window.navigator && window.navigator.userAgent || 'unknown');
            var localDeviceId = '';
            try {
                if (ApiClient && typeof ApiClient.deviceId === 'function') localDeviceId = String(ApiClient.deviceId() || '');
            } catch (_) {}

            Promise.all([
                diagnosticGetJson('RadioMeta/Diagnostics/Health', 9000).then(function (value) {
                    return { ok: true, value: value };
                }).catch(function (error) {
                    return { ok: false, error: error };
                }),
                diagnosticGetJson('RadioMeta/CurrentTracks', 9000).then(function (value) {
                    return { ok: true, value: value };
                }).catch(function (error) {
                    return { ok: false, error: error };
                }),
                diagnosticGetJson('RadioMeta/PathSettings', 9000).then(function (value) {
                    return { ok: true, value: value };
                }).catch(function (error) {
                    return { ok: false, error: error };
                }),
                diagnosticGetSessions(9000).then(function (value) {
                    return { ok: true, value: value };
                }).catch(function (error) {
                    return { ok: false, error: error };
                }),
                diagnosticGetJson('System/Info/Public', 9000).then(function (value) {
                    return { ok: true, value: value };
                }).catch(function (error) {
                    return { ok: false, error: error };
                }),
                diagnosticProbeDisplayResources(6000).then(function (value) {
                    return { ok: true, value: value };
                }).catch(function (error) {
                    return { ok: false, error: error };
                })
            ]).then(function (responses) {
                var healthResult = responses[0];
                var tracksResult = responses[1];
                var pathsResult = responses[2];
                var sessionsResult = responses[3];
                var serverInfoResult = responses[4];
                var displayProbeResult = responses[5];
                var displayProbe = displayProbeResult.ok ? displayProbeResult.value : null;
                var serverInfo = serverInfoResult.ok ? (serverInfoResult.value || {}) : null;
                var health = healthResult.ok ? (healthResult.value || {}) : null;
                var currentTrackData = tracksResult.ok ? (tracksResult.value || {}) : null;
                var pathData = pathsResult.ok ? (pathsResult.value || {}) : null;
                var sessions = sessionsResult.ok ? sessionsResult.value : [];
                var currentTracks = currentTrackData ? (diagnosticValue(currentTrackData, 'Items', 'items') || []) : [];
                var activeRadioSessions = [];

                sessions.forEach(function (session) {
                    var item = diagnosticValue(session, 'NowPlayingItem', 'nowPlayingItem');
                    if (!item) return;
                    var song = diagnosticProviderValue(item, 'RadioMetaSong');
                    var artist = diagnosticProviderValue(item, 'RadioMetaArtist');
                    var station = diagnosticProviderValue(item, 'RadioMetaStation');
                    if (song || artist || station) {
                        activeRadioSessions.push({ session: session, item: item, song: song, artist: artist, station: station });
                    }
                });

                var localSession = null;
                if (localDeviceId) {
                    localSession = sessions.find(function (session) {
                        return String(diagnosticValue(session, 'DeviceId', 'deviceId') || '') === localDeviceId;
                    }) || null;
                }
                var localRadioSession = localSession && activeRadioSessions.find(function (entry) {
                    return entry.session === localSession;
                });
                var expectedTrack = localRadioSession ? {
                    song: localRadioSession.song,
                    artist: localRadioSession.artist,
                    station: localRadioSession.station
                } : null;

                if (!healthResult.ok || !health || diagnosticValue(health, 'Available', 'available') !== true) {
                    diagnosticAddCheck(checks, 'FAIL', 'Plugin diagnostic endpoint',
                        'The Radio Meta diagnostic endpoint did not respond. Build/install the current diagnostics update and restart Emby.');
                } else {
                    var pluginVersion = String(diagnosticValue(health, 'PluginVersion', 'pluginVersion') || 'unknown');
                    diagnosticAddCheck(checks, pluginVersion.indexOf('1.3.74') === 0 ? 'PASS' : 'WARN',
                        'Plugin version',
                        'Server reports Radio Meta ' + pluginVersion +
                            (pluginVersion.indexOf('1.3.74') === 0 ? '.' : '; expected v1.3.74 for the Current Playback stream test and performance measurement panel.'));
                    var readerLimit = Number(diagnosticValue(health, 'DirectIcyMaxIntervals', 'directIcyMaxIntervals') || 0);
                    diagnosticAddCheck(checks, 'INFO', 'Direct ICY sampling limit',
                        readerLimit > 0
                            ? 'The direct ICY reader examines at most ' + readerLimit + ' metadata intervals per attempt before the FFprobe fallback. The independent portable diagnostic can capture for much longer.'
                            : 'The direct ICY sampling limit was not reported by this plugin build.');
                    var recentReaderEvents = diagnosticValue(health, 'RecentEvents', 'recentEvents') || [];
                    diagnosticAddCheck(checks, recentReaderEvents.length ? 'INFO' : 'WARN', 'Recent metadata reader trace',
                        recentReaderEvents.length
                            ? recentReaderEvents.length + ' in-memory reader event(s) are available below, including detected/filtered titles and reader errors.'
                            : 'No reader events are recorded yet. Start radio playback and rerun diagnostics after a few seconds.');
                    var perf = diagnosticValue(health, 'Performance', 'performance') || null;
                    if (perf) {
                        var networkCount = Number(diagnosticValue(perf, 'NetworkLookupCount', 'networkLookupCount') || 0);
                        var averageNetworkMs = Number(diagnosticValue(perf, 'AverageNetworkLookupMs', 'averageNetworkLookupMs') || 0);
                        var maxNetworkMs = Number(diagnosticValue(perf, 'MaxNetworkLookupMs', 'maxNetworkLookupMs') || 0);
                        var ffprobeStarted = Number(diagnosticValue(perf, 'FfprobeStartedCount', 'ffprobeStartedCount') || 0);
                        var ffprobeAverage = Number(diagnosticValue(perf, 'AverageFfprobeDurationMs', 'averageFfprobeDurationMs') || 0);
                        diagnosticAddCheck(checks, 'INFO', 'Performance measurements',
                            'Rolling in-memory window: ' + networkCount + ' network lookup(s), average uncached lookup ' + averageNetworkMs.toFixed(1) + ' ms, maximum ' + maxNetworkMs.toFixed(1) + ' ms; ' + ffprobeStarted + ' FFprobe process launch(es), average ' + ffprobeAverage.toFixed(1) + ' ms.');
                    } else {
                        diagnosticAddCheck(checks, 'INFO', 'Performance measurements', 'No performance measurements are available in this server response.');
                    }
                    diagnosticAddCheck(checks, 
                        diagnosticValue(health, 'ConfigurationAvailable', 'configurationAvailable') === true ? 'PASS' : 'FAIL',
                        'Plugin configuration',
                        diagnosticValue(health, 'ConfigurationAvailable', 'configurationAvailable') === true
                            ? 'Plugin configuration is available.'
                            : 'Configuration was not available to the loaded plugin.'
                    );
                    diagnosticAddCheck(checks, 
                        diagnosticValue(health, 'DisplayHelperResourceEmbedded', 'displayHelperResourceEmbedded') === true ? 'PASS' : 'FAIL',
                        'Embedded display helper',
                        diagnosticValue(health, 'DisplayHelperResourceEmbedded', 'displayHelperResourceEmbedded') === true
                            ? 'The display JavaScript resource is present inside the plugin DLL.'
                            : 'The expected embedded JavaScript resource is missing from the loaded DLL.'
                    );
                    diagnosticAddCheck(checks, 
                        diagnosticValue(health, 'HistoryStoreInitialized', 'historyStoreInitialized') === true ? 'PASS' : 'WARN',
                        'History service',
                        diagnosticValue(health, 'HistoryStoreInitialized', 'historyStoreInitialized') === true
                            ? 'History store initialized.'
                            : 'History store is not initialized; check plugin startup logs.'
                    );
                    var autoFolder = diagnosticValue(health, 'AutomaticStationDetection', 'automaticStationDetection') === true;
                    var folderConfigured = diagnosticValue(health, 'StationFolderConfigured', 'stationFolderConfigured') === true;
                    var folderAvailable = diagnosticValue(health, 'StationFolderAvailable', 'stationFolderAvailable') === true;
                    diagnosticAddCheck(checks, 
                        autoFolder || (folderConfigured && folderAvailable) ? 'PASS' : 'FAIL',
                        'Station folder',
                        autoFolder
                            ? 'Automatic active-station detection is enabled; no fixed folder is required.'
                            : folderAvailable
                                ? 'Configured station folder is available to the Emby process.'
                                : 'Configured station folder was not found by the Emby process. Correct the path or enable automatic detection.'
                    );
                    diagnosticAddCheck(checks, 
                        diagnosticValue(health, 'FfprobeAvailable', 'ffprobeAvailable') === true ? 'PASS' : 'WARN',
                        'FFprobe',
                        diagnosticValue(health, 'FfprobeAvailable', 'ffprobeAvailable') === true
                            ? 'FFprobe is available to the server.'
                            : 'FFprobe was not found. It is optional; metadata sources that do not need it can still work.'
                    );
                    diagnosticAddCheck(checks, 
                        diagnosticValue(health, 'CurrentlyPlayingStationDetected', 'currentlyPlayingStationDetected') === true ? 'PASS' : 'WARN',
                        'Playback station detection',
                        diagnosticValue(health, 'CurrentlyPlayingStationDetected', 'currentlyPlayingStationDetected') === true
                            ? 'Radio Meta observed station "' + (diagnosticValue(health, 'CurrentlyPlayingStationName', 'currentlyPlayingStationName') || 'unnamed station') +
                                '". Stream URL available: ' +
                                (diagnosticValue(health, 'CurrentlyPlayingStreamUrlAvailable', 'currentlyPlayingStreamUrlAvailable') === true ? 'yes' : 'no') + '.'
                            : 'No recent station capture. Start radio in an Emby client, wait several seconds, then run diagnostics again.'
                    );
                    var trackCount = Number(diagnosticValue(health, 'CurrentTrackCount', 'currentTrackCount') || 0);
                    diagnosticAddCheck(checks, trackCount > 0 ? 'PASS' : 'WARN',
                        'Accepted metadata cache',
                        trackCount > 0
                            ? trackCount + ' accepted current-track record(s) are cached.'
                            : 'No accepted track metadata is cached right now. Start a station and wait for a song title and artist.');
                }

                if (serverInfoResult.ok && serverInfo) {
                    var embyVersion = String(diagnosticValue(serverInfo, 'Version', 'version') || 'unknown');
                    diagnosticAddCheck(checks, 'PASS', 'Emby server version', 'Emby reports version ' + embyVersion + '.');
                } else {
                    diagnosticAddCheck(checks, 'WARN', 'Emby server version', 'Could not read System/Info/Public; other checks may still be valid.');
                }

                if (displayProbe) {
                    var embeddedProbe = displayProbe.embedded || {};
                    var legacyProbe = displayProbe.legacy || {};
                    var embeddedOk = embeddedProbe.status === 200 && embeddedProbe.looksLikeModule;
                    diagnosticAddCheck(checks, embeddedOk ? 'PASS' : 'FAIL', 'Embedded display resource URL',
                        embeddedOk
                            ? 'ConfigurationPage resource returned HTTP 200 with JavaScript module content (' + embeddedProbe.bytes + ' characters).'
                            : 'ConfigurationPage resource did not return a recognizable JavaScript module. HTTP ' +
                                (embeddedProbe.status || 'request error') + ', content type=' + (embeddedProbe.contentType || '(none)') +
                                (embeddedProbe.error ? ', error=' + embeddedProbe.error : '') + '. Verify the DLL resource and Emby web route.');
                    diagnosticAddCheck(checks, legacyProbe.status === 404 ? 'INFO' : (legacyProbe.status === 200 ? 'INFO' : 'WARN'),
                        'Legacy display resource URL',
                        legacyProbe.status === 404
                            ? 'The old /web/__plugin/ URL returns 404; this is expected for the current embedded-resource loader and is not the URL the helper should use.'
                            : 'Legacy URL HTTP ' + (legacyProbe.status || 'request error') +
                                (legacyProbe.contentType ? ', content type=' + legacyProbe.contentType : '') +
                                (legacyProbe.error ? ', error=' + legacyProbe.error : '') + '.');
                } else {
                    diagnosticAddCheck(checks, 'WARN', 'Display resource URL probe',
                        'Automatic fetch checks could not complete. Inspect browser network errors and verify the Emby web resource route.');
                }

                if (tracksResult.ok && currentTrackData && !diagnosticValue(currentTrackData, 'Error', 'error')) {
                    diagnosticAddCheck(checks, 'PASS', 'Current-track API',
                        'The Radio Meta CurrentTracks endpoint responded (' + currentTracks.length + ' record(s)).');
                } else {
                    diagnosticAddCheck(checks, 'FAIL', 'Current-track API',
                        'Could not read Radio Meta CurrentTracks. Check that the plugin is loaded and review server logs.');
                }

                if (pathsResult.ok && pathData && !diagnosticValue(pathData, 'Error', 'error')) {
                    diagnosticAddCheck(checks, 'PASS', 'Settings API',
                        'The station-folder and FFprobe settings endpoint responded; path values are intentionally omitted from this report.');
                } else {
                    diagnosticAddCheck(checks, 'WARN', 'Settings API',
                        'The settings endpoint could not be checked. This is separate from current song metadata processing.');
                }

                if (sessionsResult.ok) {
                    diagnosticAddCheck(checks, 'PASS', 'Emby session API',
                        'The browser can read Emby sessions (' + sessions.length + ' session(s) visible).');
                    if (activeRadioSessions.length) {
                        diagnosticAddCheck(checks, 'PASS', 'Radio Meta session metadata',
                            activeRadioSessions.length + ' active session(s) expose at least one RadioMeta ProviderId.');
                    } else {
                        diagnosticAddCheck(checks, 'WARN', 'Radio Meta session metadata',
                            'No active session exposes RadioMetaSong/Artist/Station right now. Start a radio station and rerun.');
                    }
                } else {
                    diagnosticAddCheck(checks, 'FAIL', 'Emby session API',
                        'The browser could not read Emby sessions. Refresh Emby Web, sign in again if needed, and rerun diagnostics.');
                }

                if (localRadioSession) {
                    expectedTrack = { song: localRadioSession.song, artist: localRadioSession.artist, station: localRadioSession.station };
                    diagnosticAddCheck(checks, 
                        expectedTrack.song && expectedTrack.artist && expectedTrack.station ? 'PASS' : 'WARN',
                        'This browser session metadata',
                        String(diagnosticValue(localSession, 'Client', 'client') || 'Emby client') + ' / ' +
                            String(diagnosticValue(localSession, 'DeviceName', 'deviceName') || 'this browser') +
                            ': song=' + (expectedTrack.song || '(missing)') +
                            '; artist=' + (expectedTrack.artist || '(missing)') +
                            '; station=' + (expectedTrack.station || '(missing)') + '.'
                    );
                } else if (localSession) {
                    var localItem = diagnosticValue(localSession, 'NowPlayingItem', 'nowPlayingItem');
                    diagnosticAddCheck(checks, 'WARN', 'This browser session metadata',
                        localItem
                            ? 'This browser has a playing item, but its session has no Radio Meta provider IDs. The station may not have been accepted by the plugin yet.'
                            : 'This browser has no active Now Playing item. Start a Radio Meta station in this browser to test its local player.');
                } else {
                    diagnosticAddCheck(checks, 'WARN', 'This browser session metadata',
                        'Could not match this browser to an Emby device session. Device IDs are not included in the report.');
                }

                var cacheCompareEntry = localRadioSession || (activeRadioSessions.length === 1 ? activeRadioSessions[0] : null);
                var acceptedCacheTracks = health ? (diagnosticValue(health, 'CurrentTracks', 'currentTracks') || []) : [];
                if (cacheCompareEntry && acceptedCacheTracks.length) {
                    var sessionSongKey = diagnosticNormalize(cacheCompareEntry.song);
                    var sessionArtistKey = diagnosticNormalize(cacheCompareEntry.artist);
                    var sessionStationKey = diagnosticNormalize(cacheCompareEntry.station);
                    var cacheMatch = acceptedCacheTracks.find(function (track) {
                        var cachedSong = diagnosticNormalize(diagnosticValue(track, 'SongTitle', 'songTitle'));
                        var cachedArtist = diagnosticNormalize(diagnosticValue(track, 'Artist', 'artist'));
                        var cachedStation = diagnosticNormalize(diagnosticValue(track, 'StationName', 'stationName'));
                        return cachedSong === sessionSongKey &&
                            cachedArtist === sessionArtistKey &&
                            (!sessionStationKey || !cachedStation || cachedStation === sessionStationKey);
                    });
                    diagnosticAddCheck(checks, cacheMatch ? 'PASS' : 'WARN',
                        'Plugin cache vs session',
                        cacheMatch
                            ? 'The accepted plugin track cache agrees with the session song and artist.'
                            : 'The plugin accepted-track cache does not currently match this session metadata. This may be a track-transition timing difference; wait a few seconds and rerun if the mismatch persists.');
                } else {
                    diagnosticAddCheck(checks, 'INFO', 'Plugin cache vs session',
                        'A session could not be matched to an accepted cache entry for a direct comparison.');
                }

                diagnosticAddCheck(checks, 
                    helperLoaded ? 'PASS' : (loaderState === 'failed' || loaderErrorPresent ? 'FAIL' : 'WARN'),
                    'Display helper loaded',
                    helperLoaded
                        ? 'The embedded display helper registered in this browser.'
                        : 'Helper flag is absent; loader state=' + loaderState +
                            (loaderErrorPresent ? '; a loader error was recorded.' : '') +
                            '. Hard-refresh Emby Web; if it persists, verify the Web loader and embedded resource.'
                );
                diagnosticAddCheck(checks, 
                    loaderState === 'loaded' ? 'PASS' : (loaderState === 'failed' ? 'FAIL' : 'WARN'),
                    'Web loader',
                    loaderState === 'loaded'
                        ? 'The nowplayingbar.js loader reports that its module import completed.'
                        : 'Loader state is "' + loaderState + '". Verify the loader installation and browser console.'
                );

                var miniTargets = Array.prototype.slice.call(document.querySelectorAll('.nowPlayingBarText, .nowplayingbar-textitems'))
                    .filter(diagnosticVisible);
                var miniTarget = miniTargets.length ? miniTargets[0] : null;
                var miniText = miniTarget ? diagnosticText(miniTarget.innerText || miniTarget.textContent || '') : '';
                if (!miniTarget) {
                    diagnosticAddCheck(checks, 'WARN', 'Mini-player display',
                        'No visible mini-player text element exists in this page. Start playback in this browser and rerun diagnostics.');
                } else if (expectedTrack && expectedTrack.song && expectedTrack.artist) {
                    var miniOk = diagnosticContainsTrack(miniText, expectedTrack, false);
                    diagnosticAddCheck(checks, miniOk ? 'PASS' : 'FAIL', 'Mini-player display',
                        miniOk
                            ? 'Visible mini-player text contains the current song and artist.'
                            : 'Visible mini-player text does not match this browser session. Expected song="' +
                                expectedTrack.song + '", artist="' + expectedTrack.artist + '"; observed text="' +
                                miniText.slice(0, 180) + '". Hard-refresh first; if this persists, report the mismatch.');
                } else {
                    diagnosticAddCheck(checks, 'WARN', 'Mini-player display',
                        'Visible mini-player text="' + miniText.slice(0, 180) +
                            '". Automatic comparison needs a Radio Meta session matched to this browser.');
                }

                var largeTargets = Array.prototype.slice.call(document.querySelectorAll(
                    'h3.videoOsdParentTitle.videoOsdParentTitle-small, h3.videoOsdParentTitle, h1.videoOsdParentTitleLarge, h1.videoOsdParentTitle, #videoOsdParentTitleLarge'
                )).filter(diagnosticVisible);
                if (!largeTargets.length) {
                    diagnosticAddCheck(checks, 'INFO', 'Big-player display',
                        'Big-player title is not visible in this page. Open the big player and rerun diagnostics to verify its rendered text.');
                } else if (expectedTrack && expectedTrack.song && expectedTrack.artist) {
                    var largeText = diagnosticText(largeTargets[0].innerText || largeTargets[0].textContent || '');
                    var largeOk = diagnosticContainsTrack(largeText, expectedTrack, !!expectedTrack.station);
                    diagnosticAddCheck(checks, largeOk ? 'PASS' : 'FAIL', 'Big-player display',
                        largeOk ? 'Visible big-player title contains the song, artist, and station.'
                            : 'Big-player title does not contain all expected metadata. Observed text="' + largeText.slice(0, 180) + '".');
                } else {
                    diagnosticAddCheck(checks, 'INFO', 'Big-player display',
                        'A big-player title is visible, but a matching local Radio Meta session was not identified.');
                }

                var dashboardTargets = Array.prototype.slice.call(document.querySelectorAll(
                    '.cardText.cardText-first[data-rnp-dashboard-title="song-artist-station"]'
                )).filter(diagnosticVisible);
                if (!dashboardTargets.length) {
                    diagnosticAddCheck(checks, 'INFO', 'Dashboard Now Playing display',
                        'No visible Radio Meta dashboard card is present in this plugin settings view. This visual check is available only when the relevant Dashboard Now Playing card is rendered in this page.');
                } else if (expectedTrack && expectedTrack.song && expectedTrack.artist) {
                    var cardText = diagnosticText(dashboardTargets[0].innerText || dashboardTargets[0].textContent || '');
                    var cardOk = diagnosticContainsTrack(cardText, expectedTrack, !!expectedTrack.station);
                    diagnosticAddCheck(checks, cardOk ? 'PASS' : 'FAIL', 'Dashboard Now Playing display',
                        cardOk ? 'Visible dashboard card contains the song, artist, and station.'
                            : 'Dashboard card does not contain all expected metadata. Observed text="' + cardText.slice(0, 180) + '".');
                } else {
                    diagnosticAddCheck(checks, 'INFO', 'Dashboard Now Playing display',
                        'A marked dashboard card is visible, but it cannot be compared without a matching local Radio Meta session.');
                }

                if (!window.navigator || !('mediaSession' in window.navigator) || !window.navigator.mediaSession) {
                    diagnosticAddCheck(checks, 'WARN', 'Browser Media Session',
                        'This browser does not expose navigator.mediaSession. Lock Screen/Control Center metadata cannot be inspected here.');
                } else {
                    var mediaMetadata = window.navigator.mediaSession.metadata;
                    var mediaTitle = mediaMetadata ? String(mediaMetadata.title || '') : '';
                    var mediaArtist = mediaMetadata ? String(mediaMetadata.artist || '') : '';
                    if (!mediaMetadata) {
                        diagnosticAddCheck(checks, 'WARN', 'Browser Media Session',
                            'The browser has no Media Session metadata at the moment. Start radio playback in this browser and rerun.');
                    } else if (expectedTrack && expectedTrack.song && expectedTrack.artist) {
                        var mediaOk = diagnosticNormalize(mediaTitle) === diagnosticNormalize(expectedTrack.song) &&
                            diagnosticNormalize(mediaArtist) === diagnosticNormalize(expectedTrack.artist);
                        diagnosticAddCheck(checks, mediaOk ? 'PASS' : 'FAIL', 'Browser Media Session',
                            mediaOk ? 'Media Session title and artist match the current browser session.'
                                : 'Media Session mismatch. Expected "' + expectedTrack.song + '" by "' + expectedTrack.artist +
                                    '"; reported "' + mediaTitle + '" by "' + mediaArtist + '".');
                    } else {
                        diagnosticAddCheck(checks, 'INFO', 'Browser Media Session',
                            'Current Media Session title="' + mediaTitle + '", artist="' + mediaArtist +
                                '". Comparison requires a matching local Radio Meta session.');
                    }
                }

                var headerLines = [
                    'RADIO META DIAGNOSTIC REPORT',
                    'Generated (UTC): ' + startedAt.toISOString(),
                    'Emby server version: ' + (serverInfo ? String(diagnosticValue(serverInfo, 'Version', 'version') || 'unknown') : 'unknown'),
                    'Plugin version (reported): ' + (health ? String(diagnosticValue(health, 'PluginVersion', 'pluginVersion') || 'unknown') : 'health endpoint unavailable'),
                    'Page path: ' + pagePath,
                    'Browser: ' + userAgent,
                    'Loader state: ' + loaderState,
                    'Loader error recorded: ' + (loaderErrorPresent ? String(window.__radioMetaDisplayLoaderError || 'yes').slice(0, 240) : 'no'),
                    'Display helper global present: ' + (helperLoaded ? 'yes' : 'no'),
                    'Current accepted tracks: ' + (health ? String(diagnosticValue(health, 'CurrentTrackCount', 'currentTrackCount') || 0) : 'unknown'),
                    'Radio Meta-bearing Emby sessions: ' + activeRadioSessions.length
                ];

                if (displayProbe) {
                    var legacyReportProbe = displayProbe.legacy || {};
                    var embeddedReportProbe = displayProbe.embedded || {};
                    headerLines.push('Legacy display URL probe: HTTP ' + (legacyReportProbe.status || 'request error') +
                        '; content type=' + (legacyReportProbe.contentType || '(none)') + '; bytes=' + (legacyReportProbe.bytes || 0));
                    headerLines.push('Embedded display URL probe: HTTP ' + (embeddedReportProbe.status || 'request error') +
                        '; content type=' + (embeddedReportProbe.contentType || '(none)') + '; bytes=' + (embeddedReportProbe.bytes || 0) +
                        '; AMD module=' + (embeddedReportProbe.looksLikeModule ? 'yes' : 'no'));
                }
                if (health) {
                    headerLines.push('Plugin configuration available: ' +
                        (diagnosticValue(health, 'ConfigurationAvailable', 'configurationAvailable') === true ? 'yes' : 'no'));
                    headerLines.push('Embedded display resource present in DLL: ' +
                        (diagnosticValue(health, 'DisplayHelperResourceEmbedded', 'displayHelperResourceEmbedded') === true ? 'yes' : 'no'));
                    var healthTracks = diagnosticValue(health, 'CurrentTracks', 'currentTracks') || [];
                    if (healthTracks.length) {
                        headerLines.push('Recent accepted metadata (up to 10):');
                        healthTracks.slice(0, 10).forEach(function (track) {
                            headerLines.push('  ' +
                                String(diagnosticValue(track, 'StationName', 'stationName') || 'Unknown station') + ' | ' +
                                String(diagnosticValue(track, 'SongTitle', 'songTitle') || '(no song)') + ' | ' +
                                String(diagnosticValue(track, 'Artist', 'artist') || '(no artist)') + ' | source ' +
                                String(diagnosticValue(track, 'Source', 'source') || 'unknown') + ' | updated ' +
                                String(diagnosticValue(track, 'UpdatedAt', 'updatedAt') || 'unknown'));
                        });
                    }
                }
                if (activeRadioSessions.length) {
                    headerLines.push('Active Radio Meta sessions:');
                    activeRadioSessions.slice(0, 8).forEach(function (entry) {
                        headerLines.push('  ' +
                            String(diagnosticValue(entry.session, 'Client', 'client') || 'Unknown client') + ' / ' +
                            String(diagnosticValue(entry.session, 'DeviceName', 'deviceName') || 'Unknown device') + ' | ' +
                            (entry.song || '(no song)') + ' | ' + (entry.artist || '(no artist)') + ' | ' +
                            (entry.station || '(no station)'));
                    });
                }

                diagnosticRender(checks, headerLines, health ? (diagnosticValue(health, 'RecentEvents', 'recentEvents') || []) : [], health ? diagnosticValue(health, 'Performance', 'performance') : null);
            }).catch(function (error) {
                diagnosticAddCheck(checks, 'FAIL', 'Diagnostic runner',
                    'Unexpected diagnostic error: ' + String(error && error.message ? error.message : error || 'Unknown error'));
                diagnosticRender(checks, [
                    'RADIO META DIAGNOSTIC REPORT',
                    'Generated (UTC): ' + startedAt.toISOString(),
                    'Page path: ' + pagePath,
                    'Browser: ' + userAgent
                ], [], null);
            }).then(function () {
                runButton.disabled = false;
                runButton.innerHTML = '<span>Run diagnostics again</span>';
            });
        }

        function copyRadioMetaDiagnosticReport() {
            var report = view.querySelector('#radioDiagnosticsReport');
            var status = view.querySelector('#radioDiagnosticsStatus');
            if (!report || !report.textContent) return;
            var text = report.textContent;

            function fallbackCopy() {
                var textarea = document.createElement('textarea');
                textarea.value = text;
                textarea.setAttribute('readonly', 'readonly');
                textarea.style.position = 'fixed';
                textarea.style.left = '-10000px';
                textarea.style.top = '0';
                document.body.appendChild(textarea);
                textarea.select();
                var copied = false;
                try { copied = document.execCommand('copy'); } catch (_) { copied = false; }
                textarea.remove();
                if (status) status.textContent = copied
                    ? 'Diagnostic report copied. Paste it into your support message.'
                    : 'Copy was blocked by the browser. Select the report text below and copy it manually.';
                if (!copied) {
                    report.focus();
                    if (window.getSelection && document.createRange) {
                        var range = document.createRange();
                        range.selectNodeContents(report);
                        var selection = window.getSelection();
                        selection.removeAllRanges();
                        selection.addRange(range);
                    }
                }
            }

            if (window.navigator && window.navigator.clipboard && typeof window.navigator.clipboard.writeText === 'function') {
                window.navigator.clipboard.writeText(text).then(function () {
                    if (status) status.textContent = 'Diagnostic report copied. Paste it into your support message.';
                }).catch(fallbackCopy);
            } else fallbackCopy();
        }

        var diagnosticsRunButton = view.querySelector('#radioDiagnosticsRun');
        if (diagnosticsRunButton) diagnosticsRunButton.addEventListener('click', runRadioMetaDiagnostics);
        var diagnosticsCopyButton = view.querySelector('#radioDiagnosticsCopy');
        if (diagnosticsCopyButton) diagnosticsCopyButton.addEventListener('click', copyRadioMetaDiagnosticReport);

        var stationScanButton = view.querySelector('#radioStationScanButton');
        if (stationScanButton) {
            stationScanButton.addEventListener('click', toggleStationFolderVisibility);
        }
        updateStationFolderVisibilityButton();
        var stationCreateButton = view.querySelector('#radioStationCreateButton');
        if (stationCreateButton) stationCreateButton.addEventListener('click', createStationFile);

        function resolveCurrentPlaybackInfoStation() {
            if (!ApiClient || typeof ApiClient.getSessions !== 'function' ||
                typeof ApiClient.getPlaybackInfo !== 'function') {
                return Promise.resolve(null);
            }

            function apiValue(name) {
                try {
                    var value = ApiClient[name];
                    return typeof value === 'function' ? value.call(ApiClient) : value;
                } catch (ignore) { return null; }
            }

            function isExternalStreamUrl(value) {
                if (typeof value !== 'string' || !value.trim()) return '';
                try {
                    var parsed = new URL(value.trim(), window.location.href);
                    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return '';
                    if (parsed.username || parsed.password) return '';

                    // Reject Emby-internal playback URLs: they normally require the
                    // logged-in user's session and are not portable .strm URLs.
                    if (/^\/(?:emby\/)?(?:Audio|Videos?|Items|LiveTv|PlaybackInfo|Sessions)\//i.test(parsed.pathname)) return '';
                    var embyHost = '';
                    try {
                        var serverAddress = apiValue('serverAddress');
                        if (!serverAddress && typeof ApiClient.getUrl === 'function') serverAddress = ApiClient.getUrl('');
                        if (serverAddress) embyHost = new URL(serverAddress, window.location.href).host.toLowerCase();
                    } catch (ignore) { }
                    if (embyHost && parsed.host.toLowerCase() === embyHost && /^\/(?:emby\/)?(?:Audio|Videos?|Items|LiveTv|PlaybackInfo|Sessions)\//i.test(parsed.pathname)) return '';

                    // Never persist an Emby API/auth token into a .strm URL. Other
                    // provider-specific query values (e.g. TuneIn partnertok) are
                    // retained because the provider may require them to play.
                    var sensitiveEmbyKeys = ['api_key', 'apikey', 'x-emby-token', 'access_token', 'authorization'];
                    var hasEmbyCredential = false;
                    parsed.searchParams.forEach(function (_, key) {
                        if (sensitiveEmbyKeys.indexOf(String(key).toLowerCase()) >= 0) hasEmbyCredential = true;
                    });
                    if (hasEmbyCredential) return '';
                    return parsed.href;
                } catch (ignore) { return ''; }
            }

            return ApiClient.getSessions().then(function (sessions) {
                if (!Array.isArray(sessions)) return null;
                var currentUserId = apiValue('getCurrentUserId');
                var currentDeviceId = apiValue('deviceId');
                if (!currentDeviceId) currentDeviceId = apiValue('getDeviceId');
                var candidates = sessions.filter(function (session) {
                    var item = session && session.NowPlayingItem;
                    if (!item || !item.Id) return false;
                    var mediaType = String(item.MediaType || item.Type || '').toLowerCase();
                    return mediaType === 'audio';
                });
                if (currentUserId) {
                    var sameUser = candidates.filter(function (session) {
                        return String(session.UserId || '') === String(currentUserId);
                    });
                    if (sameUser.length) candidates = sameUser;
                }
                if (currentDeviceId) {
                    var sameDevice = candidates.filter(function (session) {
                        return String(session.DeviceId || '') === String(currentDeviceId);
                    });
                    if (sameDevice.length) candidates = sameDevice;
                }
                if (!candidates.length) return null;
                var session = candidates[0];
                var item = session.NowPlayingItem;
                var options = {};
                if (currentUserId) options.UserId = currentUserId;

                return ApiClient.getPlaybackInfo(item.Id, options).then(function (info) {
                    if (!info || typeof info !== 'object') return null;
                    var mediaSources = getValue(info, 'MediaSources', 'mediaSources');
                    if (!Array.isArray(mediaSources)) {
                        var singleSource = getValue(info, 'MediaSource', 'mediaSource');
                        mediaSources = singleSource ? [singleSource] : [];
                    }
                    mediaSources = mediaSources.slice().sort(function (a, b) {
                        return (getValue(b, 'IsInfiniteStream', 'isInfiniteStream') === true ? 1 : 0) -
                            (getValue(a, 'IsInfiniteStream', 'isInfiniteStream') === true ? 1 : 0);
                    });

                    var streamUrl = '';
                    for (var i = 0; i < mediaSources.length && !streamUrl; i++) {
                        var source = mediaSources[i];
                        var sourceCandidates = [
                            getValue(source, 'DirectStreamUrl', 'directStreamUrl'),
                            getValue(source, 'Path', 'path'),
                            getValue(source, 'ResolvedUrl', 'resolvedUrl'),
                            getValue(source, 'Url', 'url'),
                            getValue(source, 'Uri', 'uri')
                        ];
                        for (var j = 0; j < sourceCandidates.length; j++) {
                            streamUrl = isExternalStreamUrl(sourceCandidates[j]);
                            if (streamUrl) break;
                        }
                    }
                    if (!streamUrl) return null;

                    var name = String(item.Name || '').trim();
                    if (!name) name = String(info.Name || info.name || '').trim();
                    if (!name) return null;
                    return {
                        Available: true,
                        Name: name,
                        StreamUrl: streamUrl,
                        ArtworkUrl: '',
                        ItemId: String(item.Id || ''),
                        Source: 'Emby playback information',
                        Message: 'Resolved the external stream URL from Emby playback information. Review the station details before creating the .strm file.'
                    };
                });
            }).catch(function () {
                // Best-effort fallback. Do not log playback URLs, since provider URLs
                // may contain partner credentials or signed query values.
                return null;
            });
        }

        function setCurrentStreamTestResult(state, message, detail) {
            var result = view.querySelector('#radioStationStreamTestResult');
            if (!result) return;
            result.setAttribute('data-state', state || 'idle');
            result.textContent = message || '';
            result.title = detail || message || '';
        }

        function normalizeCurrentStreamTestResponse(value) {
            function parse(valueToParse) {
                if (typeof valueToParse !== 'string') return valueToParse;
                try { return JSON.parse(valueToParse); } catch (ignore) { return valueToParse; }
            }
            var payload = parse(value);
            if (payload && typeof payload === 'object') {
                if (payload.responseJSON !== undefined) payload = parse(payload.responseJSON);
                else if (payload.responseText !== undefined) payload = parse(payload.responseText);
                else if (payload.body !== undefined && typeof payload.body === 'string') payload = parse(payload.body);
            }
            var envelopes = ['Response', 'response', 'Result', 'result', 'd', 'Data', 'data', 'Body', 'body'];
            function isTestResponse(item) {
                return !!item && typeof item === 'object' &&
                    (item.MetadataDetected !== undefined || item.metadataDetected !== undefined ||
                     item.Available !== undefined || item.available !== undefined);
            }
            for (var depth = 0; depth < 5 && !isTestResponse(payload); depth++) {
                var nested = null;
                for (var i = 0; payload && typeof payload === 'object' && i < envelopes.length; i++) {
                    var candidate = parse(payload[envelopes[i]]);
                    if (isTestResponse(candidate)) { nested = candidate; break; }
                    if (!nested && candidate && typeof candidate === 'object' && envelopes.some(function (key) { return candidate[key] !== undefined; })) nested = candidate;
                }
                if (!nested) break;
                payload = nested;
            }
            return payload;
        }

        function testCurrentStreamMetadata(stationName, resolvedStreamUrl) {
            var name = String(stationName || '').trim();
            var streamUrl = String(resolvedStreamUrl || '').trim();
            if (!name) {
                setCurrentStreamTestResult('empty', 'No metadata detected · station name unavailable.', 'Radio Meta could not identify the current station.');
                return;
            }

            setCurrentStreamTestResult('checking', 'Checking stream for metadata… (up to 30 seconds)', 'Read-only test with one shared 30-second timeout. FFprobe is not launched and Ignore Metadata Phrases is not applied to this preview.');
            // POST keeps potentially signed stream URLs out of the request URL/query string.
            // The server independently validates that the candidate is a public stream URL.
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('RadioMeta/Stations/TestCurrentStreamMetadata'),
                data: JSON.stringify({ StationName: name, StreamUrl: streamUrl }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (value) {
                    if (value && typeof value.json === 'function') return value.json();
                    return value;
                }).then(function (value) {
                    var data = normalizeCurrentStreamTestResponse(value);
                    if (!data || typeof data !== 'object') throw new Error('The metadata test returned an unreadable response.');

                    var available = getValue(data, 'Available', 'available') === true;
                    var detected = getValue(data, 'MetadataDetected', 'metadataDetected') === true;
                    var artist = String(getValue(data, 'Artist', 'artist') || '').trim();
                    var song = String(getValue(data, 'SongTitle', 'songTitle') || '').trim();
                    var rawTitle = String(getValue(data, 'StreamTitle', 'streamTitle') || '').trim();
                    var source = String(getValue(data, 'Source', 'source') || '').trim();
                    var detail = String(getValue(data, 'Message', 'message') || '').trim();
                    var durationMs = Number(getValue(data, 'ElapsedMilliseconds', 'elapsedMilliseconds') || 0);
                    var duration = durationMs > 0 ? (durationMs / 1000).toFixed(1) + ' s' : '';

                    if (detected) {
                        var titleText = artist && song ? artist + ' — ' + song : (song || rawTitle);
                        if (!artist) titleText += ' · artist not supplied';
                        var foundMessage = titleText + (source ? ' · ' + source : '') + (duration ? ' · ' + duration : '');
                        setCurrentStreamTestResult('found', foundMessage, detail || foundMessage);
                        return;
                    }

                    var emptyMessage = 'No metadata detected';
                    if (!available) {
                        // Distinguish a test that never started from a completed
                        // 30-second lookup that found no usable station metadata.
                        emptyMessage = /url|stream|test not started|safety|public/i.test(detail)
                            ? 'Test not started · stream URL unavailable'
                            : 'Test not started · current station unavailable';
                        setCurrentStreamTestResult('error', emptyMessage, detail || 'The metadata test did not start because no valid active stream URL was available.');
                    } else {
                        if (detail && /timed?\s*out|timeout/i.test(detail)) {
                            emptyMessage = 'No metadata detected within 30 seconds';
                            if (duration) emptyMessage += ' · ' + duration;
                        } else if (duration) {
                            emptyMessage += ' · ' + duration;
                        }
                        setCurrentStreamTestResult('empty', emptyMessage, detail || 'The stream did not provide a usable song title during this one-shot test.');
                    }
                })
                .catch(function (error) {
                    var detail = error && error.message ? error.message : 'The metadata test request failed.';
                    setCurrentStreamTestResult('error', 'Stream metadata test unavailable.', detail);
                    console.warn('Radio Meta stream metadata test:', error);
                });
        }

        function addCurrentlyPlayingStationToForm() {
            var button = view.querySelector('#radioStationAddCurrentButton');
            var status = view.querySelector('#radioStationCreateStatus');
            var nameInput = view.querySelector('#radioStationCreateName');
            var urlInput = view.querySelector('#radioStationCreateUrl');
            var artworkInput = view.querySelector('#radioStationCreateArtworkUrl');
            if (!button || !status || !nameInput || !urlInput || !artworkInput || button.disabled) return;

            button.disabled = true;
            var previousText = button.textContent;
            button.textContent = 'Reading station...';
            status.textContent = 'Looking for the currently playing station in Emby. No files will be created by this action.';
            setCurrentStreamTestResult('checking', 'Preparing stream metadata test…', 'The test will run against the active station stream after station details are identified.');

            ApiClient.getJSON(ApiClient.getUrl('RadioMeta/Stations/CurrentlyPlaying'))
                .then(function (data) {
                    if (data && typeof data.json === 'function') return data.json();
                    function parseStationPayload(value) {
                        if (typeof value !== 'string') return value;
                        try { return JSON.parse(value); } catch (ignore) { return value; }
                    }
                    var payload = parseStationPayload(data);
                    if (payload && typeof payload === 'object') {
                        if (payload.responseJSON !== undefined) payload = parseStationPayload(payload.responseJSON);
                        else if (payload.responseText !== undefined) payload = parseStationPayload(payload.responseText);
                    }
                    var envelopeNames = ['Response', 'response', 'Result', 'result', 'd', 'Data', 'data', 'Body', 'body'];
                    function isCurrentStationDto(value) {
                        return !!value && typeof value === 'object' &&
                            (value.Available !== undefined || value.available !== undefined ||
                             value.Name !== undefined || value.name !== undefined ||
                             value.Error !== undefined || value.error !== undefined);
                    }
                    for (var depth = 0; depth < 5 && !isCurrentStationDto(payload); depth++) {
                        var nested = null;
                        for (var i = 0; payload && typeof payload === 'object' && i < envelopeNames.length; i++) {
                            var candidate = parseStationPayload(payload[envelopeNames[i]]);
                            if (isCurrentStationDto(candidate)) { nested = candidate; break; }
                            if (!nested && candidate && typeof candidate === 'object' && envelopeNames.some(function (key) { return candidate[key] !== undefined; })) nested = candidate;
                        }
                        if (!nested) break;
                        payload = nested;
                    }
                    return payload;
                })
                .catch(function (requestError) {
                    // Still try the client-side playback-info path if the plugin's
                    // in-memory capture endpoint has no candidate or cannot respond.
                    return {
                        Available: false,
                        Error: requestError && requestError.message
                            ? 'The plugin capture endpoint did not return a station (' + requestError.message + ').'
                            : 'The plugin capture endpoint did not return a station.'
                    };
                })
                .then(function (data) {
                    var available = getValue(data, 'Available', 'available') === true;
                    var existingStreamUrl = String(getValue(data, 'StreamUrl', 'streamUrl') || '').trim();
                    if (available && existingStreamUrl) return data;

                    // TuneIn/Radio Browser can omit the original URL from getSessions()
                    // and the item DTO, while Emby's GetPlaybackInfo returns it under
                    // MediaSources[].Path. Use that API only as a fallback; never save
                    // Emby's own /emby/Audio/... playback URL as a station stream.
                    return resolveCurrentPlaybackInfoStation().then(function (fallback) {
                        if (!fallback) return data;
                        var serverName = String(getValue(data, 'Name', 'name') || '').trim();
                        var serverUrl = String(getValue(data, 'StreamUrl', 'streamUrl') || '').trim();
                        var serverArtwork = String(getValue(data, 'ArtworkUrl', 'artworkUrl') || '').trim();
                        fallback.Name = serverName || fallback.Name;
                        fallback.StreamUrl = serverUrl || fallback.StreamUrl;
                        fallback.ArtworkUrl = serverArtwork || fallback.ArtworkUrl;
                        fallback.ItemId = String(getValue(data, 'ItemId', 'itemId') || getValue(fallback, 'ItemId', 'itemId') || '').trim();
                        var priorMessage = String(getValue(data, 'Message', 'message') || '').trim();
                        if (priorMessage && available) fallback.Message = priorMessage + ' ' + fallback.Message;
                        return fallback;
                    });
                })
                .then(function (data) {
                    var error = getValue(data, 'Error', 'error');
                    var available = getValue(data, 'Available', 'available') === true;
                    if (error || !available) {
                        status.textContent = error || getValue(data, 'Message', 'message') || 'No currently playing radio station could be identified. Start the station in Emby, wait a few seconds, and try again.';
                        setCurrentStreamTestResult('error', 'Test not started · current stream unavailable.', status.textContent);
                        return;
                    }

                    var name = String(getValue(data, 'Name', 'name') || '');
                    var streamUrl = String(getValue(data, 'StreamUrl', 'streamUrl') || '');
                    var artworkUrl = String(getValue(data, 'ArtworkUrl', 'artworkUrl') || '').trim();

                    // Radio Browser/Emby channel items may have a valid Primary image
                    // even when they expose no path, image source list, or provider IDs.
                    // Ask Emby for that image using the captured library item id.
                    if (!artworkUrl) {
                        var embyItemId = String(getValue(data, 'ItemId', 'itemId') || '').trim();
                        if (embyItemId && ApiClient && typeof ApiClient.getImageUrl === 'function') {
                            try {
                                artworkUrl = ApiClient.getImageUrl(embyItemId, { type: 'Primary' });
                                var accessToken = typeof ApiClient.accessToken === 'function' ? ApiClient.accessToken() : '';
                                if (artworkUrl && accessToken && !/[?&]api_key=/i.test(artworkUrl)) {
                                    artworkUrl += (artworkUrl.indexOf('?') >= 0 ? '&' : '?') + 'api_key=' + encodeURIComponent(accessToken);
                                }
                            } catch (imageUrlError) {
                                artworkUrl = '';
                            }
                        }
                    }
                    if (!name.trim() && !streamUrl.trim()) {
                        status.textContent = getValue(data, 'Message', 'message') || 'Emby did not expose enough station details to pre-fill the form.';
                        setCurrentStreamTestResult('error', 'Test not started · station details unavailable.', status.textContent);
                        return;
                    }

                    // Replace the three inputs as one pre-fill operation so an
                    // unavailable field cannot accidentally retain an older URL.
                    nameInput.value = name;
                    urlInput.value = streamUrl;
                    artworkInput.value = artworkUrl;

                    // Run separately so the station form is still filled promptly.
                    // Test the exact resolved stream URL supplied by the Current Playback workflow. The server can run this independently of its capture store.
                    testCurrentStreamMetadata(name, streamUrl);

                    var missing = [];
                    if (!name.trim()) missing.push('station name');
                    if (!streamUrl.trim()) missing.push('stream URL');
                    if (!artworkUrl.trim()) missing.push('station logo URL');
                    var source = String(getValue(data, 'Source', 'source') || 'Emby playback');
                    var note = String(getValue(data, 'Message', 'message') || '');
                    status.textContent = 'Pre-filled station details from ' + source + '. Review or edit the fields, then click Create .strm when ready. No files have been created.' +
                        (note ? ' ' + note : (missing.length ? ' Still needed or unavailable: ' + missing.join(', ') + '.' : ''));

                    var focusTarget = !name.trim() ? nameInput : (!streamUrl.trim() ? urlInput : (!artworkUrl.trim() ? artworkInput : nameInput));
                    if (typeof nameInput.scrollIntoView === 'function') nameInput.scrollIntoView({ block: 'center', behavior: 'auto' });
                    focusTarget.focus();
                })
                .catch(function (error) {
                    status.textContent = 'Could not read the current station: ' + (error && error.message ? error.message : String(error));
                    setCurrentStreamTestResult('empty', 'No metadata detected · station lookup failed.', status.textContent);
                    console.error('Radio Meta currently-playing station pre-fill:', error);
                })
                .finally(function () {
                    button.disabled = false;
                    button.textContent = previousText || 'Add to Radio Meta';
                });
        }

        var stationAddCurrentButton = view.querySelector('#radioStationAddCurrentButton');
        if (stationAddCurrentButton) stationAddCurrentButton.addEventListener('click', addCurrentlyPlayingStationToForm);
        ['#radioStationCreateName', '#radioStationCreateUrl', '#radioStationCreateArtworkUrl'].forEach(function (selector) {
            var input = view.querySelector(selector);
            if (input) input.addEventListener('keydown', function (event) {
                if (event.key === 'Enter') { event.preventDefault(); createStationFile(); }
            });
        });

        var stationModeAddButton = view.querySelector('#radioStationModeAdd');
        if (stationModeAddButton) {
            stationModeAddButton.addEventListener('click', addStationModeRule);
        }
        var stationModeNameInput = view.querySelector('#radioStationModeName');
        if (stationModeNameInput) {
            stationModeNameInput.addEventListener('input', function () {
                var name = stationModeNameInput.value.trim().toLowerCase();
                var existing = stationModeRules.find(function (rule) {
                    return getModeStation(rule).toLowerCase() === name;
                });
                if (!existing) return;
                var modeSelect = view.querySelector('#radioStationModeValue');
                var delaySelect = view.querySelector('#radioStationModeDelay');
                if (modeSelect) modeSelect.value = getStationMode(existing);
                if (delaySelect) delaySelect.value = String(getStationModeDelaySeconds(existing));
            });
            stationModeNameInput.addEventListener('keydown', function (event) {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    addStationModeRule();
                }
            });
        }

        var stationFolderChangeButton = view.querySelector('#radioStationFolderChange');
        if (stationFolderChangeButton) {
            stationFolderChangeButton.addEventListener('click', focusStationFolderSetting);
        }

        var pathSettingsSaveButton = view.querySelector('#radioPathSettingsSave');
        if (pathSettingsSaveButton) {
            pathSettingsSaveButton.addEventListener('click', savePathSettings);
        }
        ['#radioStationFolder', '#radioFfprobePath'].forEach(function (selector) {
            var input = view.querySelector(selector);
            if (input) {
                input.addEventListener('keydown', function (event) {
                    if (event.key === 'Enter') {
                        event.preventDefault();
                        savePathSettings();
                    }
                });
            }
        });

        var historyScanButton = view.querySelector('#radioHistoryScan');
        if (historyScanButton) {
            historyScanButton.addEventListener('click', toggleHistoryVisibility);
        }

        var historyClearButton = view.querySelector('#radioHistoryClear');
        if (historyClearButton) {
            historyClearButton.addEventListener('click', clearHistory);
        }

        updateHistoryVisibilityButton();

        var addButton = view.querySelector('#radioIgnoreAdd');
        if (addButton) {
            addButton.addEventListener('click', addIgnorePhrase);
        }
        var phraseInput = view.querySelector('#radioIgnorePhrase');
        if (phraseInput) {
            phraseInput.addEventListener('keydown', function (event) {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    addIgnorePhrase();
                }
            });
        }
        view.addEventListener('click', onViewClick);
        view.addEventListener('viewshow', onViewShow);
        view.addEventListener('viewhide', onViewHide);
    };
});
