$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$webUiRoot = Join-Path $projectRoot 'WebUI'
$sectionRoot = Join-Path $webUiRoot 'DashboardSections'
$outputPath = Join-Path $webUiRoot 'RadioMetaDashboard.js'

$sectionFiles = @(
    '00-Amd-Shell-And-Helper-Bootstrap.js.part',
    '01-Controller-Entry-And-Common-Helpers.js.part',
    '02-Station-Folder-Panel.js.part',
    '03-Play-History-Panel.js.part',
    '04-Station-Mode-Settings-Panel.js.part',
    '05-Ignore-Phrases-Panel.js.part',
    '06-Saved-Title-Fixes-Panel.js.part',
    '07-History-Actions-And-Path-Settings.js.part',
    '08-View-Lifecycle-And-Station-Creation.js.part',
    'Diagnostics/09-Diagnostic-Probes-And-Helpers.js.part',
    'Diagnostics/10-Diagnostic-Renderer.js.part',
    'Diagnostics/11-Diagnostic-Runner.js.part',
    'Diagnostics/12-Diagnostic-Copy-And-Fallback.js.part',
    '13-Current-Playback-And-Stream-Test-Helpers.js.part',
    '14-Current-Station-Form-And-Controller-Wiring.js.part'
)

$parts = New-Object System.Collections.Generic.List[string]
foreach ($fileName in $sectionFiles) {
    $path = Join-Path $sectionRoot $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Radio Meta dashboard source section is missing: $path"
    }
    $content = [System.IO.File]::ReadAllText($path)
    if ([string]::IsNullOrEmpty($content)) {
        throw "Radio Meta dashboard source section is empty: $path"
    }
    $parts.Add($content)
}

# These sections intentionally reassemble into one AMD resource so lexical scope,
# route/resource names, and browser request behavior remain unchanged.
$content = [string]::Join('', $parts.ToArray())
$content = $content -replace "`r`n", "`n"
$content = $content -replace "`r", "`n"

foreach ($required in @(
    "__radioMetaDashboardControllerInfo_v1382",
    "function scanStationFolder(completionNote, stationOperationData)",
    "function runRadioMetaDiagnostics()",
    "function addCurrentlyPlayingStationToForm()",
    "view.addEventListener('viewshow', onViewShow)"
)) {
    if ($content.IndexOf($required, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Generated dashboard controller is missing required marker: $required"
    }
}

if ($sectionFiles.Count -ne 15) {
    throw "Expected 15 dashboard source sections; found $($sectionFiles.Count)."
}

$utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
[System.IO.File]::WriteAllText($outputPath, $content, $utf8NoBom)
Write-Host "Built Radio Meta dashboard controller: $outputPath"
Write-Host ("Bytes: {0}; source sections: {1}" -f ([System.Text.Encoding]::UTF8.GetByteCount($content)), $sectionFiles.Count)
