using System.Collections.Frozen;
using System.Runtime.InteropServices;
namespace TurboToggle;
static class Hotkeys
{
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;
    public const uint ModAll = ModAlt | ModControl | ModShift | ModWin;
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
        [0x28] = "Down", [0x2D] = "Insert", [0x2E] = "Delete", [0x13] = "Pause", [0x14] = "CapsLock",
        [0x5B] = "LWin", [0x5C] = "RWin", [0x5D] = "Apps", [0x90] = "NumLock", [0x91] = "Scroll",
        [0x6A] = "Num*", [0x6B] = "Num+", [0x6D] = "Num-", [0x6E] = "Num.", [0x6F] = "Num/",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".",
        [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    }.ToFrozenDictionary();
    static readonly string[] FunctionKeyNames = BuildNames("F", 24, 1);
    static readonly string[] NumpadNames = BuildNames("Num", 10, 0);
    static readonly string[] ModNames = BuildModNames();
    static readonly string[] CharNames = BuildCharNames();
    static string[] BuildCharNames()
    {
        var names = new string[36];
        for (int i = 0; i < 10; i++)
            names[i] = ((char)('0' + i)).ToString();
        for (int i = 0; i < 26; i++)
            names[10 + i] = ((char)('A' + i)).ToString();
        return names;
    }
    static string[] BuildNames(string prefix, int count, int start)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = prefix + (start + i);
        return names;
    }
    static string[] BuildModNames()
    {
        var names = new string[16];
        for (uint m = 0; m < names.Length; m++)
        {
            string result = "";
            if ((m & ModControl) != 0) result = "Ctrl";
            if ((m & ModAlt) != 0) result = result.Length == 0 ? "Alt" : result + "+Alt";
            if ((m & ModShift) != 0) result = result.Length == 0 ? "Shift" : result + "+Shift";
            if ((m & ModWin) != 0) result = result.Length == 0 ? "Win" : result + "+Win";
            names[m] = result;
        }
        return names;
    }
    public static string KeyName(uint vk)
    {
        if (vk is >= 0x30 and <= 0x39)
            return CharNames[vk - 0x30];
        if (vk is >= 0x41 and <= 0x5A)
            return CharNames[10 + (vk - 0x41)];
        if (vk is >= 0x70 and <= 0x87)
            return FunctionKeyNames[vk - 0x70];
        if (vk is >= 0x60 and <= 0x69)
            return NumpadNames[vk - 0x60];
        return SpecialNames.TryGetValue(vk, out var name) ? name : "0x" + vk.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string ModsToString(uint mods) => ModNames[mods & ModAll];
    public static string ToString(uint mods, uint vk)
    {
        var name = ModsToString(mods);
        return name.Length == 0 ? KeyName(vk) : name + "+" + KeyName(vk);
    }
    public static bool IsCopilot(uint mods, uint vk) =>
        vk == 0x86 && (mods == (ModShift | ModWin) || mods == 0);
    public static string DisplayName(uint mods, uint vk) =>
        IsCopilot(mods, vk) ? "Copilot key" : ToString(mods, vk);
    public static bool IsSupportedCombo(uint mods, uint vk) =>
        vk != 0 && vk != 0x5D && vk <= 0xDE
        && (mods & ~ModAll) == 0
        && !(mods == ModWin && vk == 0x42)
        && (mods != 0 || (vk >= 0x70 && vk <= 0x87));
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
    readonly object _lock = new();
    public HotkeyWindow()
    {
        const int HWND_MESSAGE = -3;
        var cp = new CreateParams
        {
            Caption = "TurboToggleHotkeyWindow",
            Parent = new IntPtr(HWND_MESSAGE),
        };
        CreateHandle(cp);
    }
    public (uint Mods, uint Vk)? Current => _registered ? (_mods, _vk) : null;
    public void Unregister()
    {
        lock (_lock)
        {
            if (!_registered)
                return;
            if (Handle == IntPtr.Zero)
            {
                _registered = false;
                return;
            }
            if (UnregisterHotKey(Handle, HotkeyId))
            {
                _registered = false;
                return;
            }
            Program.LogError(new InvalidOperationException(
                "UnregisterHotKey failed with Win32 error " + Marshal.GetLastWin32Error() + "."));
        }
    }
    public bool Register(uint mods, uint vk)
    {
        lock (_lock)
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
                else
                {
                    Program.LogError(new InvalidOperationException("Hotkey register failed and rollback re-register failed."));
                }
            }
            return false;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            try { HotkeyPressed?.Invoke(); } catch (Exception ex) { Program.LogError(ex); }
            return;
        }
        try { base.WndProc(ref m); } catch (Exception ex) { Program.LogError(ex); }
    }
    bool _disposed;
    public void Dispose()
    {
        lock (_lock)
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
        }
    }
}
