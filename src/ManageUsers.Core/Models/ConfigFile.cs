using YamlDotNet.Serialization;

namespace ManageUsers.Models;

/// <summary>
/// Config.yaml as written, with every key nullable so a key the file leaves out falls
/// through to the built-in default instead of reading as an empty value.
/// </summary>
public sealed class ConfigFile
{
    [YamlMember(Alias = "exclusions")]
    public List<string>? Exclusions { get; set; }

    [YamlMember(Alias = "delete_admins")]
    public bool? DeleteAdmins { get; set; }

    [YamlMember(Alias = "deletable_admins")]
    public List<string>? DeletableAdmins { get; set; }

    [YamlMember(Alias = "policies")]
    public List<PolicyRule>? Policies { get; set; }

    [YamlMember(Alias = "default_policy")]
    public DefaultPolicyFile? DefaultPolicy { get; set; }

    [YamlMember(Alias = "end_of_term_dates")]
    public List<TermDate>? EndOfTermDates { get; set; }

    [YamlMember(Alias = "inventory_path")]
    public string? InventoryPath { get; set; }
}

public sealed class DefaultPolicyFile
{
    [YamlMember(Alias = "duration_days")]
    public int? DurationDays { get; set; }

    [YamlMember(Alias = "strategy")]
    public string? Strategy { get; set; }

    [YamlMember(Alias = "force_at_end_of_term")]
    public bool? ForceAtEndOfTerm { get; set; }
}
