# ManageUsers

Signed Windows binary that manages local user accounts on shared devices. Removes inactive accounts based on area/room-driven deletion policies, end-of-term forced deletion, and creation/login-based strategies.

Designed for enterprise environments with 10,000+ devices managed by [Cimian](https://github.com/windowsadmins/cimian).

## How It Works

1. Reads device inventory from `C:\ProgramData\Management\Inventory.yaml` (or a custom path via `--inventory`)
2. Resolves its settings from policy, machine settings and `C:\ProgramData\Management\ManageUsers\Config.yaml` (see [Settings](#settings)) — first matching rule wins
3. Enumerates local users, gathers creation dates and last login times
4. Deletes users that exceed the inactivity threshold
5. Cleans up orphaned accounts and profiles
6. Clears each removed profile's recycle bin, and sweeps recycle bins whose SID no longer has a profile
7. Runs as a signed SYSTEM scheduled task — no PowerShell, no script block logging noise

## Policy Configuration

All deletion policies are defined in `C:\ProgramData\Management\ManageUsers\Config.yaml`. Rules are evaluated in order — first match wins. Area and room values are regex patterns matched against inventory.

```yaml
exclusions:
  - svc-admin
  - svc-helpdesk
  - kiosk-user

policies:
  - name: "Kiosk Labs"
    match:
      area: "Library|Kiosk|DropIn"
    duration_days: 2
    strategy: creation_only
    force_at_end_of_term: false

  - name: "Studio Labs"
    match:
      area: "Studio|Workshop"
      room: "A200|C310"
    duration_days: 30
    strategy: creation_only
    force_at_end_of_term: false

  - name: "Assigned Labs"
    match:
      area: "Classroom|Lab"
      room: "D105|E220"
    duration_days: 42
    strategy: login_and_creation
    force_at_end_of_term: true

default_policy:
  duration_days: 28
  strategy: login_and_creation
  force_at_end_of_term: false

end_of_term_dates:
  - month: 4
    day: 30
  - month: 8
    day: 31
  - month: 12
    day: 31
```

**Strategies:**
- **creation_only** — deletes accounts older than the threshold regardless of login activity
- **login_and_creation** — deletes accounts that are both old enough *and* haven't logged in recently

**Match logic:** When both `area` and `room` are specified, either can match (OR). When only one is specified, it must match.

## Settings

Each setting is resolved on its own, highest precedence first:

1. A command-line flag for this run (`--inventory`)
2. Policy: `HKLM\SOFTWARE\Policies\ManageUsers`
3. Machine settings: `HKLM\SOFTWARE\ManageUsers\Settings`
4. `C:\ProgramData\Management\ManageUsers\Config.yaml`
5. The built-in default

Both registry keys are read in the 64-bit view. Every setting can be set by policy. Each run logs the source of every setting that did not come from its default, and a value it cannot use is logged and skipped so the next layer applies.

| Registry value | Type | Config.yaml key |
|---|---|---|
| `Exclusions` | REG_MULTI_SZ, or REG_SZ separated by `;` `,` or newlines | `exclusions` |
| `DeleteAdmins` | REG_DWORD 0/1, or REG_SZ `true`/`false` | `delete_admins` |
| `DeletableAdmins` | REG_MULTI_SZ, or separated REG_SZ | `deletable_admins` |
| `Policies` | REG_SZ or REG_MULTI_SZ holding the YAML or JSON rule list | `policies` |
| `DefaultPolicyDurationDays` | REG_DWORD | `default_policy.duration_days` |
| `DefaultPolicyStrategy` | REG_SZ | `default_policy.strategy` |
| `DefaultPolicyForceAtEndOfTerm` | REG_DWORD 0/1, or REG_SZ `true`/`false` | `default_policy.force_at_end_of_term` |
| `EndOfTermDates` | REG_MULTI_SZ, or separated REG_SZ, of month-day pairs such as `4-30` | `end_of_term_dates` |
| `InventoryPath` | REG_SZ | `inventory_path` |

A list value that is present but empty clears the list for lower layers. The built-in end-of-term dates (April 30, August 31, December 31) apply only when no layer supplies policy rules.

For example, this sets the policy rules as JSON:

```pwsh
New-Item -Path 'HKLM:\SOFTWARE\Policies\ManageUsers' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\ManageUsers' -Name Policies -Value '[{"name":"Kiosks","match":{"catalog":"^Kiosk$"},"duration_days":1,"strategy":"creation_only"}]'
```

### File permissions

manageusers runs as SYSTEM, so it reads `Config.yaml`, `Sessions.yaml` and the inventory file only when no non-administrator could have written them. A file is refused, and the run logs why, when it or any folder above it is owned by an account other than SYSTEM, Administrators or TrustedInstaller, is a link, or grants another account the right to write, delete or re-permission it. The run then carries on as if the file were absent.

The installer gives `C:\ProgramData\Management\ManageUsers` an explicit ACL: Administrators and SYSTEM full control, Users read, not inherited from ProgramData. A file in that folder that a non-administrator created before the lockdown keeps its owner and stays refused until an administrator replaces it.

The logs live under `C:\ProgramData\ManagedUsers`, which the installer locks the same way, removing any link it finds inside. At run time manageusers deletes a link found where a log folder or log file belongs, and replaces a log file that is a hard link, so it never writes through one.

## Configuration

### Inventory (read-only)
`C:\ProgramData\Management\Inventory.yaml` — written by your enrollment system:
```yaml
area: "Studio"
location: "A200"
usage: "Shared"
```

The path can be changed with the `InventoryPath` setting, or for one run with `--inventory <path>`.

### Exclusions

Exclusions are merged from three sources (all case-insensitive):

1. **Built-in** — Administrator, DefaultAccount, Guest, WDAGUtilityAccount, defaultuser0 (hardcoded in `AppConstants.cs`)
2. **The `Exclusions` setting** (policy, machine settings or Config.yaml `exclusions:`) — fleet-wide service accounts (e.g. `svc-admin`, `svc-helpdesk`)
3. **Sessions.yaml `Exclusions:`** — machine-specific overrides, editable locally

The currently logged-in console user is also excluded automatically.

### Sessions
`C:\ProgramData\Management\ManageUsers\Sessions.yaml` — machine-specific exclusions and deferred state:
```yaml
Exclusions:
  - local-svc-account
DeferredDeletes: []
```

## Building

```pwsh
# Build, sign, and package .msi for both architectures (default)
.\build.ps1

# Build arm64 only
.\build.ps1 -Architecture arm64

# Package existing binaries into .msi (skip dotnet build)
.\build.ps1 -Msi

# Also create .nupkg (Chocolatey) packages
.\build.ps1 -Nupkg
```

Produces `release/x64/manageusers.exe`, `release/arm64/manageusers.exe`, and per-arch `.msi` packages in `build/`.

Run the unit tests:

```pwsh
dotnet test tests/ManageUsers.Tests
```

## Usage

```pwsh
# Dry run (no deletions)
manageusers.exe --simulate

# Live deletion
manageusers.exe

# Force mode (threshold = 0, deletes all non-excluded users)
manageusers.exe --force

# Use a custom inventory file
manageusers.exe --inventory "D:\Config\inventory.yaml"

# Version
manageusers.exe --version
```

## Scheduling

Deployed via Cimian as a `.msi` package. The postinstall script registers a scheduled task:
- Runs as SYSTEM
- Daily at 3:00 AM + at startup
- Action: `C:\Program Files\sbin\manageusers.exe`

## Project Structure

```
ManageUsers/
├── build.ps1                         # Build + sign script
├── ManageUsers.sln
├── build-info.yaml                   # cimipkg package metadata
├── scripts/                          # Install/uninstall scripts
│   └── postinstall.ps1
├── tests/ManageUsers.Tests/          # Settings, file-permission and log-link tests
└── src/ManageUsers/
    ├── ManageUsers.csproj
    ├── app.manifest
    ├── Program.cs                    # CLI entry point + mutex guard
    ├── Models/
    │   ├── AppConstants.cs           # Paths, registry keys and exclusion list
    │   ├── ConfigFile.cs             # Config.yaml as written (nullable keys)
    │   ├── DeletionPolicy.cs         # Policy + strategy enums
    │   ├── InventoryData.cs          # Inventory.yaml model
    │   ├── PolicyConfig.cs           # Config.yaml model
    │   ├── SessionsData.cs           # Sessions.yaml model
    │   └── UserSessionInfo.cs        # Per-user session data
    └── Services/
        ├── ConfigService.cs          # Settings resolution + YAML read/write
        ├── FileTrust.cs              # Refuses files a non-admin could write
        ├── LogService.cs             # File + console logging with rotation
        ├── ManageUsersEngine.cs      # Main orchestrator
        ├── PolicyService.cs          # Config-driven policy evaluation
        ├── RecycleBinService.cs      # Per-SID recycle bin removal + orphan sweep
        ├── RepairService.cs          # Orphan repair + hidden user registry
        ├── SafeLogFile.cs            # Opens logs without writing through links
        ├── SettingsResolver.cs       # CLI > policy > machine settings > Config.yaml > default
        ├── SettingsSource.cs         # HKLM registry layers (64-bit view)
        ├── UserDeletionService.cs    # Core deletion + deferred processing
        └── UserEnumerationService.cs # Win32 user/profile enumeration
```
