using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace LiteMixer;

/// <summary>
/// Says a new version is ready and shows what's in it. It appears on its own, so by default it doesn't take the
/// keyboard: whatever you're typing stays in the app you're typing in, and Enter can't accept it by accident.
/// </summary>
public partial class UpdateWindow : Window
{
    const int MaxNotes = 600;

    public event Action? Accepted;

    internal UpdateWindow(string version, string currentVersion, string notes)
    {
        InitializeComponent();
        Headline.Text = $"LiteMixer {version} is available";
        Current.Text = $"You have version {currentVersion}.";
        notes = notes.Trim();
        Notes.Text = notes.Length > MaxNotes ? notes[..MaxNotes] + "..." : notes;
        if (notes.Length == 0) NotesBox.Visibility = Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    void Later_Click(object sender, RoutedEventArgs e) => Close();

    void Update_Click(object sender, RoutedEventArgs e) => Accept();

    internal void Accept()
    {
        Close();
        Accepted?.Invoke();
    }

    /// <summary>True during a fullscreen game or presentation, or with the screen locked: not the moment to pop anything up.</summary>
    internal static bool IsUserBusy() => SHQueryUserNotificationState(out int state) == 0 && IsBusyState(state);

    // QUNS_NOT_PRESENT, QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE.
    internal static bool IsBusyState(int state) => state is 1 or 2 or 3 or 4;

    /// <summary>A version pops up once by itself. Asking for it always shows it.</summary>
    internal static bool ShouldOffer(string? lastOffered, string version, bool userAsked) => userAsked || lastOffered != version;

    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);
}
