namespace ManageUsers.Models;

/// <summary>
/// Aggregated session data for a single local user account.
/// </summary>
public sealed class UserSessionInfo
{
    public required string Username { get; init; }
    public required string Sid { get; init; }
    public DateTime? LastLogin { get; init; }
    public DateTime CreationDate { get; init; }
    public string? ProfilePath { get; init; }
    public bool HasProfile { get; init; }
}
