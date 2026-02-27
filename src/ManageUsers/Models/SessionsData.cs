using YamlDotNet.Serialization;

namespace ManageUsers.Models;

/// <summary>
/// Represents C:\ProgramData\Management\Users\Sessions.yaml — the exclusion and deferred-delete state file.
/// </summary>
public sealed class SessionsData
{
    [YamlMember(Alias = "Exclusions")]
    public List<string> Exclusions { get; set; } = [];

    [YamlMember(Alias = "DeferredDeletes")]
    public List<string> DeferredDeletes { get; set; } = [];
}
