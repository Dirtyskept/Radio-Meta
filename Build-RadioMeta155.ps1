param(
    # Test build against the installed Emby (%APPDATA%\Emby-Server\system) instead of
    # EmbyReferencePath. That DLL may not load on older Emby versions: don't release it.
    [switch]$UseInstalledEmby
)
$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'RadioMeta\RadioMeta.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Radio Meta project file was not found: $projectPath"
}

$required = @('MediaBrowser.Common.dll', 'MediaBrowser.Controller.dll', 'MediaBrowser.Model.dll')
if ($UseInstalledEmby) {
    $embyPath = Join-Path $env:APPDATA 'Emby-Server\system'
} else {
    # The folder is set in one place: EmbyReferencePath in RadioMeta.csproj.
    $projectText = Get-Content -LiteralPath $projectPath -Raw
    if ($projectText -notmatch '<EmbyReferencePath[^>]*>([^<]+)</EmbyReferencePath>') {
        throw 'EmbyReferencePath was not found in RadioMeta.csproj.'
    }
    $referencePath = $Matches[1].Trim()
    if ([System.IO.Path]::IsPathRooted($referencePath)) { $embyPath = $referencePath }
    else { $embyPath = Join-Path (Split-Path $projectPath -Parent) $referencePath }
}
foreach ($name in $required) {
    $path = Join-Path $embyPath $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        if ($UseInstalledEmby) { throw "Required Emby assembly was not found: $path" }
        throw "Missing $path. Copy MediaBrowser.Common/Controller/Model.dll from that Emby's system folder into $embyPath (see RadioMeta\EmbyReferences\README.txt), or change EmbyReferencePath in RadioMeta.csproj."
    }
}

Write-Host 'Building Radio Meta 1.3.155 against:' -ForegroundColor Cyan
foreach ($name in $required) {
    $info = (Get-Item -LiteralPath (Join-Path $embyPath $name)).VersionInfo
    Write-Host ("  {0}  {1}" -f $name, $info.FileVersion)
}
Write-Host "  from $embyPath"
if ($UseInstalledEmby) {
    Write-Host 'TEST BUILD against the installed Emby: this DLL may not load on older Emby versions. Do not release it.' -ForegroundColor Yellow
}

& dotnet restore $projectPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}

$buildArgs = @($projectPath, '-c', 'Release', '-t:Rebuild')
if ($UseInstalledEmby) { $buildArgs += '-p:UseInstalledEmby=true'; $buildArgs += "-p:EmbySystemDir=$embyPath" }
& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$dllPath = Join-Path (Split-Path $projectPath -Parent) 'bin\Release\net8.0\RadioMeta.dll'
if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
    throw "Build reported success but RadioMeta.dll was not found: $dllPath"
}

Write-Host ''
Write-Host 'BUILD SUCCEEDED' -ForegroundColor Green
Get-Item -LiteralPath $dllPath | Select-Object FullName, Length, LastWriteTime | Format-List
