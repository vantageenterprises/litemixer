using System.Text.Json;
using Microsoft.Win32;

namespace LiteMixer;

public sealed class ChannelSettings
{
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
    /// <summary>Output device for this channel; null follows the Windows default output.</summary>
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class AppSettings
{
    public double MasterVolume { get; set; } = 100;
    public bool MasterMuted { get; set; }
    public double Balance { get; set; }
    public string? DefaultOutputId { get; set; }
    public string? DefaultInputId { get; set; }
    /// <summary>Mic level in percent; negative means never set, so Windows' level is left alone.</summary>
    public double MicVolume { get; set; } = -1;
    public bool? MicMuted { get; set; }
    public Dictionary<string, ChannelSettings> Channels { get; set; } = new();
    /// <summary>User-created channels in display order; they sit between Media and Aux.</summary>
    public List<CustomChannel> CustomChannels { get; set; } = new();
    /// <summary>App key (lowercase exe name) to channel id.</summary>
    public Dictionary<string, string> Assignments { get; set; } = new();
    /// <summary>Channel for every app without an explicit assignment (games, launchers, anything new).</summary>
    public string DefaultChannel { get; set; } = "game";
    public bool StartWithWindows { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Version the tray already announced, so each new version pops up only once.</summary>
    public string? LastAnnouncedVersion { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    /// <summary>Action id to key combo, for example "mic-mute" = "Ctrl+Alt+M". Empty means unbound.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>Highest volume a channel fader may reach (channel id to percent). Missing means 100.</summary>
    public Dictionary<string, double> Caps { get; set; } = new();
    /// <summary>Volume increases bigger than a few percent ramp up instead of jumping.</summary>
    public bool FadeIn { get; set; } = true;

    public bool DuckEnabled { get; set; }
    /// <summary>Ducked channels play at this percent of their level while chat is talking.</summary>
    public double DuckLevel { get; set; } = 40;
    public List<string> DuckChannels { get; set; } = ["game", "media"];

    public bool MicBeep { get; set; } = true;
    public double MicBeepVolume { get; set; } = 10;

    /// <summary>Per-app level inside its channel (app key to percent). Missing means 100.</summary>
    public Dictionary<string, double> AppVolumes { get; set; } = new();
    public List<string> HiddenApps { get; set; } = new();
    public List<MixProfile> Profiles { get; set; } = new();

    public bool CompactOpen { get; set; }
    public double? CompactLeft { get; set; }
    public double? CompactTop { get; set; }
}

public sealed class CustomChannel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#22D3EE";
}

public sealed class MixProfile
{
    public string Name { get; set; } = "";
    public double Master { get; set; } = 100;
    public Dictionary<string, double> Volumes { get; set; } = new();
    public Dictionary<string, bool> Mutes { get; set; } = new();
    public double Balance { get; set; }
}

public static class HotkeyActions
{
    public static readonly (string Id, string Label, string Default)[] All =
    [
        ("mic-mute", "Mute / unmute mic", "Ctrl+Alt+End"),
        ("master-up", "Master volume up", "Ctrl+Alt+Up"),
        ("master-down", "Master volume down", "Ctrl+Alt+Down"),
        ("game-up", "Game volume up", "Ctrl+Alt+PageUp"),
        ("game-down", "Game volume down", "Ctrl+Alt+PageDown"),
        ("balance-game", "Balance toward game", "Ctrl+Alt+Left"),
        ("balance-chat", "Balance toward chat", "Ctrl+Alt+Right"),
        ("balance-center", "Center balance", "Ctrl+Alt+Home"),
        ("duck-toggle", "Auto-duck on / off", ""),
        ("compact", "Show / hide compact mixer", "Ctrl+Alt+K"),
        ("open", "Open LiteMixer", "Ctrl+Alt+L"),
    ];

    /// <summary>Adds any action missing from saved settings with its default combo.</summary>
    public static void FillDefaults(AppSettings settings)
    {
        // The first build defaulted mic mute to Ctrl+Alt+M, which other apps commonly own.
        if (settings.Hotkeys.TryGetValue("mic-mute", out var mic) && mic == "Ctrl+Alt+M") settings.Hotkeys.Remove("mic-mute");
        // Actions that no longer exist must not keep holding their key combos.
        foreach (var id in settings.Hotkeys.Keys.Where(k => All.All(a => a.Id != k)).ToList())
            settings.Hotkeys.Remove(id);
        foreach (var (id, _, combo) in All)
            settings.Hotkeys.TryAdd(id, combo);
    }
}

public static class SettingsStore
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteMixer");

    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch
        {
            // A corrupt file falls back to defaults; keep a copy so nothing is silently lost.
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return new();
    }

    /// <summary>Set by the offscreen UI check so it can never overwrite real settings.</summary>
    public static bool ReadOnly { get; set; }

    public static void Save(AppSettings settings)
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, FilePath, true);
        }
        catch { }
    }
}

public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "LiteMixer";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            else
                key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
