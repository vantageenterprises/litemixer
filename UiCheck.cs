using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiteMixer.Audio;

namespace LiteMixer;

/// <summary>
/// Build-time check (--ui-check &lt;dir&gt;): renders the windows offscreen to PNGs and exercises the channel logic.
/// Uses sample settings and never starts the mixer, so it reads no real settings and changes no audio.
/// </summary>
internal static class UiCheck
{
    /// <summary>--verify-update: writes the updater's verdict on a file. It never runs the file.</summary>
    public static void VerifyFile(string path, string report)
    {
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
        var expected = new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart);
        File.WriteAllText(report, SignatureCheck.Problem(path, expected) ?? "OK");
        Environment.Exit(0);
    }

    /// <summary>--update-dry-run: fetches and verifies the latest real release without running it or changing anything.</summary>
    public static async Task UpdateDryRun(Updater updater, string report)
    {
        string result;
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiteMixer-Updater");
            var json = await client.GetStringAsync($"https://api.github.com/repos/{Updater.Repository}/releases/latest");
            var release = Updater.ParseRelease(json) ?? throw new InvalidDataException("No usable installer on the latest release.");
            var path = await updater.DownloadVerifiedAsync(release);
            result = $"OK version {release.Version}, sha256 {(release.Sha256 == null ? "not provided" : "checked")}, verified {new FileInfo(path).Length} bytes";
            Updater.CleanUp();
        }
        catch (Exception ex)
        {
            result = "FAILED: " + ex.Message;
        }
        File.WriteAllText(report, result);
        Environment.Exit(0);
    }

    public static void Run(App app, string folder)
    {
        SettingsStore.ReadOnly = true;
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        void Check(string name, bool ok) => report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}");

        var settings = new AppSettings { DefaultChannel = "game" };
        settings.Channels["game"] = new ChannelSettings { Volume = 40 };
        settings.Channels["chat"] = new ChannelSettings { Volume = 55 };
        settings.Channels["media"] = new ChannelSettings { Volume = 30 };
        var model = new MixerModel(settings);
        app.SetModelForCheck(model);

        // ---- Channel logic ----
        var streaming = model.AddChannel("Streaming");
        var browser = model.AddChannel("Browser Games");
        var names = string.Join(",", model.ScrollChannels.Select(c => c.Id.StartsWith("custom-") ? c.DisplayName : c.Id));
        Check($"new channels sit left of aux, oldest first: {names}", names == "game,chat,media,Streaming,Browser Games,aux");
        Check("Master first and Mic last in the full list", model.Channels[0] == model.Master && model.Channels[^1] == model.Mic);
        Check($"new channel starts at the default channel's level ({streaming.Volume})", streaming.Volume == 40);
        Check("custom channels get different colors", streaming.Accent.ToString() != browser.Accent.ToString());
        Check("empty name refused", model.ChannelNameProblem("   ") != null);
        Check("duplicate name refused, any case", model.ChannelNameProblem("streaming") != null && model.ChannelNameProblem("GAME") != null);
        Check("renaming to its own name allowed", model.ChannelNameProblem("Streaming", streaming) == null);

        model.Assign("obs64", streaming);
        Check("app assigned to custom channel", model.ChannelOfApp("obs64") == streaming);
        model.SetDefaultChannel(browser);
        Check("unassigned apps follow a custom default", model.ChannelOfApp("newgame") == browser);
        model.SetCap(browser, 50);

        model.RenameChannel(streaming, "Stream");
        Check("rename updates the title", streaming.Title == "STREAM" && settings.CustomChannels[0].Name == "Stream");

        model.DeleteChannel(browser);
        Check("deleting the default channel falls back to Game", model.ChannelOfApp("newgame") == model.Game && settings.DefaultChannel == "game");
        Check("deleted channel's cap and settings removed", !settings.Caps.ContainsKey(browser.Id) && !settings.Channels.ContainsKey(browser.Id));
        Check("deleted channel left the lists", !model.Channels.Contains(browser) && !model.ScrollChannels.Contains(browser) && !model.OutputChannels.Contains(browser));
        model.DeleteChannel(streaming);
        Check("deleting a channel returns its apps to the default", model.ChannelOfApp("obs64") == model.Game);
        model.DeleteChannel(model.Game);
        Check("built-in channels cannot be deleted", model.Channels.Contains(model.Game));

        // Reloading from saved settings must keep channels, level and order.
        var kept = model.AddChannel("Reload Test");
        kept.Volume = 22;
        var reloaded = new MixerModel(settings);
        var again = reloaded.Channels.FirstOrDefault(c => c.DisplayName == "Reload Test");
        Check("channel survives a restart with its level and place", again?.Volume == 22 && reloaded.ScrollChannels[^1] == reloaded.Aux && reloaded.ScrollChannels[^2] == again);
        model.DeleteChannel(kept);

        // ---- Updater decisions ----
        const string goodUrl = "https://github.com/vantageenterprises/litemixer/releases/download/v1.2.0/LiteMixer_1.2.exe";
        string Release(string tag, string url) =>
            $$"""{"tag_name":"{{tag}}","body":"Notes","assets":[{"name":"LiteMixer_1.2.exe","browser_download_url":"{{url}}","digest":"sha256:ABC"}]}""";
        var parsedRelease = Updater.ParseRelease(Release("v1.2.0", goodUrl));
        Check("release parsed with version, notes and digest", parsedRelease is { Notes: "Notes", Sha256: "ABC" } && parsedRelease.Version == new Version(1, 2, 0));
        Check("download from another site refused", Updater.ParseRelease(Release("v1.2.0", "https://evil.example/LiteMixer_1.2.exe")) == null);
        Check("download from another GitHub repo refused", Updater.ParseRelease(Release("v1.2.0", "https://github.com/someone/litemixer/releases/download/v1.2.0/LiteMixer_1.2.exe")) == null);
        Check("nonsense tag ignored", Updater.ParseRelease(Release("latest", goodUrl)) == null);
        Check("release with no installer ignored", Updater.ParseRelease("""{"tag_name":"v1.2.0","assets":[]}""") == null);
        Check("newer version offered", Updater.IsNewer(new Version(1, 0, 1), new Version(1, 0, 0)));
        Check("same version not offered", !Updater.IsNewer(new Version(1, 0, 0), new Version(1, 0, 0)));
        Check("older version not offered", !Updater.IsNewer(new Version(0, 9, 0), new Version(1, 0, 0)));

        // ---- Default look: no custom channels ----
        var window = Offscreen(new MainWindow(model));
        SampleData(model);
        Pump();
        Save(window, folder, "1-default");
        Check("no scrollbar while the four standard channels fit", window.ChannelScroll.ScrollableWidth <= 0.5);
        Check("window keeps its normal size", window.ActualWidth < 1160);

        // ---- Overflow ----
        foreach (var name in new[] { "Streaming", "Browser Games", "A Really Long Name Here", "Recording" }) model.AddChannel(name);
        SampleData(model);
        Pump();
        Save(window, folder, "2-many");
        Check($"horizontal scrolling appears with extra channels (scrollable {window.ChannelScroll.ScrollableWidth:0})", window.ChannelScroll.ScrollableWidth > 100);

        var recording = model.Channels.First(c => c.DisplayName == "Recording");
        window.ChannelScroll.ScrollToHorizontalOffset(window.ChannelScroll.ScrollableWidth);
        Pump();
        Save(window, folder, "3-scrolled");
        var host = (FrameworkElement)window.ChannelList.ItemContainerGenerator.ContainerFromItem(recording);
        double x = host.TransformToAncestor(window.ChannelScroll).Transform(new Point()).X;
        Check("scrolling brings the last custom channel into view", x >= 0 && x + host.ActualWidth <= window.ChannelScroll.ViewportWidth + 1);

        var menu = window.BuildChannelMenu();
        Check($"channel menu lists every channel ({menu.Items.Count})", menu.Items.Count == model.Channels.Count);
        menu.ApplyTemplate();
        menu.Measure(new Size(300, 600));
        menu.Arrange(new Rect(menu.DesiredSize));
        Pump();
        SaveVisual(menu, (int)menu.ActualWidth, (int)menu.ActualHeight, folder, "4-channel-menu");

        var dialog = Offscreen(PromptWindow.Sample("New channel", "Name your channel. It starts at Game's level; drag apps into it from any channel.", "Add channel", "Game", "You already have a channel called Game."));
        Save(dialog, folder, "5-name-dialog");
        Check($"name dialog shows the typed text ('{dialog.Input.Text}', {dialog.Input.ActualWidth:0}x{dialog.Input.ActualHeight:0}, extent {dialog.Input.ExtentWidth:0}x{dialog.Input.ExtentHeight:0}, viewport {dialog.Input.ViewportWidth:0}x{dialog.Input.ViewportHeight:0})",
            dialog.Input.Text == "Game" && dialog.Input.ViewportHeight >= dialog.Input.ExtentHeight - 1);
        Check($"name dialog shows the problem ({dialog.Problem.Visibility})", dialog.Problem.Visibility == Visibility.Visible);
        Save(Offscreen(new CompactWindow(model)), folder, "6-compact");

        var settingsWindow = Offscreen(new SettingsWindow(model));
        settingsWindow.Height = 1500;
        Pump();
        Save(settingsWindow, folder, "7-settings");

        File.WriteAllText(Path.Combine(folder, "report.txt"), report.ToString());
        Environment.Exit(report.ToString().Contains("FAIL") ? 1 : 0);
    }

    static void SampleData(MixerModel model)
    {
        string[] apps = ["Sample Game", "Game Launcher", "Voice Chat", "Web Browser", "Recorder", "Music Player"];
        int next = 0;
        foreach (var channel in model.Channels)
        {
            channel.DeviceName = channel == model.Mic ? "Headset Microphone" : "Headphones";
            if (channel == model.Master || channel == model.Aux) continue;
            var app = apps[next++ % apps.Length];
            channel.SyncApps([new AppChip(app.ToLowerInvariant(), app, channel != model.Mic, 100, null)]);
            channel.Peak = 0.35;
        }
    }

    static T Offscreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        Pump();
        return window;
    }

    static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    static void Save(Window window, string folder, string name) =>
        SaveVisual(window, (int)window.ActualWidth, (int)window.ActualHeight, folder, name);

    static void SaveVisual(Visual visual, int width, int height, string folder, string name)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(width, 1), Math.Max(height, 1), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
}
