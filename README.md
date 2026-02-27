# ManageUsers

Signed Windows binary that manages local user accounts on shared devices. Removes inactive accounts based on area/room-driven deletion policies, end-of-term forced deletion, and creation/login-based strategies.

Designed for enterprise environments with 10,000+ devices managed by [Cimian](https://github.com/windowsadmins/cimian).

## How It Works

1. Reads device inventory from `C:\ProgramData\Management\Inventory.yaml` (or a custom path via `--inventory`)
2. Calculates deletion policy based on area/room rules defined in `PolicyService`
3. Enumerates local users, gathers creation dates and last login times
4. Deletes users that exceed the inactivity threshold
5. Cleans up orphaned accounts and profiles
6. Runs as a signed SYSTEM scheduled task — no PowerShell, no script block logging noise

## Policy Matrix

Policies are determined by matching the device's `area` and `location` fields from inventory. Customize `PolicyService.cs` to fit your environment.

| Match Pattern | Duration | Strategy | End of Term |
|---|---|---|---|
| High-turnover labs (kiosks, drop-in) | 2 days | Creation only | — |
| Medium-use studios | 30 days | Creation only | — |
| Assigned labs / classrooms | 6 weeks | Login + Creation | Force delete all |
| Default (no match) | 4 weeks | Login + Creation | — |

**Strategies:**
- **Creation only** — deletes accounts older than the threshold regardless of login activity
- **Login + Creation** — deletes accounts that are both old enough *and* haven't logged in recently

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
│   ├── postinstall.ps1
│   ├── preinstall.ps1
│   └── Sessions.yaml.template
├── deploy/                           # Cimian pkgsinfo manifests
└── src/ManageUsers/
    ├── ManageUsers.csproj
    ├── app.manifest
    ├── Program.cs                    # CLI entry point + mutex guard
    ├── Models/
    │   ├── AppConstants.cs           # Paths, durations, exclusion list
    │   ├── DeletionPolicy.cs         # Policy + strategy enums
    │   ├── InventoryData.cs          # Inventory.yaml model
    │   ├── SessionsData.cs          # Sessions.yaml model
    │   └── UserSessionInfo.cs        # Per-user session data
    └── Services/
        ├── ConfigService.cs          # YAML read/write + exclusion merge
        ├── LogService.cs             # File + console logging with rotation
        ├── ManageUsersEngine.cs      # Main orchestrator
        ├── PolicyService.cs          # Area/room → policy calculation
        ├── RepairService.cs          # Orphan repair + hidden user registry
        ├── UserDeletionService.cs    # Core deletion + deferred processing
        └── UserEnumerationService.cs # WMI user/profile enumeration
```
