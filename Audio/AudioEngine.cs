using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteMixer.Audio;

public sealed record DeviceInfo(string Id, string Name);

internal sealed class SessionInfo
{
    public required object Com { get; init; }
    public required string InstanceId { get; init; }
    public required string DeviceId { get; init; }
    public required uint Pid { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required bool IsSystem { get; init; }
    /// <summary>Full exe path when Windows allows reading it (used for the chip icon).</summary>
    public string? Path { get; init; }

    public ISimpleAudioVolume? Volume => Com as ISimpleAudioVolume;
    public IAudioMeterInformation? Meter => Com as IAudioMeterInformation;
}

/// <summary>Thin facade over Core Audio: devices, sessions, endpoint volume and system defaults.</summary>
internal sealed class AudioEngine : IDisposable
{
    const uint DeviceStateActive = 1;
    const uint ClsCtxAll = 23;
    const ushort VtLpwstr = 31;
    const int SystemSoundsSession = 0;

    static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    static Guid _eventContext = new("6f1c2a43-0d9e-4c55-9b0b-3c7a2e6d5a11");

    readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
    readonly Watcher _watcher = new();
    readonly Dictionary<string, IAudioSessionManager2> _managers = new();
    readonly Dictionary<string, IAudioEndpointVolume> _endpointVolumes = new();
    readonly Dictionary<string, IAudioMeterInformation> _endpointMeters = new();
    readonly Dictionary<uint, (string Key, string Name, string? Path)?> _processes = new();
    readonly uint _ownPid = (uint)Environment.ProcessId;
    IPolicyConfig? _policy;

    public AudioEngine()
    {
        try { _enumerator.RegisterEndpointNotificationCallback(_watcher); } catch { }
    }

    /// <summary>True when Windows reported a device or session change since the last poll.</summary>
    public bool Dirty => _watcher.Dirty;
    public void ClearDirty() => _watcher.Dirty = false;

    public List<DeviceInfo> GetDevices(EDataFlow flow)
    {
        var list = new List<DeviceInfo>();
        IMMDeviceCollection? collection = null;
        try
        {
            collection = _enumerator.EnumAudioEndpoints(flow, DeviceStateActive);
            uint count = collection.GetCount();
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    device = collection.Item(i);
                    list.Add(new DeviceInfo(device.GetId(), ReadName(device)));
                }
                catch { }
                finally { Release(device); }
            }
        }
        catch { }
        finally { Release(collection); }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public string? GetDefaultId(EDataFlow flow)
    {
        IMMDevice? device = null;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(flow, ERole.Console);
            return device.GetId();
        }
        catch { return null; }
        finally { Release(device); }
    }

    /// <summary>Makes a device the Windows default for every role (console, multimedia, communications).</summary>
    public void SetDefault(string deviceId)
    {
        try
        {
            _policy ??= (IPolicyConfig)new PolicyConfigClient();
            _policy.SetDefaultEndpoint(deviceId, ERole.Console);
            _policy.SetDefaultEndpoint(deviceId, ERole.Multimedia);
            _policy.SetDefaultEndpoint(deviceId, ERole.Communications);
        }
        catch { }
    }

    public List<SessionInfo> GetSessions(IEnumerable<DeviceInfo> devices, bool includeSystemSounds)
    {
        var list = new List<SessionInfo>();
        foreach (var device in devices)
        {
            var manager = GetManager(device.Id);
            if (manager == null) continue;

            IAudioSessionEnumerator? sessions = null;
            try
            {
                sessions = manager.GetSessionEnumerator();
                int count = sessions.GetCount();
                for (int i = 0; i < count; i++)
                {
                    object? com = null;
                    try
                    {
                        com = sessions.GetSession(i);
                        var info = Describe(com, device.Id);
                        if (info != null && (includeSystemSounds || !info.IsSystem)) list.Add(info);
                        else Release(com);
                    }
                    catch { Release(com); }
                }
            }
            catch { }
            finally { Release(sessions); }
        }
        return list;
    }

    /// <summary>
    /// Holds this program's own audio sessions at full volume. Everything it captures is scaled by its own session, so
    /// another mixer, or Windows' Volume mixer, turning it down would quietly lower the stream mix.
    /// </summary>
    public void HoldOwnSessionsAtFull(IEnumerable<DeviceInfo> devices)
    {
        foreach (var device in devices)
        {
            var manager = GetManager(device.Id);
            if (manager == null) continue;
            IAudioSessionEnumerator? sessions = null;
            try
            {
                sessions = manager.GetSessionEnumerator();
                int count = sessions.GetCount();
                for (int i = 0; i < count; i++)
                {
                    object? com = null;
                    try
                    {
                        com = sessions.GetSession(i);
                        if (com is IAudioSessionControl2 control && control.GetProcessId() == _ownPid && com is ISimpleAudioVolume volume)
                        {
                            volume.SetMasterVolume(1, ref _eventContext);
                            volume.SetMute(false, ref _eventContext);
                        }
                    }
                    catch { }
                    finally { Release(com); }
                }
            }
            catch { }
            finally { Release(sessions); }
        }
    }

    SessionInfo? Describe(object com, string deviceId)
    {
        var control = (IAudioSessionControl2)com;
        if (control.GetState() == AudioSessionState.Expired) return null;

        uint pid = control.GetProcessId();
        if (pid == _ownPid) return null;

        bool isSystem = control.IsSystemSoundsSession() == SystemSoundsSession;
        var identity = isSystem ? ("#system", "System Sounds", null) : Identify(pid);
        // audiodg hosts driver-level audio effects; it is not an app.
        if (identity == null || identity.Value.Key == "audiodg") return null;

        return new SessionInfo
        {
            Com = com,
            InstanceId = control.GetSessionInstanceIdentifier(),
            DeviceId = deviceId,
            Pid = pid,
            Key = identity.Value.Key,
            Name = identity.Value.Name,
            IsSystem = isSystem,
            Path = identity.Value.Path,
        };
    }

    (string Key, string Name, string? Path)? Identify(uint pid)
    {
        if (_processes.TryGetValue(pid, out var cached)) return cached;

        (string Key, string Name, string? Path)? result = null;
        var path = QueryProcessPath(pid);
        if (path != null)
        {
            var exe = Path.GetFileNameWithoutExtension(path);
            string? description = null;
            try { description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { }
            result = (exe.ToLowerInvariant(), string.IsNullOrWhiteSpace(description) ? exe : description, path);
        }
        else
        {
            // Protected processes (some anti-cheat games) refuse OpenProcess; fall back to the process list.
            try
            {
                using var process = Process.GetProcessById((int)pid);
                result = (process.ProcessName.ToLowerInvariant(), process.ProcessName, null);
            }
            catch { }
        }

        _processes[pid] = result;
        return result;
    }

    public void PruneProcessCache(HashSet<uint> livePids)
    {
        foreach (var pid in _processes.Keys.Where(p => !livePids.Contains(p)).ToList())
            _processes.Remove(pid);
    }

    public float? GetEndpointVolume(string? deviceId)
    {
        var endpoint = EndpointVolume(deviceId);
        if (endpoint == null) return null;
        try { return endpoint.GetMasterVolumeLevelScalar(); } catch { return null; }
    }

    public bool? GetEndpointMute(string? deviceId)
    {
        var endpoint = EndpointVolume(deviceId);
        if (endpoint == null) return null;
        try { return endpoint.GetMute(); } catch { return null; }
    }

    public void SetEndpointVolume(string? deviceId, float level)
    {
        var endpoint = EndpointVolume(deviceId);
        if (endpoint == null) return;
        try { endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _eventContext); } catch { }
    }

    public void SetEndpointMute(string? deviceId, bool mute)
    {
        var endpoint = EndpointVolume(deviceId);
        if (endpoint == null) return;
        try { endpoint.SetMute(mute, ref _eventContext); } catch { }
    }

    public float GetEndpointPeak(string? deviceId)
    {
        if (deviceId == null) return 0;
        if (!_endpointMeters.TryGetValue(deviceId, out var meter))
        {
            meter = Activate<IAudioMeterInformation>(deviceId);
            if (meter == null) return 0;
            _endpointMeters[deviceId] = meter;
        }
        try { return meter.GetPeakValue(); } catch { return 0; }
    }

    public static void SetSessionVolume(SessionInfo session, float level, bool mute)
    {
        var volume = session.Volume;
        if (volume == null) return;
        try
        {
            volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref _eventContext);
            volume.SetMute(mute, ref _eventContext);
        }
        catch { }
    }

    public static float GetSessionPeak(SessionInfo session)
    {
        try { return session.Meter?.GetPeakValue() ?? 0; } catch { return 0; }
    }

    /// <summary>Drops cached per-device COM objects after the device list changes.</summary>
    public void ResetDeviceCaches()
    {
        foreach (var manager in _managers.Values)
        {
            try { manager.UnregisterSessionNotification(_watcher); } catch { }
            Release(manager);
        }
        _managers.Clear();
        foreach (var endpoint in _endpointVolumes.Values) Release(endpoint);
        _endpointVolumes.Clear();
        foreach (var meter in _endpointMeters.Values) Release(meter);
        _endpointMeters.Clear();
    }

    IAudioSessionManager2? GetManager(string deviceId)
    {
        if (_managers.TryGetValue(deviceId, out var manager)) return manager;
        manager = Activate<IAudioSessionManager2>(deviceId);
        if (manager == null) return null;
        try
        {
            // Windows only delivers session-created events after the enumerator has been read once.
            Release(manager.GetSessionEnumerator());
            manager.RegisterSessionNotification(_watcher);
        }
        catch { }
        _managers[deviceId] = manager;
        return manager;
    }

    IAudioEndpointVolume? EndpointVolume(string? deviceId)
    {
        if (deviceId == null) return null;
        if (_endpointVolumes.TryGetValue(deviceId, out var endpoint)) return endpoint;
        endpoint = Activate<IAudioEndpointVolume>(deviceId);
        if (endpoint != null) _endpointVolumes[deviceId] = endpoint;
        return endpoint;
    }

    T? Activate<T>(string deviceId) where T : class
    {
        IMMDevice? device = null;
        try
        {
            device = _enumerator.GetDevice(deviceId);
            var iid = typeof(T).GUID;
            return device.Activate(ref iid, ClsCtxAll, IntPtr.Zero) as T;
        }
        catch { return null; }
        finally { Release(device); }
    }

    static string ReadName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            store = device.OpenPropertyStore(0);
            var key = FriendlyNameKey;
            store.GetValue(ref key, out var value);
            try
            {
                return value.VarType == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) ?? "Unknown device" : "Unknown device";
            }
            finally { NativeAudio.PropVariantClear(ref value); }
        }
        catch { return "Unknown device"; }
        finally { Release(store); }
    }

    internal static string? QueryProcessPath(uint pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    public static void Release(List<SessionInfo> sessions)
    {
        foreach (var session in sessions) Release(session.Com);
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        ResetDeviceCaches();
        Release(_policy);
        Release(_enumerator);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>Receives Core Audio callbacks on MTA threads; it only raises a flag for the UI-thread poll.</summary>
    sealed class Watcher : IMMNotificationClient, IAudioSessionNotification
    {
        public volatile bool Dirty = true;

        public int OnSessionCreated(IntPtr newSession) { Dirty = true; return 0; }
        public int OnDeviceStateChanged(string deviceId, uint newState) { Dirty = true; return 0; }
        public int OnDeviceAdded(string deviceId) { Dirty = true; return 0; }
        public int OnDeviceRemoved(string deviceId) { Dirty = true; return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) { Dirty = true; return 0; }
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
