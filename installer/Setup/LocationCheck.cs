using System.Text;

namespace LiteMixerSetup;

internal static partial class Installer
{
    /// <summary>
    /// Build-time check (--check-location &lt;dir&gt;): tests choosing an install folder, all inside the given folder.
    /// Touches no registry entries, shortcuts or real installs. The result is written to location-report.txt.
    /// </summary>
    public static void CheckLocation(string folder)
    {
        var report = new StringBuilder();
        void Check(string name, bool ok) => report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder);

        // Picking a folder never means installing straight into it.
        Check("a picked folder gets its own LiteMixer folder", TargetFor(@"D:\Apps") == @"D:\Apps\LiteMixer");
        Check($"a drive root works ({TargetFor(@"D:\")})", TargetFor(@"D:\") == @"D:\LiteMixer");
        Check("a trailing slash changes nothing", TargetFor(@"D:\Apps\") == @"D:\Apps\LiteMixer");
        Check("a folder already called LiteMixer isn't doubled", TargetFor(@"D:\Apps\LiteMixer") == @"D:\Apps\LiteMixer" && TargetFor(@"D:\Apps\litemixer\") == @"D:\Apps\litemixer");

        // Only folders named for the app are ever trusted from the record.
        Check("the default folder is valid", IsAppFolderPath(DefaultDir));
        Check("a custom folder is valid", IsAppFolderPath(@"D:\Apps\LiteMixer"));
        Check("an unrelated folder is never trusted", !IsAppFolderPath(@"D:\Games") && !IsAppFolderPath(@"C:\") && !IsAppFolderPath(@"C:\Users\Someone"));
        Check("a relative path is never trusted", !IsAppFolderPath("LiteMixer") && !IsAppFolderPath(@"D:LiteMixer"));

        // What can't be chosen.
        Check("a network folder is refused", FolderProblem(@"\\server\share\LiteMixer") != null);
        Check("a very long path is refused", FolderProblem(Path.Combine(root, new string('x', 200), "LiteMixer")) != null);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Check("the Windows folder is refused", FolderProblem(TargetFor(windows)) != null);
        Check("a drive letter that isn't there is refused", FolderProblem(@"Q:\NoSuchDriveHere\LiteMixer") != null || !Directory.Exists(@"Q:\"));

        var ok = Path.Combine(root, "fine", "LiteMixer");
        Directory.CreateDirectory(Path.GetDirectoryName(ok)!);
        Check($"a normal folder is accepted ({FolderProblem(ok) ?? "no problem"})", FolderProblem(ok) == null);
        Check("checking leaves nothing behind", Directory.GetFileSystemEntries(Path.GetDirectoryName(ok)!).Length == 0);

        var deep = Path.Combine(root, "not", "made", "yet", "LiteMixer");
        Check("a folder that doesn't exist yet is accepted", FolderProblem(deep) == null);

        var taken = Path.Combine(root, "taken", "LiteMixer");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "my-file.txt"), "keep");
        Check("a LiteMixer folder holding someone's files is refused", FolderProblem(taken) != null);
        var takenNew = Path.Combine(root, "taken-new", "LiteMixer");
        Directory.CreateDirectory(takenNew + ".old");
        File.WriteAllText(Path.Combine(takenNew + ".old", "my-file.txt"), "keep");
        Check("a neighbor the installer would replace is protected too", FolderProblem(takenNew) != null);
        var empty = Path.Combine(root, "empty", "LiteMixer");
        Directory.CreateDirectory(empty);
        Check("an empty LiteMixer folder is fine", FolderProblem(empty) == null);
        var earlier = Path.Combine(root, "earlier", "LiteMixer");
        Directory.CreateDirectory(earlier);
        File.WriteAllText(Path.Combine(earlier, "LiteMixer.exe"), "");
        Check("an earlier install can be replaced", FolderProblem(earlier) == null);

        // Installing beside other files, then again, then removing: neighbors must survive all of it.
        var parent = Path.Combine(root, "custom");
        var target = TargetFor(parent);
        Directory.CreateDirectory(parent);
        var neighborFile = Path.Combine(parent, "neighbor.txt");
        var neighborFolder = Path.Combine(parent, "NeighborFolder");
        File.WriteAllText(neighborFile, "keep");
        Directory.CreateDirectory(neighborFolder);
        File.WriteAllText(Path.Combine(neighborFolder, "inside.txt"), "keep");

        Extract(target + ".new");
        Swap(target + ".new", target);
        Check("the app unpacks into the custom folder", HasApp(target));
        Check("no temporary folders stay behind", !Directory.Exists(target + ".new") && !Directory.Exists(target + ".old"));
        var stamp = Path.Combine(target, "from-first-install.txt");
        File.WriteAllText(stamp, "old");

        Extract(target + ".new");
        Swap(target + ".new", target);
        Check("updating in place replaces the old files", HasApp(target) && !File.Exists(stamp));
        Check("updating leaves neighbors alone", File.Exists(neighborFile) && File.Exists(Path.Combine(neighborFolder, "inside.txt")));

        var link = Path.Combine(root, "custom.lnk");
        WriteShortcut(link, Path.Combine(target, "LiteMixer.exe"), "");
        Check("a shortcut can point at the custom folder", File.Exists(link));

        // Removing.
        var wrong = Path.Combine(root, "Games");
        Directory.CreateDirectory(wrong);
        File.WriteAllText(Path.Combine(wrong, "LiteMixer.exe"), "looks like the app");
        File.WriteAllText(Path.Combine(wrong, "precious.txt"), "keep");
        RemoveAppFolder(wrong);
        Check("removing refuses a folder not named for the app", File.Exists(Path.Combine(wrong, "precious.txt")));
        RemoveAppFolder(taken);
        Check("removing refuses a LiteMixer folder without the app in it", File.Exists(Path.Combine(taken, "my-file.txt")));
        RemoveAppFolder(target);
        Check("removing deletes the install", !Directory.Exists(target));
        Check("removing leaves neighbors alone", File.Exists(neighborFile) && File.Exists(Path.Combine(neighborFolder, "inside.txt")) && Directory.Exists(parent));

        // The chosen folder sticks, even after the record it might have come from is gone.
        var before = InstallDir;
        InstallDir = target;
        Check("the chosen folder is kept", InstallDir == target && AppExe == Path.Combine(target, "LiteMixer.exe"));
        InstallDir = before;

        // Display.
        Check("short paths are left alone", ShortPath(@"D:\Apps\LiteMixer") == @"D:\Apps\LiteMixer");
        var shortened = ShortPath(@"C:\Users\Someone\AppData\Local\Programs\LiteMixer");
        Check($"long paths keep their end ({shortened})", shortened.StartsWith(@"C:\...\", StringComparison.Ordinal) && shortened.EndsWith(@"\LiteMixer", StringComparison.Ordinal) && shortened.Length <= 34);
        Check("a path with one long part still shows", ShortPath(@"C:\" + new string('x', 60)).Length > 0);

        File.WriteAllText(Path.Combine(root, "location-report.txt"), report.ToString());
    }
}
