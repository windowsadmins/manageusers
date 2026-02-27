using ManageUsers.Models;
using System.Text.RegularExpressions;

namespace ManageUsers.Services;

/// <summary>
/// Calculates the deletion policy from area/room inventory data. Ports calculateDeletionPolicies() from bash.
/// </summary>
public sealed class PolicyService
{
    private readonly LogService _log;

    public PolicyService(LogService log)
    {
        _log = log;
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

        // Library, DOC, CommDesign areas → 2 days, creation-only
        if (Regex.IsMatch(area, @"Library|DOC|CommDesign", RegexOptions.IgnoreCase))
        {
            _log.Info($"Area '{area}' matched Library/DOC/CommDesign policy → 2 days, CreationOnly");
            return new DeletionPolicy
            {
                DurationDays = AppConstants.TwoDays,
                Strategy = DeletionStrategy.CreationOnly,
                ForceTermDeletion = false
            };
        }

        // Photo, Illustration areas or specific rooms → 30 days, creation-only
        if (Regex.IsMatch(area, @"Photo|Illustration", RegexOptions.IgnoreCase)
            || Regex.IsMatch(room, @"B1110|D3360", RegexOptions.IgnoreCase))
        {
            _log.Info($"Area '{area}'/Room '{room}' matched Photo/Illustration policy → 30 days, CreationOnly");
            return new DeletionPolicy
            {
                DurationDays = AppConstants.ThirtyDays,
                Strategy = DeletionStrategy.CreationOnly,
                ForceTermDeletion = false
            };
        }

        // FMSA, NMSA areas or specific rooms → 6 weeks normally, immediate at end of term
        if (Regex.IsMatch(area, @"FMSA|NMSA", RegexOptions.IgnoreCase)
            || Regex.IsMatch(room, @"B1122|B4120", RegexOptions.IgnoreCase))
        {
            if (isEndOfTerm)
            {
                _log.Info($"Area '{area}'/Room '{room}' matched FMSA/NMSA policy + end of term → force delete");
                return new DeletionPolicy
                {
                    DurationDays = 0,
                    Strategy = DeletionStrategy.LoginAndCreation,
                    ForceTermDeletion = true
                };
            }

            _log.Info($"Area '{area}'/Room '{room}' matched FMSA/NMSA policy → 6 weeks, LoginAndCreation");
            return new DeletionPolicy
            {
                DurationDays = AppConstants.SixWeeks,
                Strategy = DeletionStrategy.LoginAndCreation,
                ForceTermDeletion = false
            };
        }

        // Default → 4 weeks, login-and-creation
        _log.Info($"Area '{area}'/Room '{room}' using default policy → 4 weeks, LoginAndCreation");
        return new DeletionPolicy
        {
            DurationDays = AppConstants.FourWeeks,
            Strategy = DeletionStrategy.LoginAndCreation,
            ForceTermDeletion = false
        };
    }

    /// <summary>
    /// Check if current date is at or past an end-of-term boundary (Apr 30, Aug 31, Dec 31).
    /// </summary>
    public static bool IsEndOfTerm() => IsEndOfTerm(DateTime.Now);

    public static bool IsEndOfTerm(DateTime date)
    {
        return (date.Month == 4 && date.Day >= 30)
            || (date.Month == 8 && date.Day >= 31)
            || (date.Month == 12 && date.Day >= 31);
    }
}
