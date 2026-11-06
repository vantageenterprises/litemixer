using System.Diagnostics;

namespace LiteMixerSetup;

internal static class Program
{
    // Build-time checks. None of them touch the installed app, the registry or real shortcuts:
    //   --render <dir>        draws every screen to PNG files
    //   --test-install <dir>  unpacks the payload and writes a test shortcut inside <dir>
    //   --preview             opens the real window, but its buttons only simulate the work
    [STAThread]
    static int Main(string[] args)
    {
        Gfx.Startup();

        if (ArgAfter(args, "--render") is { } renderDir)
        {
            Render(renderDir);
            return 0;
        }
        if (ArgAfter(args, "--test-install") is { } testDir)
        {
            try
            {
                Installer.TestInstall(testDir);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(testDir, "error.txt"), ex.ToString());
                return 1;
            }
        }

        bool preview = args.Contains("--preview");
        bool installing = Installer.HasPayload;
        if (!installing && !preview && !args.Contains("--relocated") && Installer.RelocateUninstaller()) return 0;

        // --update: started by the app after the user agreed, so it goes straight to work and reopens the app.
        bool autoUpdate = installing && args.Contains("--update");
        bool updating = installing && Installer.IsInstalled;
        var ui = new Ui(autoUpdate ? Screen.Working : installing ? Screen.Welcome : Screen.ConfirmRemove,
            toggle: installing && (!updating || Installer.DesktopShortcutExists),
            updating, Window.ReducedMotion());

        Window? window = null;
        Task? work = null;
        double simulated = 0;
        var clock = Stopwatch.StartNew();
        bool launch = false;

        void Start(Action action, Screen success)
        {
            Installer.Progress = 0;
            work = Task.Run(() =>
            {
                try
                {
                    if (preview)
                    {
                        while (simulated < 1)
                        {
                            Thread.Sleep(30);
                            simulated += 0.008;
                            Installer.Progress = (float)Math.Min(simulated, 1);
                        }
                        simulated = 0;
                    }
                    else
                    {
                        action();
                    }
                    ui.Screen = success;
                }
                catch (Exception ex)
                {
                    ui.ErrorText = FriendlyError(ex);
                    ui.Screen = Screen.Failed;
                }
            });
        }

        void OnClick(Ui.Hit hit)
        {
            switch (hit)
            {
                case Ui.Hit.Close:
                    window!.Close();
                    return;
                case Ui.Hit.Minimize:
                    Win32.Minimize(window!.Handle);
                    return;
                case Ui.Hit.Toggle:
                    ui.ToggleOn = !ui.ToggleOn;
                    return;
                case Ui.Hit.License:
                    Process.Start(new ProcessStartInfo("https://github.com/vantageenterprises/litemixer/blob/main/LICENSE") { UseShellExecute = true });
                    return;
            }

            switch (ui.Screen)
            {
                case Screen.Welcome when hit == Ui.Hit.Primary:
                case Screen.Failed when hit == Ui.Hit.Primary && installing:
                {
                    bool desktop = ui.ToggleOn;
                    ui.Screen = Screen.Working;
                    Start(() => Installer.Install(desktop), Screen.Done);
                    break;
                }
                case Screen.Done when hit == Ui.Hit.Primary:
                    launch = true;
                    window!.Close();
                    break;
                case Screen.ConfirmRemove when hit == Ui.Hit.Primary:
                case Screen.Failed when hit == Ui.Hit.Primary && !installing:
                {
                    bool settings = ui.ToggleOn;
                    ui.Screen = Screen.Removing;
                    Start(() => Installer.Uninstall(settings), Screen.Removed);
                    break;
                }
                default:
                    window!.Close();
                    break;
            }
        }

        void OnTick()
        {
            ui.Progress = Installer.Progress;
            if (autoUpdate && ui.Screen == Screen.Done && !launch)
            {
                launch = true;
                window!.Close();
            }
        }

        if (autoUpdate)
        {
            bool desktop = Installer.DesktopShortcutExists;
            Start(() => Installer.Install(desktop), Screen.Done);
        }

        window = new Window(ui, OnClick, OnTick, preview);
        window.Run();
        _ = clock;

        if (preview) return 0;
        if (launch) Process.Start(new ProcessStartInfo(Installer.AppExe) { UseShellExecute = true });
        if (!installing) Installer.ScheduleSelfDelete();
        return 0;
    }

    static string? ArgAfter(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static string FriendlyError(Exception ex) => ex switch
    {
        UnauthorizedAccessException or IOException when ex.HResult == unchecked((int)0x80070070) =>
            "There isn't enough free disk space. Free up some room and try again.",
        UnauthorizedAccessException or IOException =>
            $"A LiteMixer file is in use or locked. Close anything using it and try again.\n\n{ex.Message}",
        _ => ex.Message,
    };

    static void Render(string folder)
    {
        Directory.CreateDirectory(folder);
        const int scale = 2;

        void Save(string name, Screen screen, double progress = 0, bool updating = false, bool toggle = false, string error = "")
        {
            var ui = new Ui(screen, toggle, updating, reducedMotion: false) { Progress = progress, ErrorText = error };
            ui.Settle(7.5);
            Gfx.RenderToPng(Ui.Width * scale, Ui.Height * scale, scale, ui.Draw, Path.Combine(folder, name + ".png"));
        }

        Save("1-welcome", Screen.Welcome, toggle: true);
        Save("2-update", Screen.Welcome, updating: true);
        Save("3-installing", Screen.Working, progress: 0.62);
        Save("4-done", Screen.Done);
        Save("5-failed", Screen.Failed, error: "A LiteMixer file is in use or locked. Close anything using it and try again.");
        Save("6-remove", Screen.ConfirmRemove);
        Save("7-removing", Screen.Removing, progress: 0.4);
        Save("8-removed", Screen.Removed);
    }
}

internal static partial class Win32
{
    public static void Minimize(nint hwnd) => ShowWindow(hwnd, 6 /* SW_MINIMIZE */);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);
}
