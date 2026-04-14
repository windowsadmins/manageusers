using YamlDotNet.Serialization;

namespace ManageUsers.Models;

/// <summary>
/// Top-level Config.yaml model — defines policy rules and end-of-term dates.
/// </summary>
public sealed class PolicyConfig
{
    [YamlMember(Alias = "exclusions")]
    public List<string> Exclusions { get; set; } = [];

    [YamlMember(Alias = "policies")]
    public List<PolicyRule> Policies { get; set; } = [];

    [YamlMember(Alias = "default_policy")]
    public DefaultPolicyRule DefaultPolicy { get; set; } = new();

    [YamlMember(Alias = "end_of_term_dates")]
    public List<TermDate> EndOfTermDates { get; set; } = [];
}

/// <summary>
/// A single policy rule — matched in order, first match wins.
/// </summary>
public sealed class PolicyRule
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "match")]
    public MatchCriteria Match { get; set; } = new();

    [YamlMember(Alias = "duration_days")]
    public int DurationDays { get; set; }

    [YamlMember(Alias = "strategy")]
    public string Strategy { get; set; } = "login_and_creation";

    [YamlMember(Alias = "force_at_end_of_term")]
    public bool ForceAtEndOfTerm { get; set; }
}

/// <summary>
/// Regex patterns to match against inventory fields (area, room, usage).
/// </summary>
public sealed class MatchCriteria
{
    [YamlMember(Alias = "area")]
    public string? Area { get; set; }

    [YamlMember(Alias = "room")]
    public string? Room { get; set; }

    [YamlMember(Alias = "usage")]
    public string? Usage { get; set; }
}

/// <summary>
/// Default policy when no rules match.
/// </summary>
public sealed class DefaultPolicyRule
{
    [YamlMember(Alias = "duration_days")]
    public int DurationDays { get; set; } = 28;

    [YamlMember(Alias = "strategy")]
    public string Strategy { get; set; } = "login_and_creation";

    [YamlMember(Alias = "force_at_end_of_term")]
    public bool ForceAtEndOfTerm { get; set; }
}

/// <summary>
/// A month/day pair representing an end-of-term boundary.
/// </summary>
public sealed class TermDate
{
    [YamlMember(Alias = "month")]
    public int Month { get; set; }

    [YamlMember(Alias = "day")]
    public int Day { get; set; }
}
