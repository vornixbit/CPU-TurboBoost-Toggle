using System;
using System.Runtime.InteropServices;

namespace TurboToggle;

sealed class CopilotHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYUP = 0x0105;

    const uint VK_LWIN = 0x5B;
    const uint VK_LSHIFT = 0x10;
    const uint VK_F23 = 0x86;

    const long ArmTimeoutMs = 50;
    const long CopilotHeldTimeoutMs = 1000;

    delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    enum Stage { Idle, Win, WinShift, CopilotHeld }

    readonly LowLevelHookProc _hookProc;
    IntPtr _hook;
    Stage _stage;
    long _armedAt;
    long _copilotEnteredAt;
    bool _swallowF23Up;
    bool _disposed;
    bool _suspended;

    public bool Suspended
    {
        get => _suspended;
        set
        {
            _suspended = value;
            if (value)
            {
                _stage = Stage.Idle;
                _swallowF23Up = false;
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
        if (_disposed || _hook != IntPtr.Zero)
            return _hook != IntPtr.Zero;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, GetModuleHandleW(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _stage = Stage.Idle;
        _swallowF23Up = false;
    }

    static bool IsArmed(Stage stage) => stage is Stage.Win or Stage.WinShift;

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || Suspended)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool up = msg is WM_KEYUP or WM_SYSKEYUP;
        if (!down && !up)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        uint vk = (uint)Marshal.ReadInt32(lParam);
        long now = Environment.TickCount64;

        if (_stage == Stage.CopilotHeld && now - _copilotEnteredAt > CopilotHeldTimeoutMs)
        {
            _stage = Stage.Idle;
            _swallowF23Up = false;
        }

        if (IsArmed(_stage) && now - _armedAt > ArmTimeoutMs)
            _stage = Stage.Idle;

        if (down)
        {
            if (vk == VK_F23)
            {
                if (_stage == Stage.CopilotHeld || _swallowF23Up)
                    return 1;
                _stage = Stage.CopilotHeld;
                _copilotEnteredAt = now;
                _swallowF23Up = true;
                try { Pressed?.Invoke(); } catch (Exception ex) { Program.LogError(ex); }
                return 1;
            }
            if (vk is VK_LWIN or 0x5C && _stage == Stage.Idle)
            {
                _stage = Stage.Win;
                _armedAt = now;
            }
            else if (vk is VK_LSHIFT or 0xA0 or 0xA1 && _stage == Stage.Win)
            {
                _stage = Stage.WinShift;
                _armedAt = now;
            }
            else if (IsArmed(_stage))
            {
                _stage = Stage.Idle;
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (_swallowF23Up && vk == VK_F23)
        {
            _swallowF23Up = false;
            _stage = Stage.Idle;
            return 1;
        }
        if (vk is VK_LWIN or 0x5C)
        {
            if (_stage == Stage.CopilotHeld)
                _stage = Stage.Idle;
            else if (IsArmed(_stage))
                _stage = Stage.Idle;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Uninstall();
        GC.SuppressFinalize(this);
    }
}
