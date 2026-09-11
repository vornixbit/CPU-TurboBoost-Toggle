using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TurboToggle;

sealed class HotkeyCaptureForm : Form
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYUP = 0x0105;

    delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    readonly LowLevelHookProc _hookProc;
    readonly Label _label;
    readonly string _defaultText;
    readonly System.Windows.Forms.Timer _timeout;
    readonly Font _font;
    IntPtr _hook;
    bool _hookFailed;

    public (uint Mods, uint Vk)? Result { get; private set; }

    public bool UserCancelled { get; private set; }

    bool _timedOut;

    public HotkeyCaptureForm()
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Text = Program.AppName;
        ClientSize = new Size(360, 92);

        _defaultText = $"{Localization.Tr("press_keys")}\n{Localization.Tr("cancel_hint")}";
        _font = new Font("Segoe UI", 10f);
        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = _font,
            Text = _defaultText,
        };
        Controls.Add(_label);

        _hookProc = HookProc;
        _timeout = new System.Windows.Forms.Timer { Interval = 10_000 };
        _timeout.Tick += (_, _) => { _timedOut = true; Close(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _hookFailed = true;
            CloseDeferred();
            return;
        }
        _timeout.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (Result is null && !_timedOut && !_hookFailed)
            UserCancelled = true;
        _timeout.Stop();
        _timeout.Dispose();
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _font.Dispose();
        base.OnFormClosed(e);
    }

    static uint CurrentMods()
    {
        uint mods = 0;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= Hotkeys.ModControl;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= Hotkeys.ModAlt;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= Hotkeys.ModShift;
        if (((GetAsyncKeyState(0x5B) & 0x8000) != 0) || ((GetAsyncKeyState(0x5C) & 0x8000) != 0))
            mods |= Hotkeys.ModWin;
        return mods;
    }

    void CloseDeferred()
    {
        try
        {
            if (!IsDisposed)
                BeginInvoke(new Action(Close));
        }
        catch
        {
        }
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return HookCore(nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    IntPtr HookCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        int msg = wParam.ToInt32();
        if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            uint vk = (uint)Marshal.ReadInt32(lParam);
            uint mods = CurrentMods();

            if (vk == 0x1B)
            {
                UserCancelled = true;
                CloseDeferred();
                return 1;
            }

            bool isModifier = vk is 0x10 or 0x11 or 0x12
                or >= 0xA0 and <= 0xA5
                or 0x5B or 0x5C;
            if (vk == 0x5D)
            {
                _label.Text = Hotkeys.KeyName(vk) + " " + Localization.Tr("hotkey_unsupported");
                return 1;
            }
            if (!isModifier && (mods != 0 || vk is >= 0x70 and <= 0x87))
            {
                Result = (mods, vk);
                CloseDeferred();
                return 1;
            }

            _label.Text = mods == 0
                ? _defaultText
                : Hotkeys.ModsToString(mods) + " …";

            return 1;
        }
        if (msg is WM_KEYUP or WM_SYSKEYUP)
        {
            uint mods = CurrentMods();
            _label.Text = mods == 0
                ? _defaultText
                : Hotkeys.ModsToString(mods) + " …";
            return 1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
