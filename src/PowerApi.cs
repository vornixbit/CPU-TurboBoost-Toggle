using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TurboToggle;

static class PowerApi
{
    public const uint ValueOff = 0;
    public const uint ValueOn = 2;

    static uint? _lastNonZero;
    static readonly object _lastNonZeroLock = new();
    static int _readErrorLogged;

    static void LogReadError(Exception ex)
    {
        if (Interlocked.Exchange(ref _readErrorLogged, 1) == 0)
            Program.LogError(ex);
    }

    public static uint ResolveOnValue()
    {
        lock (_lastNonZeroLock)
            return _lastNonZero ?? ValueOn;
    }

    static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern uint PowerGetActiveScheme(IntPtr userPowerKey, out IntPtr activeScheme);

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern uint PowerSetActiveScheme(IntPtr userPowerKey, in Guid scheme);

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern uint PowerGetActualOverlayScheme(out Guid overlayScheme);

    [DllImport("powrprof.dll", SetLastError = true, EntryPoint = "PowerSetActiveOverlayScheme")]
    static extern uint PowerSetActiveOverlaySchemeNative(Guid overlayScheme);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    public static Guid? GetActiveScheme()
    {
        try
        {
            uint rc = PowerGetActiveScheme(IntPtr.Zero, out var ptr);
            if (ptr == IntPtr.Zero)
                return null;
            try
            {
                if (rc != 0)
                    return null;
                return Marshal.PtrToStructure<Guid>(ptr);
            }
            finally
            {
                LocalFree(ptr);
            }
        }
        catch (Exception ex)
        {
            LogReadError(ex);
            return null;
        }
    }

    public static Guid? GetActiveOverlay()
    {
        try
        {
            if (PowerGetActualOverlayScheme(out var g) == 0 && !g.Equals(Guid.Empty))
                return g;
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        return null;
    }

    const byte AcLineOnline = 1;
    const byte AcLineOffline = 0;
    const byte BatteryFlagUnknown = 128;

    public static bool OnAcPower()
    {
        try
        {
            if (GetSystemPowerStatus(out var st))
            {
                if (st.ACLineStatus == AcLineOnline)
                    return true;
                if (st.ACLineStatus == AcLineOffline)
                    return false;
                if ((st.BatteryFlag & BatteryFlagUnknown) != 0)
                    return true;
            }
        }
        catch
        {
        }
        return true;
    }

    static uint? ReadIndex(Guid scheme, bool ac)
    {
        uint rc = ac
            ? PowerReadACValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, out var v)
            : PowerReadDCValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, out v);
        return rc == 0 ? v : null;
    }

    public static bool? ReadState()
    {
        try
        {
            var scheme = GetActiveScheme();
            if (scheme is null)
                return null;
            var overlay = GetActiveOverlay();
            bool ac = OnAcPower();

            if (overlay is not null)
            {
                var v = ReadIndex(overlay.Value, ac);
                if (v is not null)
                {
                    if (v != ValueOff)
                    {
                        lock (_lastNonZeroLock)
                            _lastNonZero = v;
                    }
                    return v != ValueOff;
                }
            }

            var baseValue = ReadIndex(scheme.Value, ac);
            if (baseValue is not null)
            {
                if (baseValue != ValueOff)
                {
                    lock (_lastNonZeroLock)
                        _lastNonZero = baseValue;
                }
                return baseValue != ValueOff;
            }

            return null;
        }
        catch (Exception ex)
        {
            LogReadError(ex);
            return null;
        }
    }

    public static (uint Ac, uint Dc) WriteBoost(Guid scheme, uint value)
    {
        uint ac = PowerWriteACValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, value);
        uint dc = PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, value);
        return (ac, dc);
    }

    public static uint ReapplyActiveScheme(Guid scheme) =>
        PowerSetActiveScheme(IntPtr.Zero, in scheme);

    public static uint? ActivateOverlay(Guid overlay)
    {
        try
        {
            return PowerSetActiveOverlaySchemeNative(overlay);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async System.Threading.Tasks.Task<bool> VerifyStateAsync(
        bool target, int tries = 5, int delayMs = 100,
        System.Threading.CancellationToken ct = default)
    {
        for (int i = 0; i < tries; i++)
        {
            if (ReadState() == target)
                return true;
            if (i == tries - 1)
                break;
            try
            {
                await System.Threading.Tasks.Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return false;
            }
            if (ct.IsCancellationRequested)
                return false;
            delayMs = Math.Min(delayMs * 2, 250);
        }
        return false;
    }
}
