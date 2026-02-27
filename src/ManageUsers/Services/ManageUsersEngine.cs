using ManageUsers.Models;

namespace ManageUsers.Services;

/// <summary>
/// Main orchestrator — runs the full ManageUsers workflow.
/// </summary>
public sealed class ManageUsersEngine
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly PolicyService _policy;
    private readonly UserEnumerationService _enum;
    private readonly UserDeletionService _delete;
    private readonly RepairService _repair;
    private readonly bool _simulate;
    private readonly bool _force;

    public ManageUsersEngine(bool simulate, bool force)
    {
        _simulate = simulate;
        _force = force;
        _log = new LogService();
        _config = new ConfigService(_log);
        _policy = new PolicyService(_log);
        _enum = new UserEnumerationService(_log);
        _delete = new UserDeletionService(_log, _config, simulate);
        _repair = new RepairService(_log);
    }

    public int Run()
    {
        _log.Info("========================================");
        _log.Info("ManageUsers starting");
        _log.Info($"Mode: {(_simulate ? "SIMULATE" : "LIVE")} | Force: {_force}");
        _log.Info("========================================");

        try
        {
            // Load configuration
            var sessions = _config.LoadSessions();
            var inventory = _config.LoadInventory();
            var exclusions = _config.GetEffectiveExclusions(sessions);

            _log.Info($"Exclusions loaded: {exclusions.Count} users");
            _log.Info($"Inventory: area={inventory.Area}, location={inventory.Location}, usage={inventory.Usage}");

            // Process deferred deletions from previous runs
            _delete.ProcessDeferredDeletions(sessions);

            // Gather user session data
            var users = _enum.GetUserSessions(exclusions);
            _log.Info($"Found {users.Count} non-excluded user(s) to evaluate");

            if (users.Count == 0)
            {
                _log.Info("No users to process — exiting");
                return 0;
            }

            // Repair user states
            _repair.RepairUserStates(users);

            // Calculate deletion policy
            var isEndOfTerm = PolicyService.IsEndOfTerm();
            var policy = _policy.Calculate(inventory, isEndOfTerm, _force);
            _log.Info($"Deletion policy: {policy}");
            _log.Info($"End of term: {isEndOfTerm}");

            // Main deletion loop
            var deletedCount = 0;
            var now = DateTime.Now;

            foreach (var user in users)
            {
                var shouldDelete = EvaluateUser(user, policy, now);

                if (shouldDelete)
                {
                    if (_delete.DeleteUser(user.Username, sessions))
                        deletedCount++;
                }
            }

            // Clean up orphaned users
            var orphans = _enum.FindOrphanedUsers(exclusions);
            if (orphans.Count > 0)
            {
                _log.Info($"Found {orphans.Count} orphaned user(s)");
                _delete.RemoveOrphanedUsers(orphans, sessions);
                deletedCount += orphans.Count;
            }

            // Update hidden users on login screen
            _repair.UpdateHiddenUsers(exclusions);

            _log.Info("========================================");
            _log.Info($"ManageUsers complete — {deletedCount} user(s) removed");
            _log.Info("========================================");
            return 0;
        }
        catch (Exception ex)
        {
            _log.Error($"Fatal error: {ex.Message}");
            _log.Error(ex.StackTrace ?? "");
            return 1;
        }
        finally
        {
            _log.Dispose();
        }
    }

    private bool EvaluateUser(UserSessionInfo user, DeletionPolicy policy, DateTime now)
    {
        // Force term deletion — delete everything
        if (policy.ForceTermDeletion)
        {
            _log.Info($"End-of-term force deletion: {user.Username}");
            return true;
        }

        var threshold = TimeSpan.FromDays(policy.DurationDays);

        switch (policy.Strategy)
        {
            case DeletionStrategy.CreationOnly:
            {
                var age = now - user.CreationDate;
                if (age >= threshold)
                {
                    _log.Info($"CreationOnly: {user.Username} created {age.Days}d ago (threshold {policy.DurationDays}d) — DELETE");
                    return true;
                }
                _log.Info($"CreationOnly: {user.Username} created {age.Days}d ago (threshold {policy.DurationDays}d) — keep");
                return false;
            }

            case DeletionStrategy.LoginAndCreation:
            {
                var creationAge = now - user.CreationDate;
                var loginAge = user.LastLogin.HasValue ? now - user.LastLogin.Value : TimeSpan.MaxValue;

                if (creationAge >= threshold && loginAge >= threshold)
                {
                    _log.Info($"LoginAndCreation: {user.Username} created {creationAge.Days}d ago, last login {(user.LastLogin.HasValue ? $"{loginAge.Days}d ago" : "never")} (threshold {policy.DurationDays}d) — DELETE");
                    return true;
                }
                _log.Info($"LoginAndCreation: {user.Username} created {creationAge.Days}d ago, last login {(user.LastLogin.HasValue ? $"{loginAge.Days}d ago" : "never")} (threshold {policy.DurationDays}d) — keep");
                return false;
            }

            default:
                return false;
        }
    }
}
