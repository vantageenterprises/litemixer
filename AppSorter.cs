using System.Text.Json;
using Microsoft.Win32;

namespace LiteMixer;

/// <summary>
/// Works out which channel an app belongs in the first time it plays sound. Returns a channel id, or null when
/// there's no good reason to pick one, so the app opens in the default channel. Only reads; it changes nothing.
/// </summary>
internal static class AppSorter
{
    const string GameChannel = "game", ChatChannel = "chat", VideoChannel = "media", MusicChannel = "aux";

    /// <summary>Exe name (lowercase, no extension) to channel id. Checked first, because it's an exact match.</summary>
    static readonly Dictionary<string, string> KnownApps = Build(
        (ChatChannel,
        [
            "discord", "discordptb", "discordcanary", "vesktop", "guilded", "teamspeak", "ts3client_win64", "ts3client_win32",
            "mumble", "slack", "teams", "ms-teams", "zoom", "skype", "telegram", "signal", "whatsapp", "element", "webex",
        ]),
        (MusicChannel,
        [
            "spotify", "itunes", "applemusic", "amazonmusic", "tidal", "deezer", "qobuz", "musicbee", "foobar2000", "aimp",
            "winamp", "cider", "plexamp",
        ]),
        (VideoChannel,
        [
            // Browsers play everything, but one app can only be in one channel, and video is their most common use.
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen", "waterfox", "librewolf",
            "vlc", "mpv", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayer64", "potplayermini",
            "potplayermini64", "wmplayer", "plex", "netflix", "stremio", "kodi", "jellyfinmediaplayer",
        ]),
        (GameChannel,
        [
            "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient", "battle.net", "eadesktop", "origin",
            "ubisoftconnect", "upc", "riotclientux", "riotclientservices",
        ]));

    /// <summary>Programs that run other programs. Windows may list one as a game, but that says nothing about the next app using it.</summary>
    static readonly HashSet<string> GenericHosts = new(
    [
        "java", "javaw", "python", "pythonw", "node", "electron", "dotnet", "cmd", "powershell", "pwsh", "rundll32",
        "wscript", "cscript", "mshta", "explorer", "dwm", "msedgewebview2",
    ], StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, string> Build(params (string Channel, string[] Exes)[] groups)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (channel, exes) in groups)
            foreach (var exe in exes)
                map[exe] = channel;
        return map;
    }

    public static string? Suggest(string key, string? path) => Suggest(key, path, ReadGameFolders, ReadWindowsGames);

    /// <summary>The lists are read only when needed: most apps are settled by the checks before them.</summary>
    internal static string? Suggest(string key, string? path, Func<IEnumerable<string>> gameFolders, Func<IEnumerable<string>> windowsGames)
    {
        if (KnownApps.TryGetValue(key, out var known)) return known;
        if (path != null && (IsInLibraryFolder(path) || IsInside(path, gameFolders()))) return GameChannel;
        if (!GenericHosts.Contains(key) && IsWindowsGame(key, path, windowsGames())) return GameChannel;
        return null;
    }

    /// <summary>Steam, Epic and GOG library folders, on any drive. Their launchers and tools live nearby, so those are left out.</summary>
    internal static bool IsInLibraryFolder(string path)
    {
        var normalized = path.Replace('/', '\\');
        bool Has(string part) => normalized.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has(@"\steamapps\common\") || Has(@"\GOG Games\") || Has(@"\GOG Galaxy\Games\")) return true;
        return Has(@"\Epic Games\") && !Has(@"\Epic Games\Launcher\") && !Has(@"\Epic Games\UE_");
    }

    static bool IsInside(string path, IEnumerable<string> folders)
    {
        var normalized = path.Replace('/', '\\');
        foreach (var folder in folders)
        {
            var root = folder.Replace('/', '\\').TrimEnd('\\');
            if (root.Length > 3 && normalized.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Matches the full path when known. Some games hide their path, so then the exe name has to do.</summary>
    static bool IsWindowsGame(string key, string? path, IEnumerable<string> games)
    {
        foreach (var game in games)
        {
            var listed = game.Trim().Trim('"').Replace('/', '\\');
            if (path != null ? listed.Equals(path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
                             : Path.GetFileNameWithoutExtension(listed).Equals(key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Install folders of Epic and GOG games placed somewhere other than the usual library folder.</summary>
    internal static List<string> ReadGameFolders()
    {
        var folders = new List<string>();
        try
        {
            var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(manifests))
                foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
                    if (ReadEpicFolder(file) is { } folder) folders.Add(folder);
        }
        catch { }
        try
        {
            using var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            foreach (var id in gog?.GetSubKeyNames() ?? [])
            {
                using var game = gog!.OpenSubKey(id);
                if (game?.GetValue("path") is string folder && folder.Length > 0) folders.Add(folder);
            }
        }
        catch { }
        return folders;
    }

    static string? ReadEpicFolder(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            // Epic also installs engines and tools through its launcher.
            if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array &&
                !categories.EnumerateArray().Any(c => string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase)))
                return null;
            return root.TryGetProperty("InstallLocation", out var location) ? location.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The games Windows itself has recognized on this PC.</summary>
    internal static List<string> ReadWindowsGames()
    {
        var games = new List<string>();
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var id in store?.GetSubKeyNames() ?? [])
            {
                using var child = store!.OpenSubKey(id);
                if (child?.GetValue("MatchedExeFullPath") is string path && path.Length > 0) games.Add(path);
            }
        }
        catch { }
        return games;
    }
}
