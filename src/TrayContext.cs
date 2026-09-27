using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
namespace TurboToggle;
sealed class TrayContext : ApplicationContext
{
    const int PollIntervalMs = 120000;
    const int HotkeyRetryIntervalMs = 500;
    const int StartupUpdateDelayMs = 15000;
    const int InitialStateRetryDelayMs = 2000;
    const int BalloonRateLimitSeconds = 2;
    const int ImportantBalloonRepeatSeconds = 10;
    const int AutostartCacheTtlSeconds = 30;
    const int BalloonRateLimitMs = BalloonRateLimitSeconds * 1000;
    const int ImportantBalloonRepeatMs = ImportantBalloonRepeatSeconds * 1000;
    const int AutostartCacheTtlMs = AutostartCacheTtlSeconds * 1000;
    const int MaxHotkeyRetries = 4;
    readonly NotifyIcon _icon;
    readonly HotkeyWindow _hotkeyWindow;
    readonly CopilotHook _copilotHook = new();
    readonly System.Windows.Forms.Timer _fallbackTimer;
    readonly System.Windows.Forms.Timer _hotkeyRetryTimer;
    readonly CancellationTokenSource _startupCts = new();
    readonly AppConfig _config;
    readonly SynchronizationContext? _ui;
    readonly CancellationToken _shutdownToken;
    ToolStripMenuItem? _enableItem;
    ToolStripMenuItem? _disableItem;
    ToolStripMenuItem? _autostartItem;
    ToolStripMenuItem? _restoreItem;
    ToolStripMenuItem? _notificationsItem;
    ToolStripMenuItem? _autoUpdateItem;
    ToolStripMenuItem? _checkNowItem;
    ToolStripMenuItem? _hotkeyOffItem;
    ToolStripMenuItem? _hotkeyItem;
    ToolStripMenuItem? _settingsItem;
    volatile bool _turboOn;
    volatile uint _mods, _vk;
    volatile bool _hotkeyEnabled = true;
    volatile bool _restoreOnExit;
    volatile bool _notifications = true;
    volatile bool _autoUpdate = true;
    volatile bool _checkingUpdates;
    volatile string _updateUrl = "";
    volatile bool _updateClickArmed;
    volatile string _updateClickArmedFor = "";
    volatile uint _retryPrevMods, _retryPrevVk;
    volatile HotkeyEntry? _retryPrevEntry;
    volatile bool _retryPrevEnabled;
    bool? _retryPrevEnabledCfg;
    volatile int _hotkeyRetries;
    volatile bool _exiting;
    volatile bool _applying;
    volatile bool _disposed;
    bool IsShuttingDown => _exiting || _disposed;
    volatile bool _autostartCached;
    volatile bool _autostartBusy;
    int _restored;
    int _autostartSeq;
    bool _menuRebuildPending;
    bool _rebuildOnMenuClose;
    const long ExpiredTicks = long.MinValue;
    static long AgeMs(long sinceTicks) =>
        sinceTicks == ExpiredTicks ? long.MaxValue : Environment.TickCount64 - sinceTicks;
    long _lastAutostartCheckTicks = ExpiredTicks;
    long _lastBalloonTicks = ExpiredTicks;
    string _lastBalloonText = "";
    public TrayContext(AppConfig config)
    {
        _config = config;
        _shutdownToken = _startupCts.Token;
        Localization.Language = Localization.IsSupported(_config.Lang)
            ? _config.Lang
            : Localization.Detect();
        var saved = _config.Hotkey ?? new HotkeyEntry();
        _mods = saved.Mods;
        _vk = saved.Vk;
        _hotkeyEnabled = _config.HotkeyEnabled ?? true;
        _restoreOnExit = _config.RestoreOnExit;
        _notifications = _config.Notifications ?? true;
        _autoUpdate = _config.AutoUpdateCheck ?? true;
        var initialState = PowerApi.ReadState();
        _turboOn = initialState ?? false;
        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
        _copilotHook.Pressed += OnHotkeyPressed;
        _icon = new NotifyIcon
        {
            Icon = IconFactory.Get(_turboOn),
            ContextMenuStrip = BuildMenu() ?? FallbackMenu(),
            Visible = true,
        };
        _ui = SynchronizationContext.Current;
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_updateClickArmed)
            {
                _updateClickArmed = false;
                OpenUpdatePage();
            }
        };
        UpdateTooltip();
        RefreshAutostartCacheAsync();
        _hotkeyRetryTimer = new System.Windows.Forms.Timer { Interval = HotkeyRetryIntervalMs };
        _hotkeyRetryTimer.Tick += HotkeyRetryTick;
        UpdateHotkeyRegistration();
        _fallbackTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _fallbackTimer.Tick += (_, _) => OnPowerChanged();
        _fallbackTimer.Start();
        if (initialState is null)
            RetryInitialState();
        try
        {
            SystemEvents.SessionEnded += OnSessionEnded;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        try
        {
            var scheduler = SynchronizationContext.Current is not null
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : TaskScheduler.Default;
            _ = Task.Delay(StartupUpdateDelayMs, _shutdownToken).ContinueWith(t =>
            {
                if (t.IsCanceled || IsShuttingDown || !_autoUpdate)
                    return;
                _ = CheckForUpdatesAsync(manual: false);
            }, scheduler);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void RetryInitialState()
    {
        try
        {
            _ = Task.Delay(InitialStateRetryDelayMs, _shutdownToken).ContinueWith(t =>
            {
                if (t.IsCanceled || IsShuttingDown)
                    return;
                OnPowerChanged();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void OnPowerChanged()
    {
        try
        {
            if (IsShuttingDown) return;
            PowerApi.RefreshPowerSource();
            PowerApi.InvalidateSchemeCacheForRefresh();
            var state = PowerApi.ReadState();
            if (state is { } value && value != _turboOn && !_applying)
            {
                _turboOn = value;
                UpdateVisuals();
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void HotkeyRetryTick(object? sender, EventArgs e)
    {
        try
        {
            if (IsShuttingDown || !_hotkeyEnabled)
            {
                _hotkeyRetryTimer.Stop();
                return;
            }
            if (Hotkeys.IsCopilot(_mods, _vk))
            {
                _hotkeyRetryTimer.Stop();
                return;
            }
            if (_hotkeyWindow.Register(_mods, _vk))
            {
                _hotkeyRetryTimer.Stop();
                NotifyHotkeySet(_mods, _vk);
                UpdateTooltip();
                UpdateChecks();
                return;
            }
            if (--_hotkeyRetries <= 0)
            {
                _hotkeyRetryTimer.Stop();
                bool isStartupRetry = _mods == _retryPrevMods && _vk == _retryPrevVk;
                if (isStartupRetry)
                {
                    _hotkeyEnabled = false;
                    _config.HotkeyEnabled = false;
                    ConfigStore.RequestSave(_config);
                    UpdateHotkeyRegistration(armRetry: false);
                    UpdateVisuals();
                    Notify(Localization.Tr("hotkey_failed"), important: true);
                }
                else
                {
                    _mods = _retryPrevMods;
                    _vk = _retryPrevVk;
                    _hotkeyEnabled = _retryPrevEnabled;
                    _config.Hotkey = _retryPrevEntry;
                    _config.HotkeyEnabled = _retryPrevEnabledCfg;
                    ConfigStore.RequestSave(_config);
                    UpdateHotkeyRegistration(armRetry: false);
                    UpdateVisuals();
                    Notify(Localization.Tr("hotkey_failed"), important: true);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            _hotkeyRetryTimer.Stop();
        }
    }
    void OnHotkeyPressed()
    {
        try
        {
            if (_applying || _exiting)
                return;
            PostToUi(() => _ = ToggleAsync());
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    Task ToggleAsync() => SetStateAsync(null);
    internal static bool? ResolveTarget(bool? requested, bool? current) =>
        requested ?? (current is { } state ? !state : null);
    async Task SetStateAsync(bool? requested)
    {
        if (_applying || _exiting)
            return;
        try
        {
            _applying = true;
            SetMenuEnabled(false);
            PowerApi.RefreshPowerSource();
            var scheme = PowerApi.GetActiveSchemeFresh();
            if (scheme is null)
            {
                Notify(Localization.Tr("cannot_scheme"), important: true);
                return;
            }
            var overlayBefore = PowerApi.GetActiveOverlay();
            var target = ResolveTarget(requested, PowerApi.ReadState(scheme.Value, overlayBefore));
            if (target is null)
            {
                Notify(Localization.Tr("cannot_confirm"), important: true);
                return;
            }
            bool on = target.Value;
            if (_exiting)
                return;
            uint value = on ? PowerApi.ValueOn : PowerApi.ValueOff;
            _ = PowerApi.WriteBoost(scheme.Value, value);
            uint? overlayRc = null;
            bool overlayWriteOk = true;
            if (overlayBefore is not null)
            {
                var (ovAc, ovDc) = PowerApi.WriteBoost(overlayBefore.Value, value);
                overlayWriteOk = ovAc == 0 && ovDc == 0;
                overlayRc = PowerApi.ActivateOverlay(overlayBefore.Value);
            }
            var current = PowerApi.GetActiveScheme();
            if (current is null)
            {
                var check = PowerApi.ReadState();
                if (check is { } v)
                {
                    _turboOn = v;
                    UpdateVisuals();
                }
                Notify(Localization.Tr("cannot_confirm"), important: true);
                return;
            }
            if (current.Value != scheme.Value || PowerApi.GetActiveOverlay() != overlayBefore)
            {
                Notify(Localization.Tr("cannot_scheme"), important: true);
                return;
            }
            _ = PowerApi.ReapplyActiveScheme(scheme.Value);
            bool? fast = PowerApi.ReadState();
            if (_exiting)
                return;
            bool applied = await PowerApi.VerifyStateAsync(on, fast, ct: _shutdownToken);
            if (_exiting)
                return;
            var currentAfter = PowerApi.GetActiveScheme();
            var overlayAfter = PowerApi.GetActiveOverlay();
            if (currentAfter is null || currentAfter.Value != scheme.Value
                || overlayAfter != overlayBefore)
            {
                Notify(Localization.Tr("cannot_scheme"), important: true);
                return;
            }
            var final = PowerApi.ReadState(currentAfter.Value, overlayAfter);
            if (final is not null)
                _turboOn = final.Value;
            else if (applied)
                _turboOn = on;
            UpdateVisuals();
            if (final is null)
                Notify(Localization.Tr("cannot_confirm"), important: true);
            else if (final == on)
                Notify(Localization.Tr(_turboOn ? "turbo_enabled" : "turbo_disabled"), skipRateLimit: true);
            else if (overlayBefore is not null && overlayWriteOk && overlayRc == 0)
                Notify(Localization.Tr("power_mode_override"));
            else
                Notify(Localization.Tr("not_applied"), important: true);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            Notify(Localization.Tr("cannot_confirm"), important: true);
        }
        finally
        {
            _applying = false;
            if (!_exiting && !_disposed)
            {
                SetMenuEnabled(true);
                if (_menuRebuildPending)
                {
                    _menuRebuildPending = false;
                    RebuildMenu();
                }
            }
            else if (_exiting)
                TryRestoreBoost();
        }
    }
    void SetMenuEnabled(bool enabled)
    {
        if (_disposed || _icon.ContextMenuStrip is null)
            return;
        try
        {
            SetEnabled(_enableItem, enabled);
            SetEnabled(_disableItem, enabled);
            SetEnabled(_hotkeyItem, enabled);
            SetEnabled(_settingsItem, enabled);
            SetEnabled(_autostartItem, enabled && !_autostartBusy);
            SetEnabled(_checkNowItem, enabled && !_checkingUpdates);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void UpdateHotkeyRegistration(bool armRetry = true)
    {
        _hotkeyRetryTimer.Stop();
        _hotkeyRetries = 0;
        _hotkeyWindow.Unregister();
        _copilotHook.Uninstall();
        if (!_hotkeyEnabled || IsShuttingDown)
            return;
        if (Hotkeys.IsCopilot(_mods, _vk))
        {
            if (!_copilotHook.Install())
                Notify(Localization.Tr("hotkey_failed"), important: true);
        }
        else if (!_hotkeyWindow.Register(_mods, _vk) && armRetry)
        {
            ArmHotkeyRetry(_mods, _vk, _config.Hotkey ?? new HotkeyEntry(_mods, _vk),
                _hotkeyEnabled, _config.HotkeyEnabled);
        }
    }
    void ArmHotkeyRetry(uint prevMods, uint prevVk, HotkeyEntry? prevEntry,
        bool prevEnabled, bool? prevEnabledCfg)
    {
        _retryPrevMods = prevMods;
        _retryPrevVk = prevVk;
        _retryPrevEntry = prevEntry;
        _retryPrevEnabled = prevEnabled;
        _retryPrevEnabledCfg = prevEnabledCfg;
        _hotkeyRetries = MaxHotkeyRetries;
        _hotkeyRetryTimer.Start();
    }
    void ApplyHotkey(uint mods, uint vk, bool copilotKeyHeld = false)
    {
        try
        {
            uint prevMods = _mods, prevVk = _vk;
            var prevEntry = _config.Hotkey;
            bool prevEnabled = _hotkeyEnabled;
            bool? prevEnabledCfg = _config.HotkeyEnabled;
            bool prevCopilot = Hotkeys.IsCopilot(prevMods, prevVk) && _copilotHook.IsInstalled;
            _mods = mods;
            _vk = vk;
            _hotkeyEnabled = true;
            bool ok;
            bool retryArmed = false;
            if (Hotkeys.IsCopilot(mods, vk))
            {
                _hotkeyRetryTimer.Stop();
                _hotkeyRetries = 0;
                _hotkeyWindow.Unregister();
                _copilotHook.Uninstall();
                ok = _copilotHook.Install();
                if (ok && copilotKeyHeld)
                    _copilotHook.IgnoreUntilKeyUp();
            }
            else
            {
                _copilotHook.Uninstall();
                ok = _hotkeyWindow.Register(mods, vk);
                if (ok)
                {
                    _hotkeyRetryTimer.Stop();
                    _hotkeyRetries = 0;
                }
                else if (_hotkeyWindow.Current is null && !prevCopilot)
                {
                    ArmHotkeyRetry(prevMods, prevVk, prevEntry, prevEnabled, prevEnabledCfg);
                    retryArmed = true;
                }
                else
                {
                    _hotkeyRetryTimer.Stop();
                }
            }
            if (!ok && !retryArmed)
            {
                _mods = prevMods;
                _vk = prevVk;
                _hotkeyEnabled = prevEnabled;
                _config.Hotkey = prevEntry;
                _config.HotkeyEnabled = prevEnabledCfg;
                UpdateHotkeyRegistration();
            }
            else
            {
                _config.Hotkey = new HotkeyEntry(mods, vk);
                _config.HotkeyEnabled = true;
            }
            ConfigStore.RequestSave(_config);
            UpdateVisuals();
            if (ok)
                NotifyHotkeySet(mods, vk);
            else
                Notify(Localization.Tr(Hotkeys.IsCopilot(mods, vk) ? "hotkey_failed" : "hotkey_busy"), important: true);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void SetHotkeyEnabled(bool on)
    {
        try
        {
            _hotkeyEnabled = on;
            _config.HotkeyEnabled = on;
            ConfigStore.RequestSave(_config);
            UpdateHotkeyRegistration();
            UpdateVisuals();
            if (on)
                NotifyHotkeySet(_mods, _vk);
            else
                Notify(Localization.Tr("hotkey_disabled"));
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void CaptureCustomHotkey()
    {
        bool resumeRetry = _hotkeyRetryTimer.Enabled;
        int savedRetries = _hotkeyRetries;
        _hotkeyRetryTimer.Stop();
        _copilotHook.Suspended = true;
        _hotkeyWindow.Unregister();
        bool applied = false;
        try
        {
            using var form = new HotkeyCaptureForm();
            form.ShowDialog();
            if (form.Result is { } combo)
            {
                applied = true;
                ApplyHotkey(combo.Mods, combo.Vk, copilotKeyHeld: form.CopilotKeyHeld);
            }
            else if (form.HookFailed)
            {
                Notify(Localization.Tr("hotkey_failed"), important: true);
            }
            else if (!form.UserCancelled)
            {
                Notify(Localization.Tr("capture_none"));
            }
        }
        finally
        {
            _copilotHook.Suspended = false;
            if (!applied)
            {
                if (resumeRetry)
                {
                    _hotkeyRetries = savedRetries;
                    _hotkeyRetryTimer.Start();
                }
                else
                {
                    UpdateHotkeyRegistration();
                }
            }
            if (resumeRetry && !_hotkeyRetryTimer.Enabled && _hotkeyEnabled
                && _hotkeyWindow.Current is null && _hotkeyRetries > 0 && !_exiting && !_disposed)
                _hotkeyRetryTimer.Start();
        }
    }
    void SetLanguage(string code)
    {
        try
        {
            if (!Localization.IsSupported(code) || Localization.Language == code)
                return;
            Localization.Language = code;
            _config.Lang = code;
            ConfigStore.RequestSave(_config);
            UpdateVisuals();
            RebuildMenu();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void TryRestoreBoost()
    {
        if (_restoreOnExit && Interlocked.Exchange(ref _restored, 1) == 0)
            RestoreBoostBestEffort();
    }
    void NotifyHotkeySet(uint mods, uint vk) =>
        Notify($"{Localization.Tr("hotkey_set")} {Hotkeys.DisplayName(mods, vk)}");
    void ToggleBool(bool current, Action<bool> configSetter, Action<bool> fieldSetter)
    {
        try
        {
            bool value = !current;
            fieldSetter(value);
            configSetter(value);
            ConfigStore.RequestSave(_config);
            UpdateChecks();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void ToggleRestoreOnExit()
    {
        try
        {
            bool value = !_restoreOnExit;
            _restoreOnExit = value;
            _config.RestoreOnExit = value;
            ConfigStore.RequestSave(_config);
            UpdateChecks();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void ToggleNotifications() => ToggleBool(_notifications, v => _config.Notifications = v, val => _notifications = val);
    void ToggleAutoUpdate() => ToggleBool(_autoUpdate, v => _config.AutoUpdateCheck = v, val => _autoUpdate = val);
    void ToggleAutostart()
    {
        if (_autostartBusy)
            return;
        _autostartBusy = true;
        SetEnabled(_autostartItem, false);
        int seq = ++_autostartSeq;
        try
        {
            Task.Run(() =>
            {
                bool target = !Autostart.IsEnabled();
                return (Target: target, Ok: Autostart.SetEnabled(target));
            }).ContinueWith(t =>
            {
                if (IsShuttingDown || seq != _autostartSeq)
                    return;
                _autostartBusy = false;
                if (t.IsCompletedSuccessfully && t.Result.Ok)
                {
                    _lastAutostartCheckTicks = Environment.TickCount64;
                    _autostartCached = t.Result.Target;
                    Notify(Localization.Tr(t.Result.Target ? "autostart_on" : "autostart_off"));
                }
                else
                {
                    _lastAutostartCheckTicks = ExpiredTicks;
                    Notify(Localization.Tr("autostart_error"), important: true);
                }
                UpdateChecks();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            _autostartBusy = false;
            UpdateChecks();
            Program.LogError(ex);
        }
    }
    async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates || IsShuttingDown)
            return;
        _checkingUpdates = true;
        if (!_exiting && !_disposed && _icon.ContextMenuStrip is not null)
            SetEnabled(_checkNowItem, false);
        try
        {
            var result = await Updater.CheckAsync(_shutdownToken);
            if (IsShuttingDown)
                return;
            if (result.Status == Updater.Status.Available)
            {
                _updateUrl = result.Url;
                if (manual)
                {
                    _updateClickArmedFor = result.Tag;
                    var open = MessageBox.Show(
                        Localization.Tr("update_available") + " " + result.Tag + "\n" +
                        Localization.Tr("open_releases_page"),
                        Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (open == DialogResult.Yes)
                        OpenUpdatePage();
                }
                else if (_updateClickArmedFor != result.Tag)
                {
                    _updateClickArmedFor = result.Tag;
                    _updateClickArmed = true;
                    Notify(Localization.Tr("update_available") + " " + result.Tag,
                        updateBalloon: true);
                }
            }
            else
            {
                _updateUrl = "";
                _updateClickArmed = false;
                _updateClickArmedFor = "";
                if (manual)
                {
                    if (result.Status == Updater.Status.Failed && result.Url != "")
                    {
                        _updateUrl = result.Url;
                        var open = MessageBox.Show(
                            Localization.Tr("update_check_failed") + "\n" +
                            Localization.Tr("open_releases_page"),
                            Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                        if (open == DialogResult.Yes)
                            OpenUpdatePage();
                    }
                    else
                    {
                        MessageBox.Show(
                            Localization.Tr(result.Status switch
                            {
                                Updater.Status.UpToDate => "up_to_date",
                                Updater.Status.NoReleases => "no_releases",
                                _ => "update_check_failed",
                            }),
                            Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        finally
        {
            _checkingUpdates = false;
            UpdateChecks();
        }
    }
    void OpenUpdatePage()
    {
        if (_updateUrl != "")
            OpenUrl(_updateUrl);
    }
    static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo(url) { UseShellExecute = true };
            p.Start();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    static void RestoreBoostBestEffort()
    {
        try
        {
            PowerApi.RefreshPowerSource();
            var scheme = PowerApi.GetActiveSchemeFresh();
            if (scheme is null)
                return;
            uint value = PowerApi.ValueOn;
            PowerApi.WriteBoost(scheme.Value, value);
            var overlay = PowerApi.GetActiveOverlay();
            if (overlay is not null)
            {
                PowerApi.WriteBoost(overlay.Value, value);
                PowerApi.ActivateOverlay(overlay.Value);
            }
            var current = PowerApi.GetActiveScheme();
            if (current is null || current.Value != scheme.Value)
                return;
            PowerApi.ReapplyActiveScheme(scheme.Value);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void RefreshAutostartCacheAsync(bool force = false)
    {
        if (_autostartBusy)
            return;
        if (!force && AgeMs(_lastAutostartCheckTicks) < AutostartCacheTtlMs)
            return;
        int seq = _autostartSeq;
        try
        {
            Task.Run(() => Autostart.IsEnabled()).ContinueWith(t =>
            {
                if (IsShuttingDown || seq != _autostartSeq || _autostartBusy)
                    return;
                if (!t.IsCompletedSuccessfully)
                    return;
                _lastAutostartCheckTicks = Environment.TickCount64;
                _autostartCached = t.Result;
                UpdateChecks();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void Notify(string message, bool important = false, bool updateBalloon = false, bool skipRateLimit = false)
    {
        if (!updateBalloon)
            _updateClickArmed = false;
        if ((!_notifications && !important) || _exiting || !_icon.Visible)
            return;
        long age = AgeMs(_lastBalloonTicks);
        if (!important && !skipRateLimit && age < BalloonRateLimitMs)
            return;
        if ((important || skipRateLimit) && message == _lastBalloonText && age < ImportantBalloonRepeatMs)
            return;
        _lastBalloonTicks = Environment.TickCount64;
        _lastBalloonText = message;
        try
        {
            _icon.ShowBalloonTip(0, Program.AppName, message, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void OnSessionEnded(object sender, SessionEndedEventArgs e)
    {
        if (IsShuttingDown)
            return;
        _exiting = true;
        TryRestoreBoost();
        PostToUi(SessionEndedCleanup);
    }
    void SessionEndedCleanup()
    {
        if (_disposed)
            return;
        try
        {
            _fallbackTimer.Stop();
            ConfigStore.FlushPending();
            Application.Exit();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (IsShuttingDown)
            return;
        PostToUi(OnPowerChanged);
    }
    void PostToUi(Action action)
    {
        var ui = _ui;
        if (ui is null)
        {
            try { action(); }
            catch (Exception ex) { Program.LogError(ex); }
            return;
        }
        try { ui.Post(_ => action(), null); }
        catch (Exception ex) { Program.LogError(ex); }
    }
    void ExitApp()
    {
        if (_exiting)
            return;
        _exiting = true;
        try
        {
            _fallbackTimer.Stop();
            _hotkeyRetryTimer.Stop();
            ConfigStore.FlushPending();
            _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
            _copilotHook.Pressed -= OnHotkeyPressed;
            _copilotHook.Uninstall();
            try { TryRestoreBoost(); }
            finally
            {
                _icon.Visible = false;
                Application.Exit();
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            try { _icon.Visible = false; } catch { }
            try { Application.Exit(); } catch { }
        }
    }
    void UpdateVisuals()
    {
        try
        {
            var icon = IconFactory.Get(_turboOn);
            if (!ReferenceEquals(_icon.Icon, icon))
                _icon.Icon = icon;
            UpdateTooltip();
            UpdateChecks();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    bool _tooltipCached;
    bool _tipOn, _tipHotkeyEnabled;
    uint _tipMods, _tipVk;
    string _tipLang = "";
    string _tipText = "";
    void UpdateTooltip()
    {
        if (_tooltipCached && _tipOn == _turboOn && _tipHotkeyEnabled == _hotkeyEnabled
            && _tipMods == _mods && _tipVk == _vk && _tipLang == Localization.Language)
        {
            if (_icon.Text != _tipText)
                _icon.Text = _tipText;
            return;
        }
        string hk = _hotkeyEnabled ? Hotkeys.DisplayName(_mods, _vk) : Localization.Tr("disabled");
        var text = TruncateTooltip(Localization.Tr(_turboOn ? "status_on" : "status_off") + " [" + hk + "]");
        _tooltipCached = true;
        _tipOn = _turboOn;
        _tipHotkeyEnabled = _hotkeyEnabled;
        _tipMods = _mods;
        _tipVk = _vk;
        _tipLang = Localization.Language;
        _tipText = text;
        if (_icon.Text != text)
            _icon.Text = text;
    }
    internal const int TooltipMaxLength = 127;

    internal static string TruncateTooltip(string text)
    {
        const int max = TooltipMaxLength;
        if (text.Length <= max)
            return text;
        int len = max - 1;
        if (char.IsHighSurrogate(text[len - 1]) && len < text.Length && char.IsLowSurrogate(text[len]))
            len--;
        while (len > 0 && char.GetUnicodeCategory(text[len - 1])
            is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.EnclosingMark)
            len--;
        if (len <= 0)
            len = max - 1;
        return text[..len] + "\u2026";
    }
    void UpdateChecks()
    {
        if (_disposed || _exiting || _icon.ContextMenuStrip is null)
            return;
        try
        {
            SetChecked(_enableItem, _turboOn);
            SetChecked(_disableItem, !_turboOn);
            SetChecked(_autostartItem, _autostartCached);
            SetEnabled(_autostartItem, !_autostartBusy && !_applying);
            SetChecked(_restoreItem, _restoreOnExit);
            SetChecked(_notificationsItem, _notifications);
            SetChecked(_autoUpdateItem, _autoUpdate);
            SetEnabled(_checkNowItem, !_checkingUpdates && !_applying);
            SetChecked(_hotkeyOffItem, !_hotkeyEnabled);
            foreach (var (item, mods, vk) in _presetItems)
                SetChecked(item, _hotkeyEnabled && _mods == mods && _vk == vk);
            foreach (var (code, item) in _langItems)
                SetChecked(item, Localization.Language == code);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    static void SetChecked(ToolStripMenuItem? item, bool value)
    {
        if (item is null || item.Checked == value)
            return;
        item.Checked = value;
    }
    static void SetEnabled(ToolStripMenuItem? item, bool value)
    {
        if (item is null || item.Enabled == value)
            return;
        item.Enabled = value;
    }
    void RebuildMenu()
    {
        if (IsShuttingDown)
        {
            UpdateChecks();
            return;
        }
        if (_applying)
        {
            _menuRebuildPending = true;
            UpdateChecks();
            return;
        }
        try
        {
            var old = _icon.ContextMenuStrip;
            if (old is { Visible: true })
            {
                if (!_rebuildOnMenuClose)
                {
                    _rebuildOnMenuClose = true;
                    old.Closed += OnMenuClosedForRebuild;
                }
                return;
            }
            var next = BuildMenu();
            if (next is null)
                return;
            _icon.ContextMenuStrip = next;
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    void OnMenuClosedForRebuild(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        if (sender is ContextMenuStrip menu)
            menu.Closed -= OnMenuClosedForRebuild;
        _rebuildOnMenuClose = false;
        if (IsShuttingDown)
            return;
        try { RebuildMenu(); }
        catch (Exception ex) { Program.LogError(ex); }
    }
    readonly List<(ToolStripMenuItem item, uint mods, uint vk)> _presetItems = new();
    readonly Dictionary<string, ToolStripMenuItem> _langItems = new();
    ContextMenuStrip? BuildMenu()
    {
        ContextMenuStrip? menu = null;
        try
        {
            var presets = new List<(ToolStripMenuItem item, uint mods, uint vk)>();
            var langs = new Dictionary<string, ToolStripMenuItem>();
            var enableItem = new ToolStripMenuItem(Localization.Tr("enable")) { Checked = _turboOn };
            enableItem.Click += async (_, _) => await SetStateAsync(true);
            var disableItem = new ToolStripMenuItem(Localization.Tr("disable")) { Checked = !_turboOn };
            disableItem.Click += async (_, _) => await SetStateAsync(false);
            var settingsItem = new ToolStripMenuItem(Localization.Tr("settings"));
            var settings = BuildSettingsItems(langs);
            settingsItem.DropDownItems.AddRange(settings.Items);
            var (hotkeyItem, hotkeyOffItem) = BuildHotkeyMenu(presets);
            var exit = new ToolStripMenuItem(Localization.Tr("exit"));
            exit.Click += (_, _) => ExitApp();
            menu = new ContextMenuStrip();
            menu.Items.AddRange([
                new ToolStripMenuItem(Program.Title) { Enabled = false },
                new ToolStripSeparator(),
                enableItem,
                disableItem,
                new ToolStripSeparator(),
                hotkeyItem,
                settingsItem,
                BuildDonateMenu(),
                new ToolStripSeparator(),
                exit,
            ]);
            menu.Opening += OnMenuOpening;
            settingsItem.DropDown.Opening += (_, _) => RefreshAutostartCacheAsync();

            _presetItems.Clear();
            _presetItems.AddRange(presets);
            _langItems.Clear();
            foreach (var (code, item) in langs)
                _langItems[code] = item;
            _enableItem = enableItem;
            _disableItem = disableItem;
            _settingsItem = settingsItem;
            _hotkeyItem = hotkeyItem;
            _hotkeyOffItem = hotkeyOffItem;
            _autostartItem = settings.Autostart;
            _restoreItem = settings.Restore;
            _notificationsItem = settings.Notifications;
            _autoUpdateItem = settings.AutoUpdate;
            _checkNowItem = settings.CheckNow;
            if (_applying) SetMenuEnabled(false);
            return menu;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            try { menu?.Dispose(); } catch { }
            return null;
        }
    }
    ContextMenuStrip FallbackMenu()
    {
        var menu = new ContextMenuStrip();
        var exit = new ToolStripMenuItem(Localization.Tr("exit"));
        exit.Click += (_, _) => ExitApp();
        menu.Items.AddRange([
            new ToolStripMenuItem(Program.Title) { Enabled = false },
            new ToolStripSeparator(),
            exit,
        ]);
        return menu;
    }
    void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        UpdateChecks();
        RefreshAutostartCacheAsync();
    }
    (ToolStripMenuItem Item, ToolStripMenuItem Off) BuildHotkeyMenu(
        List<(ToolStripMenuItem item, uint mods, uint vk)> presets)
    {
        var hotkeyItem = new ToolStripMenuItem(Localization.Tr("hotkey"));
        foreach (var (label, mods, vk) in Hotkeys.Presets)
        {
            var item = new ToolStripMenuItem(label) { Checked = _hotkeyEnabled && _mods == mods && _vk == vk };
            item.Click += (_, _) => ApplyHotkey(mods, vk);
            hotkeyItem.DropDownItems.Add(item);
            presets.Add((item, mods, vk));
        }
        var custom = new ToolStripMenuItem(Localization.Tr("custom"));
        custom.Click += (_, _) => CaptureCustomHotkey();
        hotkeyItem.DropDownItems.Add(custom);
        hotkeyItem.DropDownItems.Add(new ToolStripSeparator());
        var offItem = new ToolStripMenuItem(Localization.Tr("disabled")) { Checked = !_hotkeyEnabled };
        offItem.Click += (_, _) => SetHotkeyEnabled(!_hotkeyEnabled);
        hotkeyItem.DropDownItems.Add(offItem);
        return (hotkeyItem, offItem);
    }
    (ToolStripItem[] Items, ToolStripMenuItem Autostart, ToolStripMenuItem Restore,
        ToolStripMenuItem Notifications, ToolStripMenuItem AutoUpdate, ToolStripMenuItem CheckNow)
        BuildSettingsItems(Dictionary<string, ToolStripMenuItem> langItems)
    {
        var autostartItem = new ToolStripMenuItem(Localization.Tr("autostart"))
        {
            Checked = _autostartCached,
            Enabled = !_autostartBusy,
        };
        autostartItem.Click += (_, _) => ToggleAutostart();
        var restoreItem = new ToolStripMenuItem(Localization.Tr("restore_on_exit")) { Checked = _restoreOnExit };
        restoreItem.Click += (_, _) => ToggleRestoreOnExit();
        var notificationsItem = new ToolStripMenuItem(Localization.Tr("notifications")) { Checked = _notifications };
        notificationsItem.Click += (_, _) => ToggleNotifications();
        var autoUpdateItem = new ToolStripMenuItem(Localization.Tr("auto_update_check")) { Checked = _autoUpdate };
        autoUpdateItem.Click += (_, _) => ToggleAutoUpdate();
        var checkNowItem = new ToolStripMenuItem(Localization.Tr("check_updates")) { Enabled = !_checkingUpdates };
        checkNowItem.Click += (_, _) => _ = CheckForUpdatesAsync(manual: true);
        var langItem = new ToolStripMenuItem(Localization.Tr("language"));
        foreach (var code in Localization.AllLangs)
        {
            var item = new ToolStripMenuItem(Localization.LangName(code)) { Checked = Localization.Language == code };
            item.Click += (_, _) => SetLanguage(code);
            langItem.DropDownItems.Add(item);
            langItems[code] = item;
        }
        ToolStripItem[] items =
        [
            autostartItem,
            new ToolStripSeparator(),
            langItem,
            new ToolStripSeparator(),
            restoreItem,
            notificationsItem,
            new ToolStripSeparator(),
            autoUpdateItem,
            checkNowItem,
        ];
        return (items, autostartItem, restoreItem, notificationsItem, autoUpdateItem, checkNowItem);
    }
    static ToolStripMenuItem BuildDonateMenu()
    {
        var donate = new ToolStripMenuItem(Localization.Tr("github_donate"));
        donate.DropDownItems.AddRange([
            CreateLink("GitHub", "https://github.com/vornixbit/CPU-TurboBoost-Toggle"),
            new ToolStripSeparator(),
            CreateLink("Patreon", "https://www.patreon.com/vornixbit"),
            CreateLink("Ko-fi", "https://ko-fi.com/vornixbit"),
            CreateLink("PayPal", "https://www.paypal.com/ncp/payment/KZPBTMMCPVU3U"),
        ]);
        return donate;
        static ToolStripMenuItem CreateLink(string label, string url)
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => OpenUrl(url);
            return item;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }
        _disposed = true;
        if (disposing)
        {
            Step(() => _icon.Visible = false);
            Step(() =>
            {
                SystemEvents.SessionEnded -= OnSessionEnded;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            });
            Step(ConfigStore.FlushPending);
            Step(() =>
            {
                _startupCts.Cancel();
                _startupCts.Dispose();
            });
            Step(() =>
            {
                _fallbackTimer.Stop();
                _fallbackTimer.Dispose();
            });
            Step(() =>
            {
                _hotkeyRetryTimer.Stop();
                _hotkeyRetryTimer.Dispose();
            });
            Step(() =>
            {
                _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
                _hotkeyWindow.Dispose();
            });
            Step(() =>
            {
                _copilotHook.Pressed -= OnHotkeyPressed;
                _copilotHook.Dispose();
            });
            Step(() =>
            {
                _icon.ContextMenuStrip?.Dispose();
                _icon.ContextMenuStrip = null;
                _icon.Dispose();
            });
            Step(IconFactory.Release);
        }
        base.Dispose(disposing);
        static void Step(Action action)
        {
            try { action(); }
            catch (Exception ex) { Program.LogError(ex); }
        }
    }
}
