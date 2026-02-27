namespace ManageUsers.Models;

/// <summary>
/// Deletion strategy determines which timestamps are considered when evaluating user inactivity.
/// </summary>
public enum DeletionStrategy
{
    /// <summary>Only the account creation date matters — delete after N days since creation.</summary>
    CreationOnly,

    /// <summary>Both login and creation date matter — delete only if BOTH exceed N days.</summary>
    LoginAndCreation
}

/// <summary>
/// Deletion policy derived from area/room inventory data.
/// </summary>
public sealed class DeletionPolicy
{
    /// <summary>Duration in days after which inactive accounts are deleted.</summary>
    public int DurationDays { get; init; }

    /// <summary>Strategy for evaluating inactivity.</summary>
    public DeletionStrategy Strategy { get; init; }

    /// <summary>If true, force-delete all non-excluded users at end of term regardless of duration.</summary>
    public bool ForceTermDeletion { get; init; }

    public override string ToString() =>
        $"Policy(Duration={DurationDays}d, Strategy={Strategy}, ForceTermDeletion={ForceTermDeletion})";
}
