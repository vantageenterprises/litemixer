using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LiteMixer;

/// <summary>Small dark dialog: asks for a name, or just confirms when no validator is given.</summary>
public partial class PromptWindow : Window
{
    readonly Func<string, string?>? _validate;

    PromptWindow(Window? owner, string title, string message, string accept, string initial, Func<string, string?>? validate)
    {
        _validate = validate;
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Title = title;
        Message.Text = message;
        Accept.Content = accept;
        Input.Text = initial;
        if (validate == null) Input.Visibility = Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            if (validate == null) return;
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>The entered name, or null if cancelled.</summary>
    public static string? AskName(Window? owner, string title, string message, string accept, string initial, Func<string, string?> validate)
    {
        var dialog = new PromptWindow(owner, title, message, accept, initial, validate);
        return dialog.ShowDialog() == true ? dialog.Input.Text.Trim() : null;
    }

    public static bool Confirm(Window? owner, string title, string message, string accept) =>
        new PromptWindow(owner, title, message, accept, "", null).ShowDialog() == true;

    /// <summary>Just tells the user something, with one button.</summary>
    public static void Inform(Window? owner, string title, string message) =>
        InformWindow(owner, title, message).ShowDialog();

    internal static PromptWindow InformWindow(Window? owner, string title, string message)
    {
        var dialog = new PromptWindow(owner, title, message, "OK", "", null);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        return dialog;
    }

    /// <summary>For the offscreen UI check.</summary>
    internal static PromptWindow Sample(string title, string message, string accept, string initial, string? problem)
    {
        var dialog = new PromptWindow(null, title, message, accept, initial, _ => problem);
        dialog.ShowProblem(problem);
        return dialog;
    }

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
            Submit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ShowProblem(null);

    void Accept_Click(object sender, RoutedEventArgs e) => Submit();

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Submit()
    {
        var problem = _validate?.Invoke(Input.Text);
        if (problem != null)
        {
            ShowProblem(problem);
            return;
        }
        DialogResult = true;
    }

    void ShowProblem(string? problem)
    {
        Problem.Text = problem ?? "";
        Problem.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>Dark title bar with a caption color matching the windows (Windows 11).</summary>
internal static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int caption = 0x0026211E;
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
