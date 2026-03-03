# ManageUsers postinstall — set up config and scheduled task
# cimipkg auto-injects: $installLocation, $payloadRoot, $payloadDir
$ErrorActionPreference = 'Stop'

# Fallback — sbin-installer doesn't set $env:installLocation yet (TODO in PackageInstaller.cs)
if (-not $installLocation) { $installLocation = 'C:\Program Files\sbin' }

$binaryPath = Join-Path $installLocation 'manageusers.exe'
$configDir = 'C:\ProgramData\Management\ManageUsers'
$logDir = 'C:\ProgramData\Management\ManageUsers\Logs'
$taskName = 'WinAdmins-ManageUsers'

Write-Host ''
Write-Host '[ManageUsers] Installing ManageUsers package' -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green

# Verify binary was deployed by sbin-installer
if (-not (Test-Path $binaryPath)) {
    Write-Host "[ManageUsers] ERROR: Binary not found at $binaryPath" -ForegroundColor Red
    exit 1
}
Write-Host "[ManageUsers] Binary found: $binaryPath" -ForegroundColor Cyan

# Ensure directories exist
foreach ($dir in @($configDir, $logDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Host "[ManageUsers] Created directory: $dir" -ForegroundColor Gray
    }
}

# Deploy Sessions.yaml from embedded template if not present
$sessionsFile = Join-Path $configDir 'Sessions.yaml'
if (-not (Test-Path $sessionsFile)) {
    $sessionsTemplate = @'
Exclusions:
  - Administrator
  - DefaultAccount
  - Guest
  - WDAGUtilityAccount
  - defaultuser0
DeferredDeletes: []
'@
    Set-Content -Path $sessionsFile -Value $sessionsTemplate -Encoding UTF8
    Write-Host '[ManageUsers] Initialized Sessions.yaml from template' -ForegroundColor Gray
}

#region Register Scheduled Task

Write-Host ''
Write-Host '[ManageUsers] Configuring scheduled task...' -ForegroundColor Yellow

# Remove existing task if present
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host '[ManageUsers] Removed existing task' -ForegroundColor Gray
}

$action = New-ScheduledTaskAction -Execute $binaryPath
$triggerDaily = New-ScheduledTaskTrigger -Daily -At '03:00'
$triggerStartup = New-ScheduledTaskTrigger -AtStartup

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -MultipleInstances IgnoreNew

$principal = New-ScheduledTaskPrincipal `
    -UserId 'NT AUTHORITY\SYSTEM' `
    -LogonType ServiceAccount `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger @($triggerDaily, $triggerStartup) `
    -Settings $settings `
    -Principal $principal `
    -Description 'ManageUsers - Removes inactive local user accounts on shared devices.' | Out-Null

Write-Host "[ManageUsers] Scheduled task registered: $taskName" -ForegroundColor Green
Write-Host '[ManageUsers]   Daily at 3:00 AM + at startup' -ForegroundColor Gray
Write-Host '[ManageUsers]   Runs as SYSTEM' -ForegroundColor Gray

#endregion

# Verify
$verifyTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($verifyTask) {
    Write-Host "[ManageUsers] Task verification successful (State: $($verifyTask.State))" -ForegroundColor Green
} else {
    Write-Host '[ManageUsers] ERROR: Task verification failed' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '[ManageUsers] Postinstall completed successfully.' -ForegroundColor Green
exit 0
