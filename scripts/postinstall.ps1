# ManageUsers postinstall — verify binary, seed working directories.
# Scheduled task is registered by the ManageUsersPrefs package so the
# schedule is a preference and can be updated without rebuilding the binary.
# cimipkg auto-injects: $installLocation, $payloadRoot, $payloadDir
$ErrorActionPreference = 'Stop'

if (-not $installLocation) { $installLocation = 'C:\Program Files\sbin' }

$binaryPath = Join-Path $installLocation 'manageusers.exe'
$configDir = 'C:\ProgramData\Management\ManageUsers'
$logDir = 'C:\ProgramData\Management\ManageUsers\Logs'

Write-Host ''
Write-Host '[ManageUsers] Installing ManageUsers package' -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green

if (-not (Test-Path $binaryPath)) {
    Write-Host "[ManageUsers] ERROR: Binary not found at $binaryPath" -ForegroundColor Red
    exit 1
}
Write-Host "[ManageUsers] Binary found: $binaryPath" -ForegroundColor Cyan

foreach ($dir in @($configDir, $logDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Host "[ManageUsers] Created directory: $dir" -ForegroundColor Gray
    }
}

$sessionsFile = Join-Path $configDir 'Sessions.yaml'
if (-not (Test-Path $sessionsFile)) {
    $sessionsTemplate = @'
Exclusions:
  - Administrator
  - DefaultAccount
  - Guest
  - WDAGUtilityAccount
  - defaultuser0
  - winadmins
  - ithelp
DeferredDeletes: []
'@
    Set-Content -Path $sessionsFile -Value $sessionsTemplate -Encoding UTF8
    Write-Host '[ManageUsers] Initialized Sessions.yaml from template' -ForegroundColor Gray
}

Write-Host ''
Write-Host '[ManageUsers] Postinstall completed successfully.' -ForegroundColor Green
Write-Host '[ManageUsers] Schedule is owned by ManageUsersPrefs.' -ForegroundColor Gray
exit 0
