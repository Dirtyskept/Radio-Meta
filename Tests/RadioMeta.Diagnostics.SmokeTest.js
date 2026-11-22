/*
 * Static regression checks for Radio Meta 1.3.153 diagnostic versioning and
 * local Emby session selection. Run: node .\Tests\RadioMeta.Diagnostics.SmokeTest.js
 */
"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");

const runnerPath = path.join(__dirname, "..", "RadioMeta", "WebUI", "DashboardSections", "Diagnostics", "11-Diagnostic-Runner.js.part");
const shellPath = path.join(__dirname, "..", "RadioMeta", "WebUI", "DashboardSections", "00-Amd-Shell.js.part");
const servicePath = path.join(__dirname, "..", "RadioMeta", "Diagnostics", "RadioMetaDiagnosticsService.cs");
const projectPath = path.join(__dirname, "..", "RadioMeta", "RadioMeta.csproj");
const integrationClassPath = path.join(__dirname, "..", "RadioMeta", "RadioMetaPlayerIntegration.cs");
const runner = fs.readFileSync(runnerPath, "utf8");
const shell = fs.readFileSync(shellPath, "utf8");
const service = fs.readFileSync(servicePath, "utf8");
const project = fs.readFileSync(projectPath, "utf8");
const integrationClass = fs.readFileSync(integrationClassPath, "utf8");

assert(shell.includes("__radioMetaDashboardControllerInfo_v1353"), "Dashboard version marker should be 1.3.153");
assert(shell.includes("version: '1.3.153'"), "Dashboard controller should report 1.3.153");
assert(project.includes("<Version>1.3.153</Version>"), "Plugin project version should be 1.3.153");
assert(runner.includes("/^1\\.3\\./.test(pluginVersion)"), "Diagnostics should accept the 1.3 mainline");
assert(!runner.includes("experiment remains unverified"), "Obsolete unverified experiment wording must be removed");
assert(runner.includes("localRadioSessionCandidates = activeRadioSessions.filter"), "Local session selection must consider Radio Meta-bearing rows");
assert(runner.includes("localRadioSessionCandidates[0]"), "A Radio Meta-bearing session must be preferred for the same device");
assert(runner.includes("PlayerIntegrationBootstrapInstalled"), "Diagnostics should report DLL bootstrap installation");
assert(runner.includes("PlayerIntegrationResourceEmbedded"), "Diagnostics should report embedded resource presence");
assert(service.includes("RadioMetaPlayerIntegration.IsInstalled"), "Health endpoint should expose actual bootstrap state");
assert(service.includes("RadioMetaPlayerIntegration.InstallStatus"), "Health endpoint should expose installation status");
assert(integrationClass.includes("BEGIN RADIOMETA DLL PLAYER INTEGRATION -->"), "Installer should use the stable mainline marker");

// Run the actual local-session selection block against a duplicate-device fixture.
const blockStart = runner.indexOf("// Emby can return more than one session row with the same browser DeviceId.");
const blockEnd = runner.indexOf("var expectedTrack = localRadioSession ?", blockStart);
assert(blockStart >= 0 && blockEnd > blockStart, "Could not locate the local session selection logic");
const selectionBlock = runner.slice(blockStart, blockEnd);
const emptyDuplicate = { DeviceId: "firefox-device", NowPlayingItem: null };
const stationItem = { Id: "strm-station-id", ProviderIds: { RadioMetaSong: "Test Song", RadioMetaArtist: "Test Artist" } };
const activeSession = { DeviceId: "firefox-device", NowPlayingItem: stationItem };
const activeEntry = { session: activeSession, item: stationItem, song: "Test Song", artist: "Test Artist", station: "Test Station" };
const value = (object, pascal, camel) => object ? (object[pascal] !== undefined ? object[pascal] : (object[camel] !== undefined ? object[camel] : "")) : "";
const fakeWindow = {
    __radioMetaPlayerIntegration1353: {
        modules: {
            currentPlayer: { get: () => ({ isLocalPlayer: true }) },
            playbackManager: { currentItem: () => ({ Id: "strm-station-id" }) }
        }
    }
};
const selected = new Function("sessions", "activeRadioSessions", "localDeviceId", "window", "diagnosticValue",
    selectionBlock + "\nreturn { localSession: localSession, localRadioSession: localRadioSession };")(
        [emptyDuplicate, activeSession], [activeEntry], "firefox-device", fakeWindow, value);
assert.strictEqual(selected.localSession, activeSession, "The Radio Meta-bearing session should beat an empty duplicate device row");
assert.strictEqual(selected.localRadioSession, activeEntry, "The current browser should be linked to the active Radio Meta track");

// The bridge check must FAIL when the hook on Emby's display event is missing
// (1.3.153: Edge's pagehide had removed it while the players kept the title).
const hookMatch = /var browserDisplayHookInPlace = ([\s\S]*?);\n/.exec(runner);
assert(hookMatch, "Diagnostics should check the player display hook");
const hookInPlace = new Function("browserModulesConnected", "browserPlayerBridge", "return " + hookMatch[1] + ";");
const embyTrigger = function () { }, radioMetaHook = function () { };
assert.strictEqual(hookInPlace(true, { modules: { events: { trigger: radioMetaHook } }, eventTriggerWrapper: radioMetaHook, eventTriggerOriginal: embyTrigger }), true,
    "A hook in place should pass");
assert.strictEqual(hookInPlace(true, { modules: { events: { trigger: embyTrigger } }, eventTriggerWrapper: null, eventTriggerOriginal: null }), false,
    "A removed hook should fail");
assert.strictEqual(hookInPlace(true, { modules: { events: { trigger: embyTrigger } }, eventTriggerWrapper: radioMetaHook, eventTriggerOriginal: embyTrigger }), false,
    "Emby's original back in place should fail");
assert(/browserDisplayHookInPlace \? 'PASS' : 'FAIL'/.test(runner), "A missing display hook should be a FAIL");

// 1.3.153: Settings save must decode Emby's reply and never blank the fields
// from a reply it could not read (blank fields were then saved by the next Save).
const pathSettingsPart = fs.readFileSync(path.join(__dirname, "..", "RadioMeta", "WebUI", "DashboardSections", "07-History-Actions-And-Path-Settings.js.part"), "utf8");
const saveBlock = pathSettingsPart.slice(pathSettingsPart.indexOf("function savePathSettings"));
assert(/getUrl\('RadioMeta\/PathSettings'\)[\s\S]*?dataType: 'json'[\s\S]*?\.then\(radioMetaReadJson\)/.test(saveBlock), "Settings save must ask for JSON and decode the reply");
assert(saveBlock.includes("if (replyHas('StationFolder', 'stationFolder'))"), "Station folder field must only be filled from a value the reply contains");
assert(runner.includes("RecentSettingsSaves"), "Diagnostics should report recent settings saves");
const pathService = fs.readFileSync(path.join(__dirname, "..", "RadioMeta", "RadioPathSettingsService.cs"), "utf8");
assert(pathService.includes("plugin.SettingsSaveEvents.Record(saveEvent)"), "Settings saves must be recorded");

// 1.3.153: every rule list asks before removing, and rule changes are logged.
const sectionsDir = path.join(__dirname, "..", "RadioMeta", "WebUI", "DashboardSections");
const modesPart = fs.readFileSync(path.join(sectionsDir, "04-Station-Mode-Settings-Panel.js.part"), "utf8");
const lifecyclePart = fs.readFileSync(path.join(sectionsDir, "08-View-Lifecycle-And-Station-Creation.js.part"), "utf8");
const fixesPart = fs.readFileSync(path.join(sectionsDir, "06-Saved-Title-Fixes-Panel.js.part"), "utf8");
assert(/function removeStationModeRule[\s\S]*?radioMetaConfirm\(/.test(modesPart), "Removing a station/device setting must ask first");
assert(/Remove the ignore phrase[\s\S]*?saveIgnoreRules\(nextRules/.test(lifecyclePart), "Removing an ignore phrase must ask first");
assert((fixesPart.match(/radioMetaConfirm\(/g) || []).length >= 2, "Removing title fixes must ask first");
const rulesService = fs.readFileSync(path.join(__dirname, "..", "RadioMeta", "RadioMetadataRulesService.cs"), "utf8");
assert((rulesService.match(/RadioMetaRuleChangeLog\.Log\(/g) || []).length === 4, "All four rule lists must log their changes");

console.log("PASS: Radio Meta diagnostics recognize the 1.3 mainline and no longer describe the verified display bridge as an unverified experiment.");
console.log("PASS: local-session matching prefers the Radio Meta-bearing session row for the current browser device.");
console.log("PASS: health diagnostics report the actual DLL bootstrap and embedded player-resource state.");
console.log("PASS: settings saves decode Emby's reply, never blank the fields from an unreadable reply, and are recorded for Diagnostics.");
console.log("PASS: every metadata rule list asks before removing, and all four log their changes to the Emby log.");
console.log("PASS: the browser bridge check fails when Radio Meta's hook on Emby's player display is missing.");
