using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace TurboToggle;
static class SafeFile
{
    const uint GenericWrite = 0x40000000;
    const uint FileReadAttributes = 0x80;
    const uint OpenAlways = 4;
    const uint FileAttributeReparsePoint = 0x400;
    const uint FileAttributeDirectory = 0x10;
    const uint FileFlagBackupSemantics = 0x02000000;
    const uint FileFlagOpenReparsePoint = 0x00200000;
    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);
    internal static FileStream? OpenVerified(string path, bool truncateExisting, FileShare share)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SafeFileHandle handle;
            try
            {
                handle = CreateFileW(path, GenericWrite | FileReadAttributes, (uint)share,
                    IntPtr.Zero, OpenAlways,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            }
            catch
            {
                return null;
            }
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 4390 && DeleteReparseByPath(path))
                    continue;
                return null;
            }
            ByHandleFileInformation info;
            try
            {
                if (!GetFileInformationByHandle(handle, out info))
                {
                    handle.Dispose();
                    return null;
                }
            }
            catch
            {
                handle.Dispose();
                return null;
            }
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                handle.Dispose();
                if (!TryDeleteReparse(path))
                    return null;
                continue;
            }
            if ((info.FileAttributes & FileAttributeDirectory) != 0)
            {
                handle.Dispose();
                return null;
            }
            FileStream? stream = null;
            try
            {
                stream = new FileStream(handle, FileAccess.Write);
                if (truncateExisting)
                    stream.SetLength(0);
                else
                    stream.Seek(0, SeekOrigin.End);
                return stream;
            }
            catch
            {
                if (stream is not null)
                    stream.Dispose();
                else
                    handle.Dispose();
                return null;
            }
        }
        return null;
    }
    static bool DeleteReparseByPath(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return TryDeleteReparse(path);
        }
        catch
        {
        }
        return false;
    }
    static bool TryDeleteReparse(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
        }
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                return RemoveDirectoryW(path);
        }
        catch
        {
        }
        return false;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool RemoveDirectoryW(string lpPathName);
}
