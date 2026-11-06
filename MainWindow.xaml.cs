using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace LiteMixer;

public partial class MainWindow : Window
{
    const string DragFormat = "LiteMixer.AppKey";

    readonly MixerModel _model;
    Point _dragStart;
    EventHandler? _scrollAnimation;

    public MainWindow(MixerModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        UpdateBanner.DataContext = App.Current.Updater;
        RestorePosition();

        PlaceBalance(animate: false);
        NotifyCollectionChangedEventHandler onOrderChanged = (_, _) => PlaceBalance(animate: true);
        model.ScrollChannels.CollectionChanged += onOrderChanged;
        Closed += (_, _) => model.ScrollChannels.CollectionChanged -= onOrderChanged;
    }

    void UpdateBanner_Click(object sender, RoutedEventArgs e) => App.Current.ShowUpdatePrompt(userAsked: true);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        App.Current.UpdateMeters();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        App.Current.UpdateMeters();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        EndMove(keep: false);
        if (WindowState == WindowState.Normal)
        {
            _model.Settings.WindowLeft = Left;
            _model.Settings.WindowTop = Top;
            _model.SaveNow();
        }
    }

    void RestorePosition()
    {
        var s = _model.Settings;
        if (s.WindowLeft is { } left && s.WindowTop is { } top &&
            left >= SystemParameters.VirtualScreenLeft - 50 &&
            top >= SystemParameters.VirtualScreenTop - 10 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 200 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
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

    static ChannelVM ChannelOf(object sender) => (ChannelVM)((FrameworkElement)sender).DataContext;

    static void Open(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- Custom channels ----

    void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.AskName(this, "New channel",
            $"Name your channel. It starts at {_model.DefaultChannel.DisplayName}'s level; drag apps into it from any channel.",
            "Add channel", "", input => _model.ChannelNameProblem(input));
        if (name == null) return;
        var channel = _model.AddChannel(name);
        // The new column exists only after the next layout pass.
        Dispatcher.BeginInvoke(() => GoToChannel(channel), DispatcherPriority.Loaded);
    }

    void RenameChannel(ChannelVM channel)
    {
        var name = PromptWindow.AskName(this, "Rename channel", $"New name for {channel.DisplayName}:",
            "Rename", channel.DisplayName, input => _model.ChannelNameProblem(input, channel));
        if (name != null) _model.RenameChannel(channel, name);
    }

    void DeleteChannel(ChannelVM channel)
    {
        var target = _model.DefaultChannel == channel ? _model.Game : _model.DefaultChannel;
        int apps = channel.Apps.Count;
        var message = apps > 0
            ? $"Its apps move to {target.DisplayName} and play at {target.DisplayName}'s level ({target.VolumeText})."
            : "This can't be undone.";
        if (PromptWindow.Confirm(this, $"Delete {channel.DisplayName}?", message, "Delete channel"))
            _model.DeleteChannel(channel);
    }

    void ChannelMenu_Click(object sender, RoutedEventArgs e) => Open(BuildChannelMenu(), (UIElement)sender);

    /// <summary>Every channel with its color and level; picking one scrolls to it and flashes it.</summary>
    internal ContextMenu BuildChannelMenu()
    {
        var menu = new ContextMenu();
        foreach (var channel in _model.Channels)
        {
            var row = new DockPanel { Width = 200, LastChildFill = true };
            var level = new TextBlock { Text = channel.VolumeText, FontSize = 11.5, Foreground = (Brush)FindResource("DimBrush"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(level, Dock.Right);
            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = channel.Accent, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(level);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = channel.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

            var item = new MenuItem { Header = row, Style = (Style)FindResource("PlainMenuItem") };
            item.Click += (_, _) => GoToChannel(channel);
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>Scrolls a channel to the middle of the view (Master and Mic are always visible) and flashes it.</summary>
    internal void GoToChannel(ChannelVM channel)
    {
        FrameworkElement? host = channel == _model.Master ? MasterHost
            : channel == _model.Mic ? MicHost
            : ChannelList.ItemContainerGenerator.ContainerFromItem(channel) as FrameworkElement;
        if (host == null) return;

        if (channel != _model.Master && channel != _model.Mic)
        {
            UpdateLayout();
            double x = host.TransformToAncestor(ChannelStrip).Transform(new Point()).X;
            double target = Math.Clamp(x - (ChannelScroll.ViewportWidth - host.ActualWidth) / 2, 0, ChannelScroll.ScrollableWidth);
            AnimateScroll(target);
        }

        if (FindByName(host, "FlashRing") is { } ring)
        {
            if (SystemParameters.ClientAreaAnimation)
                ring.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1.1)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }
    }

    void AnimateScroll(double target)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            ChannelScroll.ScrollToHorizontalOffset(target);
            return;
        }

        if (_scrollAnimation != null) CompositionTarget.Rendering -= _scrollAnimation;
        double from = ChannelScroll.HorizontalOffset;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const double duration = 0.26;
        _scrollAnimation = (_, _) =>
        {
            double t = Math.Min(clock.Elapsed.TotalSeconds / duration, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            ChannelScroll.ScrollToHorizontalOffset(from + (target - from) * eased);
            if (t >= 1)
            {
                CompositionTarget.Rendering -= _scrollAnimation;
                _scrollAnimation = null;
            }
        };
        CompositionTarget.Rendering += _scrollAnimation;
    }

    static FrameworkElement? FindByName(DependencyObject parent, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    // ---- Horizontal scrolling ----

    /// <summary>Shift+wheel anywhere, or the plain wheel over the scrollbar, scrolls channels sideways.</summary>
    void ChannelScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        bool overBar = e.OriginalSource is DependencyObject source && IsInsideScrollBar(source);
        if (!overBar && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        ChannelScroll.ScrollToHorizontalOffset(ChannelScroll.HorizontalOffset - e.Delta * 0.75);
        e.Handled = true;
    }

    static bool IsInsideScrollBar(DependencyObject element)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is ScrollBar) return true;
        return false;
    }

    /// <summary>Holding a dragged app near either edge scrolls, so far-away channels can be reached.</summary>
    void ChannelScroll_PreviewDragOver(object sender, DragEventArgs e)
    {
        const double edge = 48, step = 14;
        double x = e.GetPosition(ChannelScroll).X;
        if (x < edge) ChannelScroll.ScrollToHorizontalOffset(ChannelScroll.HorizontalOffset - step);
        else if (x > ChannelScroll.ViewportWidth - edge) ChannelScroll.ScrollToHorizontalOffset(ChannelScroll.HorizontalOffset + step);
    }

    /// <summary>Over a column's non-drop area; the wells handle their own drops.</summary>
    void ChannelScroll_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    // ---- Moving channels ----

    // A column is 178 wide with a 4 gap. The list stays capturing throughout, because a moved column
    // briefly leaves the visual tree and would lose the mouse if it held the capture itself.
    internal const double ColumnPitch = 182;
    const double HoldSeconds = 0.2;
    static readonly Duration SlideTime = TimeSpan.FromMilliseconds(150);

    sealed class ChannelMove(ChannelVM channel, FrameworkElement host, int origin, double grab, double startX, double startViewportX)
    {
        public ChannelVM Channel { get; } = channel;
        public FrameworkElement Host { get; } = host;
        public int Origin { get; } = origin;
        public double Grab { get; } = grab;
        public double StartX { get; } = startX;
        public double StartViewportX { get; } = startViewportX;
        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
        public TimeSpan LastFrame { get; set; }
        public bool Lifted { get; set; }
    }

    ChannelMove? _move;

    internal bool IsMoveLifted => _move?.Lifted == true;
    internal double BalanceLeft => Math.Min(_model.ScrollChannels.IndexOf(_model.Game), _model.ScrollChannels.IndexOf(_model.Chat)) * ColumnPitch;

    void Header_Down(object sender, MouseButtonEventArgs e)
    {
        if (!ChannelOf(sender).CanMove || _move != null || !ChannelList.CaptureMouse()) return;
        if (BeginMove(ChannelOf(sender), Mouse.GetPosition(ChannelStrip).X, Mouse.GetPosition(ChannelScroll).X))
            CompositionTarget.Rendering += MoveFrame;
        else
            ChannelList.ReleaseMouseCapture();
        e.Handled = true;
    }

    void ChannelList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_move == null) return;
        EndMove(keep: true);
        e.Handled = true;
    }

    void ChannelList_LostCapture(object sender, MouseEventArgs e) => EndMove(keep: false);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_move == null || e.Key != Key.Escape) return;
        EndMove(keep: false);
        e.Handled = true;
    }

    void MoveFrame(object? sender, EventArgs e)
    {
        if (_move is not { } move) return;
        var now = move.Clock.Elapsed;
        double seconds = (now - move.LastFrame).TotalSeconds;
        move.LastFrame = now;
        UpdateMove(Mouse.GetPosition(ChannelStrip).X, Mouse.GetPosition(ChannelScroll).X, seconds, now.TotalSeconds >= HoldSeconds);
    }

    internal bool BeginMove(ChannelVM channel, double stripX, double viewportX)
    {
        if (!channel.CanMove || _move != null) return false;
        if (ChannelList.ItemContainerGenerator.ContainerFromItem(channel) is not FrameworkElement host) return false;
        int index = _model.ScrollChannels.IndexOf(channel);
        _move = new ChannelMove(channel, host, index, stripX - index * ColumnPitch, stripX, viewportX);
        return true;
    }

    /// <summary>One step of a move: pointer positions along the strip and the visible area, and the time since the last step.</summary>
    internal void UpdateMove(double stripX, double viewportX, double seconds, bool held)
    {
        if (_move is not { } move) return;
        if (!move.Lifted)
        {
            if (!held && Math.Abs(stripX - move.StartX) < SystemParameters.MinimumHorizontalDragDistance) return;
            Lift(move);
        }

        // Only scrolls once the pointer has travelled toward that edge, so grabbing a column near the edge doesn't.
        double speed = AutoScrollSpeed(viewportX, ChannelScroll.ViewportWidth);
        if ((speed < 0 && viewportX < move.StartViewportX) || (speed > 0 && viewportX > move.StartViewportX))
            ChannelScroll.ScrollToHorizontalOffset(ChannelScroll.HorizontalOffset + speed * seconds);

        int last = _model.ScrollChannels.Count - 1;
        double left = Math.Clamp(stripX - move.Grab, 0, last * ColumnPitch);
        int index = (int)Math.Round(left / ColumnPitch);
        PlaceAt(move.Channel, index);
        TransformOf(move.Host).X = left - index * ColumnPitch;
    }

    /// <summary>How fast to scroll, in pixels per second, for a pointer this far along the visible area. Past either edge is full speed.</summary>
    internal static double AutoScrollSpeed(double x, double width)
    {
        const double edge = 56, fastest = 900;
        double depth = x < edge ? x - edge : x > width - edge ? x - (width - edge) : 0;
        return Math.Clamp(depth / edge, -1, 1) * fastest;
    }

    void Lift(ChannelMove move)
    {
        move.Lifted = true;
        if (_scrollAnimation != null)
        {
            CompositionTarget.Rendering -= _scrollAnimation;
            _scrollAnimation = null;
        }
        var transform = TransformOf(move.Host);
        double shown = transform.X;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = shown;
        Panel.SetZIndex(move.Host, 1);
        move.Host.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
        if (FindByName(move.Host, "FlashRing") is { } ring)
        {
            ring.BeginAnimation(OpacityProperty, null);
            ring.Opacity = 1;
        }
        Mouse.OverrideCursor = Cursors.SizeWE;
    }

    /// <summary>Moves a channel, sliding the columns it passes from where they were to their new places.</summary>
    void PlaceAt(ChannelVM channel, int index)
    {
        int current = _model.ScrollChannels.IndexOf(channel);
        if (index == current) return;
        double shift = index > current ? ColumnPitch : -ColumnPitch;
        var passed = _model.ScrollChannels
            .Skip(Math.Min(current, index))
            .Take(Math.Abs(index - current) + 1)
            .Where(c => c != channel)
            .ToList();
        _model.MoveChannel(channel, index);
        foreach (var other in passed)
            if (ChannelList.ItemContainerGenerator.ContainerFromItem(other) is FrameworkElement host)
                SlideHome(host, shift);
    }

    static TranslateTransform TransformOf(FrameworkElement host)
    {
        if (host.RenderTransform is not TranslateTransform transform) host.RenderTransform = transform = new TranslateTransform();
        return transform;
    }

    /// <summary>Slides a column to its layout position, starting from where it shows now plus an extra offset.</summary>
    static void SlideHome(FrameworkElement host, double extra, Action? done = null)
    {
        var transform = TransformOf(host);
        double start = transform.X + extra;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        if (!SystemParameters.ClientAreaAnimation || start == 0)
        {
            done?.Invoke();
            return;
        }
        var slide = new DoubleAnimation(start, 0, SlideTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) slide.Completed += (_, _) => done();
        transform.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    /// <summary>Finishes a move. keep=true saves the new order; false puts the channel back where it started.</summary>
    internal void EndMove(bool keep)
    {
        if (_move is not { } move) return;
        _move = null; // Cleared first: releasing the capture below calls back into this.
        CompositionTarget.Rendering -= MoveFrame;
        if (ChannelList.IsMouseCaptured) ChannelList.ReleaseMouseCapture();
        if (!move.Lifted) return;

        Mouse.OverrideCursor = null;
        move.Host.Effect = null;
        if (FindByName(move.Host, "FlashRing") is { } ring) ring.Opacity = 0;

        int current = _model.ScrollChannels.IndexOf(move.Channel);
        if (!keep) PlaceAt(move.Channel, move.Origin);
        double extra = keep ? 0 : (current - move.Origin) * ColumnPitch;
        SlideHome(move.Host, extra, () =>
        {
            if (_move?.Host != move.Host) Panel.SetZIndex(move.Host, 0);
        });
        if (keep && current != move.Origin) _model.SaveChannelOrder();
    }

    void PlaceBalance(bool animate)
    {
        BalanceShift.BeginAnimation(TranslateTransform.XProperty, null);
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            BalanceShift.X = BalanceLeft;
            return;
        }
        BalanceShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(BalanceLeft, SlideTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---- Menus ----

    void AppMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        App.Current.AddMixerItems(menu);
        menu.Items.Add(new Separator());
        App.Current.AddAppItems(menu);
        Open(menu, (UIElement)sender);
    }

    void Gear_Click(object sender, RoutedEventArgs e)
    {
        var channel = ChannelOf(sender);
        var menu = new ContextMenu();
        menu.Items.Add(Menus.Item("Volume 100%", () => channel.Volume = 100));
        menu.Items.Add(Menus.Item("Volume 50%", () => channel.Volume = 50));
        menu.Items.Add(Menus.Item(channel.Muted ? "Unmute" : "Mute", () => channel.Muted = !channel.Muted));
        if (channel.Kind == ChannelKind.Output && channel != _model.DefaultChannel)
            menu.Items.Add(Menus.Item($"Move all apps to {_model.DefaultChannel.DisplayName}", () => _model.UnassignChannel(channel),
                enabled: channel.Apps.Count > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menus.Item("Color and icon...", () => ChannelLookWindow.Ask(this, _model, channel)));
        if (channel.IsCustom)
        {
            menu.Items.Add(Menus.Item("Rename channel...", () => RenameChannel(channel)));
            menu.Items.Add(Menus.Item("Delete channel...", () => DeleteChannel(channel)));
        }
        if (channel.Kind == ChannelKind.Master)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Menus.Label("NEW APPS GO TO"));
            foreach (var output in _model.Channels.Where(c => c.Kind == ChannelKind.Output))
                menu.Items.Add(Menus.Item(output.DisplayName, () => _model.SetDefaultChannel(output), output == _model.DefaultChannel));
            menu.Items.Add(new Separator());
            App.Current.AddAppItems(menu);
        }
        Open(menu, (UIElement)sender);
    }

    void Device_Click(object sender, RoutedEventArgs e)
    {
        var channel = ChannelOf(sender);
        var menu = new ContextMenu();
        switch (channel.Kind)
        {
            case ChannelKind.Master:
                menu.Items.Add(Menus.Label("WINDOWS DEFAULT OUTPUT"));
                foreach (var device in _model.RenderDevices)
                    menu.Items.Add(Menus.Item(device.Name, () => _model.SetDefaultOutput(device), device.Id == _model.DefaultOutputId));
                break;

            case ChannelKind.Mic:
                menu.Items.Add(Menus.Label("WINDOWS DEFAULT INPUT"));
                foreach (var device in _model.CaptureDevices)
                    menu.Items.Add(Menus.Item(device.Name, () => _model.SetDefaultInput(device), device.Id == _model.DefaultInputId));
                break;

            default:
                var saved = _model.SavedChannelDevice(channel);
                var defaultName = _model.RenderDevices.FirstOrDefault(d => d.Id == _model.DefaultOutputId)?.Name ?? "none";
                menu.Items.Add(Menus.Label($"{channel.Title} OUTPUT"));
                menu.Items.Add(Menus.Item($"Windows default ({defaultName})", () => _model.SetChannelDevice(channel, null), saved == null));
                menu.Items.Add(new Separator());
                foreach (var device in _model.RenderDevices)
                    menu.Items.Add(Menus.Item(device.Name, () => _model.SetChannelDevice(channel, device), device.Id == saved));
                if (saved != null && _model.RenderDevices.All(d => d.Id != saved))
                    menu.Items.Add(Menus.Item($"{_model.SavedChannelDeviceName(channel) ?? "Saved device"} (not connected)", () => { }, true, false));
                break;
        }
        Open(menu, (UIElement)sender);
    }

    void Chip_Menu(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppChip chip) return;
        var menu = new ContextMenu();
        menu.Items.Add(Menus.Label(chip.Name.ToUpperInvariant()));

        if (chip.Draggable)
        {
            menu.Items.Add(AppVolumeSlider(chip, menu));
            menu.Items.Add(new Separator());
            var current = _model.ChannelOfApp(chip.Key);
            foreach (var channel in _model.OutputChannels)
                menu.Items.Add(Menus.Item($"Move to {channel.DisplayName}", () => _model.Assign(chip.Key, channel), channel == current));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Menus.Item("Hide from list", () => _model.SetHidden(chip.Key, true)));
        _model.FreezeApps = true;
        menu.Closed += (_, _) =>
        {
            _model.FreezeApps = false;
            _model.RefreshApps();
        };
        Open(menu, (UIElement)sender);
        e.Handled = true;
    }

    /// <summary>Per-app level inside its channel. Chips refresh when the menu closes, so the menu stays put while dragging.</summary>
    FrameworkElement AppVolumeSlider(AppChip chip, ContextMenu menu)
    {
        var label = new TextBlock { FontSize = 11.5, Foreground = (Brush)FindResource("SoftTextBrush"), Margin = new Thickness(0, 0, 0, 4) };
        var slider = new Slider { Style = (Style)FindResource("PlainSlider"), Width = 190, Value = _model.AppVolume(chip.Key) };
        void UpdateLabel() => label.Text = $"App volume  {slider.Value:0}%";
        UpdateLabel();
        slider.ValueChanged += (_, _) =>
        {
            UpdateLabel();
            _model.SetAppVolume(chip.Key, slider.Value, refreshChips: false);
        };
        slider.PreviewMouseWheel += (_, args) =>
        {
            slider.Value = Math.Clamp(slider.Value + (args.Delta > 0 ? 5 : -5), 0, 100);
            args.Handled = true;
        };
        var panel = new StackPanel { Margin = new Thickness(12, 4, 12, 6) };
        panel.Children.Add(label);
        panel.Children.Add(slider);
        return panel;
    }

    // ---- Wheel ----

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

    void Device_Wheel(object sender, MouseWheelEventArgs e)
    {
        _model.AdjustDeviceVolume(ChannelOf(sender), e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

    // ---- Drag and drop ----

    void Chip_Down(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    void Chip_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var element = (FrameworkElement)sender;
        if (element.DataContext is not AppChip { Draggable: true } chip) return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        DragDrop.DoDragDrop(element, new DataObject(DragFormat, chip.Key), DragDropEffects.Move);
    }

    void Well_DragOver(object sender, DragEventArgs e)
    {
        var well = (Border)sender;
        var channel = (ChannelVM)well.DataContext;
        bool ok = channel.AcceptsDrop && e.Data.GetDataPresent(DragFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        well.BorderBrush = ok ? channel.Accent : Brushes.Transparent;
        e.Handled = true;
    }

    void Well_DragLeave(object sender, DragEventArgs e) => ((Border)sender).BorderBrush = Brushes.Transparent;

    void Well_Drop(object sender, DragEventArgs e)
    {
        var well = (Border)sender;
        well.BorderBrush = Brushes.Transparent;
        if (e.Data.GetData(DragFormat) is string key) _model.Assign(key, (ChannelVM)well.DataContext);
        e.Handled = true;
    }
}
