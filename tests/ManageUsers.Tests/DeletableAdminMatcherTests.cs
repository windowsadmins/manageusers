using ManageUsers.Services;
using Xunit;

namespace ManageUsers.Tests;

/// <summary>deletable_admins takes exact names and trailing-* prefixes, and exclusions always win.</summary>
public sealed class DeletableAdminMatcherTests
{
    private static DeletableAdminMatcher Matcher(string[] entries, params string[] exclusions) => new(entries, exclusions);

    [Fact]
    public void ExactEntryMatchesOnlyThatNameIgnoringCase()
    {
        var m = Matcher(["lab-admin"]);

        Assert.True(m.Matches("lab-admin"));
        Assert.True(m.Matches("LAB-ADMIN"));
        Assert.False(m.Matches("lab-admin1"));
        Assert.False(m.Matches("lab"));
    }

    [Fact]
    public void TrailingStarMatchesNamesStartingWithThePrefixIgnoringCase()
    {
        var m = Matcher(["admin-*"]);

        Assert.True(m.Matches("admin-1"));
        Assert.True(m.Matches("Admin-Lab"));
        Assert.False(m.Matches("admin-"));
        Assert.False(m.Matches("admin"));
        Assert.False(m.Matches("svc-admin-1"));
    }

    [Fact]
    public void ExclusionsWinOverExactAndPrefixEntries()
    {
        var m = Matcher(["admin-*", "lab-admin"], "Admin-Keep", "lab-admin");

        Assert.True(m.Matches("admin-1"));
        Assert.False(m.Matches("admin-keep"));
        Assert.False(m.Matches("lab-admin"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("**")]
    [InlineData("*-admin")]
    [InlineData("ad*min")]
    [InlineData("admin-**")]
    public void BareOrNonTrailingStarIsRejectedAndMatchesNothing(string entry)
    {
        var m = Matcher([entry]);

        Assert.Equal([entry], m.Rejected);
        Assert.Empty(m.Entries);
        Assert.False(m.Matches("admin-1"));
        Assert.False(m.Matches("x-admin"));
        Assert.False(m.Matches(entry));
    }

    [Fact]
    public void BlankAndDuplicateEntriesAreDropped()
    {
        var m = Matcher(["", "  ", " admin-* ", "ADMIN-*", "lab-admin", "Lab-Admin"]);

        Assert.Equal(["admin-*", "lab-admin"], m.Entries);
        Assert.Empty(m.Rejected);
    }

    [Fact]
    public void NoneMatchesNothing()
    {
        Assert.False(DeletableAdminMatcher.None.Matches("admin-1"));
        Assert.Equal(0, DeletableAdminMatcher.None.Count);
    }
}
