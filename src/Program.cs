using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
namespace TurboToggle;
static class Program
{
    public const string AppName = "Turbo Toggle";
    public static readonly Version CurrentVersion = ComputeVersion();
    static readonly string _versionText = ComputeVersionText(CurrentVersion);
    public static string VersionText => _versionText;
    static readonly string _titleText = AppName + VersionText;
    public static string Title => _titleText;
    static Version ComputeVersion()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null)
                return new Version(1, 0);
            return new Version(
                Math.Max(v.Major, 0),
                Math.Max(v.Minor, 0),
                Math.Max(v.Build, 0),
                Math.Max(v.Revision, 0));
        }
        catch
        {
            return new Version(1, 0);
        }
    }
    static string ComputeVersionText(Version v)
    {
        var text = $" v{v.Major}.{v.Minor}.{v.Build}";
        return v.Revision > 0 ? $"{text}.{v.Revision}" : text;
    }
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogError(e.Exception); e.SetObserved(); };
        try
        {
            Run();
        }
        finally
        {
            ShutdownLog();
        }
    }
    static void Run()
    {
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        AppConfig config;
        try
        {
            config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            LogError(ex);
            config = new AppConfig();
        }
        bool persistDetectedLanguage = false;
        if (Localization.IsSupported(config.Lang))
        {
            Localization.Language = config.Lang;
        }
        else
        {
            Localization.Language = Localization.Detect();
            config.Lang = Localization.Language;
            persistDetectedLanguage = true;
        }
        if (!SingleInstance(out var mutex))
        {
            MessageBox.Show(Localization.Tr("already_running"), AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using (mutex)
        {
            if (persistDetectedLanguage)
            {
                ConfigStore.Save(config);
            }
            TrayContext ctx;
            try
            {
                ctx = new TrayContext(config);
            }
            catch (Exception ex)
            {
                LogError(ex);
                MessageBox.Show(Localization.Tr("startup_failed"), AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using (ctx)
                Application.Run(ctx);
        }
    }
    static bool SingleInstance(out Mutex? mutex)
    {
        const string name = @"Global\TurboToggleMutex";
        const int acquireTimeoutMs = 500;
        mutex = null;
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
    static readonly object LogLock = new();
    static readonly string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurboToggle");
    static readonly string _logPath = Path.Combine(_logDir, "error.log");
    const long MaxLogBytes = 256 * 1024;
    const long LogThrottleMs = 60_000;
    static StreamWriter? _logWriter;
    static string _throttleKey = "";
    static long _throttleWindowStart;
    static int _throttleSuppressed;
    static string? _throttleSummary;
    internal static void LogError(Exception? ex)
    {
        if (ex is null) return;
        Exception? rotateError = null;
        try
        {
            lock (LogLock)
            {
                if (ShouldWrite(ex))
                {
                    WriteRecord(Format(ex), out rotateError);
                    if (_throttleSummary is { } summary)
                    {
                        _throttleSummary = null;
                        WriteRecord($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [throttled] {summary}{Environment.NewLine}{Environment.NewLine}", out var summaryRotateError);
                        rotateError ??= summaryRotateError;
                    }
                }
            }
        }
        catch { }
        if (rotateError is not null)
        {
            try
            {
                lock (LogLock)
                {
                    if (ShouldWrite(rotateError))
                        WriteRecord(Format(rotateError), out _);
                }
            }
            catch { }
        }
    }
    static bool ShouldWrite(Exception ex)
    {
        var key = ex.GetType().FullName + ": " + ex.Message;
        long now = Environment.TickCount64;
        if (!string.Equals(key, _throttleKey, StringComparison.Ordinal))
        {
            QueueThrottleSummary();
            _throttleKey = key;
            _throttleWindowStart = now;
            _throttleSuppressed = 0;
            return true;
        }
        if (now - _throttleWindowStart >= LogThrottleMs)
        {
            QueueThrottleSummary();
            _throttleWindowStart = now;
            _throttleSuppressed = 0;
            return true;
        }
        _throttleSuppressed++;
        return false;
    }
    static void QueueThrottleSummary()
    {
        if (_throttleSuppressed > 0)
        {
            _throttleSummary = $"{_throttleSuppressed} more of \"{_throttleKey}\" within {LogThrottleMs / 1000}s";
        }
        _throttleSuppressed = 0;
    }
    static string Format(Exception ex) =>
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";
    static void WriteRecord(string line, out Exception? rotateError)
    {
        rotateError = null;
        var writer = EnsureLogWriter();
        if (writer is null)
            return;
        writer.Write(line);
        writer.Flush();
        if (writer.BaseStream.Position > MaxLogBytes)
            rotateError = RotateLog();
    }
    static bool _logClosed;
    static StreamWriter? EnsureLogWriter()
    {
        if (_logClosed || _logWriter is not null)
            return _logWriter;
        try
        {
            Directory.CreateDirectory(_logDir);
            var stream = SafeFile.OpenVerified(_logPath, truncateExisting: false, FileShare.Read);
            if (stream is null)
                return null;
            _logWriter = new StreamWriter(stream) { AutoFlush = false };
        }
        catch
        {
            _logWriter = null;
        }
        return _logWriter;
    }
    static int _rotateFailureLogged;
    static Exception? RotateLog()
    {
        try
        {
            _logWriter?.Dispose();
            _logWriter = null;
            try
            {
                if (File.Exists(_logPath + ".1"))
                    File.Move(_logPath + ".1", _logPath + ".2", overwrite: true);
            }
            catch { }
            File.Move(_logPath, _logPath + ".1", overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            try { SafeFile.OpenVerified(_logPath, truncateExisting: true, FileShare.Read)?.Dispose(); }
            catch { }
            if (Interlocked.Exchange(ref _rotateFailureLogged, 1) == 0)
                return ex;
            return null;
        }
    }
    internal static void ShutdownLog()
    {
        lock (LogLock)
        {
            _logClosed = true;
            try
            {
                if (_throttleSummary is { } summary)
                {
                    _throttleSummary = null;
                    _logWriter?.Write(
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [throttled] {summary}{Environment.NewLine}{Environment.NewLine}");
                }
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
            catch { }
            _logWriter = null;
        }
    }
}
