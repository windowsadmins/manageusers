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
/// A file is trusted when it, and every folder above it, is owned by SYSTEM,
/// Administrators or TrustedInstaller, is not a link, and grants no other account a
/// right that would let it change the file's contents: write or append to the file,
/// delete or rename it, or rewrite its ACL or owner. On a folder, creating files is
/// allowed, since anything a user creates there is owned by that user and fails the
/// owner test; deleting entries in the folder is not. ACEs that only apply to children
/// (inherit-only) do not describe the object itself and are skipped.
///
/// The installer gives C:\ProgramData\Management\ManageUsers an explicit ACL that
/// passes this test; files created there before the lockdown keep their owner and are
/// refused until an administrator replaces them.
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

    /// <summary>The checker the tool uses: SYSTEM, Administrators and TrustedInstaller.</summary>
    public static FileTrust Default { get; } = new([SystemSid, AdministratorsSid, TrustedInstallerSid]);

    private readonly HashSet<SecurityIdentifier> _trusted;

    /// <param name="trustedSids">Accounts allowed to own and write trusted files.</param>
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
            if (file.Exists)
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return $"{full} is a link";
                var reason = Assess(file.GetAccessControl(), isDirectory: false);
                if (reason != null) return $"{full}: {reason}";
            }

            var start = Directory.Exists(full) ? new DirectoryInfo(full) : file.Directory;
            for (var dir = start; dir != null; dir = dir.Parent)
            {
                if (!dir.Exists) continue;
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return $"{dir.FullName} is a link";
                var reason = Assess(dir.GetAccessControl(), isDirectory: true);
                if (reason != null) return $"{dir.FullName}: {reason}";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"could not read the permissions of {path}: {ex.Message}";
        }
    }

    /// <summary>
    /// Checks one security descriptor. Returns null when only trusted accounts own or can
    /// write the object, or the reason another account can.
    /// </summary>
    public string? Assess(FileSystemSecurity security, bool isDirectory)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !_trusted.Contains(owner))
            return $"owned by {Describe(owner)}, not by SYSTEM, Administrators or TrustedInstaller";

        var writeRights = isDirectory ? FolderWriteRights : FileWriteRights;
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
