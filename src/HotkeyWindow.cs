using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TurboToggle;

static class Hotkeys
{
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;

    public static readonly (string Label, uint Mods, uint Vk)[] Presets =
    {
        ("Ctrl+Alt+B",   ModControl | ModAlt,   0x42),
        ("Ctrl+Alt+T",   ModControl | ModAlt,   0x54),
        ("Ctrl+Shift+B", ModControl | ModShift, 0x42),
        ("Ctrl+Shift+T", ModControl | ModShift, 0x54),
        ("F9",           0,                     0x78),
        ("Copilot key",  ModShift | ModWin,     0x86),
    };

    static readonly FrozenDictionary<uint, string> SpecialNames = new Dictionary<uint, string>
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x1B] = "Esc",
        [0x20] = "Space", [0x21] = "PgUp", [0x22] = "PgDn", [0x23] = "End",
        [0x24] = "Home", [0x25] = "Left", [0x26] = "Up", [0x27] = "Right",
        [0x28] = "Down", [0x2D] = "Insert", [0x2E] = "Delete", [0x13] = "Pause",
        [0x5B] = "LWin", [0x5C] = "RWin", [0x5D] = "Apps", [0x90] = "NumLock", [0x91] = "Scroll",
        [0x6A] = "Num*", [0x6B] = "Num+", [0x6D] = "Num-", [0x6E] = "Num.", [0x6F] = "Num/",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".",
        [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    }.ToFrozenDictionary();

    public static string KeyName(uint vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87)
            return "F" + (vk - 0x6F);
        if (vk is >= 0x60 and <= 0x69)
            return "Num" + (vk - 0x60);
        return SpecialNames.TryGetValue(vk, out var name) ? name : "0x" + vk.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string ModsToString(uint mods)
    {
        var parts = new List<string>(4);
        if ((mods & ModControl) != 0) parts.Add("Ctrl");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModWin) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    public static string ToString(uint mods, uint vk)
    {
        var name = ModsToString(mods);
        return name.Length == 0 ? KeyName(vk) : name + "+" + KeyName(vk);
    }

    public static bool IsCopilot(uint mods, uint vk) =>
        vk == 0x86 && (mods == (ModShift | ModWin) || mods == 0);

    public static string DisplayName(uint mods, uint vk) =>
        IsCopilot(mods, vk) ? "Copilot key" : ToString(mods, vk);
}

sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const int HotkeyId = 1;
    const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? HotkeyPressed;

    bool _registered;
    uint _mods, _vk;

    public HotkeyWindow()
    {
        var cp = new CreateParams
        {
            Caption = "TurboToggleHotkeyWindow",
            Parent = new IntPtr(-3),
        };
        CreateHandle(cp);
    }

    public (uint Mods, uint Vk)? Current => _registered ? (_mods, _vk) : null;

    public void Unregister()
    {
        if (_registered && Handle != IntPtr.Zero)
            UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    public bool Register(uint mods, uint vk)
    {
        if (_disposed || Handle == IntPtr.Zero)
            return false;
        if (_registered && _mods == mods && _vk == vk)
            return true;

        uint prevMods = _mods;
        uint prevVk = _vk;
        bool hadPrevious = _registered;

        if (_registered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        if (RegisterHotKey(Handle, HotkeyId, mods | MOD_NOREPEAT, vk))
        {
            _registered = true;
            _mods = mods;
            _vk = vk;
            return true;
        }

        if (hadPrevious)
        {
            _registered = RegisterHotKey(Handle, HotkeyId, prevMods | MOD_NOREPEAT, prevVk);
            if (_registered)
            {
                _mods = prevMods;
                _vk = prevVk;
            }
        }
        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_registered && Handle != IntPtr.Zero)
                UnregisterHotKey(Handle, HotkeyId);
        }
        catch { }
        _registered = false;
        try
        {
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }
        catch { }
        GC.SuppressFinalize(this);
    }
}
