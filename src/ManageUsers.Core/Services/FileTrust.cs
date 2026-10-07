using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ManageUsers.Services;

/// <summary>
/// Decides whether a file ManageUsers reads as SYSTEM could have been written by a
/// non-administrator. Config.yaml decides which accounts may be deleted, and
/// Inventory.yaml and Sessions.yaml steer the same decisions, so a file a standard user
/// could change is ignored rather than trusted.
/// </summary>
/// <remarks>
/// Every folder above the file must be owned by SYSTEM, Administrators or
/// TrustedInstaller, must not be a link, and must not let another account delete or
/// rename its entries or rewrite its ACL or owner. Creating entries is allowed there,
/// since what a user creates is owned by that user. The file itself must not be a link
/// and must grant no other account the right to write, append, delete, or rewrite its
/// ACL or owner.
///
/// Then the owner. A file owned by SYSTEM, Administrators or TrustedInstaller is
/// trusted. A file owned by an individual account is trusted only in a locked folder,
/// one where no other account can create files either: there only an administrator can
/// have made it. An administrator who edits a file elevated owns it under their own
/// account, and that is the case this allows. <see cref="NormalizeOwner"/> then hands
/// such a file to Administrators, so the individual account no longer holds the owner's
/// implicit right to change its ACL.
///
/// The installer locks C:\ProgramData\Management\ManageUsers and the data folder. The
/// first time it locks a folder it quarantines entries owned by an account it cannot
/// show to be an administrator, so nothing a standard user left there before the
/// lockdown is trusted afterwards. ACEs that only apply to children (inherit-only)
/// do not describe the object itself and are skipped.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FileTrust
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>OWNER RIGHTS: grants whatever the owner gets, and the owner is checked separately.</summary>
    private static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    private const FileSystemRights FileWriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const FileSystemRights FolderWriteRights =
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>A locked folder also lets no other account create files or folders in it.</summary>
    private const FileSystemRights LockedFolderRights =
        FolderWriteRights | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;

    /// <summary>The checker the tool uses: SYSTEM, Administrators and TrustedInstaller.</summary>
    public static FileTrust Default { get; } = new([SystemSid, AdministratorsSid, TrustedInstallerSid]);

    private readonly HashSet<SecurityIdentifier> _trusted;

    /// <param name="trustedSids">Accounts allowed to own trusted files and folders anywhere.</param>
    public FileTrust(IEnumerable<SecurityIdentifier> trustedSids)
    {
        _trusted = new HashSet<SecurityIdentifier>(trustedSids) { OwnerRightsSid };
    }

    /// <summary>
    /// Checks <paramref name="path"/> (a file or a folder) and every folder above it.
    /// Returns null when it can be trusted, or the reason it cannot. A file need not
    /// exist; its folders are still checked.
    /// </summary>
    public string? WhyUntrusted(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var file = new FileInfo(full);
            var start = Directory.Exists(full) ? new DirectoryInfo(full) : file.Directory;
            for (var dir = start; dir != null; dir = dir.Parent)
            {
                if (!dir.Exists) continue;
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return $"{dir.FullName} is a link";
                var reason = Assess(dir.GetAccessControl(), isDirectory: true);
                if (reason != null) return $"{dir.FullName}: {reason}";
            }

            if (file.Exists)
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return $"{full} is a link";
                var reason = AssessFile(file.GetAccessControl(), file.Directory!.GetAccessControl());
                if (reason != null) return $"{full}: {reason}";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"could not read the permissions of {path}: {ex.Message}";
        }
    }

    /// <summary>
    /// Checks one security descriptor on its own. Returns null when it is owned by a trusted
    /// account and grants no other account a right to change it, or the reason it does not.
    /// </summary>
    public string? Assess(FileSystemSecurity security, bool isDirectory)
    {
        var owner = OwnerOf(security);
        if (owner == null || !_trusted.Contains(owner))
            return $"owned by {Describe(owner)}, not by SYSTEM, Administrators or TrustedInstaller";
        return WhoCanModify(security, isDirectory ? FolderWriteRights : FileWriteRights);
    }

    /// <summary>
    /// Checks a file against its folder. Returns null when no other account can change the
    /// file and either a trusted account owns it or the folder is locked.
    /// </summary>
    public string? AssessFile(FileSecurity file, DirectorySecurity folder)
    {
        var writer = WhoCanModify(file, FileWriteRights);
        if (writer != null) return writer;

        var owner = OwnerOf(file);
        if (owner != null && _trusted.Contains(owner)) return null;
        if (IsLocked(folder)) return null;
        return $"owned by {Describe(owner)}, in a folder where non-administrators can create files";
    }

    /// <summary>
    /// True when the folder is owned by a trusted account and no other account can create,
    /// delete or rename entries in it, or rewrite its ACL or owner.
    /// </summary>
    public bool IsLocked(DirectorySecurity folder)
    {
        var owner = OwnerOf(folder);
        return owner != null && _trusted.Contains(owner) && WhoCanModify(folder, LockedFolderRights) == null;
    }

    /// <summary>
    /// Hands a trusted file owned by an individual account to Administrators, so that
    /// account no longer holds the owner's implicit right to change its ACL. Returns a
    /// line for the log when it changed the owner, otherwise null. Needs an elevated or
    /// SYSTEM process; anything else leaves the file as it is.
    /// </summary>
    public string? NormalizeOwner(string path)
    {
        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            var owner = OwnerOf(file.GetAccessControl(AccessControlSections.Owner));
            if (owner == null || _trusted.Contains(owner) || WhyUntrusted(path) != null) return null;

            var security = new FileSecurity();
            security.SetOwner(AdministratorsSid);
            file.SetAccessControl(security);
            return $"Gave ownership of {file.FullName} to Administrators (was {Describe(owner)})";
        }
        catch
        {
            return null;
        }
    }

    private string? WhoCanModify(FileSystemSecurity security, FileSystemRights writeRights)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || _trusted.Contains(sid)) continue;

            var mask = (int)rule.FileSystemRights;
            if ((mask & (GenericAll | GenericWrite)) != 0 || (rule.FileSystemRights & writeRights) != 0)
                return $"{Describe(sid)} can modify it ({rule.FileSystemRights})";
        }
        return null;
    }

    private static SecurityIdentifier? OwnerOf(FileSystemSecurity security) =>
        security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid == null) return "an unknown owner";
        try
        {
            return $"{sid.Translate(typeof(NTAccount)).Value} ({sid.Value})";
        }
        catch
        {
            return sid.Value;
        }
    }
}
