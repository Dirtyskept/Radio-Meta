EMBY REFERENCES
===============

Radio Meta is compiled against the OLDEST Emby it supports, so one DLL loads on
that Emby and on newer ones. A DLL compiled against a newer Emby doesn't load on
older Emby versions: Emby skips it without a message.

This folder holds Emby's own assemblies for that build, one folder per version:

    EmbyReferences\4.10.2\MediaBrowser.Common.dll
    EmbyReferences\4.10.2\MediaBrowser.Controller.dll
    EmbyReferences\4.10.2\MediaBrowser.Model.dll

They are only compiled against. They are not part of the plugin, and they are
NOT included in Radio Meta's source zips (they are Emby's files). Keep this
folder when you replace the source with a new release: extracting a release
over the old folder keeps it; deleting the old folder first does not.

HOW TO FILL IT (once, on a server running that Emby version)
-------------------------------------------------------------
In PowerShell, in the folder that contains RadioMeta.csproj:

    $dest = ".\EmbyReferences\4.10.2"
    New-Item -ItemType Directory -Force $dest | Out-Null
    $sys = "$env:APPDATA\Emby-Server\system"
    Copy-Item "$sys\MediaBrowser.Common.dll","$sys\MediaBrowser.Controller.dll","$sys\MediaBrowser.Model.dll" $dest
    Get-ChildItem $dest -Filter *.dll | ForEach-Object { "{0}  {1}" -f $_.Name, $_.VersionInfo.FileVersion }

All three should show the same Emby version (4.10.2.x).

CHANGING THE VERSION LATER
--------------------------
The folder is set in ONE line in RadioMeta.csproj:

    <EmbyReferencePath ...>EmbyReferences\4.10.2</EmbyReferencePath>

Change it to another folder (relative to RadioMeta.csproj, or a full path)
that holds the three DLLs of the Emby version you want as the minimum. The
build script reads the same line.

TEST BUILDS
-----------
Build against the installed Emby instead:
  .\Build-RadioMeta<version>.ps1 -UseInstalledEmby
  (or dotnet build ... -p:UseInstalledEmby=true)
The build says it is a TEST BUILD. Don't release that DLL.
