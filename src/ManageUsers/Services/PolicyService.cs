using ManageUsers.Models;
using System.Text.RegularExpressions;

namespace ManageUsers.Services;

/// <summary>
/// Calculates the deletion policy from area/room inventory data using Config.yaml rules.
/// Rules are evaluated in order — first match wins.
/// </summary>
public sealed class PolicyService
{
    private readonly LogService _log;
    private readonly PolicyConfig _config;

    public PolicyService(LogService log, PolicyConfig config)
    {
        _log = log;
        _config = config;
    }

    /// <summary>
    /// Determine the deletion policy based on area, room, and whether it's end of term.
    /// </summary>
    public DeletionPolicy Calculate(InventoryData inventory, bool isEndOfTerm, bool forceMode)
    {
        if (forceMode)
        {
            _log.Info("Force mode — duration set to 0 days");
            return new DeletionPolicy
            {
                DurationDays = 0,
                Strategy = DeletionStrategy.LoginAndCreation,
                ForceTermDeletion = false
            };
        }

        var area = inventory.Area.Trim();
        var room = inventory.Location.Trim();

        // Evaluate rules in order — first match wins
        foreach (var rule in _config.Policies)
        {
            if (Matches(rule.Match, area, room))
            {
                if (isEndOfTerm && rule.ForceAtEndOfTerm)
                {
                    _log.Info($"Rule '{rule.Name}' matched + end of term → force delete");
                    return new DeletionPolicy
                    {
                        DurationDays = 0,
                        Strategy = DeletionStrategy.LoginAndCreation,
                        ForceTermDeletion = true
                    };
                }

                _log.Info($"Rule '{rule.Name}' matched → {rule.DurationDays} days, {rule.Strategy}");
                return new DeletionPolicy
                {
                    DurationDays = rule.DurationDays,
                    Strategy = ParseStrategy(rule.Strategy),
                    ForceTermDeletion = false
                };
            }
        }

        // No rule matched — use default
        var def = _config.DefaultPolicy;
        _log.Info($"No rule matched area='{area}'/room='{room}' → default policy: {def.DurationDays} days, {def.Strategy}");
        return new DeletionPolicy
        {
            DurationDays = def.DurationDays,
            Strategy = ParseStrategy(def.Strategy),
            ForceTermDeletion = false
        };
    }

    private static bool Matches(MatchCriteria match, string area, string room)
    {
        bool areaMatch = string.IsNullOrWhiteSpace(match.Area)
            || Regex.IsMatch(area, match.Area, RegexOptions.IgnoreCase);
        bool roomMatch = string.IsNullOrWhiteSpace(match.Room)
            || Regex.IsMatch(room, match.Room, RegexOptions.IgnoreCase);

        // If both are specified, either can match (OR logic).
        // If only one is specified, that one must match.
        if (!string.IsNullOrWhiteSpace(match.Area) && !string.IsNullOrWhiteSpace(match.Room))
            return areaMatch || roomMatch;

        return areaMatch && roomMatch;
    }

    private static DeletionStrategy ParseStrategy(string strategy) =>
        strategy?.ToLowerInvariant() switch
        {
            "creation_only" => DeletionStrategy.CreationOnly,
            _ => DeletionStrategy.LoginAndCreation
        };

    /// <summary>
    /// Check if current date is at or past an end-of-term boundary based on Config.yaml dates.
    /// </summary>
    public bool IsEndOfTerm() => IsEndOfTerm(DateTime.Now);

    public bool IsEndOfTerm(DateTime date)
    {
        if (_config.EndOfTermDates.Count == 0)
            return false;

        foreach (var term in _config.EndOfTermDates)
        {
            if (date.Month == term.Month && date.Day >= term.Day)
                return true;
        }

        return false;
    }
}
