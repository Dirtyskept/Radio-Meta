# Radio Meta — Emby plugin

Shows the song and artist that is playing on an internet radio station, inside Emby's own players: the mini-player, the full player, Dashboard Now Playing, the phone lock screen and CarPlay.

## What it does
- **Live song info:** reads the station's stream title and Shoutcast status pages, learns which one is faster for each station, and falls back to FFprobe if neither answers. It understands common title formats and skips adverts and station jingles.
- **In Emby's players:** song and artist appear in Emby's normal player text. The artist links to that artist in your library when you have them, otherwise to the station.
- **Stations:** your stations are `.strm` files in a Station folder. Radio Meta can create, edit and delete them, add logos and keep a play history. It can also show song info for radio streams outside that folder, such as stations from another plugin.
- **Rules:** per station (delay song changes, or turn lookups off) and per device. You can also add phrases to ignore.
- **Proxy (optional, per station):** for streams Emby can't play directly, FFmpeg repackages the audio as MP3. See [PROXY-QUICK-START.txt](PROXY-QUICK-START.txt).
- **Settings page** (Plugins › Radio Meta), laid out like Emby's own pages, with Stations, History, Metadata Rules, Settings and Diagnostics tabs. Diagnostics never shows stream links, tokens, paths or session IDs.

## What it changes in Emby
Radio Meta adds one marked script block to Emby's web start page (`system/dashboard-ui/index.html`), so the web player can show song info. See [WHAT-RADIO-META-CHANGES-IN-EMBY.txt](WHAT-RADIO-META-CHANGES-IN-EMBY.txt) and [WHAT-RADIO-META-CHANGES-IN-THE-WEB-PLAYER.txt](WHAT-RADIO-META-CHANGES-IN-THE-WEB-PLAYER.txt).

## Install
1. Download `RadioMeta.dll` from [Releases](../../releases). It needs Emby 4.10.2 or newer.
2. Back up your current `RadioMeta.dll`, if you have one.
3. Copy the new DLL into Emby's `plugins` folder and restart Emby.
4. Press Ctrl+F5 once in each browser.

## Build (Windows)
Copy Emby 4.10.2's `MediaBrowser.Common`, `Controller` and `Model.dll` into `RadioMeta/EmbyReferences/4.10.2` (see the README there), then run `.\Build-RadioMeta154.ps1`. Tests are in `Tests/`.

## Licence
Copyright (C) 2026 Dirtyskept. Radio Meta is free software under the [GNU General Public License, version 3](LICENSE): you may use, change and share it, and a changed version you share must also be under GPL-3.0. It comes with no warranty.
