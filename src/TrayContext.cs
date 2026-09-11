using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TurboToggle;

sealed class TrayContext : ApplicationContext
{
    const int PollIntervalMs = 2000;
    const int HotkeyRetryIntervalMs = 500;
    const int StartupUpdateDelayMs = 15000;
    const int BalloonRateLimitSeconds = 2;
    const int AutostartCacheTtlSeconds = 30;

    readonly NotifyIcon _icon;
    readonly HotkeyWindow _hotkeyWindow;
    readonly CopilotHook _copilotHook = new();
    readonly System.Windows.Forms.Timer _pollTimer;
    readonly System.Windows.Forms.Timer _hotkeyRetryTimer;
    readonly CancellationTokenSource _startupCts = new();
    readonly AppConfig _config;

    ToolStripMenuItem _enableItem = null!;
    ToolStripMenuItem _disableItem = null!;
    ToolStripMenuItem _autostartItem = null!;
    ToolStripMenuItem _restoreItem = null!;
    ToolStripMenuItem _notificationsItem = null!;
    ToolStripMenuItem _autoUpdateItem = null!;
    ToolStripMenuItem _checkNowItem = null!;
    ToolStripMenuItem _hotkeyOffItem = null!;
    ToolStripMenuItem _hotkeyItem = null!;
    ToolStripMenuItem _settingsItem = null!;

    bool _turboOn;
    uint _mods, _vk;
    bool _hotkeyEnabled = true;
    bool _restoreOnExit;
    bool _notifications = true;
    bool _autoUpdate = true;
    bool _checkingUpdates;
    string _updateUrl = "";
    bool _updateClickArmed;
    string _updateClickArmedFor = "";
    uint _retryPrevMods, _retryPrevVk;
    HotkeyEntry? _retryPrevEntry;
    bool _retryPrevEnabled;
    bool? _retryPrevEnabledCfg;
    int _hotkeyRetries;
    bool _exiting;
    bool _applying;
    bool _disposed;
    bool _autostartCached;
    bool _autostartBusy;
    bool _restored;
    int _autostartSeq;
    bool _menuRebuildPending;
    bool _rebuildOnMenuClose;
    DateTime _lastAutostartCheck = DateTime.MinValue;
    DateTime _lastBalloon = DateTime.MinValue;

    public TrayContext(AppConfig config)
    {
        _config = config;
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

        _turboOn = PowerApi.ReadState() ?? true;

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
        _copilotHook.Pressed += OnHotkeyPressed;

        _autostartCached = false;
        _icon = new NotifyIcon
        {
            Icon = IconFactory.Get(_turboOn),
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
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

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += PollTick;
        _pollTimer.Start();

        try
        {
            SystemEvents.SessionEnding += OnSessionEnding;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }

        try
        {
            _ = Task.Delay(StartupUpdateDelayMs, _startupCts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled || _exiting || _disposed || !_autoUpdate)
                    return;
                _ = CheckForUpdatesAsync(manual: false);
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

    void PollTick(object? sender, EventArgs e)
    {
        try
        {
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
        if (_exiting || _disposed || !_hotkeyEnabled)
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
            Notify(Localization.Tr("hotkey_set") + " " + Hotkeys.DisplayName(_mods, _vk));
            UpdateTooltip();
            if (_icon.ContextMenuStrip is null || !_icon.ContextMenuStrip.Visible)
                RebuildMenu();
            else
                UpdateChecks();
            return;
        }
        if (--_hotkeyRetries <= 0)
        {
            _hotkeyRetryTimer.Stop();
            _mods = _retryPrevMods;
            _vk = _retryPrevVk;
            _hotkeyEnabled = _retryPrevEnabled;
            _config.Hotkey = _retryPrevEntry;
            _config.HotkeyEnabled = _retryPrevEnabledCfg;
            ConfigStore.Save(_config);
            UpdateHotkeyRegistration(armRetry: false);
            UpdateVisuals();
            RefreshMenuSmart();
            Notify(Localization.Tr("hotkey_failed"), important: true);
        }
    }

    void OnHotkeyPressed()
    {
        if (_applying || _exiting)
            return;
        _ = ToggleAsync();
    }

    async Task ToggleAsync()
    {
        await SetStateAsync(!_turboOn).ConfigureAwait(true);
    }

    async Task SetStateAsync(bool on)
    {
        if (_applying || _exiting)
            return;
        _applying = true;
        SetMenuEnabled(false);
        try
        {
            var scheme = PowerApi.GetActiveScheme();
            if (scheme is null)
            {
                Notify(Localization.Tr("cannot_scheme"), important: true);
                return;
            }
            var overlayBefore = PowerApi.GetActiveOverlay();

            var currentState = PowerApi.ReadState();
            if (currentState == on)
            {
                _turboOn = on;
                UpdateVisuals();
                Notify(Localization.Tr(on ? "turbo_enabled" : "turbo_disabled"));
                return;
            }
            if (_exiting)
                return;

            uint value = on ? PowerApi.ResolveOnValue() : PowerApi.ValueOff;
            var (acRc, dcRc) = PowerApi.WriteBoost(scheme.Value, value);
            bool writeOk = acRc == 0 && dcRc == 0;

            uint? overlayRc = null;
            if (overlayBefore is not null)
            {
                PowerApi.WriteBoost(overlayBefore.Value, value);
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

            uint applyRc = PowerApi.ReapplyActiveScheme(scheme.Value);

            bool? fast = PowerApi.ReadState();
            if (_exiting)
                return;
            bool applied = fast == on || await PowerApi.VerifyStateAsync(on).ConfigureAwait(true);
            if (_exiting)
                return;

            var final = PowerApi.ReadState();
            if (final is not null)
                _turboOn = final.Value;
            else if (applied)
                _turboOn = on;
            UpdateVisuals();

            if (final is null)
                Notify(Localization.Tr("cannot_confirm"), important: true);
            else if (applied && writeOk && applyRc == 0)
                Notify(Localization.Tr(_turboOn ? "turbo_enabled" : "turbo_disabled"));
            else if (overlayBefore is not null && overlayRc == 0)
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
            else if (_exiting && _restoreOnExit && !_restored)
            {
                _restored = true;
                RestoreBoostBestEffort();
            }
        }
    }

    void SetMenuEnabled(bool enabled)
    {
        if (_disposed || _icon.ContextMenuStrip is null)
            return;
        _enableItem.Enabled = enabled;
        _disableItem.Enabled = enabled;
        _hotkeyItem.Enabled = enabled;
        _settingsItem.Enabled = enabled;
        _autostartItem.Enabled = enabled && !_autostartBusy;
        _checkNowItem.Enabled = enabled && !_checkingUpdates;
    }

    void UpdateHotkeyRegistration(bool armRetry = true)
    {
        _hotkeyRetryTimer.Stop();
        _hotkeyRetries = 0;
        _hotkeyWindow.Unregister();
        _copilotHook.Uninstall();
        if (!_hotkeyEnabled || _exiting || _disposed)
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
        _hotkeyRetries = 4;
        _hotkeyRetryTimer.Start();
    }

    void ApplyHotkey(uint mods, uint vk)
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
        ConfigStore.Save(_config);
        UpdateVisuals();
        RefreshMenuSmart();
        if (ok)
            Notify(Localization.Tr("hotkey_set") + " " + Hotkeys.DisplayName(mods, vk));
        else
            Notify(Localization.Tr(Hotkeys.IsCopilot(mods, vk) ? "hotkey_failed" : "hotkey_busy"), important: true);
    }

    void RefreshMenuSmart()
    {
        if (_icon.ContextMenuStrip is null || !_icon.ContextMenuStrip.Visible)
            RebuildMenu();
        else
            UpdateChecks();
    }

    void SetHotkeyEnabled(bool on)
    {
        _hotkeyEnabled = on;
        _config.HotkeyEnabled = on;
        ConfigStore.Save(_config);
        UpdateHotkeyRegistration();
        UpdateVisuals();
        RebuildMenu();
        if (on)
            Notify(Localization.Tr("hotkey_set") + " " + Hotkeys.DisplayName(_mods, _vk));
        else
            Notify(Localization.Tr("hotkey_disabled"));
    }

    void CaptureCustomHotkey()
    {
        bool resumeRetry = _hotkeyRetryTimer.Enabled;
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
                ApplyHotkey(combo.Mods, combo.Vk);
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
                UpdateHotkeyRegistration();
            if (resumeRetry && _hotkeyEnabled && _hotkeyWindow.Current is null && _hotkeyRetries > 0 && !_exiting && !_disposed)
                _hotkeyRetryTimer.Start();
        }
    }

    void SetLanguage(string code)
    {
        if (!Localization.IsSupported(code) || Localization.Language == code)
            return;
        Localization.Language = code;
        _config.Lang = code;
        ConfigStore.Save(_config);
        UpdateVisuals();
        RebuildMenu();
    }

    void ToggleAutostart()
    {
        if (_autostartBusy)
            return;
        _autostartBusy = true;
        _autostartItem.Enabled = false;
        int seq = ++_autostartSeq;
        bool target = !_autostartCached;
        Task.Run(() => Autostart.SetEnabled(target)).ContinueWith(t =>
        {
            if (_exiting || _disposed || seq != _autostartSeq)
                return;
            _autostartBusy = false;
            _lastAutostartCheck = DateTime.UtcNow;
            bool ok = t.IsCompletedSuccessfully && t.Result;
            if (ok)
            {
                _autostartCached = target;
                Notify(Localization.Tr(target ? "autostart_on" : "autostart_off"));
            }
            else
            {
                Notify(Localization.Tr("autostart_error"), important: true);
            }
            UpdateChecks();
            if (_icon.ContextMenuStrip is not null)
                _autostartItem.Enabled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void ToggleRestoreOnExit()
    {
        _restoreOnExit = !_restoreOnExit;
        _config.RestoreOnExit = _restoreOnExit;
        ConfigStore.Save(_config);
        UpdateChecks();
    }

    void ToggleNotifications()
    {
        _notifications = !_notifications;
        _config.Notifications = _notifications;
        ConfigStore.Save(_config);
        UpdateChecks();
    }

    void ToggleAutoUpdate()
    {
        _autoUpdate = !_autoUpdate;
        _config.AutoUpdateCheck = _autoUpdate;
        ConfigStore.Save(_config);
        UpdateChecks();
    }

    async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates || _exiting || _disposed)
            return;
        _checkingUpdates = true;
        if (!_exiting && !_disposed && _icon.ContextMenuStrip is not null)
            _checkNowItem.Enabled = false;
        try
        {
            var result = await Updater.CheckAsync(_startupCts.Token).ConfigureAwait(true);
            if (_exiting || _disposed)
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
                        important: true, updateBalloon: true);
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
            if (!_exiting && !_disposed && _icon.ContextMenuStrip is not null)
                _checkNowItem.Enabled = true;
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
            || (uri.Scheme != "http" && uri.Scheme != "https"))
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
            if (PowerApi.ReadState() == true)
                return;
            var scheme = PowerApi.GetActiveScheme();
            if (scheme is null)
                return;
            uint value = PowerApi.ResolveOnValue();
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
        catch (Exception ex) when (ex is not StackOverflowException && ex is not OutOfMemoryException)
        {
            Program.LogError(ex);
        }
    }
    void RefreshAutostartCacheAsync(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastAutostartCheck).TotalSeconds < AutostartCacheTtlSeconds)
            return;
        int seq = _autostartSeq;
        Task.Run(() => Autostart.IsEnabled()).ContinueWith(t =>
        {
            if (_exiting || _disposed || seq != _autostartSeq || _autostartBusy)
                return;
            if (!t.IsCompletedSuccessfully)
                return;
            _lastAutostartCheck = DateTime.UtcNow;
            _autostartCached = t.Result;
            UpdateChecks();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Notify(string message, bool important = false, bool updateBalloon = false)
    {
        if (!updateBalloon)
            _updateClickArmed = false;
        if ((!_notifications && !important) || _exiting || !_icon.Visible)
            return;
        if (!important && (DateTime.UtcNow - _lastBalloon).TotalSeconds < BalloonRateLimitSeconds)
            return;
        if (!important)
            _lastBalloon = DateTime.UtcNow;
        try
        {
            _icon.ShowBalloonTip(0, Program.AppName, message, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;
        _pollTimer.Stop();
        try
        {
            if (_restoreOnExit && !_restored)
            {
                _restored = true;
                RestoreBoostBestEffort();
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    void ExitApp()
    {
        if (_exiting)
            return;
        _exiting = true;
        _pollTimer.Stop();
        _hotkeyRetryTimer.Stop();
        _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
        _copilotHook.Pressed -= OnHotkeyPressed;
        _copilotHook.Uninstall();
        try
        {
            if (_restoreOnExit && !_restored)
            {
                _restored = true;
                RestoreBoostBestEffort();
            }
        }
        finally
        {
            _icon.Visible = false;
            Application.Exit();
        }
    }

    void UpdateVisuals()
    {
        var icon = IconFactory.Get(_turboOn);
        if (!ReferenceEquals(_icon.Icon, icon))
            _icon.Icon = icon;
        UpdateTooltip();
        UpdateChecks();
    }

    void UpdateTooltip()
    {
        string hk = _hotkeyEnabled ? Hotkeys.DisplayName(_mods, _vk) : Localization.Tr("disabled");
        var text = TruncateTooltip(Localization.Tr(_turboOn ? "status_on" : "status_off") + " [" + hk + "]");
        if (_icon.Text != text)
            _icon.Text = text;
    }

    internal static string TruncateTooltip(string text)
    {
        const int max = 127;
        if (text.Length <= max)
            return text;
        int len = char.IsHighSurrogate(text[max - 2]) ? max - 2 : max - 1;
        return text[..len] + "\u2026";
    }

    void UpdateChecks()
    {
        if (_disposed || _exiting || _icon.ContextMenuStrip is null)
            return;
        _enableItem.Checked = _turboOn;
        _disableItem.Checked = !_turboOn;
        _autostartItem.Checked = _autostartCached;
        _autostartItem.Enabled = !_autostartBusy && !_applying;
        _restoreItem.Checked = _restoreOnExit;
        _notificationsItem.Checked = _notifications;
        _autoUpdateItem.Checked = _autoUpdate;
        _checkNowItem.Enabled = !_checkingUpdates && !_applying;
        _hotkeyOffItem.Checked = !_hotkeyEnabled;
        foreach (var (item, mods, vk) in _presetItems)
            item.Checked = _hotkeyEnabled && _mods == mods && _vk == vk;
        foreach (var (code, item) in _langItems)
            item.Checked = Localization.Language == code;
    }

    void RebuildMenu()
    {
        if (_exiting || _disposed)
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
        _icon.ContextMenuStrip = BuildMenu();
        old?.Dispose();
        UpdateChecks();
    }

    void OnMenuClosedForRebuild(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        if (sender is ContextMenuStrip menu)
            menu.Closed -= OnMenuClosedForRebuild;
        _rebuildOnMenuClose = false;
        if (_exiting || _disposed)
            return;
        RebuildMenu();
    }

    readonly List<(ToolStripMenuItem item, uint mods, uint vk)> _presetItems = new();
    readonly Dictionary<string, ToolStripMenuItem> _langItems = new();

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _presetItems.Clear();
        _langItems.Clear();

        var title = new ToolStripMenuItem(Program.Title) { Enabled = false };

        _enableItem = new ToolStripMenuItem(Localization.Tr("enable")) { Checked = _turboOn };
        _enableItem.Click += async (_, _) => await SetStateAsync(true).ConfigureAwait(true);

        _disableItem = new ToolStripMenuItem(Localization.Tr("disable")) { Checked = !_turboOn };
        _disableItem.Click += async (_, _) => await SetStateAsync(false).ConfigureAwait(true);

        _settingsItem = new ToolStripMenuItem(Localization.Tr("settings"));

        _autostartItem = new ToolStripMenuItem(Localization.Tr("autostart"))
        {
            Checked = _autostartCached,
            Enabled = !_autostartBusy,
        };
        _autostartItem.Click += (_, _) => ToggleAutostart();

        var hotkeyItem = _hotkeyItem = new ToolStripMenuItem(Localization.Tr("hotkey"));
        foreach (var (label, mods, vk) in Hotkeys.Presets)
        {
            var item = new ToolStripMenuItem(label) { Checked = _hotkeyEnabled && _mods == mods && _vk == vk };
            item.Click += (_, _) => ApplyHotkey(mods, vk);
            hotkeyItem.DropDownItems.Add(item);
            _presetItems.Add((item, mods, vk));
        }
        var custom = new ToolStripMenuItem(Localization.Tr("custom"));
        custom.Click += (_, _) => CaptureCustomHotkey();
        hotkeyItem.DropDownItems.Add(custom);
        hotkeyItem.DropDownItems.Add(new ToolStripSeparator());
        _hotkeyOffItem = new ToolStripMenuItem(Localization.Tr("disabled"))
        {
            Checked = !_hotkeyEnabled,
        };
        _hotkeyOffItem.Click += (_, _) => SetHotkeyEnabled(!_hotkeyEnabled);
        hotkeyItem.DropDownItems.Add(_hotkeyOffItem);

        _restoreItem = new ToolStripMenuItem(Localization.Tr("restore_on_exit"))
        {
            Checked = _restoreOnExit,
        };
        _restoreItem.Click += (_, _) => ToggleRestoreOnExit();

        _notificationsItem = new ToolStripMenuItem(Localization.Tr("notifications"))
        {
            Checked = _notifications,
        };
        _notificationsItem.Click += (_, _) => ToggleNotifications();

        _autoUpdateItem = new ToolStripMenuItem(Localization.Tr("auto_update_check"))
        {
            Checked = _autoUpdate,
        };
        _autoUpdateItem.Click += (_, _) => ToggleAutoUpdate();

        _checkNowItem = new ToolStripMenuItem(Localization.Tr("check_updates"))
        {
            Enabled = !_checkingUpdates,
        };
        _checkNowItem.Click += (_, _) => _ = CheckForUpdatesAsync(manual: true);

        var langItem = new ToolStripMenuItem(Localization.Tr("language"));
        foreach (var code in Localization.AllLangs)
        {
            var item = new ToolStripMenuItem(Localization.LangName(code))
            {
                Checked = Localization.Language == code,
            };
            item.Click += (_, _) => SetLanguage(code);
            langItem.DropDownItems.Add(item);
            _langItems[code] = item;
        }

        _settingsItem.DropDownItems.AddRange(new ToolStripItem[]
        {
            _autostartItem,
            new ToolStripSeparator(),
            langItem,
            new ToolStripSeparator(),
            _restoreItem,
            _notificationsItem,
            new ToolStripSeparator(),
            _autoUpdateItem,
            _checkNowItem,
        });

        var exit = new ToolStripMenuItem(Localization.Tr("exit"));
        exit.Click += (_, _) => ExitApp();

        var githubDonate = new ToolStripMenuItem(Localization.Tr("github_donate"));
        var githubItem = new ToolStripMenuItem("GitHub");
        githubItem.Click += (_, _) => OpenUrl("https://github.com/vornixbit/CPU-TurboBoost-Toggle");
        var patreonItem = new ToolStripMenuItem("Patreon");
        patreonItem.Click += (_, _) => OpenUrl("https://www.patreon.com/vornixbit");
        var kofiItem = new ToolStripMenuItem("Ko-fi");
        kofiItem.Click += (_, _) => OpenUrl("https://ko-fi.com/vornixbit");
        var paypalItem = new ToolStripMenuItem("PayPal");
        paypalItem.Click += (_, _) => OpenUrl("https://www.paypal.com/ncp/payment/KZPBTMMCPVU3U");
        githubDonate.DropDownItems.AddRange(new ToolStripItem[]
        {
            githubItem,
            new ToolStripSeparator(),
            patreonItem,
            kofiItem,
            paypalItem,
        });

        menu.Items.AddRange(new ToolStripItem[]
        {
            title,
            new ToolStripSeparator(),
            _enableItem,
            _disableItem,
            new ToolStripSeparator(),
            hotkeyItem,
            _settingsItem,
            githubDonate,
            new ToolStripSeparator(),
            exit,
        });

        menu.Opening += (_, _) =>
        {
            UpdateChecks();
            RefreshAutostartCacheAsync();
        };

        if (_applying)
            SetMenuEnabled(false);

        return menu;
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
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch (Exception ex) { Program.LogError(ex); }
            _startupCts.Cancel();
            _startupCts.Dispose();
            _pollTimer.Stop();
            _hotkeyRetryTimer.Stop();
            _pollTimer.Dispose();
            _hotkeyRetryTimer.Dispose();
            _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
            _hotkeyWindow.Dispose();
            _copilotHook.Pressed -= OnHotkeyPressed;
            _copilotHook.Dispose();
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.ContextMenuStrip = null;
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
