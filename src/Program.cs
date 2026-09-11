using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TurboToggle;

static class Program
{
    public const string AppName = "Turbo Toggle";

    static readonly string _versionText = ComputeVersionText();

    public static string VersionText => _versionText;

    public static string Title => AppName + VersionText;

    static string ComputeVersionText()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null)
                return "";
            return $" v{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
        catch
        {
            return "";
        }
    }

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogError(e.Exception); e.SetObserved(); };

        AppConfig config;
        try
        {
            config = ConfigStore.Load();
            if (Localization.IsSupported(config.Lang))
            {
                Localization.Language = config.Lang;
            }
            else
            {
                Localization.Language = Localization.Detect();
                config.Lang = Localization.Language;
                ConfigStore.Save(config);
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
            config = new AppConfig();
            Localization.Language = Localization.Detect();
        }

        if (!IsAdministrator())
        {
            RelaunchElevated();
            return;
        }

        if (!SingleInstance(out var mutex))
        {
            MessageBox.Show(Localization.Tr("already_running"), AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using (mutex)
        {
            using var ctx = new TrayContext(config);
            Application.Run(ctx);
        }
    }

    static bool SingleInstance(out Mutex mutex)
    {
        const string name = @"Global\TurboToggleMutex";
        const int acquireTimeoutMs = 500;
        mutex = null!;
        Mutex? m = null;
        try
        {
            m = new Mutex(false, name, out _);
            bool owned;
            try
            {
                owned = m.WaitOne(acquireTimeoutMs);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned)
                return false;
            try
            {
                var sec = new MutexSecurity();
                var sid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                sec.AddAccessRule(new MutexAccessRule(
                    sid,
                    MutexRights.Synchronize | MutexRights.Modify,
                    AccessControlType.Allow));
                m.SetAccessControl(sec);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
            mutex = m;
            m = null;
            return true;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
        finally
        {
            m?.Dispose();
        }
    }

    static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
    }

    static void RelaunchElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            MessageBox.Show(Localization.Tr("admin_required"),
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            };
            p.Start();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(Localization.Tr("admin_required"),
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static readonly object LogLock = new();
    static readonly string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurboToggle");
    static readonly string _logPath = Path.Combine(_logDir, "error.log");
    static readonly Lazy<bool> _logDirReady = new(() =>
    {
        Directory.CreateDirectory(_logDir);
        return true;
    });

    internal static void LogError(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            _ = _logDirReady.Value;
            var path = _logPath;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";
            lock (LogLock)
            {
                const long MaxLogSizeBytes = 256 * 1024;
                const int LogRetryCount = 3;
                const int LogRetryDelayMs = 50;
                try
                {
                    if (new FileInfo(path).Length > MaxLogSizeBytes)
                        File.Move(path, path + ".1", overwrite: true);
                }
                catch { }
                for (int attempt = 0; attempt < LogRetryCount; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var sw = new StreamWriter(fs) { AutoFlush = true };
                        sw.Write(line);
                        break;
                    }
                    catch (IOException) when (attempt < LogRetryCount - 1)
                    {
                        Thread.Sleep(LogRetryDelayMs * (attempt + 1));
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
        }
    }
}
