using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ManageUsers.Services;

/// <summary>
/// Opens log files without writing through a link. manageusers writes its logs as
/// SYSTEM, so a link left where a log or day directory belongs would turn every
/// appended line into a write somewhere else.
/// </summary>
/// <remarks>
/// <see cref="RemoveLinks"/> deletes any link (symbolic link or junction) found on the
/// way down from the logs root; the link itself goes, never what it points to. Then
/// <see cref="OpenAppend"/> opens the file without following a reparse point and, if
/// what it opened is a link or has a second name (a hard link), deletes that name and
/// starts a fresh file in its place. The installer locks the logs folder so a standard
/// user cannot put a link back afterwards.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SafeLogFile
{
    /// <summary>
    /// Deletes every link at <paramref name="root"/> and at each existing component below
    /// it down to <paramref name="path"/>. Returns one line per link removed.
    /// </summary>
    public static List<string> RemoveLinks(string root, string path)
    {
        var notes = new List<string>();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = fullRoot;
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
        var parts = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar);

        for (var i = -1; i < parts.Length; i++)
        {
            if (i >= 0) current = Path.Combine(current, parts[i]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }
            if (!attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            if (attributes.HasFlag(FileAttributes.Directory))
                Directory.Delete(current);
            else
                File.Delete(current);
            notes.Add($"Removed a link at {current}");
        }

        return notes;
    }

    /// <summary>
    /// Opens <paramref name="path"/> for appending, creating it if needed, never through a
    /// link. A link or hard link found at the path is deleted and a new file created.
    /// </summary>
    public static StreamWriter OpenAppend(string path, List<string>? notes = null)
    {
        var handle = Open(path, OpenAlways);
        if (IsLinked(handle))
        {
            handle.Dispose();
            File.Delete(path);
            notes?.Add($"Replaced {path}: it was linked to another file");
            handle = Open(path, CreateNew);
        }

        var stream = new FileStream(handle, FileAccess.Write);
        stream.Seek(0, SeekOrigin.End);
        return new StreamWriter(stream) { AutoFlush = true };
    }

    private static SafeFileHandle Open(string path, uint disposition)
    {
        var handle = CreateFileW(path, GenericWrite | FileReadAttributes, FileShareRead, IntPtr.Zero,
            disposition, FileAttributeNormal | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open {path}", new Win32Exception(error));
        }
        return handle;
    }

    private static bool IsLinked(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 || info.NumberOfLinks > 1;
    }

    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareRead = 0x1;
    private const uint CreateNew = 1;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
