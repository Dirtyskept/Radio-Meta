RADIO META TESTS (v1.3.153)
===========================

None of these tests use the network, a real Emby server or audio. They are
development checks; a live test in Emby is still needed.

1. OFFLINE C# METADATA TESTS
----------------------------
Run-RadioMeta-Metadata-Tests.cmd (needs the .NET 8 SDK). It builds a small
test program that links the plugin's own parsing and routing source files, then
prints PASS / FAIL lines.
Covers: ICY title extraction, HTML entities, punctuation, FFprobe tags,
artist/title splitting and stream-title formats (iHeartRadio, Triton, "~",
"||", adverts), adaptive routing and endpoint backoff, credential detection,
and the web-address (WebStreams) station core.
A REVIEW line needs a human judgment and is not a failure. If anything fails,
copy the output to the Radio Meta development chat.

To add a case, add a ("short name", () => ...) entry to
RadioMeta.MetadataTests\Program.cs with a real input and the expected output.
Don't assert an artist for an ambiguous station prefix without evidence.

2. NODE SMOKE TESTS (optional, need Node.js)
-------------------------------------------
Run from the package folder:

  node .\Tests\RadioMeta.Bootstrap.SmokeTest.js
      The index.html block: loads the player script through Emby's plugin
      manager, has no version number, uses the shared lock name.

  node .\Tests\RadioMeta.Diagnostics.SmokeTest.js
      Diagnostics version checks, local-session matching and bootstrap status.

  node .\Tests\RadioMeta.PlayerIntegration.SmokeTest.js
      The player script against simulated Emby modules. Run it once plainly
      and once with each of these set to 1:
        RADIOMETA_TEST_LIBRARY_ARTIST     artist found in the library; link added
                                          without a second full player refresh
        RADIOMETA_TEST_SLOW_ARTIST_LOOKUP song shown without waiting for the
                                          artist lookup
        RADIOMETA_TEST_SERVER_FALLBACK    song taken from /RadioMeta/MyCurrentTracks
        RADIOMETA_TEST_BIG_PLAYER_SWITCH  station change: old song cleared, layout
                                          held, list rows keep station names
        RADIOMETA_TEST_BIG_PLAYER_OPEN    new song with the big player open: no
                                          full refresh, logo kept
        RADIOMETA_TEST_BIG_PLAYER_CLOSED  new song with it closed: normal refresh
        RADIOMETA_TEST_OUTDATED_SCRIPT    older cached script: one Ctrl+F5 message
        RADIOMETA_TEST_PAGEHIDE           pagehide with the page kept open: hooks
                                          stay, or are put back, and the next
                                          song still reaches the players

All of them in PowerShell, from the package folder:

  node .\Tests\RadioMeta.Bootstrap.SmokeTest.js; node .\Tests\RadioMeta.Diagnostics.SmokeTest.js; node .\Tests\RadioMeta.PlayerIntegration.SmokeTest.js; foreach ($m in 'LIBRARY_ARTIST','SLOW_ARTIST_LOOKUP','SERVER_FALLBACK','BIG_PLAYER_SWITCH','BIG_PLAYER_OPEN','BIG_PLAYER_CLOSED','OUTDATED_SCRIPT','PAGEHIDE') { Set-Item -Path "env:RADIOMETA_TEST_$m" -Value '1'; node .\Tests\RadioMeta.PlayerIntegration.SmokeTest.js; Remove-Item -Path "env:RADIOMETA_TEST_$m" }

Every line should start with PASS.
