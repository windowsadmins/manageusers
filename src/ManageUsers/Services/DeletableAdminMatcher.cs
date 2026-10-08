namespace ManageUsers.Services;

/// <summary>
/// Decides whether a protected local administrator is opted in to deletion by
/// deletable_admins while delete_admins is false.
///
/// An entry is an exact account name, or a prefix ending in a single trailing *
/// ("admin-*" matches "admin-1" and "Admin-Lab", not "admin"). Matching is
/// case-insensitive. An account in the exclusions always wins: it never matches,
/// whatever the list says. A bare "*", or a * anywhere but the end, is refused and
/// returned in <see cref="Rejected"/>, because it would opt every admin in and
/// defeat the delete_admins guard.
/// </summary>
public sealed class DeletableAdminMatcher
{
    public static readonly DeletableAdminMatcher None = new([], []);

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _prefixes = [];
    private readonly HashSet<string> _exclusions;

    public DeletableAdminMatcher(IEnumerable<string>? entries, IEnumerable<string> exclusions)
    {
        _exclusions = new HashSet<string>(exclusions, StringComparer.OrdinalIgnoreCase);

        foreach (var raw in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var entry = raw.Trim();
            var star = entry.IndexOf('*');

            if (star < 0)
            {
                if (_exclusions.Contains(entry)) continue;
                if (_names.Add(entry)) Entries.Add(entry);
            }
            else if (star == entry.Length - 1 && star > 0)
            {
                var prefix = entry[..star];
                if (!_prefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                {
                    _prefixes.Add(prefix);
                    Entries.Add(entry);
                }
            }
            else
            {
                Rejected.Add(entry);
            }
        }
    }

    /// <summary>The entries in effect, in list order, as written.</summary>
    public List<string> Entries { get; } = [];

    /// <summary>Entries refused because their * is bare or not trailing.</summary>
    public List<string> Rejected { get; } = [];

    public int Count => Entries.Count;

    public bool Matches(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || _exclusions.Contains(name)) return false;
        if (_names.Contains(name)) return true;
        return _prefixes.Exists(p => name.Length > p.Length && name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
