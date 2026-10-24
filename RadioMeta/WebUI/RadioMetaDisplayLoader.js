/* Radio Meta display loader module. Embedded and served by RadioMeta.dll.
   The WebLoader contains only a small AMD bootstrap pointer to this resource. */
define([], function () {
    "use strict";

    ;(function () {
        "use strict";

        var stateKey = "__radioMetaDisplayLoaderState_v1366";
        var helperKey = "__radioMetaIntegratedDisplay_v1323";
        var legacyHelperModule = "ConfigurationPage?name=RadioMetaIntegratedDisplay.js";
        var importTimeoutMs = 12000;
        var startupRetryDelays = [250, 500, 1000, 2000, 3000, 4000, 4000];
        var importRetryDelays = [1000, 3000];
        var startupAttempts = 0;
        var importAttempts = 0;

        function clearLoaderError() {
            try { delete window.__radioMetaDisplayLoaderError; } catch (ignore) {}
        }

        function markLoaded() {
            window[stateKey] = "loaded";
            clearLoaderError();
        }

        // Emby Web can be hosted below a base path (for example /emby/web/).
        // Build the resource URL from the active web-client path instead of assuming
        // the server is installed at the origin root.
        function getHelperModulePath() {
            var pathname = String(window.location && window.location.pathname || "/");
            var lowerPath = pathname.toLowerCase();
            var webPosition = lowerPath.lastIndexOf("/web/");
            var basePath = webPosition >= 0 ? pathname.slice(0, webPosition) : "";
            if (webPosition < 0 && /\/web$/i.test(pathname)) {
                basePath = pathname.slice(0, -4);
            }
            return basePath + "/web/ConfigurationPage?name=RadioMetaIntegratedDisplay.js";
        }

        if (window[helperKey] === true) {
            markLoaded();
            return;
        }
        if (window[stateKey] === "loading") {
            return;
        }
        // Recover from a stale "loaded" flag left by an earlier loader that marked
        // success without verifying that the module initializer actually ran.
        if (window[stateKey] === "loaded") {
            try { delete window[stateKey]; } catch (ignore) { window[stateKey] = ""; }
        }

        function errorMessage(error) {
            var message = error && error.message ? error.message : String(error || "Unknown error");
            message = String(message).replace(/([?&](?:X-Emby-Token|api_key|token)=)[^&\s]+/gi, "$1[REDACTED]");
            return message.slice(0, 300);
        }

        function errorStatus(error) {
            var status = error && (error.status || error.statusCode || (error.response && error.response.status));
            status = Number(status);
            return isFinite(status) ? status : 0;
        }

        function isPermanentError(error) {
            var status = errorStatus(error);
            if (status === 401 || status === 403 || status === 404 || status === 410) {
                return true;
            }
            var message = errorMessage(error);
            return /\b(?:401|403|404|410)\b|not found|forbidden|unauthorized|module not found|no such module/i.test(message);
        }

        function fail(error) {
            window[stateKey] = "failed";
            window.__radioMetaDisplayLoaderError = errorMessage(error);
            console.warn(
                "Radio Meta display helper could not be loaded. Verify the Radio Meta plugin and its display resource, then reload Emby Web.",
                window.__radioMetaDisplayLoaderError
            );
        }

        function handleImportFailure(error) {
            if (isPermanentError(error)) {
                fail(error);
                return;
            }

            var delayIndex = importAttempts - 1;
            if (delayIndex >= 0 && delayIndex < importRetryDelays.length) {
                window.setTimeout(loadHelper, importRetryDelays[delayIndex]);
                return;
            }

            fail(error);
        }

        function scheduleStartupRetryOrFail(message) {
            if (startupAttempts < startupRetryDelays.length) {
                var delay = startupRetryDelays[startupAttempts];
                startupAttempts += 1;
                window.setTimeout(loadHelper, delay);
                return;
            }
            fail(new Error(message));
        }

        function loadHelper() {
            if (window[helperKey] === true) {
                markLoaded();
                return;
            }
            if (window[stateKey] !== "loading") {
                return;
            }

            var amdRequire = window.require;
            var hasAmdLoader = typeof amdRequire === "function" &&
                typeof window.define === "function" && window.define.amd;
            var emby = window.Emby;
            var useAmd = !!hasAmdLoader;

            if (!useAmd && (!emby || typeof emby.importModule !== "function")) {
                scheduleStartupRetryOrFail("Emby's AMD loader and Emby.importModule did not become available during startup.");
                return;
            }

            importAttempts += 1;
            var result;
            var settled = false;
            var timer = window.setTimeout(function () {
                if (settled) return;
                settled = true;
                // The module request cannot be cancelled safely. A late successful
                // registration is still detected by onSuccess and clears this error.
                fail(new Error("Timed out waiting 12 seconds for the Radio Meta display helper."));
            }, importTimeoutMs);

            function onSuccess() {
                if (window[helperKey] === true) {
                    if (!settled) {
                        settled = true;
                        window.clearTimeout(timer);
                    }
                    markLoaded();
                    return;
                }
                if (settled) return;
                settled = true;
                window.clearTimeout(timer);
                handleImportFailure(new Error("The module request completed but the Radio Meta helper did not register."));
            }

            function onFailure(error) {
                if (settled) return;
                settled = true;
                window.clearTimeout(timer);
                handleImportFailure(error || new Error("The helper module request failed."));
            }

            try {
                if (useAmd) {
                    // This is the same AMD route verified in the browser test. The
                    // absolute web-client URL ensures the module resolves from the
                    // Emby installation's web root (including a configured base path).
                    result = amdRequire([getHelperModulePath()], onSuccess);
                } else {
                    // Compatibility fallback for clients that expose Emby.importModule
                    // but not the global AMD require function.
                    result = emby.importModule(legacyHelperModule);
                }
            } catch (error) {
                onFailure(error);
                return;
            }

            if (result && typeof result.then === "function") {
                Promise.resolve(result).then(onSuccess, onFailure);
            } else if (!useAmd) {
                // Older Emby.importModule implementations may complete without a
                // promise. Do not claim success unless the factory set its flag.
                onSuccess();
            }
        }

        window[stateKey] = "loading";
        loadHelper();
    }());

    return true;
});
