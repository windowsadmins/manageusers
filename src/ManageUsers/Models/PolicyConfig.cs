using YamlDotNet.Serialization;

namespace ManageUsers.Models;

/// <summary>
/// Top-level Config.yaml model — defines exclusions, policy rules, and end-of-term dates.
/// </summary>
public sealed class PolicyConfig
{
    [YamlMember(Alias = "exclusions")]
    public List<string> Exclusions { get; set; } = [];

    /// <summary>
    /// Security identifiers whose profiles and accounts are never deleted, whatever
    /// the profile folder is called.
    /// </summary>
    /// <remarks>
    /// <c>exclusions:</c> can only match a name, and for a directory account the
    /// only name ManageUsers sees is the profile folder. That folder is not a stable
    /// identifier: when a second profile arrives for the same user -- a domain
    /// profile and later an Entra profile after the device changes join type, or a
    /// profile recreated after a partial delete -- Windows names the new folder
    /// <c>user.DOMAIN</c>, <c>user.000</c> and so on, and an exclusion for
    /// <c>user</c> no longer matches it. The SID does not change, so a list of SIDs
    /// protects every profile the user has on the device.
    ///
    /// Entra ID accounts sign in with an S-1-12-1 SID derived from the user's
    /// object id; on-premises domain accounts use their S-1-5-21 domain SID. A
    /// generator can emit both from a directory group without any device-side
    /// lookup. Matched case-insensitively, exact.
    ///
    /// A client that predates this key ignores it (IgnoreUnmatchedProperties), so
    /// the list can ship ahead of the client that reads it.
    /// </remarks>
    [YamlMember(Alias = "protected_sids")]
    public List<string> ProtectedSids { get; set; } = [];

    /// <summary>
    /// When false (the default), any local account that is a member of the local
    /// Administrators group is never deleted — even if it has no profile, has never
    /// logged in, or is past the age threshold. Set to true to opt in to deleting
    /// admin accounts (the legacy behaviour). This is a safety net so service/SSH
    /// admin accounts (e.g. winadmins) survive on devices that never received the
    /// exclusions list.
    /// </summary>
    [YamlMember(Alias = "delete_admins")]
    public bool DeleteAdmins { get; set; }

    /// <summary>
    /// Specific admin account names that ARE eligible for deletion even while the
    /// global admin guard is on (delete_admins: false). Use this to reap a named
    /// stale/rogue local admin without exposing every admin to deletion. Ignored
    /// when delete_admins is true (everything is already deletable). An account in
    /// the exclusions list always wins over this list (exclusion = never delete).
    /// Matched case-insensitively by account name.
    /// </summary>
    [YamlMember(Alias = "deletable_admins")]
    public List<string> DeletableAdmins { get; set; } = [];

    [YamlMember(Alias = "policies")]
    public List<PolicyRule> Policies { get; set; } = [];

    [YamlMember(Alias = "default_policy")]
    public DefaultPolicyRule DefaultPolicy { get; set; } = new();

    [YamlMember(Alias = "end_of_term_dates")]
    public List<TermDate> EndOfTermDates { get; set; } = [];

    /// <summary>
    /// Set when Config.yaml exists but could not be parsed. The run then deletes
    /// nothing, including the corrupt-profile remediation that otherwise bypasses
    /// retention policy, because the exclusions it would have honoured are unknown.
    /// </summary>
    [YamlIgnore]
    public bool Unreadable { get; set; }
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
/// Regex patterns to match against inventory fields (catalog, area, room, usage).
/// </summary>
public sealed class MatchCriteria
{
    /// <summary>
    /// The device's catalog, e.g. Curriculum, Kiosk, Staff.
    /// </summary>
    /// <remarks>
    /// This is what separates a teaching lab from every other shared device, and it
    /// was the missing half of the fleet policy. Config.yaml has keyed rules on
    /// `catalog:` since it was written, but the deserializer ignores unknown keys,
    /// so both Shared rules collapsed to their `usage: ^Shared$` test alone. First
    /// match wins and "Shared devices that never reap" is listed first, so every
    /// teaching lab took the never-reap branch and none of them have ever reaped.
    ///
    /// That collapse was deliberate and safe -- an out-of-date client stops deleting
    /// rather than deletes the wrong thing -- but it was meant to be temporary. The
    /// cost of it running for months: on a Digital Fabrication workstation, 288 user
    /// profiles accumulated, each leaving a pair of per-user OneDrive scheduled
    /// tasks behind. At 806 tasks the Task Scheduler wedged, which stopped the
    /// machine installing anything and blocked it from rebooting.
    /// </remarks>
    [YamlMember(Alias = "catalog")]
    public string? Catalog { get; set; }

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
