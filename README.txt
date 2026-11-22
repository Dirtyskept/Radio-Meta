RADIO META v1.3.153 — SOURCE PACKAGE
====================================

Radio Meta is an Emby plugin that shows the live song and artist of internet
radio stations in Emby's own players: the mini-player, the big player,
Dashboard Now Playing, the lock screen and CarPlay. This package is the source
code, not a compiled DLL.

READ FIRST
----------
- WHAT-RADIO-META-CHANGES-IN-EMBY.txt — the one Emby file Radio Meta changes
  (a marked block in system\dashboard-ui\index.html), when, why, and how to
  check or remove it.
- WHAT-RADIO-META-CHANGES-IN-THE-WEB-PLAYER.txt — everything the player script
  changes inside the Emby web app while a page is open.

WHAT IT DOES
------------
Stations
- Stations are .strm files in your Station folder. Radio Meta can create,
  edit and delete them, add logos, and keep a play history.
- "Stations outside the station folder" (on by default) also gives metadata
  to audio .strm files elsewhere, for example a library made by another
  plugin, and to audio played straight from a web address with no .strm (for
  example WebStreams). Only your Station folder is listed or edited.
- A blank Station folder means automatic mode: any audio .strm that plays.

Metadata
- Reads the stream's ICY title and Shoutcast status pages, learns which is
  faster per station, and falls back to FFprobe after a configurable wait.
- Recognises common stream-title formats (artist - title, iHeartRadio, Triton,
  "~" and "||" records) and skips adverts and station markers.
- Per-station rules: a delay before song changes are shown, or no song lookup
  at all. Per-device rules match on the Emby device name.

Players
- Song and artist appear in Emby's native player text; the artist links to
  your library artist when there is one, otherwise to the station.
- The big player keeps its layout and logo steady, and its play-queue list
  keeps station names.

Proxy (optional, per station)
- For streams Emby can't play directly, FFmpeg re-packages the audio as MP3 on
  127.0.0.1:18741. See PROXY-QUICK-START.txt.

Settings page
- Laid out like Emby's own admin pages: tabs in Emby's header (Stations,
  History, Metadata Rules, Settings, Diagnostics), Emby fields and switches,
  list rows, and Emby dialogs for adding, editing and confirming.
- Diagnostics reports plugin health, current tracks, proxy health and recent
  metadata activity without stream URLs, tokens, paths or session IDs.

BUILD ON WINDOWS
----------------
1. Extract this ZIP.
2. Open PowerShell in the extracted RadioMeta-v1.3.153 folder.
3. Run: .\Build-RadioMeta153.ps1
   It builds against the Emby assemblies in RadioMeta\EmbyReferences\4.10.2
   (the oldest supported Emby), so the DLL loads on 4.10.2 and later. Fill
   that folder once, see RadioMeta\EmbyReferences\README.txt. The folder is
   set by the EmbyReferencePath line in RadioMeta.csproj.
4. Run Tests\Run-RadioMeta-Metadata-Tests.cmd (offline C# tests). If Node.js
   is installed, run the smoke tests listed in Tests\README.txt.
5. Back up your working RadioMeta.dll before installing the new one.
6. After installing, press Ctrl+F5 once in each browser.

PACKAGE CONTENTS
----------------
- RadioMeta\ — plugin source and embedded web files.
  - WebUI\DashboardSections\ — the settings page script in 15 readable parts;
    the build joins them into RadioMetaDashboard.js.
  - WebUI\RadioMetaPlayerIntegration.js — the player script.
  - docs\LOGGING-AND-PRIVACY.md — what the plugin logs and what it never logs.
- Tests\ — offline C# metadata tests and Node smoke tests.
- Logs\CHANGELOG.txt — every 1.3.x release, newest first.
- Logs\older-notes\ — earlier branch histories and cleanup notes.
- PROXY-QUICK-START.txt, PROXY-TROUBLESHOOTING.txt,
  PROXY-IMPLEMENTATION-WALKTHROUGH.txt — the proxy.
- PERSISTENT-ADAPTIVE-ROUTING.txt — the saved metadata-routing state and how
  to block a Shoutcast endpoint by hand.
- RELEASE-NOTES.txt — this release in a few lines.

COMPATIBILITY
-------------
- Plugin GUID 8a7f6e31-9d42-4b85-a103-72c6f0e54b19 and the configuration file
  format are unchanged across 1.3.x.
- Built and tested against Emby Server 4.10.
