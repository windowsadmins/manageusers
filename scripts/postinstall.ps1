# ManageUsers postinstall — verify binary, seed working directories, lock down settings.
# Scheduled task is registered by the ManageUsersPrefs package so the
# schedule is a preference and can be updated without rebuilding the binary.
# cimipkg auto-injects: $installLocation, $payloadRoot, $payloadDir
$ErrorActionPreference = 'Stop'

if (-not $installLocation) { $installLocation = 'C:\Program Files\sbin' }

$binaryPath = Join-Path $installLocation 'manageusers.exe'
$configDir = 'C:\ProgramData\Management\ManageUsers'
$logDir = 'C:\ProgramData\ManagedUsers\logs'

Write-Host ''
Write-Host '[ManageUsers] Installing ManageUsers package' -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green

if (-not (Test-Path $binaryPath)) {
    Write-Host "[ManageUsers] ERROR: Binary not found at $binaryPath" -ForegroundColor Red
    exit 1
}
Write-Host "[ManageUsers] Binary found: $binaryPath" -ForegroundColor Cyan

# The owner this process may assign: SYSTEM when it runs as SYSTEM; an elevated
# administrator may only assign the Administrators group.
$isSystem = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18'
$ownerSid = if ($isSystem) { 'S-1-5-18' } else { 'S-1-5-32-544' }

# manageusers runs as SYSTEM: it acts on Config.yaml and Sessions.yaml, and writes its
# logs, so only administrators may change either folder. ProgramData lets any user create
# files below it, and a folder created there inherits that: replace it with SYSTEM and
# Administrators full control, Users read, inherited by everything below and not
# inherited from ProgramData. The owner changes too, since whoever owns a folder can
# rewrite its ACL. Existing files drop explicit entries and take the folder's ACL; their
# owner is left alone, as manageusers refuses a settings file a non-administrator owns.
# Walks below a locked folder without following links: a link is deleted, and every
# other entry has its explicit ACL entries dropped before anything inside it is visited,
# so nothing can be planted behind the walk. icacls /T is not used because it would
# follow a junction out of the folder.
function Reset-Below([string]$Path) {
    foreach ($entry in [System.IO.Directory]::GetFileSystemEntries($Path)) {
        $attributes = [System.IO.File]::GetAttributes($entry)
        if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            if ($attributes -band [System.IO.FileAttributes]::Directory) { [System.IO.Directory]::Delete($entry) }
            else { [System.IO.File]::Delete($entry) }
            Write-Host "[ManageUsers] Removed link at $entry" -ForegroundColor Yellow
            continue
        }
        & icacls.exe $entry /reset /C /Q | Out-Null
        if ($attributes -band [System.IO.FileAttributes]::Directory) { Reset-Below $entry }
    }
}

function Lock-Folder([string]$Path) {
    # A link in place of the folder would send every read and write elsewhere.
    $existing = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($existing -and ($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        [System.IO.Directory]::Delete($Path)
        Write-Host "[ManageUsers] Removed link at $Path" -ForegroundColor Yellow
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        Write-Host "[ManageUsers] Created directory: $Path" -ForegroundColor Gray
    }

    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetSecurityDescriptorSddlForm('D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)')
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier($ownerSid)))
    Set-Acl -LiteralPath $Path -AclObject $acl
    Reset-Below $Path
    Write-Host "[ManageUsers] Locked down $Path (Administrators and SYSTEM full control, Users read)" -ForegroundColor Gray
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
$trustedOwners = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
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

Write-Host ''
Write-Host '[ManageUsers] Postinstall completed successfully.' -ForegroundColor Green
Write-Host '[ManageUsers] Schedule is owned by ManageUsersPrefs.' -ForegroundColor Gray
exit 0
