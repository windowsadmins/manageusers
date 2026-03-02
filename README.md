# ManageUsers

Signed Windows binary that manages local user accounts on shared devices. Removes inactive accounts based on area/room-driven deletion policies, end-of-term forced deletion, and creation/login-based strategies.

Designed for enterprise environments with 10,000+ devices managed by [Cimian](https://github.com/windowsadmins/cimian).

## How It Works

1. Reads device inventory from `C:\ProgramData\Management\Inventory.yaml` (or a custom path via `--inventory`)
2. Loads policy rules from `C:\ProgramData\Management\ManageUsers\Config.yaml` — first matching rule wins
3. Enumerates local users, gathers creation dates and last login times
4. Deletes users that exceed the inactivity threshold
5. Cleans up orphaned accounts and profiles
6. Runs as a signed SYSTEM scheduled task — no PowerShell, no script block logging noise

## Policy Configuration

All deletion policies are defined in `C:\ProgramData\Management\ManageUsers\Config.yaml`. Rules are evaluated in order — first match wins. Area and room values are regex patterns matched against inventory.

```yaml
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
      room: "B1110|D3360"
    duration_days: 30
    strategy: creation_only
    force_at_end_of_term: false

  - name: "Assigned Labs"
    match:
      area: "Classroom|Lab"
      room: "B1122|B4120"
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

## Configuration

### Inventory (read-only)
`C:\ProgramData\Management\Inventory.yaml` — written by your enrollment system:
```yaml
area: "Studio"
location: "B1110"
usage: "Shared"
```

You can override this path at runtime with `--inventory <path>`.

### Sessions
`C:\ProgramData\Management\ManageUsers\Sessions.yaml` — exclusions and deferred state:
```yaml
Exclusions:
  - svc-account
  - lab-admin
DeferredDeletes: []
```

Built-in Windows accounts (Administrator, DefaultAccount, Guest, WDAGUtilityAccount, defaultuser0) are always excluded automatically.

## Building

```pwsh
# Build and sign for both architectures
.\build.ps1 -Sign

# Build arm64 only
.\build.ps1 -Sign -Architecture arm64

# Deploy signed binaries to Cimian payload
.\build.ps1 -Sign -Deploy
```

Produces `release/x64/manageusers.exe` and `release/arm64/manageusers.exe`.

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

Deployed via Cimian as a `.pkg` package. The postinstall script registers a scheduled task:
- Runs as SYSTEM
- Daily at 3:00 AM + at startup
- Action: `C:\Program Files\sbin\manageusers.exe`

## Project Structure

```
ManageUsers/
├── build.ps1                         # Build + sign script
├── ManageUsers.sln
├── build/pkg/                        # Cimian packaging files
│   ├── build-info.yaml
│   ├── Config.yaml.template
│   ├── postinstall.ps1
│   ├── preinstall.ps1
│   └── Sessions.yaml.template
├── deploy/                           # Cimian pkgsinfo manifests
└── src/ManageUsers/
    ├── ManageUsers.csproj
    ├── app.manifest
    ├── Program.cs                    # CLI entry point + mutex guard
    ├── Models/
    │   ├── AppConstants.cs           # Paths and exclusion list
    │   ├── DeletionPolicy.cs         # Policy + strategy enums
    │   ├── InventoryData.cs          # Inventory.yaml model
    │   ├── PolicyConfig.cs           # Config.yaml model
    │   ├── SessionsData.cs           # Sessions.yaml model
    │   └── UserSessionInfo.cs        # Per-user session data
    └── Services/
        ├── ConfigService.cs          # YAML read/write + config loading
        ├── LogService.cs             # File + console logging with rotation
        ├── ManageUsersEngine.cs      # Main orchestrator
        ├── PolicyService.cs          # Config-driven policy evaluation
        ├── RepairService.cs          # Orphan repair + hidden user registry
        ├── UserDeletionService.cs    # Core deletion + deferred processing
        └── UserEnumerationService.cs # WMI user/profile enumeration
```
