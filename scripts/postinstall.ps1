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

# A link in place of the settings folder would send every read and write elsewhere.
$existing = Get-Item -LiteralPath $configDir -Force -ErrorAction SilentlyContinue
if ($existing -and ($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
    [System.IO.Directory]::Delete($configDir)
    Write-Host "[ManageUsers] Removed link at $configDir" -ForegroundColor Yellow
}

foreach ($dir in @($configDir, $logDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Host "[ManageUsers] Created directory: $dir" -ForegroundColor Gray
    }
}

# The owner this process may assign: SYSTEM when it runs as SYSTEM; an elevated
# administrator may only assign the Administrators group.
$isSystem = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18'
$ownerSid = if ($isSystem) { 'S-1-5-18' } else { 'S-1-5-32-544' }

# manageusers also checks the folders above its own. Whoever owns ProgramData\Management
# can rewrite its ACL, so an owner other than SYSTEM, Administrators or TrustedInstaller
# (an interactive session that created it first) is replaced. Its entries are left alone;
# the tool refuses anything below a folder a non-administrator can change.
$trustedOwners = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
$managementRoot = Split-Path $configDir -Parent
$rootAcl = Get-Acl -LiteralPath $managementRoot
$rootOwner = $rootAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
if ($trustedOwners -notcontains $rootOwner) {
    $rootAcl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier($ownerSid)))
    Set-Acl -LiteralPath $managementRoot -AclObject $rootAcl
    Write-Host "[ManageUsers] Changed owner of $managementRoot from $rootOwner" -ForegroundColor Yellow
}

# manageusers runs as SYSTEM and acts on Config.yaml and Sessions.yaml, so only
# administrators may change them. ProgramData lets any user create files below it, and a
# folder created there inherits that: replace it with SYSTEM and Administrators full
# control, Users read, inherited by everything below and not inherited from ProgramData.
# The owner becomes SYSTEM (Administrators when run from an elevated session), since
# whoever owns the folder can rewrite its ACL.
$lockedSddl = 'D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)'
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetSecurityDescriptorSddlForm($lockedSddl)
$acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier($ownerSid)))
Set-Acl -LiteralPath $configDir -AclObject $acl
Write-Host "[ManageUsers] Locked down $configDir (Administrators and SYSTEM full control, Users read)" -ForegroundColor Gray

# Existing files drop any explicit entries and take the folder's ACL. Their owner is left
# alone: manageusers refuses a file a non-administrator owns, and logs why.
& icacls.exe "$configDir\*" /reset /T /C /Q | Out-Null

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
