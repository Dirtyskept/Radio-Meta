$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$relativePath = 'modules\nowplayingbar\nowplayingbar.js'
$defaultRoot = Join-Path $env:APPDATA 'Emby-Server\system\dashboard-ui'
$candidates = @()
$possibleRoots = @(
    $defaultRoot,
    (Join-Path $env:ProgramData 'Emby-Server\system\dashboard-ui'),
    (Join-Path $env:ProgramFiles 'Emby-Server\system\dashboard-ui')
)
if (${env:ProgramFiles(x86)}) { $possibleRoots += (Join-Path ${env:ProgramFiles(x86)} 'Emby-Server\system\dashboard-ui') }
foreach ($candidate in $possibleRoots) {
    if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate $relativePath) -PathType Leaf) -and $candidate -notin $candidates) { $candidates += $candidate }
}
if ($candidates.Count -eq 1) {
    $dashboardRoot = $candidates[0]
} elseif ($candidates.Count -gt 1) {
    Write-Host 'More than one Emby dashboard file was found:' -ForegroundColor Yellow
    for ($i = 0; $i -lt $candidates.Count; $i++) { Write-Host (('{0}. {1}' -f ($i + 1), $candidates[$i])) }
    $choice = Read-Host 'Choose a number or press Enter to type a path'
    $number = 0
    if ([int]::TryParse($choice, [ref]$number) -and $number -ge 1 -and $number -le $candidates.Count) { $dashboardRoot = $candidates[$number - 1] }
    else { $dashboardRoot = (Read-Host 'Full path to dashboard-ui').Trim().Trim('"') }
} else {
    Write-Host 'Automatic discovery did not find the Emby dashboard.' -ForegroundColor Yellow
    $dashboardRoot = (Read-Host 'Full path to Emby system\dashboard-ui').Trim().Trim('"')
}
if (-not (Test-Path -LiteralPath $dashboardRoot -PathType Container)) { throw "Dashboard folder not found: $dashboardRoot" }
$target = Join-Path $dashboardRoot $relativePath
$payload = Join-Path $packageRoot 'Files\Install\modules\nowplayingbar\nowplayingbar.js'
if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) { throw "Loader payload missing: $payload" }
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Target file not found: $target" }
$expectedPayloadHash = 'D341832F57BD3CF817E08EDE214914C7D50C2B5A141F0C41EEA05C24C433693F'
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToUpperInvariant()
if ($payloadHash -ne $expectedPayloadHash) { throw 'Package payload hash check failed. No target file was changed.' }
$currentHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()
$acceptedHashes = @(
    'B6032C570D4B6083D274A3756425EF98D6904F6334E8D2FB34EE443B17F791E3',
    'F76034051672EDAACB5312E997CBFA78E41039AF0E37598370C2CC9F0C17147A',
    '6524D875CFF92FEC1C715ABD46E3F56032D3F61FF4E69481FBB177115D12B7E3',
    'CCC210CD20CFB518D3240D386208B9A0515D83CB292019C6AACB76F8EF259D09',
    '79EC3261A9CBF775D59B25EF2091DB319C1F5F30506D96AB7A38FACB06FCBA03',
    'DD3CAED8D64A903B51274AB0172A88B712471C23EA732901BA9C21562B1E55B4',
    # Previously installed Radio Meta v1.3.64 loader uploaded for compatibility review.
    'F0D23BCB4C2DD31C17B48C3602FA03CB8C275A2C62BB4A514DC352910628E574',
    # Previously installed v1.3.74 packaged loader before this cleanup; allow a safe in-place update.
    'FF7D0285B24760FA2227F6D70276AD03DDE3C9F7E6ECEBC4343B65A7696DA298',
    # Intermediate retry-safe loader build; allow verified in-place update.
    'B65A024E9B2C1FBD79D3739C509065D7BF9B3F9DACF76E8E8D85CD09B6D83DC9',
    # Previous v1.3.74 Safe Cleanup payload; allow a hash-verified update to the retry-safe loader.
    '8AD7DF8E0BD6DBAE8BE4B27A309358359D4EEEE890C162E96794C98D51573234',
    # Previous Rev 2 payload; allow a hash-verified in-place upgrade to the AMD-loader fix.
    '3385C6FD7EFFF405B4EFC82E15D50F5980B7A44157B8066C7C7F58A3681D24D2',
    # Previous Rev 3/Rev 4 top-level loader payload; allow safe in-place upgrade to the factory-bound startup fix.
    '4EA6010ACD063CD12FB3CBA0D43795BF0617F09FC2E919A0B1820E7B00DF87E4',
    # Rev 5 full loader implementation embedded inline; safely replace with the DLL-hosted loader pointer.
    '20D0D4AAE075E7DF9FBC2FF76E52CA02C33B343E00CB70ED4779EDD04194BBEA',
    # Rev 6 DLL-hosted loader pointer; allow in-place update to the shorter bootstrap pointer.
    '2F0CF17D918BCE7C837C5F4B35D0ADC11CC77066164A9B1C83D671F317062935',
    $expectedPayloadHash
)
if ($currentHash -notin $acceptedHashes) { throw "Safety stop: target hash $currentHash is not recognized. No files changed. This package targets the stock Emby 4.10.1 dashboard or a known Radio Meta integration release." }
if ($currentHash -eq $expectedPayloadHash) { Write-Host 'The Radio Meta plugin loader is already installed and hash-verified.' -ForegroundColor Green; exit 0 }
Write-Host ''
Write-Host 'Radio Meta v1.3.96 — WebLoader installer (DLL-hosted helper pointer)' -ForegroundColor Cyan
Write-Host ("Target file: {0}" -f $target)
Write-Host 'This changes only nowplayingbar.js: stock Emby code plus a minimal AMD pointer to the loader module embedded in RadioMeta.dll.'
Write-Host 'Build/install the matching Radio Meta plugin DLL and restart Emby before testing the display helper.'
$confirmation = Read-Host 'Back up this computer existing file and install the loader? Type INSTALL to continue'
if ($confirmation -cne 'INSTALL') { Write-Host 'Cancelled. No files changed.'; exit 0 }
$backupBase = Join-Path (Join-Path $packageRoot 'Backups') $env:COMPUTERNAME
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backupRoot = Join-Path $backupBase $stamp
$backupPath = Join-Path (Join-Path $backupRoot 'PreInstall') $relativePath
New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
Copy-Item -LiteralPath $target -Destination $backupPath -Force
$backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($backupHash -ne $currentHash) { throw 'Pre-install backup verification failed. No replacement attempted.' }
$state = [pscustomobject]@{
    PackageVersion = '1.3.96-WebLoader-DLL-Pointer'
    ComputerName = $env:COMPUTERNAME
    DashboardRoot = $dashboardRoot
    BackupRoot = $backupRoot
    BackupPath = $backupPath
    RelativePath = $relativePath
    CreatedAt = (Get-Date).ToString('o')
    Status = 'Prepared'
    PreInstallHash = $currentHash
    InstalledHash = $expectedPayloadHash
    InstalledAt = $null
    RestoredAt = $null
    Error = $null
}
$statePath = Join-Path $backupRoot 'install-state.json'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 5), $utf8NoBom)
try {
    $temp = $target + '.radiometa-new'
    Copy-Item -LiteralPath $payload -Destination $temp -Force
    Move-Item -LiteralPath $temp -Destination $target -Force
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $expectedPayloadHash) { throw 'Installed loader SHA-256 verification failed.' }
    $state.Status = 'Installed'
    $state.InstalledAt = (Get-Date).ToString('o')
    [System.IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 5), $utf8NoBom)
    Write-Host ''
    Write-Host 'Installation succeeded; loader file hash verified.' -ForegroundColor Green
    Write-Host ("Per-computer backup: {0}" -f $backupPath)
    Write-Host 'Close/reopen Emby Web, then press Ctrl+F5.'
} catch {
    $failure = $_.Exception.Message
    try {
        $rollbackTemp = $target + '.radiometa-rollback'
        Copy-Item -LiteralPath $backupPath -Destination $rollbackTemp -Force
        Move-Item -LiteralPath $rollbackTemp -Destination $target -Force
        $rollbackHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($rollbackHash -ne $currentHash) { throw 'Automatic rollback hash verification failed.' }
        $state.Status = 'RolledBackAfterInstallError'
        $state.Error = $failure
        $state.RestoredAt = (Get-Date).ToString('o')
        [System.IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 5), $utf8NoBom)
        Write-Host ("Install failed; the exact original file was restored. {0}" -f $failure) -ForegroundColor Red
    } catch {
        Write-Host ("Install failed: {0}" -f $failure) -ForegroundColor Red
        Write-Host ("Automatic rollback also reported: {0}" -f $_.Exception.Message) -ForegroundColor Red
        Write-Host ("Original backup remains at: {0}" -f $backupPath) -ForegroundColor Yellow
    }
    throw
}
