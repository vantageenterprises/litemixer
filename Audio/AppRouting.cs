using System.Runtime.InteropServices;

namespace LiteMixer.Audio;

/// <summary>
/// Per-app output routing through Windows' internal AudioPolicyConfig factory. This is the same
/// mechanism as Settings > Sound > Volume mixer > per-app output device, so Windows remembers it.
/// </summary>
internal static unsafe class AppRouting
{
    const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";
    const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const string CaptureSuffix = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    // vtable slots: 3 IUnknown + 3 IInspectable + 19 methods we never call.
    const int SetPersistedSlot = 25;
    const int GetPersistedSlot = 26;
    const int ClearAllSlot = 27;

    static IntPtr _factory;
    static bool _loaded;

    /// <summary>Set by the offscreen checks, which must never change real routing.</summary>
    internal static bool ReadOnly { get; set; }

    static IntPtr Factory
    {
        get
        {
            if (_loaded) return _factory;
            _loaded = true;
            var iid = Environment.OSVersion.Version.Build >= 21390
                ? new Guid("ab3d4648-e242-459f-b02f-541c70306324")
                : new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
            if (WindowsCreateString(ClassName, (uint)ClassName.Length, out var name) < 0) return 0;
            try
            {
                if (RoGetActivationFactory(name, ref iid, out var factory) >= 0) _factory = factory;
            }
            finally
            {
                WindowsDeleteString(name);
            }
            return _factory;
        }
    }

    /// <summary>Routes a process to a device, or back to the Windows default when deviceId is null.</summary>
    public static bool SetAppDevice(uint pid, string? deviceId, EDataFlow flow)
    {
        if (ReadOnly) return false;
        var factory = Factory;
        if (factory == 0 || pid == 0) return false;

        IntPtr hstring = 0;
        if (!string.IsNullOrEmpty(deviceId))
        {
            var full = DevicePrefix + deviceId + (flow == EDataFlow.Render ? RenderSuffix : CaptureSuffix);
            if (WindowsCreateString(full, (uint)full.Length, out hstring) < 0) return false;
        }

        try
        {
            var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)(*(IntPtr**)factory)[SetPersistedSlot];
            int a = set(factory, pid, (int)flow, (int)ERole.Multimedia, hstring);
            int b = set(factory, pid, (int)flow, (int)ERole.Console, hstring);
            return a >= 0 && b >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hstring != 0) WindowsDeleteString(hstring);
        }
    }

    /// <summary>The device a process is pinned to (raw interface path), or null when it follows the default.</summary>
    public static string? GetAppDevice(uint pid, EDataFlow flow)
    {
        var factory = Factory;
        if (factory == 0) return null;
        try
        {
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)(*(IntPtr**)factory)[GetPersistedSlot];
            IntPtr hstring = 0;
            if (get(factory, pid, (int)flow, (int)ERole.Multimedia, &hstring) < 0 || hstring == 0) return null;
            try
            {
                var raw = WindowsGetStringRawBuffer(hstring, out var length);
                return new string((char*)raw, 0, (int)length);
            }
            finally { WindowsDeleteString(hstring); }
        }
        catch { return null; }
    }

    public static void ClearAll()
    {
        if (ReadOnly) return;
        var factory = Factory;
        if (factory == 0) return;
        try
        {
            var clear = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(IntPtr**)factory)[ClearAllSlot];
            clear(factory);
        }
        catch
        {
            // Best effort.
        }
    }

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
