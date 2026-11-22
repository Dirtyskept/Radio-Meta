/*
 * Simulates the Emby 4.10.x plugin-resource loading sequence used by the
 * DLL-generated inline bootstrap. This does not replace a live Emby test.
 * Run: node .\Tests\RadioMeta.Bootstrap.SmokeTest.js
 */
"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");

const sourcePath = path.join(__dirname, "..", "RadioMeta", "RadioMetaPlayerIntegration.cs");
const cs = fs.readFileSync(sourcePath, "utf8");
const start = cs.indexOf('private const string ScriptReference =');
const end = cs.indexOf('"</script>";', start);
if (start < 0 || end < 0) throw new Error("Could not locate the DLL bootstrap string.");

const block = cs.slice(start, end + '"</script>";'.length);
const lines = block.split(/\r?\n/).slice(1);
const parts = [];
for (const line of lines) {
    const match = line.match(/^\s*"((?:\\.|[^"\\])*)"\s*\+?\s*$/);
    if (match) parts.push(JSON.parse('"' + match[1] + '"'));
}
const markup = parts.join("") + "</script>";
const scriptMatch = markup.match(/<script[^>]*>([\s\S]*?)<\/script>/i);
if (!scriptMatch) throw new Error("Could not extract the inline bootstrap script.");
const bootstrap = scriptMatch[1];

// Shared rule 8: the block has no version number, so plugin updates don't rewrite index.html.
if (/\d+\.\d+\.\d+/.test(markup) || /__radioMetaPlayerBootstrap\d/.test(markup)) {
    throw new Error("The index.html bootstrap block must not contain a version number.");
}
// Shared rule 3: the mutex name must match the other plugins exactly.
if (!cs.includes('SharedMutexName = @"Global\\EmbyDashboardIndexWrite"')) {
    throw new Error("The shared index.html mutex name is missing or changed.");
}

const calls = [];
let pendingTimer = null;
const win = {
    console: {
        info: (...args) => calls.push(["info", ...args]),
        warn: (...args) => calls.push(["warn", ...args]),
        error: (...args) => calls.push(["error", ...args])
    },
    setTimeout(fn) { calls.push(["timer"]); pendingTimer = fn; return 1; },
    require(ids) {
        calls.push(["amd-require", ids]);
        if (ids[0] !== "https://emby.test/web/ConfigurationPage?name=RadioMetaPlayerIntegration.js") {
            return Promise.reject(new Error("Unexpected plugin resource URL: " + ids[0]));
        }
        return Promise.resolve([{ version: "test" }]);
    }
};
win.window = win;
const emby = {
    importModule(modulePath) {
        calls.push(["Emby.importModule", modulePath]);
        if (modulePath !== "./modules/common/pluginmanager.js") {
            return Promise.reject(new Error("Unexpected module path: " + modulePath));
        }
        return Promise.resolve({
            getConfigurationResourceUrl(name) {
                calls.push(["getConfigurationResourceUrl", name]);
                return "https://emby.test/web/ConfigurationPage?name=" + name;
            }
        });
    }
};

const run = () => vm.runInNewContext(bootstrap, { window: win, Promise, Error, String, console: win.console });

// 1. The page runs the block before Emby's loader exists: it must wait, not fail.
run();
if (!pendingTimer || calls.some(call => call[0] === "Emby.importModule")) {
    throw new Error("Bootstrap did not wait for Emby's module loader.");
}
// 2. Emby becomes ready; the queued retry loads the script.
win.Emby = emby;
pendingTimer();
// 3. The block runs again (for example a second copy): nothing is loaded twice.
run();

setTimeout(() => {
    const actual = calls.map(call => call[0]);
    for (const name of ["Emby.importModule", "getConfigurationResourceUrl", "amd-require"]) {
        if (!actual.includes(name)) throw new Error("Missing loader step: " + name);
    }
    if (actual.filter(name => name === "amd-require").length !== 1) throw new Error("The player script was loaded more than once.");
    if (win.__radioMetaPlayerBootstrap !== true) throw new Error("Bootstrap did not mark the script as loaded.");
    if (actual.includes("error") || actual.includes("warn")) throw new Error("Bootstrap reported an error.");
    console.log("PASS: the bootstrap has no version number and uses the shared lock name.");
    console.log("PASS: the bootstrap waits for Emby's module loader, then loads the embedded script through Emby's PluginManager and AMD require(), once.");
}, 0);
