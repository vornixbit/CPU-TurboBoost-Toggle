using System.Runtime.InteropServices;
namespace TurboToggle;
static class PowerApi
{
    public const uint ValueOff = 0;
    public const uint ValueOn = 2;
    static readonly Dictionary<(Guid Scheme, bool Ac), uint> _lastNonZero = new();
    static readonly Queue<(Guid Scheme, bool Ac)> _evictOrder = new();
    const int MaxLastNonZeroEntries = 64;
    static readonly object _memoryLock = new();
    static readonly object _schemeCacheLock = new();
    static Guid? _cachedScheme;
    static long _cachedSchemeTicks = -1;
    const int SchemeCacheTtlMs = 2000;
    static void LogReadError(Exception ex)
    {
        Program.LogError(ex);
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
    static extern uint PowerSetActiveOverlaySchemeNative(in Guid overlayScheme);
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
    static void InvalidateSchemeCache()
    {
        lock (_schemeCacheLock)
        {
            _cachedScheme = null;
            _cachedSchemeTicks = -1;
        }
    }
    internal static void InvalidateSchemeCacheForRefresh() => InvalidateSchemeCache();
    public static Guid? GetActiveScheme() => GetActiveScheme(useCache: true);
    public static Guid? GetActiveSchemeFresh() => GetActiveScheme(useCache: false);
    static Guid? GetActiveScheme(bool useCache)
    {
        if (useCache)
        {
            lock (_schemeCacheLock)
            {
                long now = Environment.TickCount64;
                if (_cachedScheme.HasValue && now - _cachedSchemeTicks < SchemeCacheTtlMs)
                    return _cachedScheme;
            }
        }
        try
        {
            uint rc = PowerGetActiveScheme(IntPtr.Zero, out var ptr);
            if (ptr == IntPtr.Zero)
                return null;
            try
            {
                if (rc != 0)
                    return null;
                var scheme = Marshal.PtrToStructure<Guid>(ptr);
                lock (_schemeCacheLock)
                {
                    _cachedScheme = scheme;
                    _cachedSchemeTicks = Environment.TickCount64;
                }
                return scheme;
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
    const byte AcLineOffline = 0;
    const byte AcLineUnknown = 255;
    static bool _lastAcPower = true;
    static bool _lastAcKnown;
    static int _acUnknownLogged;
    static long _lastAcCheckTicks = -AcCacheTtlMs;
    static readonly object _acLock = new();
    const long AcCacheTtlMs = 30_000;
    const long AcUnknownRetryTtlMs = 5_000;
    public static bool OnAcPower()
    {
        lock (_acLock)
        {
            long ttl = _lastAcKnown ? AcCacheTtlMs : AcUnknownRetryTtlMs;
            if (Environment.TickCount64 - _lastAcCheckTicks < ttl)
                return _lastAcPower;
        }
        bool haveStatus = false;
        bool statusResult = false;
        try
        {
            if (GetSystemPowerStatus(out var st) && st.ACLineStatus != AcLineUnknown)
            {
                statusResult = st.ACLineStatus != AcLineOffline;
                haveStatus = true;
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        if (haveStatus)
        {
            lock (_acLock)
            {
                _lastAcPower = statusResult;
                _lastAcCheckTicks = Environment.TickCount64;
                _lastAcKnown = true;
                _acUnknownLogged = 0;
            }
            return statusResult;
        }
        lock (_acLock)
        {
            _lastAcCheckTicks = Environment.TickCount64;
            _lastAcKnown = false;
        }
        if (Interlocked.Exchange(ref _acUnknownLogged, 1) == 0)
            LogReadError(new InvalidOperationException(
                "GetSystemPowerStatus reported an unknown AC line status; using the last-known power source."));
        lock (_acLock) { return _lastAcPower; }
    }
    public static void RefreshPowerSource()
    {
        lock (_acLock)
            _lastAcCheckTicks = -AcCacheTtlMs;
    }
    static uint? ReadIndex(Guid scheme, bool ac)
    {
        uint rc = ac
            ? PowerReadACValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, out var v)
            : PowerReadDCValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, out v);
        if (rc != 0)
            return null;
        Remember(scheme, ac, v);
        return v;
    }
    static void Remember(Guid scheme, bool ac, uint value)
    {
        if (value == ValueOff)
            return;
        lock (_memoryLock)
            RememberValue(_lastNonZero, _evictOrder, scheme, ac, value);
    }
    internal static void RememberValue(
        Dictionary<(Guid Scheme, bool Ac), uint> memory,
        Queue<(Guid Scheme, bool Ac)> order,
        Guid scheme, bool ac, uint value)
    {
        var key = (scheme, ac);
        if (!memory.ContainsKey(key))
            order.Enqueue(key);
        memory[key] = value;
        while (memory.Count > MaxLastNonZeroEntries && order.Count > 0)
            memory.Remove(order.Dequeue());
    }
    static bool? EvaluateBoostValue(uint? value) =>
        value is null ? null : value.Value != ValueOff;
    public static bool? ReadState()
    {
        try
        {
            var scheme = GetActiveScheme();
            if (scheme is null) return null;
            return ReadStateCore(scheme.Value, GetActiveOverlay());
        }
        catch (Exception ex)
        {
            LogReadError(ex);
            return null;
        }
    }
    internal static bool? ReadState(Guid scheme, Guid? overlay)
    {
        try
        {
            return ReadStateCore(scheme, overlay);
        }
        catch (Exception ex)
        {
            LogReadError(ex);
            return null;
        }
    }
    static bool? ReadStateCore(Guid scheme, Guid? overlay)
    {
        bool ac = OnAcPower();
        return EvaluateBoostValue(overlay is not null ? ReadIndex(overlay.Value, ac) : null)
            ?? EvaluateBoostValue(ReadIndex(scheme, ac));
    }
    static (uint Ac, uint Dc) PrepareBoostValues(
        Guid scheme, uint value, uint? currentAc, uint? currentDc)
    {
        lock (_memoryLock)
            return SelectValues(_lastNonZero, _evictOrder, scheme, value, currentAc, currentDc);
    }
    internal static (uint Ac, uint Dc) SelectValues(
        Dictionary<(Guid Scheme, bool Ac), uint> memory,
        Queue<(Guid Scheme, bool Ac)> order,
        Guid scheme, uint value, uint? currentAc, uint? currentDc)
    {
        if (currentAc is > 0)
            RememberValue(memory, order, scheme, true, currentAc.Value);
        if (currentDc is > 0)
            RememberValue(memory, order, scheme, false, currentDc.Value);
        if (value == ValueOff)
            return (ValueOff, ValueOff);
        return (
            memory.GetValueOrDefault((scheme, true), value),
            memory.GetValueOrDefault((scheme, false), value));
    }
    public static (uint Ac, uint Dc) WriteBoost(Guid scheme, uint value)
    {
        InvalidateSchemeCache();
        if (value == ValueOff)
        {
            _ = ReadIndex(scheme, ac: true);
            _ = ReadIndex(scheme, ac: false);
            return (
                PowerWriteACValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, ValueOff),
                PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, ValueOff));
        }
        var currentAc = ReadIndex(scheme, ac: true);
        var currentDc = ReadIndex(scheme, ac: false);
        var values = PrepareBoostValues(scheme, value, currentAc, currentDc);
        uint ac = PowerWriteACValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, values.Ac);
        uint dc = PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in SubProcessor, in PerfBoostMode, values.Dc);
        return (ac, dc);
    }
    public static uint ReapplyActiveScheme(Guid scheme)
    {
        InvalidateSchemeCache();
        try
        {
            return PowerSetActiveScheme(IntPtr.Zero, in scheme);
        }
        catch (EntryPointNotFoundException)
        {
            return 1;
        }
    }
    public static uint? ActivateOverlay(Guid overlay)
    {
        InvalidateSchemeCache();
        try
        {
            return PowerSetActiveOverlaySchemeNative(in overlay);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return null;
        }
    }
    public static async System.Threading.Tasks.Task<bool> VerifyStateAsync(
        bool target, bool? known = null, int tries = 5, int delayMs = 100,
        System.Threading.CancellationToken ct = default)
    {
        if (known == target)
            return true;
        if (known is null)
        {
            RefreshPowerSource();
            if (ReadState() == target)
                return true;
        }
        for (int i = 0; i < tries; i++)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return false;
            }
            catch (System.ObjectDisposedException)
            {
                return false;
            }
            if (ct.IsCancellationRequested)
                return false;
            RefreshPowerSource();
            if (ReadState() == target)
                return true;
            delayMs = Math.Min(delayMs * 2, 250);
        }
        return false;
    }
}
