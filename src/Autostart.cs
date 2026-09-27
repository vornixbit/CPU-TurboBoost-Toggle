using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
namespace TurboToggle;
static class Autostart
{
    const string TaskName = "TurboToggle";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "TurboToggle";
    const int ProcessWaitTimeoutMs = 15000;
    const int KillWaitTimeoutMs = 2000;
    static string ExePath => Environment.ProcessPath ?? string.Empty;
    static readonly string SystemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
    static readonly SecurityIdentifier WorldSid = new(WellKnownSidType.WorldSid, null);
    static readonly SecurityIdentifier BuiltinUsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    static readonly SecurityIdentifier InteractiveSid = new(WellKnownSidType.InteractiveSid, null);
    static readonly SecurityIdentifier AuthenticatedUsersSid = new(WellKnownSidType.AuthenticatedUserSid, null);
    static readonly string[] UntrustedRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Path.GetTempPath(),
    ];
    internal const FileSystemRights DeleteChild = (FileSystemRights)0x00000040;
    internal const FileSystemRights LeafWriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    internal const FileSystemRights AncestorWriteRights = LeafWriteRights & ~FileSystemRights.AppendData | DeleteChild;
    static readonly string TaskFilePath = Path.Combine(SystemDir, "Tasks", TaskName);
    internal static readonly string SchtasksPath = Path.Combine(SystemDir, "schtasks.exe");
    static readonly Lazy<Encoding> _schtasksOutputEncoding = new(ResolveSchtasksOutputEncoding);
    internal static Encoding SchtasksOutputEncoding => _schtasksOutputEncoding.Value;
    static readonly bool Elevated = ComputeElevated();
    static bool ComputeElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
    static Encoding ResolveSchtasksOutputEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(unchecked((int)GetOEMCP()));
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return Encoding.UTF8;
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint GetOEMCP();
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(RunValue) is string run
                && string.Equals(FirstToken(Environment.ExpandEnvironmentVariables(run.Trim())),
                    ExePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        return TaskMatchesExe();
    }
    static string FirstToken(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : s.Trim('"');
        }
        int space = s.IndexOfAny([' ', '\t']);
        return space > 0 ? s[..space] : s;
    }
    static bool ExeDirSafeForElevatedTask()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ExePath);
            if (string.IsNullOrEmpty(dir))
                return false;
            foreach (string root in UntrustedRoots)
            {
                if (IsSameOrUnder(dir, root))
                    return false;
            }
            using var self = WindowsIdentity.GetCurrent();
            var selfSid = self.User;
            if (selfSid is null)
                return false;
            if (IsWritableByUntrustedIdentity(ExePath, isFile: true, LeafWriteRights, selfSid))
                return false;
            if (IsWritableByUntrustedIdentity(dir, isFile: false, LeafWriteRights, selfSid))
                return false;
            for (string? ancestor = dir; !string.IsNullOrEmpty(ancestor);)
            {
                string? parent = Path.GetDirectoryName(ancestor);
                if (string.IsNullOrEmpty(parent)
                    || string.Equals(parent, ancestor, StringComparison.OrdinalIgnoreCase))
                    break;
                if (IsWritableByUntrustedIdentity(parent, isFile: false, AncestorWriteRights, selfSid))
                    return false;
                ancestor = parent;
            }
            return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }
    static bool IsWritableByUntrustedIdentity(string path, bool isFile, FileSystemRights rights,
        SecurityIdentifier selfSid)
    {
        FileSystemSecurity security = isFile
            ? new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner == selfSid)
            return true;
        return DaclGrantsUntrustedWrite(security.GetSecurityDescriptorBinaryForm(), selfSid, rights);
    }
    internal static bool DaclGrantsUntrustedWrite(byte[] rawSecurityDescriptor, SecurityIdentifier selfSid,
        FileSystemRights rights)
    {
        var sd = new RawSecurityDescriptor(rawSecurityDescriptor, 0);
        if (sd.DiscretionaryAcl is null)
            return true;
        int required = (int)rights;
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if (ace is not KnownAce known
                || (known.AceFlags & AceFlags.InheritOnly) != 0
                || known.AceType is not (AceType.AccessAllowed or AceType.AccessAllowedObject)
                || known.SecurityIdentifier is not { } sid
                || (known.AccessMask & required) == 0)
                continue;
            if (sid.Equals(selfSid) || sid.Equals(WorldSid) || sid.Equals(BuiltinUsersSid)
                || sid.Equals(InteractiveSid) || sid.Equals(AuthenticatedUsersSid))
                return true;
        }
        return false;
    }
    static bool IsSameOrUnder(string dir, string root)
    {
        if (string.IsNullOrEmpty(root))
            return false;
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return dir.Equals(root, StringComparison.OrdinalIgnoreCase)
            || dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static bool SetEnabled(bool on)
    {
        try
        {
            if (on)
            {
                if (!ExeDirSafeForElevatedTask())
                {
                    DeleteScheduledTask();
                    return SetRunValue();
                }
                if (CreateScheduledTask())
                {
                    if (RemoveRunValue())
                        return true;
                    DeleteScheduledTask();
                }
                else
                {
                    DeleteScheduledTask();
                }
                return SetRunValue();
            }
            bool taskRemoved = DeleteScheduledTask();
            bool removed = RemoveRunValue();
            if (!removed)
                return false;
            if (taskRemoved)
                return true;
            return !TaskExists();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }
    internal static string[] CreateTaskArguments(string exePath) =>
    [
        "/create",
        "/tn", TaskName,
        "/tr", exePath,
        "/sc", "onlogon",
        "/rl", "highest",
        "/f",
    ];
    static bool CreateScheduledTask()
    {
        if (ExePath.Length == 0)
            return false;
        var psi = new ProcessStartInfo(SchtasksPath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = SystemDir,
        };
        foreach (string argument in CreateTaskArguments(ExePath))
            psi.ArgumentList.Add(argument);
        using var p = Process.Start(psi);
        if (p is null)
            return false;
        try
        {
            if (!p.WaitForExit(ProcessWaitTimeoutMs))
            {
                try { p.Kill(); } catch (Exception ex) { Program.LogError(ex); }
                try { p.WaitForExit(KillWaitTimeoutMs); } catch { }
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            try { p.Kill(); } catch { }
        }
        return p.HasExited && p.ExitCode == 0;
    }
    static bool DeleteScheduledTask() => RunSchtasks("/delete", "/tn", TaskName, "/f");
    static bool TaskExists()
    {
        try
        {
            if (File.Exists(TaskFilePath))
                return true;
            if (Elevated)
                return false;
        }
        catch (Exception ex) { Program.LogError(ex); }
        return RunSchtasks("/query", "/tn", TaskName);
    }
    static bool TaskMatchesExe()
    {
        if (ExePath.Length == 0)
            return false;
        var xml = ReadTaskXml();
        return xml is not null && CommandMatches(xml, ExePath);
    }
    static string? ReadTaskXml()
    {
        try
        {
            if (File.Exists(TaskFilePath))
                return File.ReadAllText(TaskFilePath, Encoding.Unicode);
            if (Elevated)
                return null;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        return RunSchtasksCapture("/query", "/tn", TaskName, "/xml");
    }
    internal static bool CommandMatches(string taskXml, string exePath)
    {
        const string open = "<Command>";
        const string close = "</Command>";
        int start = taskXml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;
        start += open.Length;
        int end = taskXml.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return false;
        var command = System.Net.WebUtility.HtmlDecode(taskXml[start..end]).Trim().Trim('"');
        return string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase);
    }
    static bool RunSchtasks(params string[] args) => RunSchtasksCore(capture: false, args) is not null;
    static string? RunSchtasksCapture(params string[] args) => RunSchtasksCore(capture: true, args);
    static string? RunSchtasksCore(bool capture, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(SchtasksPath)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = SystemDir,
                RedirectStandardOutput = capture,
                StandardOutputEncoding = capture ? SchtasksOutputEncoding : null,
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            Task<string>? read = capture ? p.StandardOutput.ReadToEndAsync() : null;
            if (!p.WaitForExit(ProcessWaitTimeoutMs))
            {
                try { p.Kill(); }
                catch (Exception ex) { Program.LogError(ex); }
                finally { p.WaitForExit(KillWaitTimeoutMs); }
                Observe(read);
                return null;
            }
            if (!p.HasExited || p.ExitCode != 0)
            {
                Observe(read);
                return null;
            }
            if (read is null)
                return string.Empty;
            if (read.Wait(ProcessWaitTimeoutMs))
            {
                try { return read.Result; }
                catch { Observe(read); return null; }
            }
            Observe(read);
            return null;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return null;
        }
    }
    static void Observe(Task<string>? read)
    {
        if (read is not null)
            _ = read.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
    }
    static bool SetRunValue()
    {
        try
        {
            if (ExePath.Length == 0)
                return false;
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(RunValue, "\"" + ExePath + "\"");
            return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }
    static bool RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
                return true;
            key.DeleteValue(RunValue, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }
}
