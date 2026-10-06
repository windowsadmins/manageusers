using System.Security.AccessControl;
using System.Security.Principal;
using ManageUsers.Services;
using Xunit;

namespace ManageUsers.Tests;

public class FileTrustTests
{
    private const string Locked = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    private static FileSecurity FileAcl(string sddl)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    private static DirectorySecurity FolderAcl(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    [Theory]
    [InlineData("O:SY")]
    [InlineData("O:BA")]
    [InlineData("O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")]
    public void LockedObjectOwnedByAnAdministratorIsTrusted(string owner)
    {
        Assert.Null(FileTrust.Default.Assess(FileAcl(owner + Locked), isDirectory: false));
        Assert.Null(FileTrust.Default.Assess(FolderAcl(owner + Locked), isDirectory: true));
    }

    [Theory]
    [InlineData("O:BU")]
    [InlineData("O:S-1-5-21-1111111111-2222222222-3333333333-1001")]
    public void FileOwnedByANonAdministratorIsNotTrusted(string owner)
    {
        var reason = FileTrust.Default.Assess(FileAcl(owner + Locked), isDirectory: false);
        Assert.NotNull(reason);
        Assert.Contains("owned by", reason);
    }

    [Theory]
    [InlineData("(A;;FW;;;BU)")]
    [InlineData("(A;;0x2;;;AU)")]
    [InlineData("(A;;0x4;;;WD)")]
    [InlineData("(A;;SD;;;BU)")]
    [InlineData("(A;;WD;;;BU)")]
    [InlineData("(A;;WO;;;BU)")]
    [InlineData("(A;;GA;;;BU)")]
    [InlineData("(A;;GW;;;S-1-5-21-1111111111-2222222222-3333333333-1001)")]
    public void FileANonAdministratorCanWriteIsNotTrusted(string ace)
    {
        var reason = FileTrust.Default.Assess(FileAcl("O:SY" + Locked + ace), isDirectory: false);
        Assert.NotNull(reason);
        Assert.Contains("can modify it", reason);
    }

    [Fact]
    public void FolderWhereUsersMayOnlyCreateEntriesIsTrusted()
    {
        // ProgramData's own grant: users may add files and folders, not remove others'.
        Assert.Null(FileTrust.Default.Assess(FolderAcl("O:SY" + Locked + "(A;CI;0x116;;;BU)"), isDirectory: true));
    }

    [Theory]
    [InlineData("(A;;0x40;;;BU)")]
    [InlineData("(A;;SD;;;BU)")]
    [InlineData("(A;OICI;FA;;;AU)")]
    public void FolderWhereUsersMayRemoveEntriesIsNotTrusted(string ace)
    {
        Assert.NotNull(FileTrust.Default.Assess(FolderAcl("O:SY" + Locked + ace), isDirectory: true));
    }

    [Fact]
    public void InheritOnlyAndDenyEntriesDoNotCount()
    {
        Assert.Null(FileTrust.Default.Assess(FolderAcl("O:SY" + Locked + "(A;OICIIO;FA;;;CO)(A;OICIIO;FA;;;BU)"), isDirectory: true));
        Assert.Null(FileTrust.Default.Assess(FileAcl("O:SYD:PAI(D;;FA;;;BU)(A;;FA;;;SY)(A;;FR;;;BU)"), isDirectory: false));
    }

    private const string Individual = "O:S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void AdministratorsOwnFileInALockedFolderIsTrusted()
    {
        // An administrator who saves Config.yaml elevated owns it under their own account.
        Assert.Null(FileTrust.Default.AssessFile(FileAcl(Individual + Locked), FolderAcl("O:SY" + Locked)));
    }

    [Fact]
    public void IndividualOwnerIsNotTrustedWhereUsersCanCreateFiles()
    {
        var reason = FileTrust.Default.AssessFile(FileAcl(Individual + Locked), FolderAcl("O:SY" + Locked + "(A;CI;0x116;;;BU)"));
        Assert.NotNull(reason);
        Assert.Contains("can create files", reason);
    }

    [Fact]
    public void TrustedOwnerIsTrustedEvenWhereUsersCanCreateFiles()
    {
        // Inventory.yaml lives in a shared folder that is not locked.
        Assert.Null(FileTrust.Default.AssessFile(FileAcl("O:SY" + Locked), FolderAcl("O:SY" + Locked + "(A;CI;0x116;;;BU)")));
    }

    [Theory]
    [InlineData("(A;;FW;;;BU)")]
    [InlineData("(A;;WD;;;S-1-5-21-1111111111-2222222222-3333333333-1002)")]
    [InlineData("(A;;SD;;;AU)")]
    public void LockedFolderDoesNotExcuseAFileOthersCanChange(string ace)
    {
        var reason = FileTrust.Default.AssessFile(FileAcl(Individual + Locked + ace), FolderAcl("O:SY" + Locked));
        Assert.NotNull(reason);
        Assert.Contains("can modify it", reason);
    }

    [Theory]
    [InlineData("O:SY" + Locked, true)]
    [InlineData("O:BA" + Locked, true)]
    [InlineData("O:BU" + Locked, false)]
    [InlineData("O:SY" + Locked + "(A;CI;0x116;;;BU)", false)]
    [InlineData("O:SY" + Locked + "(A;;0x2;;;BU)", false)]
    [InlineData("O:SY" + Locked + "(A;;0x4;;;BU)", false)]
    [InlineData("O:SY" + Locked + "(A;OICIIO;FA;;;CO)", true)]
    public void LockedMeansOnlyAdministratorsCanCreateOrChangeEntries(string sddl, bool locked)
    {
        Assert.Equal(locked, FileTrust.Default.IsLocked(FolderAcl(sddl)));
    }

    [Fact]
    public void ChecksTheFileAndEveryFolderAboveIt()
    {
        // Run unelevated, this process cannot make SYSTEM own a file, so the test trusts
        // its own account alongside the tool's list and uses Users as the outsider.
        using var me = WindowsIdentity.GetCurrent();
        var trust = new FileTrust([
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),
            me.User!
        ]);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        // Under ProgramData, so the folders above are the ones the tool really sits under;
        // a profile's Temp folder carries grants of its own.
        var root = Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            $"manageusers-trust-{Guid.NewGuid():N}"));
        try
        {
            var locked = new DirectorySecurity();
            locked.SetAccessRuleProtection(true, false);
            locked.AddAccessRule(new FileSystemAccessRule(me.User!, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
            locked.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
            root.SetAccessControl(locked);

            var path = Path.Combine(root.FullName, "Config.yaml");
            File.WriteAllText(path, "delete_admins: false");
            Assert.Null(trust.WhyUntrusted(path));
            Assert.Null(trust.WhyUntrusted(Path.Combine(root.FullName, "Missing.yaml")));

            var file = new FileInfo(path);
            var writable = file.GetAccessControl();
            writable.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
            file.SetAccessControl(writable);
            Assert.Contains("Config.yaml", trust.WhyUntrusted(path));

            File.Delete(path);
            File.WriteAllText(path, "delete_admins: false");
            var open = root.GetAccessControl();
            open.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            root.SetAccessControl(open);
            var reason = trust.WhyUntrusted(path);
            Assert.NotNull(reason);
            Assert.StartsWith(root.FullName, reason);
            Assert.NotNull(trust.WhyUntrusted(root.FullName));

            // With the tool's own list, a file this unelevated test owns is refused.
            if (!new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator))
                Assert.Contains("owned by", FileTrust.Default.WhyUntrusted(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
