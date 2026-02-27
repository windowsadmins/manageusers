# postinstall.ps1
# Install ManageUsers package - sets up scheduled task for user account management

$ErrorActionPreference = 'Stop'

$installLocation = 'C:\Program Files\sbin'
$binaryPath = Join-Path $installLocation 'manageusers.exe'
$sessionsDir = 'C:\ProgramData\Management\ManageUsers'
$sessionsFile = Join-Path $sessionsDir 'Sessions.yaml'
$sessionsTemplate = Join-Path $sessionsDir 'Sessions.yaml.template'
$logDir = 'C:\ProgramData\Management\Logs'
$taskName = 'WinAdmins-ManageUsers'

Write-Host ""
Write-Host "[ManageUsers] Installing ManageUsers package" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green

# Verify binary was deployed
if (-not (Test-Path $binaryPath)) {
    Write-Host "[ManageUsers] ERROR: Binary not found at $binaryPath" -ForegroundColor Red
    exit 1
}

Write-Host "[ManageUsers] Binary found: $binaryPath" -ForegroundColor Cyan

# Ensure directories exist
foreach ($dir in @($sessionsDir, $logDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Host "[ManageUsers] Created directory: $dir" -ForegroundColor Gray
    }
}

# Initialize Sessions.yaml from template if not present
if (-not (Test-Path $sessionsFile)) {
    if (Test-Path $sessionsTemplate) {
        Copy-Item -Path $sessionsTemplate -Destination $sessionsFile -Force
        Write-Host "[ManageUsers] Initialized Sessions.yaml from template" -ForegroundColor Gray
    } else {
        Write-Host "[ManageUsers] WARNING: No Sessions.yaml template found" -ForegroundColor Yellow
    }
}

#region Register Scheduled Task

Write-Host ""
Write-Host "[ManageUsers] Configuring scheduled task..." -ForegroundColor Yellow

# Remove existing task if present
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "[ManageUsers] Removed existing task" -ForegroundColor Gray
}

# Action - run the signed binary directly (no PowerShell wrapper)
$action = New-ScheduledTaskAction -Execute $binaryPath

# Triggers - daily at 3:00 AM + at system startup
$triggerDaily = New-ScheduledTaskTrigger -Daily -At '03:00'
$triggerStartup = New-ScheduledTaskTrigger -AtStartup

# Settings
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -MultipleInstances IgnoreNew

# Principal - run as SYSTEM with highest privileges
$principal = New-ScheduledTaskPrincipal `
    -UserId 'NT AUTHORITY\SYSTEM' `
    -LogonType ServiceAccount `
    -RunLevel Highest

# Register
Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger @($triggerDaily, $triggerStartup) `
    -Settings $settings `
    -Principal $principal `
    -Description 'ManageUsers - Removes inactive local user accounts on shared devices based on area/room deletion policies.' | Out-Null

Write-Host "[ManageUsers] Scheduled task registered: $taskName" -ForegroundColor Green
Write-Host "[ManageUsers]   Daily at 3:00 AM + at startup" -ForegroundColor Gray
Write-Host "[ManageUsers]   Runs as SYSTEM" -ForegroundColor Gray

#endregion

# Verify
$verifyTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($verifyTask) {
    Write-Host "[ManageUsers] Task verification successful (State: $($verifyTask.State))" -ForegroundColor Green
} else {
    Write-Host "[ManageUsers] ERROR: Task verification failed" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "[ManageUsers] Postinstall completed successfully." -ForegroundColor Green
exit 0
