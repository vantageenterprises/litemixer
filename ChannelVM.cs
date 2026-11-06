using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace LiteMixer;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public enum ChannelKind { Master, Output, Mic }

public sealed record AppChip(string Key, string Name, bool Draggable, double Volume, ImageSource? Icon)
{
    public string Display => Volume < 100 ? $"{Name.ToUpperInvariant()} {Volume:0}%" : Name.ToUpperInvariant();
    public Visibility IconVisibility => Icon == null ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class ChannelVM : ObservableObject
{
    // Fader geometry in the main window: 250 px track, 20 px thumb.
    const double TrackTravel = 230;
    const double ThumbHalf = 10;

    static readonly Brush White = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    static readonly Brush Unrouted = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAB)));

    double _volume = 100;
    double _cap = 100;
    double _peak;
    bool _muted;
    bool _silent;
    string _title;
    string _displayName;
    string _deviceName = "";
    string _deviceTooltip = "";
    string _deviceGlyph;
    string _emptyHint = "";

    public ChannelVM(string id, string title, string glyph, string accent, ChannelKind kind, string? customName = null)
    {
        Id = id;
        Glyph = glyph;
        Kind = kind;
        IsCustom = customName != null;
        _title = customName?.ToUpperInvariant() ?? title;
        _displayName = customName ?? char.ToUpperInvariant(title[0]) + title[1..].ToLowerInvariant();
        var color = (Color)ColorConverter.ConvertFromString(accent);
        Accent = Freeze(new SolidColorBrush(color));
        ChipBrush = kind == ChannelKind.Master ? Unrouted : Accent;
        bool brightCustom = IsCustom && (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255 > 0.5;
        ChipForeground = kind == ChannelKind.Master || id is "game" or "mic" || brightCustom ? Ink : White;
        _deviceGlyph = kind == ChannelKind.Mic ? "" : "";
    }

    public string Id { get; }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public bool IsCustom { get; }
    public string Glyph { get; }
    public ChannelKind Kind { get; }
    public Brush Accent { get; }
    public Brush ChipBrush { get; }
    public Brush ChipForeground { get; }
    public bool AcceptsDrop => Kind == ChannelKind.Output;
    public string AppsLabel => "Apps";
    public ObservableCollection<AppChip> Apps { get; } = new();

    /// <summary>Text shown in the apps area while it is empty (Master explains where new apps go).</summary>
    public string EmptyHint { get => _emptyHint; set => Set(ref _emptyHint, value); }

    internal Action<ChannelVM>? VolumeChanged;
    internal Action<ChannelVM>? MutedChanged;

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 0, _cap);
            if (!Set(ref _volume, clamped))
            {
                // A drag past the cap still has to snap the slider back to the cap.
                if (Math.Round(value) != clamped) OnPropertyChanged();
                return;
            }
            OnPropertyChanged(nameof(VolumeText));
            if (!_silent) VolumeChanged?.Invoke(this);
        }
    }

    public string VolumeText => $"{_volume:0}%";

    /// <summary>Safety ceiling for the fader. 100 means no cap.</summary>
    public double Cap
    {
        get => _cap;
        set
        {
            if (!Set(ref _cap, Math.Clamp(Math.Round(value), 5, 100))) return;
            OnPropertyChanged(nameof(CapMarkerMargin));
            OnPropertyChanged(nameof(CapVisibility));
            if (_volume > _cap) Volume = _cap;
        }
    }

    public Thickness CapMarkerMargin => new(0, (1 - _cap / 100) * TrackTravel + ThumbHalf - 1, 0, 0);
    public Visibility CapVisibility => _cap < 100 ? Visibility.Visible : Visibility.Collapsed;

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            if (!_silent) MutedChanged?.Invoke(this);
        }
    }

    public double Peak
    {
        get => _peak;
        set
        {
            if (Math.Abs(_peak - value) < 0.004) return;
            _peak = value;
            OnPropertyChanged();
        }
    }

    public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value); }
    public string DeviceTooltip { get => _deviceTooltip; set => Set(ref _deviceTooltip, value); }
    public string DeviceGlyph { get => _deviceGlyph; set => Set(ref _deviceGlyph, value); }

    internal void Rename(string name)
    {
        Title = name.ToUpperInvariant();
        DisplayName = name;
    }

    /// <summary>Updates state without firing the change callbacks (used for restores and external sync).</summary>
    internal void SetSilently(double volume, bool muted)
    {
        _silent = true;
        try
        {
            Volume = volume;
            Muted = muted;
        }
        finally { _silent = false; }
    }

    internal void SyncApps(List<AppChip> apps)
    {
        if (apps.Count == Apps.Count && apps.SequenceEqual(Apps)) return;
        Apps.Clear();
        foreach (var app in apps) Apps.Add(app);
    }

    static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
