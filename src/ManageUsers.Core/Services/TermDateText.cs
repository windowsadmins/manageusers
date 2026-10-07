namespace ManageUsers.Services;

/// <summary>An end-of-term date written as month-day, such as 4-30 or 4/30.</summary>
public static class TermDateText
{
    public static bool TryParse(string text, out int month, out int day)
    {
        month = day = 0;
        var parts = text.Trim().Split(['-', '/']);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out month) && int.TryParse(parts[1], out day) &&
               month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2024, month);
    }
}
