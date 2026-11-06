using System.Text;
using System.Windows;
using System.Windows.Input;
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
        AppRouting.ReadOnly = true;
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
        Check($"new channels open right of aux, oldest first: {names}", names == "game,chat,media,aux,Streaming,Browser Games");
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
        Check("channel survives a restart with its level and place", again?.Volume == 22 && reloaded.ScrollChannels[^1] == again && reloaded.ScrollChannels[^2] == reloaded.Aux);
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
        var withStableLink = Updater.ParseRelease($$"""{"tag_name":"v1.2.0","assets":[{"name":"LiteMixer-Setup.exe","browser_download_url":"https://github.com/vantageenterprises/litemixer/releases/download/v1.2.0/LiteMixer-Setup.exe"},{"name":"LiteMixer_1.2.exe","browser_download_url":"{{goodUrl}}"}]}""");
        Check("updater ignores the fixed-name download", withStableLink?.AssetUrl == goodUrl);
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
        settingsWindow.Height = 2300;
        Pump();
        Save(settingsWindow, folder, "7-settings");

        Check("Media and Aux now read Video and Music", model.Media.Title == "VIDEO" && model.Media.DisplayName == "Video" && model.Aux.Title == "MUSIC" && model.Aux.DisplayName == "Music");
        Check("Video and Music keep their saved ids", model.Media.Id == "media" && model.Aux.Id == "aux");
        CheckMovingChannels(app, Check, folder);
        CheckChannelLooks(app, Check, folder);
        CheckAppSorting(app, Check);
        CheckUpdatePrompt(Check, folder);

        CheckStreamMix(app, Check);

        // The quick check in the menus.
        var appMenu = new System.Windows.Controls.ContextMenu();
        app.AddAppItems(appMenu);
        var headers = appMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => i.Header as string).ToList();
        Check("the menu has Check for updates, above Exit", headers.Contains("Check for updates") && headers.IndexOf("Check for updates") < headers.IndexOf("Exit LiteMixer"));
        var told = PromptWindow.InformWindow(null, "Check for updates", "You're up to date (1.3).");
        Check("an up-to-date answer has one button", told.CancelButton.Visibility == Visibility.Collapsed);
        Save(Offscreen(told), folder, "11-up-to-date");
        told.Close();

        File.WriteAllText(Path.Combine(folder, "report.txt"), report.ToString());
        Environment.Exit(report.ToString().Contains("FAIL") ? 1 : 0);
    }

    static void CheckAppSorting(App app, Action<string, bool> check)
    {
        // The rules, with fixed lists so the result doesn't depend on this PC.
        string[] folders = [@"H:\Stuff\MyEpicGame", @"H:\Stuff\MyGogGame\"];
        string[] windows = [@"E:\Blizzard\Wow.exe", @"D:\Fortnite\Fortnite.exe", @"C:\Runtime\bin\javaw.exe", "\"F:\\Quoted\\Quoted.exe\""];
        string? Sort(string key, string? path) => AppSorter.Suggest(key, path, () => folders, () => windows);

        check("chat apps go to Chat", Sort("discord", @"C:\Users\x\AppData\Local\Discord\app-1\Discord.exe") == "chat");
        check("music apps go to Music", Sort("spotify", @"C:\Apps\Spotify.exe") == "aux");
        check("video apps go to Video", Sort("vlc", @"C:\Program Files\VideoLAN\VLC\vlc.exe") == "media");
        check("browsers go to Video", Sort("chrome", null) == "media" && Sort("msedge", null) == "media" && Sort("firefox", null) == "media");
        check("game launchers go to Game", Sort("steam", @"C:\Steam\steam.exe") == "game" && Sort("steamwebhelper", null) == "game");
        check("a known app wins over the folder it's in", Sort("chrome", @"D:\SteamLibrary\steamapps\common\Odd\chrome.exe") == "media");

        check("Steam games on any drive", Sort("odd", @"Z:\Lib\SteamLibrary\steamapps\common\Odd Game\bin\odd.exe") == "game");
        check("folder matching ignores case and slash style", Sort("odd", "z:/LIB/STEAMAPPS/COMMON/Odd/odd.exe") == "game");
        check("Epic games", Sort("odd", @"D:\Epic Games\Odd\Binaries\odd.exe") == "game");
        check("Epic's launcher and engine aren't games", Sort("helper", @"C:\Program Files\Epic Games\Launcher\helper.exe") == null && Sort("editor", @"C:\Program Files\Epic Games\UE_5.4\Engine\editor.exe") == null);
        check("GOG games", Sort("odd", @"D:\GOG Games\Odd\odd.exe") == "game" && Sort("odd", @"C:\Program Files (x86)\GOG Galaxy\Games\Odd\odd.exe") == "game");
        check("Epic or GOG games installed elsewhere", Sort("odd", @"H:\Stuff\MyEpicGame\bin\odd.exe") == "game" && Sort("odd", @"h:\stuff\mygoggame\odd.exe") == "game");
        check("a folder only counts as a whole name", Sort("odd", @"H:\Stuff\MyEpicGameTools\odd.exe") == null);
        check("Steam's own folder isn't a library", Sort("odd", @"C:\Program Files (x86)\Steam\bin\odd.exe") == null);

        check("games Windows recognizes", Sort("wow", @"E:\Blizzard\Wow.exe") == "game" && Sort("fortnite", @"d:\fortnite\FORTNITE.EXE") == "game");
        check("a quoted entry in the Windows list still matches", Sort("quoted", @"F:\Quoted\Quoted.exe") == "game");
        check("same exe name in another folder isn't that game", Sort("wow", @"C:\Tools\wow.exe") == null);
        check("a game hiding its path matches by name", Sort("wow", null) == "game");
        check("generic runtimes aren't games, even when listed", Sort("javaw", @"C:\Runtime\bin\javaw.exe") == null && Sort("javaw", null) == null);
        check("unknown apps get no suggestion", Sort("somethingelse", @"C:\Program Files\Other\somethingelse.exe") == null && Sort("somethingelse", null) == null);

        // Reading the real lists must never throw, and only reads.
        check($"reads Windows' game list ({AppSorter.ReadWindowsGames().Count} games) and install folders ({AppSorter.ReadGameFolders().Count})", true);

        // In the mixer.
        var settings = new AppSettings();
        settings.Channels["game"] = new ChannelSettings { Volume = 40 };
        var model = new MixerModel(settings);
        app.SetModelForCheck(model);
        int audioCalls = 0;
        foreach (var channel in model.Channels)
        {
            channel.VolumeChanged = _ => audioCalls++;
            channel.MutedChanged = _ => audioCalls++;
        }

        model.SortApp("discord", null);
        model.SortApp("spotify", null);
        model.SortApp("vlc", null);
        model.SortApp("odd", @"D:\SteamLibrary\steamapps\common\Odd\odd.exe");
        model.SortApp("mystery", null);
        check("sorted apps land in their channels", model.ChannelOfApp("discord") == model.Chat && model.ChannelOfApp("spotify") == model.Aux && model.ChannelOfApp("vlc") == model.Media && model.ChannelOfApp("odd") == model.Game);
        check("anything else opens in the default", model.ChannelOfApp("mystery") == model.Game);
        model.SetDefaultChannel(model.Media);
        check("the default only catches what sorting didn't", model.ChannelOfApp("mystery") == model.Media && model.ChannelOfApp("odd") == model.Game && model.ChannelOfApp("discord") == model.Chat);
        model.SetDefaultChannel(model.Game);
        check("sorting isn't saved as the user's choice", settings.Assignments.Count == 0);

        model.Assign("discord", model.Media);
        check("an app you place stays placed", model.ChannelOfApp("discord") == model.Media);
        model.SortApp("discord", null);
        check("sorting never overrides your choice", model.ChannelOfApp("discord") == model.Media && settings.Assignments["discord"] == "media");

        settings.Assignments["spotify"] = "chat";
        model.UnassignChannel(model.Chat);
        check("moving a channel's apps to the default sticks", model.ChannelOfApp("spotify") == model.Game && settings.Assignments["spotify"] == "game");

        model.ResetAssignments();
        check("resetting returns apps to sorted channels", settings.Assignments.Count == 0 && model.ChannelOfApp("discord") == model.Chat && model.ChannelOfApp("spotify") == model.Aux);

        model.AutoSortApps = false;
        check("with sorting off, everything uses the default", model.ChannelOfApp("discord") == model.Game && model.ChannelOfApp("spotify") == model.Game && model.Master.EmptyHint.StartsWith("New apps and games open"));
        model.SortApp("telegram", null);
        check("with sorting off, nothing new is sorted", model.ChannelOfApp("telegram") == model.Game);
        model.AutoSortApps = true;
        check("turning sorting back on restores it", model.ChannelOfApp("discord") == model.Chat && model.Master.EmptyHint.StartsWith("New apps sort themselves"));

        var turnedOff = new MixerModel(new AppSettings { AutoSortApps = false });
        check("the off switch survives a restart", !turnedOff.AutoSortApps && turnedOff.Master.EmptyHint.StartsWith("New apps and games open"));
        check("older settings turn sorting on", new AppSettings().AutoSortApps && System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.AutoSortApps);

        // Nothing may ever land in Master, or in Mic, whatever the settings say.
        bool neverMasterOrMic = true;
        foreach (var badDefault in new[] { "master", "mic", "gone", "", null! })
        {
            var bad = new AppSettings { DefaultChannel = badDefault };
            bad.Assignments["a"] = "master";
            bad.Assignments["b"] = "mic";
            bad.Assignments["c"] = "gone";
            var damaged = new MixerModel(bad);
            foreach (var key in new[] { "a", "b", "c", "unassigned", "#system" })
                neverMasterOrMic &= damaged.ChannelOfApp(key) == damaged.Game;
            neverMasterOrMic &= damaged.DefaultChannel == damaged.Game;
        }
        check("bad settings can't put an app in Master or Mic", neverMasterOrMic);

        model.Assign("x", model.Master);
        model.Assign("x", model.Mic);
        check("apps can't be moved into Master or Mic", !settings.Assignments.ContainsKey("x") && model.ChannelOfApp("x") == model.Game);
        model.SetDefaultChannel(model.Master);
        model.SetDefaultChannel(model.Mic);
        check("Master and Mic can't be the default", model.DefaultChannel == model.Game && settings.DefaultChannel == "game");
        check("every app resolves to an output channel", new[] { "discord", "spotify", "vlc", "odd", "mystery", "x", "#system" }.All(k => model.ChannelOfApp(k)?.Kind == ChannelKind.Output));
        check("Master's app list stays empty", model.Master.Apps.Count == 0);
        check("sorting never touches channel levels", audioCalls == 0);

        // Apps using the microphone.
        check("an app using the mic is sent to the Mic channel's device", model.ShouldFollowMic(4242));
        check("only once per run of that app", !model.ShouldFollowMic(4242));
        check("process 0 is left alone", !model.ShouldFollowMic(0));
        model.AutoSortApps = false;
        check("with sorting off, mic apps are left alone", !model.ShouldFollowMic(5151));
        model.AutoSortApps = true;
        check("turning sorting back on applies again", model.ShouldFollowMic(4242) && model.ShouldFollowMic(5151));
    }

    static void CheckStreamMix(App app, Action<string, bool> check)
    {
        // Personal levels with the stream mix on: nothing audible may ever rise.
        check("a channel at zero sits at the inaudible floor", StreamMath.PersonalLevel(0, false) == (StreamMath.Floor, false));
        check("a muted channel sits at the floor too, so the stream still gets it", StreamMath.PersonalLevel(0.5, true) == (StreamMath.Floor, false));
        check("the floor is -100 dB", Math.Abs(20 * Math.Log10(StreamMath.Floor) + 100) < 0.01);
        check("a normal level is left exactly as it is", StreamMath.PersonalLevel(0.4, false) == (0.4, false));
        bool neverLouder = true;
        for (double level = 0; level <= 1; level += 0.0005)
        {
            neverLouder &= StreamMath.PersonalLevel(level, false).Level <= Math.Max(level, StreamMath.Floor);
            neverLouder &= StreamMath.PersonalLevel(level, true).Level <= StreamMath.Floor;
        }
        check("the stream mix never makes anything louder for you", neverLouder);
        check("muting drops the buffered audio instead of popping", StreamMath.IsGainJump(2, 100000));
        check("moving a fader doesn't drop any audio", !StreamMath.IsGainJump(2, 2.5f) && !StreamMath.IsGainJump(2, 1));
        check("a source starting up isn't a jump", !StreamMath.IsGainJump(0, 100));

        // Gains.
        check("the gain undoes the personal level", Math.Abs(StreamMath.Gain(1, 0.5) - 2) < 0.0001 && Math.Abs(StreamMath.Gain(0.5, 0.5) - 1) < 0.0001);
        check("a stream level of zero is silent", StreamMath.Gain(0, 0.5) == 0);
        check("the gain has a ceiling", StreamMath.Gain(1, StreamMath.Floor / 1000) == StreamMath.MaxGain);
        check("a session at nothing gives no gain", StreamMath.Gain(1, 0) == 0);

        // Which apps are captured.
        check("broadcast apps are left out, so they can't feed back", new[] { "obs64", "streamlabs desktop", "streamlabs obs", "xsplit.core" }.All(StreamMath.IsBroadcastApp));
        check("normal apps are included", !StreamMath.IsBroadcastApp("spotify") && !StreamMath.IsBroadcastApp("discord"));
        var parents = new Dictionary<uint, uint> { [20] = 10, [30] = 20, [40] = 5, [10] = 1, [50] = 50, [60] = 70, [70] = 60 };
        var targets = StreamMath.CaptureTargets([10, 30, 40], parents);
        check("a launcher isn't captured along with its game", targets.SetEquals([30, 40]));
        check("process 0 is never captured", !StreamMath.CaptureTargets([0, 40], parents).Contains(0u));
        check("a process listing itself as parent can't hang it, and stays", StreamMath.CaptureTargets([50], parents).SetEquals([50]));
        check("a parent loop can't hang it", StreamMath.CaptureTargets([60], parents).Count <= 1);

        // Which devices are allowed.
        string[] connected = ["headset", "speakers", "cable"];
        check("a device you listen on is refused", StreamMath.DeviceProblem("headset", connected, ["headset"]) != null);
        check("a cable is accepted", StreamMath.DeviceProblem("cable", connected, ["headset"]) == null);
        check("no device is reported", StreamMath.DeviceProblem(null, connected, []) != null);
        check("a missing device is reported", StreamMath.DeviceProblem("gone", connected, []) != null);
        check("cables are recognized", StreamMath.LooksLikeCable("CABLE Input (VB-Audio Virtual Cable)") && StreamMath.LooksLikeCable("Voicemeeter Input"));
        check("real outputs aren't taken for cables", !StreamMath.LooksLikeCable("Headphones (Wireless Headset)") && !StreamMath.LooksLikeCable("Speakers (Realtek Audio)"));
        check("a cable's stereo input comes before its 16 channel one", StreamMath.CableRank("CABLE Input (VB-Audio Virtual Cable)") < StreamMath.CableRank("CABLE In 16ch (VB-Audio Virtual Cable)"));
        check("a 16 channel cable still comes before a real output", StreamMath.CableRank("CABLE In 16ch (VB-Audio Virtual Cable)") < StreamMath.CableRank("Speakers (Realtek Audio)"));

        // In the mixer, with no devices and no sessions.
        var settings = new AppSettings();
        check("the stream mix is off by default", !settings.StreamMixEnabled);
        var model = new MixerModel(settings);
        app.SetModelForCheck(model);
        int audioCalls = 0;
        foreach (var channel in model.Channels)
        {
            channel.VolumeChanged = _ => audioCalls++;
            channel.MutedChanged = _ => audioCalls++;
        }
        check("stream levels start at 100%", model.StreamLevel(model.Game) == 100);
        model.SetStreamLevel(model.Game, 35);
        check("a stream level is saved", model.StreamLevel(model.Game) == 35 && settings.StreamLevels["game"] == 35);
        check("stream levels don't move your faders", model.Game.Volume == 100 && audioCalls == 0);
        model.SetStreamLevel(model.Game, 100);
        check("100% stores nothing", !settings.StreamLevels.ContainsKey("game"));
        model.StreamMixEnabled = true;
        check("turning it on with no device picks nothing and says so", settings.StreamDeviceId == null && model.StreamStatus.Length > 0);
        model.StreamMixEnabled = false;
        check("turning it off clears the message", model.StreamStatus.Length == 0);
        var custom = model.AddChannel("Stream Only");
        model.SetStreamLevel(custom, 20);
        model.DeleteChannel(custom);
        check("deleting a channel removes its stream level", settings.StreamLevels.Count == 0);
        var saved = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(new AppSettings { StreamMixEnabled = true, StreamDeviceId = "x", StreamLevels = { ["chat"] = 10 } }))!;
        check("stream settings survive a restart", saved.StreamMixEnabled && saved.StreamDeviceId == "x" && saved.StreamLevels["chat"] == 10);
        check("older settings leave it off", !System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.StreamMixEnabled);
    }

    static void CheckUpdatePrompt(Action<string, bool> check, string folder)
    {
        check("a new version offers itself", UpdateWindow.ShouldOffer("1.1", "1.2", userAsked: false) && UpdateWindow.ShouldOffer(null, "1.2", userAsked: false));
        check("each version offers itself only once", !UpdateWindow.ShouldOffer("1.2", "1.2", userAsked: false));
        check("asking always shows it", UpdateWindow.ShouldOffer("1.2", "1.2", userAsked: true));
        check("waits during fullscreen games and presentations", UpdateWindow.IsBusyState(2) && UpdateWindow.IsBusyState(3) && UpdateWindow.IsBusyState(4));
        check("waits while the screen is locked", UpdateWindow.IsBusyState(1));
        check("shows when the desktop is free", !UpdateWindow.IsBusyState(5) && !UpdateWindow.IsBusyState(6) && !UpdateWindow.IsBusyState(7));

        var window = new UpdateWindow("1.2", "1.1", "Apps now sort themselves. Games go to Game, and common chat, music and video apps go to their channels.\n\nUpdates now show up here instead of as a Windows notification.");
        check("popping up doesn't take the keyboard", !window.ShowActivated);
        check("it stays above other windows", window.Topmost);
        check("the notes show", window.Notes.Text.StartsWith("Apps now sort"));
        Offscreen(window);
        Pump();
        Shot(window, folder, "10-update");
        int accepted = 0;
        window.Accepted += () => accepted++;
        window.Close();
        check("closing is not agreeing", accepted == 0);

        var agreed = Offscreen(new UpdateWindow("1.2", "1.1", ""));
        agreed.Accepted += () => accepted++;
        check("no notes means no empty box", agreed.NotesBox.Visibility == Visibility.Collapsed);
        agreed.Accept();
        check("Update now agrees once, and closes", accepted == 1 && !agreed.IsVisible);

        var longNotes = new UpdateWindow("1.2", "1.1", new string('x', 900));
        check("very long notes are cut off", longNotes.Notes.Text.Length == 603 && longNotes.Notes.Text.EndsWith("..."));
        longNotes.Close();
    }

    static void CheckChannelLooks(App app, Action<string, bool> check, string folder)
    {
        var settings = new AppSettings { CustomChannels = [new() { Id = "custom-a", Name = "Alpha", Color = "#22D3EE" }] };
        var model = new MixerModel(settings);
        app.SetModelForCheck(model);
        var game = model.Game;
        var startGlyph = game.Glyph;
        var startColor = game.AccentColor;

        // Every icon must exist in the icon fonts of both Windows 10 and 11, or it would draw as an empty box.
        foreach (var family in new[] { "Segoe MDL2 Assets", "Segoe Fluent Icons" })
        {
            var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyphs) || !glyphs.FamilyNames.Values.Contains(family)) continue;
            var missing = ChannelLookWindow.Icons.Where(i => !glyphs.CharacterToGlyphMap.ContainsKey(i[0])).Select(i => $"{(int)i[0]:X4}").ToList();
            check($"all icons exist in {family} (missing: {string.Join(" ", missing)})", missing.Count == 0);
        }
        check("the icon list has no duplicates", ChannelLookWindow.Icons.Distinct().Count() == ChannelLookWindow.Icons.Length);

        // Color math round trips, so the picker lands on the color it was given.
        bool roundTrips = true;
        foreach (var hex in new[] { "#1FCB9C", "#3BA4F0", "#F2468E", "#9D5CF5", "#F5A623", "#000000", "#FFFFFF", "#808080", "#FF0000", "#FF00FF" })
        {
            var color = ChannelVM.ParseColor(hex)!.Value;
            var (h, s, v) = ChannelLookWindow.ToHsv(color);
            var back = ChannelLookWindow.FromHsv(h, s, v);
            roundTrips &= Math.Abs(back.R - color.R) <= 1 && Math.Abs(back.G - color.G) <= 1 && Math.Abs(back.B - color.B) <= 1;
        }
        check("the color picker math round trips", roundTrips);
        check("hex reads with or without #", ChannelVM.ParseColor("#FF8000") == ChannelVM.ParseColor("ff8000") && ChannelVM.ParseColor("FF8000") != null);
        check("bad hex is refused", ChannelVM.ParseColor("nope") == null && ChannelVM.ParseColor("#12345") == null && ChannelVM.ParseColor("GGGGGG") == null && ChannelVM.ParseColor(null) == null);
        check("near-black is flagged, normal colors aren't", ChannelLookWindow.IsHardToRead(Colors.Black) && !ChannelLookWindow.IsHardToRead(startColor));

        int audioCalls = 0, listChanges = 0;
        foreach (var channel in model.Channels)
        {
            channel.VolumeChanged = _ => audioCalls++;
            channel.MutedChanged = _ => audioCalls++;
        }
        model.ChannelsChanged += () => listChanges++;

        // The dialog: live changes, then cancel.
        var red = ChannelVM.ParseColor("#FF2020")!.Value;
        var dialog = Offscreen(new ChannelLookWindow(model, game, null));
        Pump();
        dialog.SetColor(red);
        dialog.SetGlyph(ChannelLookWindow.Icons[5]);
        check("changes show on the channel right away", game.AccentColor == red && game.Glyph == ChannelLookWindow.Icons[5]);
        check("the hex box follows the choice", dialog.HexInput.Text == "#FF2020");
        check("nothing is saved before Save", settings.Looks.Count == 0 && listChanges == 0);
        Pump();
        Shot(dialog, folder, "8-color-and-icon");
        dialog.Close();
        check("Cancel puts the old look back", game.AccentColor == startColor && game.Glyph == startGlyph && settings.Looks.Count == 0);

        // Picking from the color areas and typing hex.
        dialog = Offscreen(new ChannelLookWindow(model, game, null));
        Pump();
        dialog.SetHue(0);
        dialog.SetShade(1, 0);
        check($"top right of the square at hue 0 is pure red ({ChannelVM.ToHex(game.AccentColor)})", game.AccentColor == Color.FromRgb(255, 0, 0));
        dialog.SetShade(0, 0);
        check($"top left is white ({ChannelVM.ToHex(game.AccentColor)})", game.AccentColor == Colors.White);
        dialog.SetShade(0.5, 1);
        check($"the bottom is black ({ChannelVM.ToHex(game.AccentColor)})", game.AccentColor == Colors.Black);
        dialog.SetShade(5, -5);
        check("positions past the edge stay in range", game.AccentColor == Color.FromRgb(255, 0, 0));
        dialog.HexInput.Text = "#123ABC";
        check($"typing a hex code sets the color ({ChannelVM.ToHex(game.AccentColor)})", game.AccentColor == ChannelVM.ParseColor("#123ABC"));
        dialog.HexInput.Text = "#12";
        check("a half-typed hex code changes nothing", game.AccentColor == ChannelVM.ParseColor("#123ABC"));

        // Save.
        dialog.SetColor(red);
        dialog.SetGlyph(ChannelLookWindow.Icons[5]);
        dialog.Save();
        check("Save keeps the look and stores it", game.AccentColor == red && settings.Looks["game"].Color == "#FF2020" && settings.Looks["game"].Glyph == ChannelLookWindow.Icons[5] && listChanges == 1);
        var chip = ChannelVM.ParseColor("#FF2020")!.Value;
        check("Game's chips follow its color", ((SolidColorBrush)game.ChipBrush).Color == chip);

        var white = ChannelVM.ParseColor("#FFFFFF")!.Value;
        model.SaveLook(model.Media, white, model.Media.DefaultGlyph);
        check("light colors get dark chip text", ((SolidColorBrush)model.Media.ChipForeground).Color != Colors.White);
        model.SaveLook(model.Media, Color.FromRgb(20, 20, 60), model.Media.DefaultGlyph);
        check("dark colors get light chip text", ((SolidColorBrush)model.Media.ChipForeground).Color == Colors.White);
        model.SaveLook(model.Media, model.Media.DefaultColor, model.Media.DefaultGlyph);
        check("the default look stores nothing", !settings.Looks.ContainsKey("media"));

        // Master and Mic take a look too; Master's chips stay grey because they mean "not routed".
        model.SaveLook(model.Master, red, model.Master.Glyph);
        model.SaveLook(model.Mic, red, ChannelLookWindow.Icons[7]);
        check("Master and Mic can be customized", model.Master.AccentColor == red && model.Mic.AccentColor == red && model.Mic.Glyph == ChannelLookWindow.Icons[7]);
        check("Master's unrouted chips stay grey", ((SolidColorBrush)model.Master.ChipBrush).Color != red);

        // A custom channel can change away from its starting color and back.
        var custom = model.Channels.First(c => c.Id == "custom-a");
        model.SaveLook(custom, red, ChannelLookWindow.Icons[9]);
        var restored = new MixerModel(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!);
        var restoredCustom = restored.Channels.First(c => c.Id == "custom-a");
        check("looks survive a restart", restored.Game.AccentColor == red && restored.Game.Glyph == ChannelLookWindow.Icons[5] && restoredCustom.AccentColor == red && restoredCustom.Glyph == ChannelLookWindow.Icons[9]);

        // Reset goes back to the defaults, and stale data is harmless.
        dialog = Offscreen(new ChannelLookWindow(model, game, null));
        Pump();
        dialog.SetColor(game.DefaultColor);
        dialog.SetGlyph(game.DefaultGlyph);
        dialog.Save();
        check("resetting removes the saved look", game.AccentColor == startColor && game.Glyph == startGlyph && !settings.Looks.ContainsKey("game"));

        var damaged = new AppSettings();
        damaged.Looks["chat"] = new ChannelLook { Color = "not a color", Glyph = "" };
        damaged.Looks["gone"] = new ChannelLook { Color = "#FF0000" };
        var survivor = new MixerModel(damaged);
        check("a damaged saved look falls back to the default", survivor.Chat.AccentColor == survivor.Chat.DefaultColor && survivor.Chat.Glyph == survivor.Chat.DefaultGlyph);

        model.DeleteChannel(custom);
        check("deleting a channel removes its look", !settings.Looks.ContainsKey("custom-a"));
        check("changing looks never touches audio", audioCalls == 0);
    }

    static void CheckMovingChannels(App app, Action<string, bool> check, string folder)
    {
        // Saved layouts: an upgrade keeps what the user had, and a damaged order loses nothing.
        var customs = new List<CustomChannel> { new() { Id = "custom-a", Name = "Alpha" }, new() { Id = "custom-b", Name = "Beta" } };
        static string Ids(MixerModel m) => string.Join(",", m.ScrollChannels.Select(c => c.Id));
        check("a copy upgrading from 1.0 keeps its layout",
            Ids(new MixerModel(new AppSettings { CustomChannels = customs })) == "game,chat,media,custom-a,custom-b,aux");
        var repaired = new MixerModel(new AppSettings { CustomChannels = customs, ChannelOrder = ["aux", "gone", "aux", "custom-b"] });
        check($"a damaged saved order loses no channels ({Ids(repaired)})", Ids(repaired) == "aux,custom-b,game,chat,media,custom-a");

        var settings = new AppSettings { CustomChannels = customs };
        settings.Channels["game"] = new ChannelSettings { Volume = 40 };
        settings.Channels["chat"] = new ChannelSettings { Volume = 55 };
        var model = new MixerModel(settings);
        app.SetModelForCheck(model);
        var window = Offscreen(new MainWindow(model));
        Pump();

        int audioCalls = 0, listChanges = 0;
        foreach (var channel in model.Channels)
        {
            channel.VolumeChanged = _ => audioCalls++;
            channel.MutedChanged = _ => audioCalls++;
        }
        model.ChannelsChanged += () => listChanges++;
        var levels = model.Channels.ToDictionary(c => c, c => c.Volume);

        var game = model.Game;
        var host = (FrameworkElement)window.ChannelList.ItemContainerGenerator.ContainerFromItem(game);
        check($"column spacing matches the layout ({host.ActualWidth + host.Margin.Right})", host.ActualWidth + host.Margin.Right == MainWindow.ColumnPitch);

        check("Master cannot be moved", !window.BeginMove(model.Master, 10, 10));
        check("Mic cannot be moved", !window.BeginMove(model.Mic, 10, 10));

        // A click with no hold and no movement changes nothing.
        window.BeginMove(game, 90, 90);
        window.UpdateMove(91, 91, 0.01, held: false);
        check("a plain click doesn't pick the channel up", !window.IsMoveLifted);
        window.EndMove(keep: true);
        check("a plain click doesn't change or save the order", Ids(model) == "game,chat,media,custom-a,custom-b,aux" && settings.ChannelOrder.Count == 0);

        window.BeginMove(game, 90, 90);
        window.UpdateMove(90, 90, 0.01, held: true);
        check("holding still picks the channel up", window.IsMoveLifted);
        window.EndMove(keep: false);

        // Drag Game two places right. Its center passes each neighbor's midpoint.
        window.BeginMove(game, 90, 90);
        window.UpdateMove(90 + MainWindow.ColumnPitch * 2, 300, 0.01, held: false);
        Pump();
        check($"dragging moves the channel live ({Ids(model)})", Ids(model) == "chat,media,game,custom-a,custom-b,aux");
        check("the column survives the move", window.ChannelList.ItemContainerGenerator.ContainerFromItem(game) == host);
        check("every list agrees on the order", model.Channels[0] == model.Master && model.Channels[^1] == model.Mic
            && model.Channels.Skip(1).Take(model.ScrollChannels.Count).SequenceEqual(model.ScrollChannels)
            && model.OutputChannels.SequenceEqual(model.ScrollChannels)
            && model.CappableChannels.Skip(1).SequenceEqual(model.ScrollChannels));
        check("nothing is saved until the drop", settings.ChannelOrder.Count == 0 && listChanges == 0);
        // Part way between two places, to see the lifted column over its neighbors.
        window.UpdateMove(90 + MainWindow.ColumnPitch * 1.8, 300, 0.01, held: false);
        System.Threading.Thread.Sleep(220);
        Pump();
        Shot(window, folder, "9-moving");
        window.UpdateMove(90 + MainWindow.ColumnPitch * 2, 300, 0.01, held: false);

        window.EndMove(keep: false);
        Pump();
        check("cancelling puts the channel back", Ids(model) == "game,chat,media,custom-a,custom-b,aux" && settings.ChannelOrder.Count == 0);

        window.BeginMove(game, 90, 90);
        window.UpdateMove(90 + MainWindow.ColumnPitch * 2, 300, 0.01, held: false);
        window.EndMove(keep: true);
        Pump();
        check("dropping saves the order once", settings.ChannelOrder.SequenceEqual(["chat", "media", "game", "custom-a", "custom-b", "aux"]) && listChanges == 1);
        check($"balance follows Game and Chat ({window.BalanceLeft})", window.BalanceLeft == 0);
        model.MoveChannel(model.Chat, 3);
        check($"balance sits under the further-left of the two ({window.BalanceLeft})", window.BalanceLeft == 1 * MainWindow.ColumnPitch);
        model.MoveChannel(model.Chat, 0);

        // Dragging past either end stops at the end.
        window.BeginMove(game, MainWindow.ColumnPitch * 2 + 90, 300);
        window.UpdateMove(-5000, 300, 0.01, held: false);
        check($"dragging past the left end stops there ({Ids(model)})", model.ScrollChannels[0] == game);
        window.UpdateMove(5000, 300, 0.01, held: false);
        check($"dragging past the right end stops there ({Ids(model)})", model.ScrollChannels[^1] == game);
        window.EndMove(keep: false);

        check("moving channels never touches audio", audioCalls == 0 && model.Channels.All(c => levels[c] == c.Volume));

        var restored = new MixerModel(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!);
        check($"the order survives a restart ({Ids(restored)})", Ids(restored) == "chat,media,game,custom-a,custom-b,aux");

        // Auto-scroll.
        double width = window.ChannelScroll.ViewportWidth;
        check("no auto-scroll in the middle", MainWindow.AutoScrollSpeed(width / 2, width) == 0);
        check("auto-scrolls left near the left edge", MainWindow.AutoScrollSpeed(10, width) < 0);
        check("auto-scrolls right near the right edge", MainWindow.AutoScrollSpeed(width - 10, width) > 0);
        check("past the edge is full speed, not faster", MainWindow.AutoScrollSpeed(-500, width) == -900 && MainWindow.AutoScrollSpeed(width + 500, width) == 900);

        double scrollStart = window.ChannelScroll.HorizontalOffset;
        var near = model.ScrollChannels[0];
        window.BeginMove(near, 10, width - 10);
        window.UpdateMove(10, width - 10, 0.1, held: true);
        Pump();
        check("grabbing near an edge doesn't scroll", window.ChannelScroll.HorizontalOffset == scrollStart);
        window.UpdateMove(40, width + 5, 0.1, held: true);
        Pump();
        check($"moving toward the edge scrolls ({window.ChannelScroll.HorizontalOffset:0})", window.ChannelScroll.HorizontalOffset > scrollStart);
        window.EndMove(keep: false);
        check("the mouse cursor is restored", Mouse.OverrideCursor == null);
    }

    /// <summary>--screenshots: renders the README images from sample settings. Never starts the mixer.</summary>
    public static void Screenshots(App app, string folder)
    {
        SettingsStore.ReadOnly = true;
        AppRouting.ReadOnly = true;
        Directory.CreateDirectory(folder);

        // Levels load from sample settings: the fader setters are wired to the live volume logic.
        MixerModel NewModel(params (string Id, string Name, string Color, double Volume)[] customs)
        {
            var settings = new AppSettings { DefaultChannel = "game" };
            foreach (var (id, volume) in new[] { ("game", 65.0), ("chat", 85.0), ("media", 45.0), ("aux", 60.0) })
                settings.Channels[id] = new ChannelSettings { Volume = volume };
            foreach (var custom in customs)
            {
                settings.CustomChannels.Add(new CustomChannel { Id = custom.Id, Name = custom.Name, Color = custom.Color });
                settings.Channels[custom.Id] = new ChannelSettings { Volume = custom.Volume };
            }
            if (customs.Length > 0)
            {
                settings.ChannelOrder = ["game", "chat", "media", "aux", .. customs.Select(c => c.Id)];
                settings.Looks["custom-streaming"] = new ChannelLook { Glyph = "" };
                settings.Looks["custom-podcast"] = new ChannelLook { Glyph = "" };
                settings.Looks["custom-recording"] = new ChannelLook { Glyph = "" };
            }
            var created = new MixerModel(settings);
            app.SetModelForCheck(created);
            return created;
        }

        void Fill(MixerModel model)
        {
            foreach (var channel in model.Channels)
            {
                channel.DeviceName = channel == model.Mic ? "Headset Microphone" : channel == model.Chat ? "Headset" : "Headphones";
                string[] apps = channel == model.Game ? ["Racing Game", "Game Launcher"]
                    : channel == model.Chat ? ["Voice Chat"]
                    : channel == model.Media ? ["Web Browser", "Video Player"]
                    : channel == model.Aux ? ["Music Player"]
                    : channel == model.Mic ? ["Voice Chat"]
                    : channel.DisplayName == "Streaming" ? ["Stream Alerts"]
                    : channel.DisplayName == "Podcast" ? ["Podcast Player"]
                    : channel.DisplayName == "Recording" ? ["Recorder"]
                    : [];
                if (channel != model.Master)
                    channel.SyncApps(apps.Select(name => new AppChip(name.ToLowerInvariant(), name, channel != model.Mic, 100, null)).ToList());
                // The real meter is post-fader, so it always sits below the fader.
                channel.Peak = apps.Length > 0 || channel == model.Master ? channel.Volume / 100.0 * 0.7 : 0;
            }
        }

        var standard = NewModel();
        var window = Offscreen(new MainWindow(standard));
        Fill(standard);
        Pump();
        Shot(window, folder, "mixer");
        Shot(Offscreen(new CompactWindow(standard)), folder, "compact");
        var settingsWindow = Offscreen(new SettingsWindow(standard));
        settingsWindow.Height = 1040;
        Pump();
        Shot(settingsWindow, folder, "settings");

        var lookWindow = Offscreen(new ChannelLookWindow(standard, standard.Aux, null));
        Pump();
        lookWindow.SetColor(ChannelVM.ParseColor("#E879F9")!.Value);
        lookWindow.SetGlyph("");
        Pump();
        Shot(lookWindow, folder, "color-and-icon");
        lookWindow.Close();

        var custom = NewModel(("custom-streaming", "Streaming", "#22D3EE", 70), ("custom-podcast", "Podcast", "#A3E635", 50), ("custom-recording", "Recording", "#FACC15", 35));
        var customWindow = Offscreen(new MainWindow(custom));
        Fill(custom);
        Pump();
        customWindow.ChannelScroll.ScrollToHorizontalOffset(customWindow.ChannelScroll.ScrollableWidth);
        Pump();
        Shot(customWindow, folder, "custom-channels");

        Environment.Exit(0);
    }

    /// <summary>Saves a window's content at double resolution, without the unpainted title bar area.</summary>
    static void Shot(Window window, string folder, string name)
    {
        const int scale = 2;
        var content = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right) * scale;
        int height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom) * scale;
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
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
