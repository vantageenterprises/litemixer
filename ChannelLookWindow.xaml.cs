using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteMixer;

/// <summary>Picks a channel's color and icon. Changes show on the channel right away; Cancel puts the old look back.</summary>
public partial class ChannelLookWindow : Window
{
    /// <summary>Icon font characters offered. All of them exist in both Segoe Fluent Icons and Segoe MDL2 Assets.</summary>
    internal static readonly string[] Icons =
    [
        "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "",
    ];

    static readonly string[] Swatches =
    [
        "#F0605A", "#F2468E", "#E879F9", "#9D5CF5", "#5B6CF5", "#3BA4F0", "#22D3EE", "#1FCB9C", "#A3E635", "#FACC15",
        "#F5A623", "#FB7185", "#FDA4AF", "#C4B5FD", "#93C5FD", "#5EEAD4", "#86EFAC", "#FDE68A", "#E5E7EB", "#94A3B8",
    ];

    readonly MixerModel _model;
    readonly ChannelVM _channel;
    readonly Color _startColor;
    readonly string _startGlyph;
    readonly Dictionary<string, Button> _iconCells = new();
    readonly Dictionary<Color, Button> _swatchCells = new();

    Color _color;
    string _glyph;
    double _hue, _saturation, _brightness;
    bool _saved;
    bool _syncingHex;
    Brush? _selectedIconBrush;

    internal ChannelLookWindow(MixerModel model, ChannelVM channel, Window? owner)
    {
        _model = model;
        _channel = channel;
        _color = _startColor = channel.AccentColor;
        _glyph = _startGlyph = channel.Glyph;
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Title = $"{channel.DisplayName}: color and icon";
        PreviewName.Text = channel.Title;

        var cell = (Style)FindResource("PickCell");
        foreach (var icon in Icons)
        {
            var button = new Button { Style = cell, Content = icon };
            button.Click += (_, _) => SetGlyph(icon);
            IconGrid.Children.Add(button);
            _iconCells[icon] = button;
        }
        foreach (var swatch in Swatches)
        {
            var color = ChannelVM.ParseColor(swatch)!.Value;
            var button = new Button { Style = cell, Background = new SolidColorBrush(color), Height = 22, ToolTip = swatch };
            button.Click += (_, _) => SetColor(color);
            SwatchGrid.Children.Add(button);
            _swatchCells[color] = button;
        }

        (_hue, _saturation, _brightness) = ToHsv(_color);
        Loaded += (_, _) => Refresh(updateHex: true);
        ShadeArea.SizeChanged += (_, _) => PlaceThumbs();
        HueArea.SizeChanged += (_, _) => PlaceThumbs();
    }

    /// <summary>Opens the dialog. The channel keeps whatever was showing when it closes: the saved look, or the old one.</summary>
    public static void Ask(Window? owner, MixerModel model, ChannelVM channel) =>
        new ChannelLookWindow(model, channel, owner).ShowDialog();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Enter)
        {
            Save();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_saved) _channel.SetLook(ChannelVM.ToHex(_startColor), _startGlyph);
    }

    // ---- Choosing ----

    internal void SetGlyph(string glyph)
    {
        _glyph = glyph;
        Refresh(updateHex: false);
    }

    internal void SetColor(Color color)
    {
        _color = color;
        // Black, white and greys have no hue of their own, so keep the current one on the bar.
        var (hue, saturation, brightness) = ToHsv(color);
        if (saturation > 0 && brightness > 0) _hue = hue;
        _saturation = saturation;
        _brightness = brightness;
        Refresh(updateHex: true);
    }

    /// <summary>A spot in the shade square, as fractions: left to right is saturation, bottom to top is brightness.</summary>
    internal void SetShade(double x, double y)
    {
        _saturation = Math.Clamp(x, 0, 1);
        _brightness = 1 - Math.Clamp(y, 0, 1);
        _color = FromHsv(_hue, _saturation, _brightness);
        Refresh(updateHex: true);
    }

    internal void SetHue(double fraction)
    {
        _hue = Math.Clamp(fraction, 0, 1) * 360;
        _color = FromHsv(_hue, _saturation, _brightness);
        Refresh(updateHex: true);
    }

    internal void Save()
    {
        _model.SaveLook(_channel, _color, _glyph);
        _saved = true;
        if (IsLoaded) Close();
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _glyph = _channel.DefaultGlyph;
        SetColor(_channel.DefaultColor);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    // ---- Dragging on the color areas ----

    void Shade_Down(object sender, MouseButtonEventArgs e)
    {
        ShadeArea.CaptureMouse();
        Shade_Move(sender, e);
    }

    void Shade_Move(object sender, MouseEventArgs e)
    {
        if (!ShadeArea.IsMouseCaptured) return;
        var point = e.GetPosition(ShadeArea);
        SetShade(point.X / ShadeArea.ActualWidth, point.Y / ShadeArea.ActualHeight);
    }

    void Hue_Down(object sender, MouseButtonEventArgs e)
    {
        HueArea.CaptureMouse();
        Hue_Move(sender, e);
    }

    void Hue_Move(object sender, MouseEventArgs e)
    {
        if (HueArea.IsMouseCaptured) SetHue(e.GetPosition(HueArea).X / HueArea.ActualWidth);
    }

    void Picker_Up(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingHex) return;
        if (ChannelVM.ParseColor(HexInput.Text) is { } color && color != _color)
        {
            _color = color;
            var (hue, saturation, brightness) = ToHsv(color);
            if (saturation > 0 && brightness > 0) _hue = hue;
            _saturation = saturation;
            _brightness = brightness;
            Refresh(updateHex: false);
        }
    }

    /// <summary>Leaving the box with something unusable puts the current color back.</summary>
    void Hex_LostFocus(object sender, RoutedEventArgs e) => ShowHex();

    // ---- Showing ----

    void ShowHex()
    {
        _syncingHex = true;
        HexInput.Text = ChannelVM.ToHex(_color);
        _syncingHex = false;
    }

    void Refresh(bool updateHex)
    {
        _channel.SetLook(ChannelVM.ToHex(_color), _glyph);
        if (updateHex) ShowHex();

        PreviewGlyph.Text = _glyph;
        PreviewGlyph.Foreground = _channel.Accent;
        PreviewName.Foreground = _channel.Accent;
        ShadeHue.Background = new SolidColorBrush(FromHsv(_hue, 1, 1));
        ShadeThumb.Stroke = _brightness > 0.55 && _saturation < 0.5 ? Brushes.Black : Brushes.White;
        ReadHint.Visibility = IsHardToRead(_color) ? Visibility.Visible : Visibility.Collapsed;

        _selectedIconBrush = _channel.Accent;
        foreach (var (icon, button) in _iconCells)
        {
            bool selected = icon == _glyph;
            button.BorderBrush = selected ? _selectedIconBrush : Brushes.Transparent;
            button.Foreground = selected ? _selectedIconBrush : (Brush)FindResource("SoftTextBrush");
        }
        foreach (var (color, button) in _swatchCells)
            button.BorderBrush = color == _color ? Brushes.White : Brushes.Transparent;

        PlaceThumbs();
    }

    void PlaceThumbs()
    {
        Canvas.SetLeft(ShadeThumb, _saturation * ShadeArea.ActualWidth - ShadeThumb.Width / 2);
        Canvas.SetTop(ShadeThumb, (1 - _brightness) * ShadeArea.ActualHeight - ShadeThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _hue / 360 * HueArea.ActualWidth - HueThumb.Width / 2);
    }

    // ---- Color math ----

    /// <summary>Under this brightness, channel names and icons nearly vanish on the dark headers. Allowed, but flagged.</summary>
    internal static bool IsHardToRead(Color color) =>
        (0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B)) < 0.04;

    static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    internal static (double Hue, double Saturation, double Brightness) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), range = max - min;
        double hue = range == 0 ? 0
            : max == r ? 60 * (((g - b) / range + 6) % 6)
            : max == g ? 60 * ((b - r) / range + 2)
            : 60 * ((r - g) / range + 4);
        return (hue, max == 0 ? 0 : range / max, max);
    }

    internal static Color FromHsv(double hue, double saturation, double brightness)
    {
        double chroma = brightness * saturation;
        double section = (hue % 360) / 60;
        double x = chroma * (1 - Math.Abs(section % 2 - 1));
        (double r, double g, double b) = (int)section switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        double m = brightness - chroma;
        static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }
}
