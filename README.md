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

## Managed Users Cleanup app

`Managed Users Cleanup.exe` installs beside the CLI in `C:\Program Files\ManageUsers\`, with a Start menu entry. It has three tabs:

- **Prefs** shows every setting with the value a run uses and where it comes from: policy, this device, Config.yaml or the default. It is read-only until **Unlock**, which relaunches the app as administrator; changes then save to `HKLM\SOFTWARE\ManageUsers\Settings`. A setting set by policy is locked. Rules, exclusions and end-of-term dates have their own editors.
- **Run** offers **Simulate**, which streams the run's log and shows the decision list, and **Live cleanup**, which simulates, lists exactly the accounts and profiles it will delete for confirmation, and then runs live limited to that list. Each run asks for administrator approval unless the app is already elevated.
- **Logs** shows the audit log and each day's log, coloured by level.

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

manageusers runs as SYSTEM, so it reads `Config.yaml`, `Sessions.yaml` and the inventory file only when no non-administrator could have written them. A file is refused, and the run logs why, when:

- it or any folder above it is a link;
- any folder above it is owned by an account other than SYSTEM, Administrators or TrustedInstaller, or lets another account delete or rename its entries or change its ACL or owner;
- the file grants another account the right to write, delete or re-permission it;
- or the file is owned by an individual account in a folder where non-administrators can create files.

A file an administrator saved under their own account is trusted in a locked folder, where only administrators can create files, and the run hands it to Administrators before reading it. A refused file is treated as absent.

The installer gives `C:\ProgramData\Management\ManageUsers` an explicit ACL: Administrators and SYSTEM full control, Users read, not inherited from ProgramData. The first time it locks the folder, any entry whose owner it cannot show to be an administrator moves to a `Quarantine-<timestamp>` folder inside it, for an administrator to review.

The logs live under `C:\ProgramData\ManagedUsers`, which the installer locks and quarantines the same way, removing any link it finds inside. At run time manageusers deletes a link found where a log folder or log file belongs, and replaces a log file that is a hard link, so it never writes through one.

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

Produces `release/<arch>/manageusers.exe`, the app in `release/<arch>/app/`, and per-arch `.msi` packages in `build/`. Building the app needs the Windows SDK (for `makepri.exe`); `-SkipApp` builds the CLI alone.

A simulation also writes what it would remove to `C:\ProgramData\ManagedUsers\simulation.json`, which the app reads.

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

# Remove nothing outside the named accounts or profile folders (repeat --only)
manageusers.exe --only lab01 --only lab02

# Version
manageusers.exe --version
```

## Scheduling

Installs to `C:\Program Files\ManageUsers\`. The scheduled task comes from the separate ManageUsersPrefs package, so the schedule can change without rebuilding the binary:
- Runs as SYSTEM
- Daily at 3:00 AM + at startup
- Action: `C:\Program Files\ManageUsers\manageusers.exe`

Earlier releases installed into the shared `C:\Program Files\sbin\`. On upgrade, postinstall repoints any scheduled task that runs the old `manageusers.exe` and removes ManageUsers' own files there (`manageusers.exe`, and the `Managed Users Cleanup` folder test builds used); nothing else in `sbin` is touched.

## Project Structure

```
ManageUsers/
├── build.ps1                         # Build + sign script
├── ManageUsers.sln
├── build-info.yaml                   # cimipkg package metadata
├── scripts/                          # Install/uninstall scripts
│   └── postinstall.ps1
├── tests/ManageUsers.Tests/          # Settings, file-permission, log-link and app-logic tests
├── src/ManageUsers.Core/             # Settings, paths and file trust shared by the CLI and the app
├── src/ManageUsers.App/              # Managed Users Cleanup (WinUI 3)
└── src/ManageUsers/
    ├── ManageUsers.csproj
    ├── app.manifest
    ├── Program.cs                    # CLI entry point + mutex guard
    ├── Models/
    │   ├── DeletionPolicy.cs         # Policy + strategy enums
    │   ├── InventoryData.cs          # Inventory.yaml model
    │   ├── SessionsData.cs           # Sessions.yaml model
    │   └── UserSessionInfo.cs        # Per-user session data
    └── Services/
        ├── ConfigService.cs          # Settings resolution + YAML read/write
        ├── LogService.cs             # File + console logging with rotation
        ├── ManageUsersEngine.cs      # Main orchestrator
        ├── PlanWriter.cs             # Simulation plan for the app
        ├── PolicyService.cs          # Config-driven policy evaluation
        ├── RecycleBinService.cs      # Per-SID recycle bin removal + orphan sweep
        ├── RepairService.cs          # Orphan repair + hidden user registry
        ├── SafeLogFile.cs            # Opens logs without writing through links
        ├── UserDeletionService.cs    # Core deletion + deferred processing
        └── UserEnumerationService.cs # Win32 user/profile enumeration
```
