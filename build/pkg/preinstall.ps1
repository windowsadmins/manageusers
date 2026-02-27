# preinstall.ps1
# Remove legacy ManageUsers artifacts before new version installs

$ErrorActionPreference = 'SilentlyContinue'

Write-Host ""
Write-Host "[ManageUsers] Running preinstall cleanup..." -ForegroundColor Yellow

# Remove legacy scheduled task names (in case task was renamed)
$legacyTasks = @(
    'WinAdmins-ManageUsers',
    'ManageUsers'
)

$removedCount = 0
foreach ($taskName in $legacyTasks) {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Write-Host "[ManageUsers] Removed task: $taskName" -ForegroundColor Gray
        $removedCount++
    }
}
Write-Host "[ManageUsers] Removed $removedCount legacy task(s)" -ForegroundColor Green

# Remove legacy PowerShell script if it was from an earlier iteration
$legacyFiles = @(
    'C:\ProgramData\Management\Scripts\ManageUsers.ps1'
)

$removedFiles = 0
foreach ($file in $legacyFiles) {
    if (Test-Path $file) {
        Remove-Item -Path $file -Force -ErrorAction SilentlyContinue
        Write-Host "[ManageUsers] Removed legacy file: $file" -ForegroundColor Gray
        $removedFiles++
    }
}

if ($removedFiles -gt 0) {
    Write-Host "[ManageUsers] Removed $removedFiles legacy file(s)" -ForegroundColor Green
}

Write-Host "[ManageUsers] Preinstall cleanup completed." -ForegroundColor Green
exit 0
