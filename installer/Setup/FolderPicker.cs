using System.Runtime.InteropServices;

namespace LiteMixerSetup;

/// <summary>Windows' standard folder dialog. Called by vtable slot, like the shortcut code, because this exe is compiled natively.</summary>
internal static unsafe partial class FolderPicker
{
    const int FosPickFolders = 0x20, FosForceFileSystem = 0x40, FosPathMustExist = 0x800;
    const uint SigdnFileSysPath = 0x80058000;
    const int ErrorCancelled = unchecked((int)0x800704C7);

    // IFileOpenDialog vtable: 3 Show, 9 SetOptions, 10 GetOptions, 12 SetFolder, 17 SetTitle, 18 SetOkButtonLabel, 20 GetResult.
    const int Show = 3, SetOptions = 9, GetOptions = 10, SetFolder = 12, SetTitle = 17, SetOkLabel = 18, GetResult = 20;
    // IShellItem vtable: 5 GetDisplayName.
    const int GetDisplayName = 5;

    /// <summary>The folder picked, or null if the dialog was cancelled or couldn't open.</summary>
    public static string? Pick(nint owner, string? startIn)
    {
        var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        var iid = new Guid("D57C7288-D4AD-4768-BE02-9D969532D960");
        var itemIid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        int init = CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        nint dialog = 0, result = 0;
        try
        {
            if (CoCreateInstance(ref clsid, 0, 1, ref iid, out dialog) < 0) return null;

            uint options;
            var getOptions = (delegate* unmanaged[Stdcall]<nint, uint*, int>)(*(nint**)dialog)[GetOptions];
            if (getOptions(dialog, &options) < 0) return null;
            var setOptions = (delegate* unmanaged[Stdcall]<nint, uint, int>)(*(nint**)dialog)[SetOptions];
            if (setOptions(dialog, options | FosPickFolders | FosForceFileSystem | FosPathMustExist) < 0) return null;

            CallString(dialog, SetTitle, "Choose where to install LiteMixer");
            CallString(dialog, SetOkLabel, "Install here");

            if (!string.IsNullOrEmpty(startIn) && SHCreateItemFromParsingName(startIn, 0, ref itemIid, out var start) >= 0)
            {
                var setFolder = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)dialog)[SetFolder];
                setFolder(dialog, start);
                Marshal.Release(start);
            }

            var show = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)dialog)[Show];
            int shown = show(dialog, owner);
            if (shown == ErrorCancelled || shown < 0) return null;

            var getResult = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)dialog)[GetResult];
            nint item;
            if (getResult(dialog, &item) < 0) return null;
            result = item;

            var getName = (delegate* unmanaged[Stdcall]<nint, uint, char**, int>)(*(nint**)result)[GetDisplayName];
            char* name;
            if (getName(result, SigdnFileSysPath, &name) < 0) return null;
            try { return new string(name); }
            finally { Marshal.FreeCoTaskMem((nint)name); }
        }
        finally
        {
            if (result != 0) Marshal.Release(result);
            if (dialog != 0) Marshal.Release(dialog);
            if (init >= 0) CoUninitialize();
        }
    }

    static void CallString(nint com, int slot, string value)
    {
        var method = (delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)com)[slot];
        fixed (char* text = value) method(com, text);
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out nint item);
}
