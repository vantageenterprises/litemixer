using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace LiteMixer;

public partial class App : Application
{
    const string MutexName = "LiteMixer.SingleInstance.6f1c2a43";
    const string ShowEventName = "LiteMixer.Show.6f1c2a43";
    const string SettingsEventName = "LiteMixer.Settings.6f1c2a43";
    const string CompactEventName = "LiteMixer.Compact.6f1c2a43";
    const double HotkeyStep = 5;

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    EventWaitHandle? _settingsEvent;
    EventWaitHandle? _compactEvent;
    TrayIcon? _tray;
    HotkeyManager? _hotkeys;
    System.Windows.Threading.DispatcherTimer? _updateTimer;
    System.Windows.Threading.DispatcherTimer? _updatePromptTimer;
    UpdateWindow? _updateWindow;
    MainWindow? _main;
    CompactWindow? _compact;
    SettingsWindow? _settingsWindow;
    bool _exiting;

    public static new App Current => (App)Application.Current;
    public MixerModel Model { get; private set; } = null!;
    public Updater Updater { get; } = new();
    /// <summary>Hotkey actions whose combo another app already owns.</summary>
    public IReadOnlyCollection<string> HotkeyFailures { get; private set; } = [];

    internal void SetModelForCheck(MixerModel model) => Model = model;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int verify = Array.IndexOf(e.Args, "--verify-update");
        if (verify >= 0 && verify + 2 < e.Args.Length)
        {
            UiCheck.VerifyFile(e.Args[verify + 1], e.Args[verify + 2]);
            return;
        }
        int dryRun = Array.IndexOf(e.Args, "--update-dry-run");
        if (dryRun >= 0 && dryRun + 1 < e.Args.Length)
        {
            _ = UiCheck.UpdateDryRun(Updater, e.Args[dryRun + 1]);
            return;
        }

        int shots = Array.IndexOf(e.Args, "--screenshots");
        if (shots >= 0 && shots + 1 < e.Args.Length)
        {
            UiCheck.Screenshots(this, e.Args[shots + 1]);
            return;
        }

        int check = Array.IndexOf(e.Args, "--ui-check");
        if (check >= 0 && check + 1 < e.Args.Length)
        {
            // Runs alongside the real app: before the single-instance check, and it never starts the mixer.
            UiCheck.Run(this, e.Args[check + 1]);
            return;
        }

        bool wantsSettings = e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        bool wantsCompact = e.Args.Contains("--compact", StringComparer.OrdinalIgnoreCase);
        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running (probably in the tray): ask that instance to show its window, Settings or compact mixer.
            try
            {
                var name = wantsSettings ? SettingsEventName : wantsCompact ? CompactEventName : ShowEventName;
                using var signal = EventWaitHandle.OpenExisting(name);
                signal.Set();
            }
            catch { }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsEventName);
        _compactEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CompactEventName);
        new Thread(() =>
        {
            var signals = new WaitHandle[] { _showEvent, _settingsEvent, _compactEvent };
            Action[] handlers = [ShowMain, ShowSettings, () => SetCompact(true)];
            while (!_exiting)
            {
                int which = WaitHandle.WaitAny(signals);
                if (_exiting) return;
                Dispatcher.BeginInvoke(handlers[which]);
            }
        }) { IsBackground = true, Name = "ShowListener" }.Start();

        var settings = SettingsStore.Load();
        Model = new MixerModel(settings);
        Model.MicMuteChanged += OnMicMuteChanged;
        Model.Start();

        if (e.Args.Contains("--diag", StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllText(Path.Combine(SettingsStore.Folder, "diagnostics.txt"), Model.Diagnostics());
            ExitApp();
            return;
        }

        StartupRegistration.Apply(settings.StartWithWindows);

        _tray = new TrayIcon("LiteMixer");
        _tray.Clicked += ShowMain;
        _tray.MiddleClicked += Model.ToggleMicMute;
        _tray.Scrolled += notches => Model.Nudge(Model.Master, notches * 2);
        _tray.MenuRequested += () => _tray.ShowMenu(BuildTrayMenu());
        UpdateTray();

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += OnHotkey;
        ReloadHotkeys();

        SessionEnding += (_, _) => Model.SaveNow();

        Updater.CleanUp();
        // First look shortly after startup so it never slows boot, then once a day.
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (Model.Settings.CheckForUpdates) await CheckForUpdatesAsync();
        };
        _updateTimer.Start();

        if (settings.CompactOpen || wantsCompact) SetCompact(true);
        bool trayOnly = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (wantsSettings) ShowSettings();
        else if (!trayOnly && !wantsCompact) ShowMain();
        else Dispatcher.BeginInvoke(TrimMemory, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Hands startup and window memory back to Windows while the app sits in the tray.</summary>
    static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    public void ShowMain()
    {
        if (_exiting) return;
        if (_main == null)
        {
            // The window is created on demand and destroyed on close, so the tray-only state stays tiny.
            _main = new MainWindow(Model);
            _main.Closed += (_, _) =>
            {
                _main = null;
                UpdateMeters();
                TrimMemory();
            };
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(Model);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                ReloadHotkeys();
            };
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void SetCompact(bool open)
    {
        Model.Settings.CompactOpen = open;
        Model.QueueSave();
        if (open)
        {
            if (_compact == null)
            {
                _compact = new CompactWindow(Model);
                _compact.Closed += (_, _) =>
                {
                    _compact = null;
                    // Closed by hand (Alt+F4 or the X): stay closed next launch. Exit keeps the saved state instead.
                    if (!_exiting)
                    {
                        Model.Settings.CompactOpen = false;
                        Model.QueueSave();
                    }
                    UpdateMeters();
                };
            }
            _compact.Show();
        }
        else
        {
            _compact?.Close();
        }
        UpdateMeters();
    }

    /// <summary>Meters only run while a window that shows them is open.</summary>
    public void UpdateMeters() =>
        Model.MetersEnabled = (_main != null && _main.IsVisible && _main.WindowState != WindowState.Minimized) || _compact != null;

    // ---- Updates ----

    public async Task CheckForUpdatesAsync(bool userAsked = false)
    {
        if (!await Updater.CheckAsync() || Updater.Available is not { } release) return;
        if (userAsked)
        {
            ShowUpdatePrompt(userAsked: true);
            return;
        }
        if (!UpdateWindow.ShouldOffer(Model.Settings.LastAnnouncedVersion, Updater.Display(release.Version), userAsked: false)) return;

        // Waits out fullscreen games, and steps aside if one starts while the window is waiting for an answer.
        _updatePromptTimer ??= NewUpdatePromptTimer();
        _updatePromptTimer.Start();
        OfferUpdateWhenFree();
    }

    System.Windows.Threading.DispatcherTimer NewUpdatePromptTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => OfferUpdateWhenFree();
        return timer;
    }

    void OfferUpdateWhenFree()
    {
        bool busy = UpdateWindow.IsUserBusy();
        if (_updateWindow != null)
        {
            if (busy) _updateWindow.Hide();
            else if (!_updateWindow.IsVisible) _updateWindow.Show();
            return;
        }
        if (!busy) ShowUpdatePrompt(userAsked: false);
    }

    /// <summary>Shows what's new and asks. Only a request from the user takes the keyboard.</summary>
    public void ShowUpdatePrompt(bool userAsked)
    {
        if (Updater.Available is not { } release || Updater.IsBusy)
        {
            _updatePromptTimer?.Stop();
            return;
        }
        if (_updateWindow != null)
        {
            if (!userAsked) return;
            _updateWindow.Show();
            _updateWindow.Activate();
            return;
        }

        var version = Updater.Display(release.Version);
        Model.Settings.LastAnnouncedVersion = version;
        Model.QueueSave();

        _updateWindow = new UpdateWindow(version, Updater.CurrentText, release.Notes) { ShowActivated = userAsked };
        _updateWindow.Accepted += () => _ = DownloadAndInstallAsync(release);
        _updateWindow.Closed += (_, _) =>
        {
            _updateWindow = null;
            _updatePromptTimer?.Stop();
        };
        _updateWindow.Show();
    }

    /// <summary>Downloads, verifies and hands over to the installer, which reopens the app.</summary>
    async Task DownloadAndInstallAsync(UpdateRelease release)
    {
        ShowMain();
        try
        {
            var installer = await Updater.DownloadVerifiedAsync(release);
            Process.Start(new ProcessStartInfo(installer, "--update") { UseShellExecute = false });
            ExitApp();
        }
        catch (Exception ex)
        {
            Updater.ReportFailure(ex.Message);
            PromptWindow.Confirm(_main, "Update failed", $"{ex.Message}\n\nNothing was changed. You can try again, or download it from {Updater.ReleasesPage}", "OK");
        }
    }

    // ---- Hotkeys ----

    public IReadOnlyCollection<string> ReloadHotkeys()
    {
        if (_hotkeys == null) return [];
        HotkeyFailures = _hotkeys.Register(Model.Settings.Hotkeys);
        return HotkeyFailures;
    }

    public void SuspendHotkeys() => _hotkeys?.Register(new Dictionary<string, string>());

    void OnHotkey(string action)
    {
        switch (action)
        {
            case "mic-mute": Model.ToggleMicMute(); break;
            case "master-up": Model.Nudge(Model.Master, HotkeyStep); break;
            case "master-down": Model.Nudge(Model.Master, -HotkeyStep); break;
            case "game-up": Model.Nudge(Model.Game, HotkeyStep); break;
            case "game-down": Model.Nudge(Model.Game, -HotkeyStep); break;
            case "balance-game": Model.Balance -= 10; break;
            case "balance-chat": Model.Balance += 10; break;
            case "balance-center": Model.Balance = 0; break;
            case "duck-toggle": Model.DuckEnabled = !Model.DuckEnabled; break;
            case "compact": SetCompact(_compact == null); break;
            case "open": ShowMain(); break;
        }
    }

    // ---- Mic state ----

    void OnMicMuteChanged(bool muted, bool byThisApp)
    {
        UpdateTray();
        if (byThisApp && Model.Settings.MicBeep) Beeper.Play(muted, Model.Settings.MicBeepVolume);
    }

    void UpdateTray() =>
        _tray?.SetState(Model.Mic.Muted, Model.Mic.Muted ? "LiteMixer: mic muted" : "LiteMixer");

    // ---- Lifetime ----

    public void SetStartWithWindows(bool enabled)
    {
        Model.Settings.StartWithWindows = enabled;
        StartupRegistration.Apply(enabled);
        Model.SaveNow();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        bool compactWasOpen = _compact != null;
        _main?.Close();
        _settingsWindow?.Close();
        _compact?.Close();
        // Closing windows on exit should not forget that compact mode was on.
        Model.Settings.CompactOpen = compactWasOpen;
        _hotkeys?.Dispose();
        Model.Dispose();
        _tray?.Dispose();
        _tray = null;
        _showEvent?.Set();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && Model != null)
        {
            _exiting = true;
            _hotkeys?.Dispose();
            Model.Dispose();
            _tray?.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---- Menus ----

    ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Menus.Item("Open LiteMixer", ShowMain));
        if (Updater.Available is { } release)
            menu.Items.Add(Menus.Item($"Update to {Updater.Display(release.Version)}...", () => ShowUpdatePrompt(userAsked: true)));
        menu.Items.Add(Menus.Item(Model.Mic.Muted ? "Unmute mic" : "Mute mic", Model.ToggleMicMute));
        menu.Items.Add(new Separator());
        AddMixerItems(menu);
        menu.Items.Add(new Separator());
        AddAppItems(menu);
        return menu;
    }

    /// <summary>Profiles, compact mode, auto-duck and Settings (shared by the tray and the ☰ menu).</summary>
    public void AddMixerItems(ContextMenu menu)
    {
        if (Model.Settings.Profiles.Count > 0)
        {
            menu.Items.Add(Menus.Label("PROFILES"));
            foreach (var profile in Model.Settings.Profiles.OrderBy(p => p.Name))
                menu.Items.Add(Menus.Item(profile.Name, () => Model.ApplyProfile(profile)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Menus.Item("Compact mixer", () => SetCompact(_compact == null), _compact != null));
        menu.Items.Add(Menus.Item("Auto-duck while chat talks", () => Model.DuckEnabled = !Model.DuckEnabled, Model.DuckEnabled));
        menu.Items.Add(Menus.Item("Settings...", ShowSettings));
    }

    public void AddAppItems(ContextMenu menu)
    {
        menu.Items.Add(Menus.Item("Start with Windows", () => SetStartWithWindows(!Model.Settings.StartWithWindows),
            Model.Settings.StartWithWindows));
        menu.Items.Add(Menus.Item("Reset all app assignments", Model.ResetAssignments));
        menu.Items.Add(Menus.Item("Open settings folder", () =>
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.Folder}\"") { UseShellExecute = true });
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menus.Item("Exit LiteMixer", ExitApp));
    }
}

internal static class Menus
{
    public static MenuItem Item(string header, Action onClick, bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    public static MenuItem Label(string text) =>
        new() { Header = text, IsEnabled = false, Style = (Style)Application.Current.FindResource("MenuLabel") };
}
