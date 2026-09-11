using System;
using System.Diagnostics;
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

    public static bool IsEnabled()
    {
        if (TaskExists())
            return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }

    public static bool SetEnabled(bool on)
    {
        try
        {
            if (on)
            {
                if (CreateScheduledTask())
                {
                    RemoveRunValue();
                    return true;
                }
                return SetRunValue();
            }

            DeleteScheduledTask();
            RemoveRunValue();
            return !TaskExists();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }

    static bool CreateScheduledTask()
    {
        if (ExePath.Length == 0)
            return false;
        return RunSchtasks($"/create /tn {TaskName} /tr \"\\\"{ExePath}\\\"\" /sc onlogon /rl highest /f");
    }

    static void DeleteScheduledTask() => RunSchtasks($"/delete /tn {TaskName} /f");

    static bool TaskExists() => RunSchtasks($"/query /tn {TaskName}");

    static bool RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p is null)
                return false;
            if (!p.WaitForExit(ProcessWaitTimeoutMs))
            {
                try { p.Kill(); p.WaitForExit(KillWaitTimeoutMs); } catch { }
                return false;
            }
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
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

    static void RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
