$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$backupBase = Join-Path (Join-Path $packageRoot 'Backups') $env:COMPUTERNAME
if (-not (Test-Path -LiteralPath $backupBase -PathType Container)) { throw "No backups found for this computer: $backupBase" }
$states = @(Get-ChildItem -LiteralPath $backupBase -Filter 'install-state.json' -Recurse -File | Sort-Object LastWriteTime -Descending)
$chosenState = $null
foreach ($file in $states) {
    try {
        $candidate = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($candidate.ComputerName -eq $env:COMPUTERNAME -and $candidate.Status -eq 'Installed') { $chosenState = $candidate; break }
    } catch { }
}
if (-not $chosenState) { throw 'No completed loader installation is available to restore for this computer.' }
$target = Join-Path $chosenState.DashboardRoot $chosenState.RelativePath
$backupPath = [string]$chosenState.BackupPath
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Target file not found: $target" }
if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) { throw "Backup file not found: $backupPath" }
$currentHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()
if ($currentHash -ne [string]$chosenState.InstalledHash) { throw "Safety stop: current target hash $currentHash differs from the installed loader hash. No file changed." }
$backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($backupHash -ne [string]$chosenState.PreInstallHash) { throw 'Backup hash verification failed. No file changed.' }
Write-Host 'This restores the exact nowplayingbar.js file backed up before loader installation.' -ForegroundColor Cyan
Write-Host ("Target: {0}" -f $target)
Write-Host ("Backup: {0}" -f $backupPath)
$confirmation = Read-Host 'Type RESTORE to continue'
if ($confirmation -cne 'RESTORE') { Write-Host 'Cancelled. No files changed.'; exit 0 }
$preRestore = Join-Path (Join-Path (Join-Path $packageRoot 'Backups') $env:COMPUTERNAME) (('pre-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')))
New-Item -ItemType Directory -Path $preRestore -Force | Out-Null
Copy-Item -LiteralPath $target -Destination (Join-Path $preRestore 'nowplayingbar-loader-version.js') -Force
$temp = $target + '.radiometa-restore'
Copy-Item -LiteralPath $backupPath -Destination $temp -Force
Move-Item -LiteralPath $temp -Destination $target -Force
$restoredHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()
if ($restoredHash -ne [string]$chosenState.PreInstallHash) { throw 'Restore verification failed; preserve the backup folders and review the target file.' }
$chosenState.Status = 'Restored'
$chosenState.RestoredAt = (Get-Date).ToString('o')
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$stateFile = Join-Path $chosenState.BackupRoot 'install-state.json'
[System.IO.File]::WriteAllText($stateFile, ($chosenState | ConvertTo-Json -Depth 5), $utf8NoBom)
Write-Host ''
Write-Host 'Original file restored and hash-verified.' -ForegroundColor Green
Write-Host ("Pre-restore copy: {0}" -f $preRestore)
Write-Host 'Close and reopen Emby Web, then press Ctrl+F5.'
