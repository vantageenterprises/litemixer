using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LiteMixer;

/// <summary>Small always-on-top strip with just the faders, mutes and Balance.</summary>
public partial class CompactWindow : Window
{
    readonly MixerModel _model;

    public CompactWindow(MixerModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();

        var s = model.Settings;
        if (s.CompactLeft is { } left && s.CompactTop is { } top &&
            left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 10 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        _model.Settings.CompactLeft = Left;
        _model.Settings.CompactTop = Top;
        _model.QueueSave();
    }

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Open_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain();

    void Close_Click(object sender, RoutedEventArgs e) => App.Current.SetCompact(false);

    void Fader_Wheel(object sender, MouseWheelEventArgs e)
    {
        var slider = (Slider)sender;
        slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? 2 : -2), slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    void Balance_Wheel(object sender, MouseWheelEventArgs e)
    {
        _model.Balance += e.Delta > 0 ? 5 : -5;
        e.Handled = true;
    }

    void Balance_Reset(object sender, MouseButtonEventArgs e) => _model.Balance = 0;
}
