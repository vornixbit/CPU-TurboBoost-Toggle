using System.Runtime.InteropServices;
namespace TurboToggle;
sealed class HotkeyCaptureForm : Form
{
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    readonly NativeHooks.HookProc _hookProc;
    readonly Label _label;
    readonly string _defaultText;
    readonly System.Windows.Forms.Timer _timeout;
    readonly ModifierState _modifiers = new();
    readonly Action _updateLabelOnUi;
    readonly EventHandler _timeoutTickHandler;
    volatile IntPtr _hook;
    bool _hookFailed;
    bool _timedOut;
    volatile bool _closing;
    bool _cleanedUp;
    public (uint Mods, uint Vk)? Result { get; private set; }
    public bool UserCancelled { get; private set; }
    public bool HookFailed => _hookFailed;
    internal bool CopilotKeyHeld { get; private set; }
    public HotkeyCaptureForm()
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Text = Program.AppName;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? IconFactory.Get(true); }
        catch { try { Icon = IconFactory.Get(true); } catch { } }
        ClientSize = new Size(360, 92);
        _defaultText = $"{Localization.Tr("press_keys")}\n{Localization.Tr("cancel_hint")}";
        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = SystemFonts.MessageBoxFont,
            Text = _defaultText,
        };
        Controls.Add(_label);
        _hookProc = HookProcHandler;
        _updateLabelOnUi = UpdateLabel;
        _timeout = new System.Windows.Forms.Timer { Interval = 10_000 };
        _timeoutTickHandler = (_, _) => { _timedOut = true; _closing = true; Close(); };
        _timeout.Tick += _timeoutTickHandler;
    }
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        foreach (uint vk in new uint[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
        {
            bool down = (GetAsyncKeyState((int)vk) & 0x8000) != 0;
            _modifiers.Update(vk, down);
        }
        if ((GetAsyncKeyState((int)0x86) & 0x8000) != 0)
            CopilotKeyHeld = true;
        UpdateLabel();
        IntPtr hMod = NativeHooks.GetModuleHandleW(null);
        _hook = NativeHooks.SetWindowsHookExW(NativeHooks.WH_KEYBOARD_LL, _hookProc, hMod, 0);
        if (_hook == IntPtr.Zero)
            Program.LogError(new InvalidOperationException(
                $"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})."));
        try
        {
            if (_hook == IntPtr.Zero || hMod == IntPtr.Zero)
            {
                _hookFailed = true;
                CloseDeferred();
                return;
            }
            _timeout.Start();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        if (Result is null && !_timedOut && !_hookFailed)
            UserCancelled = true;
        Cleanup();
        base.OnFormClosed(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Cleanup();
        base.Dispose(disposing);
    }
    void Cleanup()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;
        _timeout.Stop();
        _timeout.Tick -= _timeoutTickHandler;
        _timeout.Dispose();
        if (_hook != IntPtr.Zero)
        {
            NativeHooks.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
    uint _shownMods = uint.MaxValue;
    void UpdateLabel()
    {
        uint mods = _modifiers.Mods;
        if (mods == _shownMods)
            return;
        _shownMods = mods;
        SetLabelText(mods == 0 ? _defaultText : Hotkeys.ModsToString(mods) + " …");
    }
    void ShowUnsupportedHint(uint vk)
    {
        _shownMods = uint.MaxValue;
        SetLabelText(Hotkeys.KeyName(vk) + " " + Localization.Tr("hotkey_unsupported"));
    }
    void SetLabelText(string text)
    {
        if (_label.Text != text)
            _label.Text = text;
    }
    void PostToUi(Action action) => PostToUi(action, null);
    void PostToUi(Action action, object?[]? state)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(action, state);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void CloseDeferred()
    {
        if (_closing || IsDisposed)
            return;
        _closing = true;
        PostToUi(Close);
    }
    IntPtr HookProcHandler(int nCode, IntPtr wParam, IntPtr lParam)
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
        if (nCode < 0)
            return NativeHooks.CallNextHookEx(_hook, nCode, wParam, lParam);
        int msg = wParam.ToInt32();
        bool down = msg is NativeHooks.WM_KEYDOWN or NativeHooks.WM_SYSKEYDOWN;
        bool up = msg is NativeHooks.WM_KEYUP or NativeHooks.WM_SYSKEYUP;
        if (!down && !up)
            return NativeHooks.CallNextHookEx(_hook, nCode, wParam, lParam);
        uint vk = (uint)Marshal.ReadInt32(lParam);
        if (vk == 0x86)
            CopilotKeyHeld = down;
        if (_modifiers.Update(vk, down))
        {
            PostToUi(_updateLabelOnUi);
            return 1;
        }
        if (up)
            return NativeHooks.CallNextHookEx(_hook, nCode, wParam, lParam);
        if (_closing)
            return 1;
        if (vk == 0x1B)
        {
            UserCancelled = true;
            CloseDeferred();
            return 1;
        }
        uint mods = _modifiers.Mods;
        if (vk == 0x5D || vk > 0xDE || (mods == Hotkeys.ModWin && vk == 0x42))
        {
            PostToUi(() => ShowUnsupportedHint(vk));
            return 1;
        }
        if (mods != 0 || vk is >= 0x70 and <= 0x87)
        {
            if (_closing)
                return 1;
            Result = (mods, vk);
            CloseDeferred();
            return 1;
        }
        PostToUi(_updateLabelOnUi);
        return 1;
    }
}
