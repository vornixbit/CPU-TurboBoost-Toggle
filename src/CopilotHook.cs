using System.Runtime.InteropServices;
namespace TurboToggle;
sealed class CopilotHook : IDisposable
{
    const uint VK_LWIN = 0x5B;
    const uint VK_RWIN = 0x5C;
    const uint VK_F23 = 0x86;
    const long CopilotHeldTimeoutMs = 1000;
    enum Stage { Idle, CopilotHeld }
    readonly NativeHooks.HookProc _hookProc;
    readonly object _lock = new();
    volatile IntPtr _hook;
    volatile Stage _stage;
    long _copilotEnteredAt;
    volatile bool _swallowF23Up;
    volatile bool _disposed;
    volatile bool _suspended;
    public bool Suspended
    {
        get => _suspended;
        set
        {
            lock (_lock)
            {
                _suspended = value;
                if (value)
                {
                    _stage = Stage.Idle;
                    _swallowF23Up = false;
                }
            }
        }
    }
    public bool IsInstalled => _hook != IntPtr.Zero;
    public event Action? Pressed;
    public CopilotHook()
    {
        _hookProc = HookProc;
    }
    public bool Install()
    {
        lock (_lock)
        {
            if (_disposed || _hook != IntPtr.Zero)
                return _hook != IntPtr.Zero;
            _ignoreUntilF23Up = false;
            _stage = Stage.Idle;
            _swallowF23Up = false;
            _hook = NativeHooks.SetWindowsHookExW(NativeHooks.WH_KEYBOARD_LL, _hookProc, NativeHooks.GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero)
                Program.LogError(new InvalidOperationException(
                    $"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})."));
            return _hook != IntPtr.Zero;
        }
    }
    public void Uninstall()
    {
        lock (_lock)
        {
            ReleaseHook();
            _stage = Stage.Idle;
            _swallowF23Up = false;
            _ignoreUntilF23Up = false;
        }
    }
    void ReleaseHook()
    {
        if (_hook == IntPtr.Zero)
            return;
        if (NativeHooks.UnhookWindowsHookEx(_hook))
        {
            _hook = IntPtr.Zero;
            return;
        }
        Program.LogError(new InvalidOperationException(
            $"UnhookWindowsHookEx failed (error {Marshal.GetLastWin32Error()}); "
            + "the hook stays installed and remains tracked."));
    }
    public void IgnoreUntilKeyUp()
    {
        lock (_lock) { _ignoreUntilF23Up = true; }
    }
    volatile bool _ignoreUntilF23Up;
    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try { return HookCore(nCode, wParam, lParam); }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return NativeHooks.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }
    IntPtr HookCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        bool suspended;
        lock (_lock) { suspended = _suspended; }
        if (nCode < 0 || suspended)
            return NativeHooks.CallNextHookEx(_hook, nCode, wParam, lParam);
        int msg = wParam.ToInt32();
        bool down = msg is NativeHooks.WM_KEYDOWN or NativeHooks.WM_SYSKEYDOWN;
        bool up = msg is NativeHooks.WM_KEYUP or NativeHooks.WM_SYSKEYUP;
        if (!down && !up)
            return NativeHooks.CallNextHookEx(_hook, nCode, wParam, lParam);
        uint vk = (uint)Marshal.ReadInt32(lParam);
        bool fire = false;
        int decision;
        lock (_lock)
        {
            if (_ignoreUntilF23Up)
            {
                if ((down || up) && vk == VK_F23)
                {
                    if (up)
                        _ignoreUntilF23Up = false;
                    decision = 1;
                    goto Done;
                }
            }
            if (_stage == Stage.CopilotHeld
                && Environment.TickCount64 - _copilotEnteredAt > CopilotHeldTimeoutMs)
            {
                _stage = Stage.Idle;
            }
            if (down)
            {
                if (vk == VK_F23)
                {
                    if (_stage == Stage.CopilotHeld || _swallowF23Up)
                    {
                        decision = 1;
                        goto Done;
                    }
                    _stage = Stage.CopilotHeld;
                    _copilotEnteredAt = Environment.TickCount64;
                    _swallowF23Up = true;
                    fire = true;
                    decision = 1;
                    goto Done;
                }
                decision = 0;
                goto Done;
            }
            if (_swallowF23Up && vk == VK_F23)
            {
                _swallowF23Up = false;
                _stage = Stage.Idle;
                decision = 1;
                goto Done;
            }
            if (vk is VK_LWIN or VK_RWIN && _stage == Stage.CopilotHeld)
                _stage = Stage.Idle;
            decision = 0;
        Done:;
        }
        if (fire)
        {
            Action? pressed;
            lock (_lock) { pressed = Pressed; }
            try { pressed?.Invoke(); } catch (Exception ex) { Program.LogError(ex); }
            return 1;
        }
        return decision == 1 ? 1 : NativeHooks.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseHook();
        }
    }
}
