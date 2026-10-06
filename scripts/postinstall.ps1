# ManageUsers postinstall — verify binary, move off the old shared install folder,
# seed working directories, lock down settings.
# Scheduled task is registered by the ManageUsersPrefs package so the
# schedule is a preference and can be updated without rebuilding the binary.
# cimipkg auto-injects: $installLocation, $payloadRoot, $payloadDir
$ErrorActionPreference = 'Stop'

if (-not $installLocation) { $installLocation = 'C:\Program Files\ManageUsers' }

$binaryPath = Join-Path $installLocation 'manageusers.exe'
$configDir = 'C:\ProgramData\Management\ManageUsers'
$logDir = 'C:\ProgramData\ManagedUsers\logs'

# Earlier releases installed into the folder shared by several tools.
$legacyDir = 'C:\Program Files\sbin'
$legacyBinary = Join-Path $legacyDir 'manageusers.exe'

$systemSid = 'S-1-5-18'
$adminsSid = 'S-1-5-32-544'
$trustedOwners = @($systemSid, $adminsSid, 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
$lockedDacl = 'D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)'

Write-Host ''
Write-Host '[ManageUsers] Installing ManageUsers package' -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green

if (-not (Test-Path $binaryPath)) {
    Write-Host "[ManageUsers] ERROR: Binary not found at $binaryPath" -ForegroundColor Red
    exit 1
}
Write-Host "[ManageUsers] Binary found: $binaryPath" -ForegroundColor Cyan

# ── Move off the shared folder ───────────────────────────────

# Point any scheduled task that still runs the old copy at the new one, keeping its
# arguments, working directory, triggers and principal. Only tasks whose action is
# exactly the old manageusers.exe are touched.
foreach ($task in Get-ScheduledTask -ErrorAction SilentlyContinue) {
    $changed = $false
    $actions = foreach ($action in $task.Actions) {
        $execute = if ($action.CimClass.CimClassName -eq 'MSFT_TaskExecAction') {
            [Environment]::ExpandEnvironmentVariables(($action.Execute -as [string]).Trim().Trim('"'))
        }
        if ($execute -and ($execute -ieq $legacyBinary)) {
            $changed = $true
            $splat = @{ Execute = $binaryPath }
            if ($action.Arguments) { $splat.Argument = $action.Arguments }
            if ($action.WorkingDirectory) { $splat.WorkingDirectory = $action.WorkingDirectory }
            New-ScheduledTaskAction @splat
        } else {
            $action
        }
    }
    if ($changed) {
        try {
            Set-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Action $actions | Out-Null
            Write-Host "[ManageUsers] Scheduled task $($task.TaskPath)$($task.TaskName) now runs $binaryPath" -ForegroundColor Gray
        } catch {
            Write-Host "[ManageUsers] WARNING: could not repoint task $($task.TaskName): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

# Remove the old copies: ours only, never anything else in the shared folder. Test builds
# of the app installed in its own folder there.
$legacyApp = Join-Path $legacyDir 'Managed Users Cleanup'
if ($installLocation.TrimEnd('\') -ine $legacyDir) {
    foreach ($old in @($legacyBinary, $legacyApp)) {
        if (-not (Test-Path -LiteralPath $old)) { continue }
        try {
            Remove-Item -LiteralPath $old -Recurse -Force
            Write-Host "[ManageUsers] Removed the old copy at $old" -ForegroundColor Gray
        } catch {
            Write-Host "[ManageUsers] WARNING: could not remove $old : $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

# ── Lock down the settings and data folders ──────────────────

# The owner this process may assign: SYSTEM when it runs as SYSTEM; an elevated
# administrator may only assign the Administrators group.
$isSystem = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq $systemSid
$ownerSid = if ($isSystem) { $systemSid } else { $adminsSid }

# Local Administrators members, by SID. An account that is a member only through a
# domain or Entra group cannot be resolved here and counts as unknown.
$localAdmins = @()
try {
    $localAdmins = @(Get-LocalGroupMember -SID $adminsSid -ErrorAction Stop | ForEach-Object { $_.SID.Value })
} catch {
    Write-Host "[ManageUsers] WARNING: could not list local Administrators: $($_.Exception.Message)" -ForegroundColor Yellow
}

function Get-OwnerSid([string]$Path) {
    try { (Get-Acl -LiteralPath $Path).GetOwner([System.Security.Principal.SecurityIdentifier]).Value } catch { $null }
}

# Walks below a locked folder without following links. A link is deleted. Every other
# entry has its explicit ACL entries dropped before anything inside it is visited, so
# nothing can be planted behind the walk; icacls /T is not used because it would follow
# a junction out of the folder. Then its owner: an entry owned by an individual account
# is handed to Administrators, so that account loses the owner's implicit right to
# change its ACL. On a folder's first lockdown, an entry whose owner is not known to be
# an administrator is quarantined instead: before the lockdown any user could have
# created it, and manageusers trusts what is in a locked folder.
function Reset-Below([string]$Path, [bool]$FirstLockdown, [string]$Quarantine) {
    foreach ($entry in [System.IO.Directory]::GetFileSystemEntries($Path)) {
        if ($Quarantine -and ($entry -ieq $Quarantine)) { continue }
        $attributes = [System.IO.File]::GetAttributes($entry)
        if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            if ($attributes -band [System.IO.FileAttributes]::Directory) { [System.IO.Directory]::Delete($entry) }
            else { [System.IO.File]::Delete($entry) }
            Write-Host "[ManageUsers] Removed link at $entry" -ForegroundColor Yellow
            continue
        }

        $owner = Get-OwnerSid $entry
        $knownAdmin = ($trustedOwners -contains $owner) -or ($localAdmins -contains $owner)
        if ($FirstLockdown -and -not $knownAdmin) {
            if (-not (Test-Path -LiteralPath $Quarantine)) { New-Item -ItemType Directory -Path $Quarantine -Force | Out-Null }
            $name = [System.IO.Path]::GetFileName($entry)
            $target = Join-Path $Quarantine $name
            for ($n = 2; Test-Path -LiteralPath $target; $n++) { $target = Join-Path $Quarantine "$name.$n" }
            Move-Item -LiteralPath $entry -Destination $target -Force
            & icacls.exe $target /reset /C /Q | Out-Null
            & icacls.exe $target /setowner "*$adminsSid" /C /Q | Out-Null
            Write-Host "[ManageUsers] Quarantined $entry (owner ${owner}, not known to be an administrator) to $target" -ForegroundColor Yellow
            continue
        }

        & icacls.exe $entry /reset /C /Q | Out-Null
        if ($trustedOwners -notcontains $owner) {
            & icacls.exe $entry /setowner "*$adminsSid" /C /Q | Out-Null
            Write-Host "[ManageUsers] Gave ownership of $entry to Administrators (was $owner)" -ForegroundColor Gray
        }
        if ($attributes -band [System.IO.FileAttributes]::Directory) { Reset-Below $entry $FirstLockdown $Quarantine }
    }
}

# Replaces a folder's ACL with SYSTEM and Administrators full control, Users read,
# inherited by everything below and not inherited from ProgramData, where any user can
# create files. The owner changes too, since whoever owns a folder can rewrite its ACL.
function Lock-Folder([string]$Path) {
    # A link in place of the folder would send every read and write elsewhere.
    $existing = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($existing -and ($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        [System.IO.Directory]::Delete($Path)
        Write-Host "[ManageUsers] Removed link at $Path" -ForegroundColor Yellow
        $existing = $null
    }
    if (-not $existing) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        Write-Host "[ManageUsers] Created directory: $Path" -ForegroundColor Gray
    }

    $current = (Get-Acl -LiteralPath $Path).GetSecurityDescriptorSddlForm('Access')
    $firstLockdown = $current -ne $lockedDacl
    $quarantine = Join-Path $Path ("Quarantine-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetSecurityDescriptorSddlForm($lockedDacl)
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier($ownerSid)))
    Set-Acl -LiteralPath $Path -AclObject $acl
    Reset-Below $Path $firstLockdown $quarantine
    Write-Host "[ManageUsers] Locked down $Path (Administrators and SYSTEM full control, Users read$(if ($firstLockdown) { ', first lockdown' }))" -ForegroundColor Gray
}

# manageusers also checks the folders above its settings folder. Whoever owns
# ProgramData\Management can rewrite its ACL, so an owner other than SYSTEM,
# Administrators or TrustedInstaller (an interactive session that created it first) is
# replaced. Its entries are left alone; the tool refuses anything below a folder a
# non-administrator can change. The folder is shared with other tools, so it is not
# locked down here.
$managementRoot = Split-Path $configDir -Parent
if (-not (Test-Path -LiteralPath $managementRoot)) {
    New-Item -ItemType Directory -Path $managementRoot -Force | Out-Null
}
$rootAcl = Get-Acl -LiteralPath $managementRoot
$rootOwner = $rootAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
if ($trustedOwners -notcontains $rootOwner) {
    $rootAcl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier($ownerSid)))
    Set-Acl -LiteralPath $managementRoot -AclObject $rootAcl
    Write-Host "[ManageUsers] Changed owner of $managementRoot from $rootOwner" -ForegroundColor Yellow
}

# The logs folder's parent is manageusers' own, so it is locked as a whole.
Lock-Folder $configDir
Lock-Folder (Split-Path $logDir -Parent)
if (-not (Test-Path -LiteralPath $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
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

# Managed Users Cleanup, the settings and run app, installs beside manageusers.exe.
# Give it a Start menu entry for every user.
$appExe = Join-Path $installLocation 'Managed Users Cleanup.exe'
if (Test-Path $appExe) {
    $shortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Managed Users Cleanup.lnk'
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $appExe
        $shortcut.WorkingDirectory = Split-Path $appExe -Parent
        $shortcut.Description = 'Settings, simulation and cleanup for ManageUsers'
        $shortcut.Save()
        Write-Host "[ManageUsers] Start menu shortcut: $shortcutPath" -ForegroundColor Gray
    } catch {
        Write-Host "[ManageUsers] WARNING: could not create the Start menu shortcut: $($_.Exception.Message)" -ForegroundColor Yellow
    }
} else {
    Write-Host "[ManageUsers] Managed Users Cleanup not in this package; no Start menu shortcut" -ForegroundColor Gray
}

Write-Host ''
Write-Host '[ManageUsers] Postinstall completed successfully.' -ForegroundColor Green
Write-Host '[ManageUsers] Schedule is owned by ManageUsersPrefs.' -ForegroundColor Gray
exit 0
