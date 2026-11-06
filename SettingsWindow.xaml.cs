using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteMixer;

public partial class SettingsWindow : Window
{
    readonly MixerModel _model;
    readonly SettingsVM _vm;

    public SettingsWindow(MixerModel model)
    {
        _model = model;
        _vm = new SettingsVM(model);
        DataContext = _vm;
        InitializeComponent();
        model.ProfilesChanged += _vm.ReloadProfiles;
        model.ChannelsChanged += _vm.ReloadChannels;
        Closed += (_, _) =>
        {
            model.ProfilesChanged -= _vm.ReloadProfiles;
            model.ChannelsChanged -= _vm.ReloadChannels;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    // ---- Hotkey capture ----

    void Hotkey_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Registered hotkeys would swallow the very keys being typed, so release them while capturing.
        App.Current.SuspendHotkeys();
        ((HotkeyRow)((FrameworkElement)sender).DataContext).Capturing = true;
    }

    void Hotkey_Blur(object sender, KeyboardFocusChangedEventArgs e)
    {
        ((HotkeyRow)((FrameworkElement)sender).DataContext).Capturing = false;
        _vm.ApplyHotkeys();
    }

    void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var row = (HotkeyRow)((FrameworkElement)sender).DataContext;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin) return;
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }
        if (key is Key.Back or Key.Delete)
        {
            row.Combo = "";
            Keyboard.ClearFocus();
            return;
        }

        var modifiers = Keyboard.Modifiers;
        bool functionKey = key >= Key.F1 && key <= Key.F24;
        if (modifiers == ModifierKeys.None && !functionKey)
        {
            row.Status = "Use Ctrl, Alt, Shift or Win with it, so it does not fire while typing.";
            return;
        }
        row.Combo = HotkeyManager.Format(modifiers, key);
        Keyboard.ClearFocus();
    }

    // ---- Buttons ----

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await App.Current.CheckForUpdatesAsync();

    void TestBeep_Click(object sender, RoutedEventArgs e) => Beeper.Play(true, _model.Settings.MicBeepVolume);

    void Unhide_Click(object sender, RoutedEventArgs e)
    {
        var row = (HiddenRow)((FrameworkElement)sender).DataContext;
        _model.SetHidden(row.Key, false);
        _vm.ReloadHidden();
    }

    void SaveProfile_Click(object sender, RoutedEventArgs e) => SaveProfile();

    void ProfileName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveProfile();
    }

    void SaveProfile()
    {
        var name = ProfileName.Text.Trim();
        if (name.Length == 0)
        {
            ProfileName.Focus();
            return;
        }
        _model.SaveProfile(name);
        ProfileName.Text = "";
    }

    void ApplyProfile_Click(object sender, RoutedEventArgs e) =>
        _model.ApplyProfile((MixProfile)((FrameworkElement)sender).DataContext);

    void DeleteProfile_Click(object sender, RoutedEventArgs e) =>
        _model.DeleteProfile((MixProfile)((FrameworkElement)sender).DataContext);
}

public sealed class SettingsVM : ObservableObject
{
    readonly MixerModel _model;
    AppSettings S => _model.Settings;

    public SettingsVM(MixerModel model)
    {
        _model = model;
        App.Current.Updater.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Updater.Status)) OnPropertyChanged(nameof(UpdateStatus));
        };
        Hotkeys = new(HotkeyActions.All.Select(a => new HotkeyRow(a.Id, a.Label, S.Hotkeys.GetValueOrDefault(a.Id, ""), ApplyHotkeys)));
        ShowFailures(App.Current.HotkeyFailures);
        ReloadChannels();
        ReloadHidden();
        ReloadProfiles();
    }

    public ObservableCollection<HotkeyRow> Hotkeys { get; }
    public ObservableCollection<CapRow> Caps { get; } = new();
    public ObservableCollection<DuckRow> DuckChannels { get; } = new();
    public ObservableCollection<HiddenRow> Hidden { get; } = new();
    public ObservableCollection<MixProfile> Profiles { get; } = new();

    public Visibility NoHiddenVisibility => Hidden.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool CheckForUpdates
    {
        get => S.CheckForUpdates;
        set { S.CheckForUpdates = value; OnPropertyChanged(); _model.QueueSave(); }
    }

    public string UpdateStatus =>
        App.Current.Updater.Status is { Length: > 0 } status ? status : $"Version {Updater.CurrentText}";

    public bool FadeIn
    {
        get => S.FadeIn;
        set { S.FadeIn = value; OnPropertyChanged(); _model.QueueSave(); }
    }

    public bool DuckEnabled
    {
        get => _model.DuckEnabled;
        set { _model.DuckEnabled = value; OnPropertyChanged(); }
    }

    public double DuckLevel
    {
        get => S.DuckLevel;
        set
        {
            S.DuckLevel = Math.Round(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DuckLevelText));
            _model.QueueSave();
        }
    }

    public string DuckLevelText => $"{S.DuckLevel:0}%";

    public bool MicBeep
    {
        get => S.MicBeep;
        set { S.MicBeep = value; OnPropertyChanged(); _model.QueueSave(); }
    }

    public double MicBeepVolume
    {
        get => S.MicBeepVolume;
        set
        {
            S.MicBeepVolume = Math.Round(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(MicBeepVolumeText));
            _model.QueueSave();
        }
    }

    public string MicBeepVolumeText => $"{S.MicBeepVolume:0}%";

    public void ApplyHotkeys()
    {
        foreach (var row in Hotkeys) S.Hotkeys[row.Id] = row.Combo;
        _model.QueueSave();
        ShowFailures(App.Current.ReloadHotkeys());
    }

    void ShowFailures(IReadOnlyCollection<string> failed)
    {
        foreach (var row in Hotkeys)
        {
            var duplicate = row.Combo.Length > 0 &&
                            Hotkeys.Any(other => other != row && other.Combo.Equals(row.Combo, StringComparison.OrdinalIgnoreCase));
            row.Status = failed.Contains(row.Id)
                ? "Another app already uses this combo; pick a different one."
                : duplicate ? "Used by another action here." : "";
        }
    }

    public void ReloadChannels()
    {
        Caps.Clear();
        foreach (var channel in _model.CappableChannels) Caps.Add(new CapRow(channel, _model));
        DuckChannels.Clear();
        foreach (var channel in _model.OutputChannels.Where(c => c != _model.Chat)) DuckChannels.Add(new DuckRow(channel, _model));
    }

    public void ReloadHidden()
    {
        Hidden.Clear();
        foreach (var key in S.HiddenApps.OrderBy(k => k)) Hidden.Add(new HiddenRow(key, _model.NameOfApp(key)));
        OnPropertyChanged(nameof(NoHiddenVisibility));
    }

    public void ReloadProfiles()
    {
        Profiles.Clear();
        foreach (var profile in S.Profiles.OrderBy(p => p.Name)) Profiles.Add(profile);
    }
}

public sealed class HotkeyRow : ObservableObject
{
    readonly Action _changed;
    string _combo;
    string _status = "";
    bool _capturing;

    public HotkeyRow(string id, string label, string combo, Action changed)
    {
        Id = id;
        Label = label;
        _combo = combo;
        _changed = changed;
    }

    public string Id { get; }
    public string Label { get; }

    public string Combo
    {
        get => _combo;
        set
        {
            if (!Set(ref _combo, value)) return;
            OnPropertyChanged(nameof(Display));
            _changed();
        }
    }

    public bool Capturing
    {
        get => _capturing;
        set { if (Set(ref _capturing, value)) OnPropertyChanged(nameof(Display)); }
    }

    public string Display => _capturing ? "Press keys..." : _combo.Length == 0 ? "Not set" : _combo;

    public string Status
    {
        get => _status;
        set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusVisibility)); }
    }

    public Visibility StatusVisibility => _status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class CapRow : ObservableObject
{
    readonly ChannelVM _channel;
    readonly MixerModel _model;

    public CapRow(ChannelVM channel, MixerModel model)
    {
        _channel = channel;
        _model = model;
    }

    public string Name => _channel.DisplayName;
    public Brush Accent => _channel.Accent;

    public double Value
    {
        get => _channel.Cap;
        set
        {
            _model.SetCap(_channel, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
        }
    }

    public string ValueText => _channel.Cap >= 100 ? "Off" : $"{_channel.Cap:0}%";
}

public sealed class DuckRow : ObservableObject
{
    readonly ChannelVM _channel;
    readonly MixerModel _model;

    public DuckRow(ChannelVM channel, MixerModel model)
    {
        _channel = channel;
        _model = model;
    }

    public string Name => _channel.DisplayName;

    public bool IsOn
    {
        get => _model.Settings.DuckChannels.Contains(_channel.Id);
        set
        {
            _model.Settings.DuckChannels.Remove(_channel.Id);
            if (value) _model.Settings.DuckChannels.Add(_channel.Id);
            OnPropertyChanged();
            _model.RefreshDuck();
            _model.QueueSave();
        }
    }
}

public sealed record HiddenRow(string Key, string Name);
