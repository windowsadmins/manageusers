namespace ManageUsers.Services;

/// <summary>The version an app shows: the one the release stamps from its tag.</summary>
public static class VersionText
{
    /// <summary>
    /// The informational version as stamped, zero-padded like the tag (2026.10.06.2019),
    /// with any "+&lt;commit&gt;" the SDK appends dropped. The file version would lose the
    /// padding (2026.10.6.2019).
    /// </summary>
    public static string FromInformational(string? informational)
    {
        var version = informational?.Split('+')[0].Trim();
        return string.IsNullOrEmpty(version) ? "dev" : version;
    }
}
