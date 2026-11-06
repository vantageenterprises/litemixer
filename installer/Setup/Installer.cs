using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LiteMixerSetup;

/// <summary>The actual file, shortcut and registry work. Runs on a background thread; the UI reads Progress.</summary>
internal static unsafe partial class Installer
{
    public const string Publisher = "Vantage Enterprises, LLC";
    const string UninstallerName = "Uninstall LiteMixer.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LiteMixer";
    // Uninstall entry left behind by older installer builds.
    const string OldInnoKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B3E1C1A4-6F1C-4A43-9D2E-5A11C0FFEE01}_is1";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static volatile float Progress;

    const string AppFolderName = "LiteMixer";
    static string? _installDir;

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppFolderName);

    /// <summary>
    /// Where the app lives: the chosen folder, or the one recorded at install time. Read once and kept, because
    /// uninstalling deletes the record partway through and must still remove the right folder.
    /// </summary>
    public static string InstallDir
    {
        get => _installDir ??= RecordedDir() ?? DefaultDir;
        set => _installDir = value;
    }

    static string? RecordedDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string recorded && IsAppFolderPath(recorded) ? recorded : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every install ends in a folder of its own called LiteMixer. Anything else is never trusted as one.</summary>
    internal static bool IsAppFolderPath(string path) =>
        Path.IsPathFullyQualified(path) &&
        AppFolderName.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), StringComparison.OrdinalIgnoreCase);

    static bool HasApp(string folder) => File.Exists(Path.Combine(folder, "LiteMixer.exe"));

    /// <summary>The folder to install into for a folder the user picked. Always one of its own, because installing replaces it whole.</summary>
    public static string TargetFor(string picked)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        return AppFolderName.Equals(Path.GetFileName(full), StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.Combine(full, AppFolderName);
    }

    /// <summary>Why the app can't be installed in this folder, or null when it can. Short enough for one line of the installer.</summary>
    public static string? FolderProblem(string target)
    {
        const string notThisPc = "Pick a folder on a drive in this PC.";
        const string notAllowed = "Windows won't allow that folder. Pick another.";
        if (target.Length > 180) return "That path is too long. Pick a shorter one.";
        if (target.StartsWith(@"\\", StringComparison.Ordinal)) return notThisPc;
        try
        {
            if (Path.GetPathRoot(target) is not { Length: > 0 } root || new DriveInfo(root).DriveType != DriveType.Fixed) return notThisPc;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return notThisPc;
        }

        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (windows.Length > 0 && target.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)) return notAllowed;

        try
        {
            // Installing replaces these three folders whole, so none may hold anything that isn't LiteMixer.
            if (File.Exists(target)) return "That folder has other files. Pick another.";
            foreach (var folder in new[] { target, target + ".new", target + ".old" })
                if (Directory.Exists(folder) && !HasApp(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                    return "That folder has other files. Pick another.";
            if (!CanWriteNear(target)) return notAllowed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return notAllowed;
        }
        return null;
    }

    /// <summary>Tries the real thing beside the target: a folder with a file in it, removed straight away.</summary>
    static bool CanWriteNear(string target)
    {
        var existing = Path.GetDirectoryName(target);
        while (existing != null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        if (existing == null) return false;

        var probe = Path.Combine(existing, $".litemixer-check-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(probe);
            File.WriteAllText(Path.Combine(probe, "check"), "");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { if (Directory.Exists(probe)) Directory.Delete(probe, true); } catch { }
        }
    }

    /// <summary>A path short enough for one line, keeping the end, which says the most.</summary>
    public static string ShortPath(string path, int max = 34)
    {
        if (path.Length <= max) return path;
        var root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var tail = parts.Length > 0 ? parts[^1] : "";
        for (int i = parts.Length - 2; i >= 0 && root.Length + parts[i].Length + tail.Length + 5 <= max; i--)
            tail = parts[i] + "\\" + tail;
        return tail.Length == path.Length - root.Length ? path : $"{root}...\\{tail}";
    }

    static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteMixer");
    static string StartMenu => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static bool HasPayload => Assembly.GetEntryAssembly()?.GetManifestResourceInfo("payload") != null;
    public static bool IsInstalled => File.Exists(Path.Combine(InstallDir, "LiteMixer.exe"));
    public static bool DesktopShortcutExists => File.Exists(Path.Combine(Desktop, "LiteMixer.lnk"));
    public static string AppExe => Path.Combine(InstallDir, "LiteMixer.exe");

    public static string Version
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version == null) return "1.0";
            return version.Build > 0 ? version.ToString(3) : version.ToString(2);
        }
    }

    public static void Install(bool desktopShortcut)
    {
        Progress = 0;
        StopRunningApp();
        long totalBytes = Extract(InstallDir + ".new");
        Swap(InstallDir + ".new", InstallDir);
        Progress = 0.97f;

        Directory.CreateDirectory(StartMenu);
        WriteShortcut(Path.Combine(StartMenu, "LiteMixer.lnk"), AppExe, "");
        WriteShortcut(Path.Combine(StartMenu, "LiteMixer Settings.lnk"), AppExe, "--settings");
        var desktopLink = Path.Combine(Desktop, "LiteMixer.lnk");
        if (desktopShortcut) WriteShortcut(desktopLink, AppExe, "");
        else if (File.Exists(desktopLink)) File.Delete(desktopLink);

        Registry.CurrentUser.DeleteSubKeyTree(OldInnoKey, false);
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "LiteMixer");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", Publisher);
            key.SetValue("DisplayIcon", AppExe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{Path.Combine(InstallDir, UninstallerName)}\"");
            key.SetValue("EstimatedSize", (int)(totalBytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        Progress = 1;
    }

    /// <summary>Unpacks and writes a shortcut into a scratch folder only, for build-time checks.</summary>
    public static void TestInstall(string folder)
    {
        var target = Path.Combine(folder, "app");
        Extract(target);
        WriteShortcut(Path.Combine(folder, "test.lnk"), Path.Combine(target, "LiteMixer.exe"), "--settings");
    }

    public static void Uninstall(bool deleteSettings)
    {
        Progress = 0;
        var folder = InstallDir;
        StopRunningApp();
        Progress = 0.2f;

        foreach (var link in new[]
                 {
                     Path.Combine(StartMenu, "LiteMixer.lnk"),
                     Path.Combine(StartMenu, "LiteMixer Settings.lnk"),
                     Path.Combine(Desktop, "LiteMixer.lnk"),
                 })
            if (File.Exists(link)) File.Delete(link);

        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue("LiteMixer", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(OldInnoKey, false);
        Progress = 0.4f;

        RemoveAppFolder(folder);
        Progress = 0.9f;
        if (deleteSettings) DeleteFolder(SettingsDir);
        Progress = 1;
    }

    /// <summary>When started from the install folder, runs from a temp copy so the folder can be deleted.</summary>
    public static bool RelocateUninstaller()
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        var temp = Path.Combine(Path.GetTempPath(), $"LiteMixerUninstall-{Guid.NewGuid():N}.exe");
        File.Copy(exe, temp);
        Process.Start(new ProcessStartInfo(temp) { UseShellExecute = false, ArgumentList = { "--relocated" } });
        return true;
    }

    /// <summary>Deletes the temp uninstaller copy shortly after this process exits.</summary>
    public static void ScheduleSelfDelete()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /q \"{exe}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    static long Extract(string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        using var resource = Assembly.GetEntryAssembly()!.GetManifestResourceStream("payload")
                             ?? throw new InvalidDataException("This installer has no app inside it.");
        using var brotli = new BrotliStream(resource, CompressionMode.Decompress);
        var reader = new BinaryReader(brotli);
        if (Encoding.ASCII.GetString(reader.ReadBytes(5)) != "LMIX1") throw new InvalidDataException("The installer data is damaged.");
        long total = reader.ReadInt64();
        int count = reader.ReadInt32();

        var buffer = new byte[1 << 20];
        long done = 0;
        for (int i = 0; i < count; i++)
        {
            var relative = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
            long size = reader.ReadInt64();
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installer data is damaged.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var file = File.Create(path);
            long left = size;
            while (left > 0)
            {
                int read = brotli.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                if (read <= 0) throw new InvalidDataException("The installer data is damaged.");
                file.Write(buffer, 0, read);
                left -= read;
                done += read;
                Progress = 0.95f * done / Math.Max(total, 1);
            }
        }
        return total;
    }

    /// <summary>Replaces the old folder with the new one, restoring the old one if that fails.</summary>
    static void Swap(string fresh, string target)
    {
        var old = target + ".old";
        if (Directory.Exists(old)) Directory.Delete(old, true);
        bool hadOld = Directory.Exists(target);
        if (hadOld) Retry(() => Directory.Move(target, old));
        try
        {
            Retry(() => Directory.Move(fresh, target));
        }
        catch
        {
            if (hadOld) Directory.Move(old, target);
            throw;
        }
        if (hadOld)
        {
            try { Directory.Delete(old, true); } catch { /* Leftover .old folder is harmless; removed next time. */ }
        }
    }

    static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 25 && ex is IOException or UnauthorizedAccessException)
            {
                // The app's files can stay locked for a moment after it exits.
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Deletes an install, but only a folder that is clearly one, whatever the record said.</summary>
    static void RemoveAppFolder(string folder)
    {
        if (IsAppFolderPath(folder) && HasApp(folder)) DeleteFolder(folder);
    }

    static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder)) Retry(() => Directory.Delete(folder, true));
    }

    /// <summary>Closes only the copy of LiteMixer running from the install folder. App volumes and routing stay as they are.</summary>
    static void StopRunningApp()
    {
        foreach (var process in Process.GetProcessesByName("LiteMixer"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path == null || !path.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
            }
        }
    }

    static void WriteShortcut(string linkPath, string target, string arguments)
    {
        var clsid = new Guid("00021401-0000-0000-C000-000000000046");
        var linkIid = new Guid("000214F9-0000-0000-C000-000000000046");
        var fileIid = new Guid("0000010B-0000-0000-C000-000000000046");

        int init = CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        nint link = 0, persist = 0;
        try
        {
            Check(CoCreateInstance(ref clsid, 0, 1, ref linkIid, out link));
            // IShellLinkW vtable: 7 SetDescription, 9 SetWorkingDirectory, 11 SetArguments, 17 SetIconLocation, 20 SetPath.
            CallString(link, 20, target);
            CallString(link, 9, Path.GetDirectoryName(target)!);
            CallString(link, 11, arguments);
            var setIcon = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)link)[17];
            fixed (char* icon = target) Check(setIcon(link, icon, 0));

            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)link)[0];
            nint file;
            Check(queryInterface(link, &fileIid, &file));
            persist = file;
            // IPersistFile vtable: 6 Save.
            var save = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)persist)[6];
            fixed (char* path = linkPath) Check(save(persist, path, 1));
        }
        finally
        {
            if (persist != 0) Marshal.Release(persist);
            if (link != 0) Marshal.Release(link);
            if (init >= 0) CoUninitialize();
        }
    }

    static void CallString(nint com, int slot, string value)
    {
        var method = (delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)com)[slot];
        fixed (char* text = value) Check(method(com, text));
    }

    static void Check(int hresult) => Marshal.ThrowExceptionForHR(hresult);

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
}
