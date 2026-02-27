# ManageUsers

Signed Windows binary that manages local user accounts on shared devices. Removes inactive accounts based on area/room-driven deletion policies, end-of-term forced deletion, and creation/login-based strategies.

Designed for enterprise environments with 10,000+ devices managed by [Cimian](https://github.com/windowsadmins/cimian).

## How It Works

1. Reads device inventory from `C:\ProgramData\Management\Inventory.yaml` (area, room, usage)
2. Calculates deletion policy based on area/room rules
3. Enumerates local users, gathers creation dates and last login times
4. Deletes users that exceed the inactivity threshold
5. Cleans up orphaned accounts and profiles
6. Runs as a signed SYSTEM scheduled task — no PowerShell, no script block logging noise

## Policy Matrix

| Area/Room | Duration | Strategy | End of Term |
|---|---|---|---|
| Library, DOC, CommDesign | 2 days | Creation only | — |
| Photo, Illustration, B1110, D3360 | 30 days | Creation only | — |
| FMSA, NMSA, B1122, B4120 | 6 weeks | Login + Creation | Force delete all |
| Default | 4 weeks | Login + Creation | — |

## Configuration

### Inventory (read-only)
`C:\ProgramData\Management\Inventory.yaml` — written by Cimian enrollment:
```yaml
area: "Photo"
location: "B1110"
usage: "Shared"
```

### Sessions
`C:\ProgramData\Management\Users\Sessions.yaml` — exclusions and deferred state:
```yaml
Exclusions:
  - admin
  - student
DeferredDeletes: []
```

## Building

```pwsh
# Build and sign for both architectures
.\build.ps1 -Sign

# Build arm64 only
.\build.ps1 -Sign -Architecture arm64
```

Produces `release/x64/manageusers.exe` and `release/arm64/manageusers.exe`.

## Usage

```pwsh
# Dry run
manageusers.exe --simulate

# Live deletion
manageusers.exe

# Force mode (threshold = 0)
manageusers.exe --force

# Version
manageusers.exe --version
```

## Scheduling

Deployed via Cimian as a `.pkg` package. The postinstall script registers a scheduled task:
- Runs as SYSTEM
- Daily at 3:00 AM + at startup
- Action: `C:\ProgramData\Management\Scripts\manageusers.exe`

## Project Structure

```
ManageUsers/
├── build.ps1                         # Build + sign script
├── ManageUsers.sln
└── src/ManageUsers/
    ├── ManageUsers.csproj
    ├── app.manifest
    ├── Program.cs                    # CLI entry point + mutex guard
    ├── Models/
    │   ├── AppConstants.cs           # Paths, durations, exclusion list
    │   ├── DeletionPolicy.cs         # Policy + strategy enums
    │   ├── InventoryData.cs          # Inventory.yaml model
    │   ├── SessionsData.cs           # Sessions.yaml model
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
