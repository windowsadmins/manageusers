namespace ManageUsers.Models;

/// <summary>
/// A profile folder in C:\Users with no corresponding local user account.
/// Typically an Entra ID cached profile left behind after the user stopped logging in.
/// </summary>
public sealed class StaleProfileInfo
{
    public required string FolderName { get; init; }
    public required string ProfilePath { get; init; }
    public string? Sid { get; init; }
    public DateTime CreationDate { get; init; }
    public DateTime? LastUseTime { get; init; }
    public bool HasRegistryEntry { get; init; }
}
