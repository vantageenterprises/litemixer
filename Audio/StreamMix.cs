using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LiteMixer.Audio;

/// <summary>The decisions behind the stream mix, kept apart from the audio work so they can be checked without any sound.</summary>
internal static class StreamMath
{
    /// <summary>
    /// A captured app comes through at exactly its session's volume, and a muted session captures nothing. So while the
    /// stream mix is on, a channel that's silent for you sits at this level instead of being muted: -100 dB, far below
    /// hearing, and still a clean signal for the stream once the gain below undoes it.
    /// </summary>
    public const double Floor = 0.00001;
    public const float MaxGain = (float)(1 / Floor);

    public static (double Level, bool Mute) PersonalLevel(double level, bool mute) =>
        mute || level < Floor ? (Floor, false) : (level, false);

    /// <summary>Undoes the volume the session is really at, leaving the level wanted on the stream.</summary>
    public static float Gain(double streamLevel, double appliedLevel) =>
        appliedLevel <= 0 ? 0 : (float)Math.Clamp(streamLevel / appliedLevel, 0, MaxGain);

    /// <summary>
    /// Audio already buffered was captured at the old volume. Under a big gain jump, such as muting a channel, it would
    /// come out as a loud pop on the stream, so a jump this size drops the buffer and fades back in.
    /// </summary>
    public static bool IsGainJump(float current, float target) => current > 0 && target > current * 8;

    /// <summary>Broadcast apps can play their monitoring out through an app session. Capturing that would feed the stream back into itself.</summary>
    public static bool IsBroadcastApp(string key) => key is
        "obs64" or "obs32" or "obs" or "streamlabs desktop" or "streamlabs obs" or "slobs" or
        "xsplit.core" or "xsplit.broadcaster" or "xsplit.gamecaster" or "vmix" or "vmix64" or "wirecast";

    public static bool LooksLikeCable(string deviceName) =>
        Regex.IsMatch(deviceName, "cable|virtual|voicemeeter|vb-audio", RegexOptions.IgnoreCase);

    /// <summary>
    /// Capturing a process includes its children, so a launcher would bring its game along and the game would be heard twice.
    /// Only the most specific process is captured. A launcher's own sounds drop out while its game is making sound.
    /// </summary>
    public static HashSet<uint> CaptureTargets(IEnumerable<uint> pids, IReadOnlyDictionary<uint, uint> parents)
    {
        var all = pids.Where(p => p != 0).ToHashSet();
        var targets = new HashSet<uint>(all);
        foreach (var pid in all)
        {
            var seen = new HashSet<uint> { pid };
            var current = pid;
            while (parents.TryGetValue(current, out var parent) && parent != 0 && seen.Add(parent))
            {
                targets.Remove(parent);
                current = parent;
            }
        }
        return targets;
    }

    /// <summary>
    /// Why a device can't take the stream mix, or null if it can. Anything the user listens on is refused: the stream mix
    /// plays at its own level, so sending it to a headset could be far louder than their faders.
    /// </summary>
    public static string? DeviceProblem(string? deviceId, IEnumerable<string> connected, IEnumerable<string> listening)
    {
        if (string.IsNullOrEmpty(deviceId)) return "Choose an output device.";
        if (!connected.Contains(deviceId)) return "That device isn't connected.";
        if (listening.Contains(deviceId)) return "You're listening on that device, so the stream mix won't use it. Choose a virtual cable.";
        return null;
    }
}

/// <summary>Where the mix ends up.</summary>
internal interface IStreamOutput : IDisposable
{
    int Rate { get; }
    void Start();
    void WaitForRoom();
    int FramesFree();
    /// <summary>Takes stereo samples.</summary>
    void Write(float[] stereo, int frames);
}

/// <summary>Plays the mix to a real device through Windows' shared audio engine.</summary>
internal sealed unsafe class DeviceOutput : IStreamOutput
{
    const uint EventCallback = 0x00040000;
    const long BufferDuration = 400_000; // 40 ms

    readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
    readonly IMMDevice _device;
    readonly IAudioClient _client;
    readonly IAudioRenderClient _render;
    readonly AutoResetEvent _ready = new(false);
    readonly int _channels;
    readonly uint _size;

    /// <param name="silentForCheck">For the offscreen check: mutes this session before it can start, so nothing is heard.</param>
    public DeviceOutput(string? deviceId, bool silentForCheck = false)
    {
        _device = deviceId == null ? _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console) : _enumerator.GetDevice(deviceId);
        var iid = typeof(IAudioClient).GUID;
        _client = (IAudioClient)_device.Activate(ref iid, 1, IntPtr.Zero);

        var format = _client.GetMixFormat();
        try
        {
            ushort tag = (ushort)Marshal.ReadInt16(format, 0);
            _channels = Marshal.ReadInt16(format, 2);
            Rate = Marshal.ReadInt32(format, 4);
            int bits = Marshal.ReadInt16(format, 14);
            bool isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(format, 24) == 3);
            if (!isFloat || bits != 32) throw new NotSupportedException("That device uses an audio format the stream mix can't send to.");

            var session = Guid.Empty;
            _client.Initialize(0, EventCallback, BufferDuration, 0, format, ref session);
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }

        _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
        _size = _client.GetBufferSize();
        var renderIid = typeof(IAudioRenderClient).GUID;
        _render = (IAudioRenderClient)_client.GetService(ref renderIid);

        if (silentForCheck)
        {
            var volumeIid = typeof(ISimpleAudioVolume).GUID;
            var volume = (ISimpleAudioVolume)_client.GetService(ref volumeIid);
            var context = Guid.NewGuid();
            volume.SetMute(true, ref context);
            volume.SetMasterVolume(0.01f, ref context);
            if (!volume.GetMute()) throw new InvalidOperationException("Couldn't mute the check's own session.");
            Marshal.ReleaseComObject(volume);
        }
    }

    public int Rate { get; }

    public void Start() => _client.Start();

    public void WaitForRoom() => _ready.WaitOne(200);

    public int FramesFree() => (int)(_size - _client.GetCurrentPadding());

    public void Write(float[] stereo, int frames)
    {
        var buffer = (float*)_render.GetBuffer((uint)frames);
        for (int f = 0; f < frames; f++)
        {
            float left = stereo[f * 2], right = stereo[f * 2 + 1];
            if (_channels == 1)
            {
                buffer[f] = (left + right) / 2;
                continue;
            }
            int at = f * _channels;
            buffer[at] = left;
            buffer[at + 1] = right;
            for (int c = 2; c < _channels; c++) buffer[at + c] = 0;
        }
        _render.ReleaseBuffer((uint)frames, 0);
    }

    public void Dispose()
    {
        try { _client.Stop(); } catch { }
        AudioEngine.Release(_render);
        AudioEngine.Release(_client);
        AudioEngine.Release(_device);
        AudioEngine.Release(_enumerator);
        _ready.Dispose();
    }
}

/// <summary>
/// Captures each app separately, applies its stream level, and plays the sum to one output. Nothing here touches what the
/// user hears: it only reads copies of each app's audio, on its own thread, and a failure here stops only the stream mix.
/// </summary>
internal sealed unsafe class StreamMix : IDisposable
{
    const int Primed = 960;        // 20 ms at 48 kHz: how much to hold before a source starts playing
    const int MaxBuffered = 7200;  // 150 ms: past this a source has drifted or stalled, and catches up
    const uint SilentPacket = 2;
    const double FadeInSeconds = 1;

    readonly Func<IStreamOutput> _openOutput;
    readonly ConcurrentDictionary<uint, float> _gains = new();
    readonly ConcurrentQueue<Source> _opened = new();
    readonly ConcurrentDictionary<uint, DateTime> _retryAt = new();
    readonly Dictionary<uint, Source> _sources = new();
    readonly HashSet<uint> _opening = new();
    readonly Thread _thread;
    volatile HashSet<uint> _wanted = new();
    volatile bool _stop;
    int _rate;

    public StreamMix(Func<IStreamOutput> openOutput)
    {
        _openOutput = openOutput;
        _thread = new Thread(Run) { IsBackground = true, Name = "LiteMixer stream mix", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Set when the mix stops by itself, with the reason. Stays null while running.</summary>
    public string? Failure { get; private set; }
    /// <summary>Why the last app couldn't be captured. The mix carries on without it and tries again.</summary>
    public string? LastCaptureError { get; private set; }
    public bool IsAlive => _thread.IsAlive && Failure == null;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    long _framesWritten;

    public void Start() => _thread.Start();

    public void SetGain(uint pid, float gain) => _gains[pid] = gain;

    public void SetSources(HashSet<uint> pids) => _wanted = pids;

    void Run()
    {
        var mmcss = NativeAudio.AvSetMmThreadCharacteristicsW("Pro Audio", out _);
        try
        {
            using var output = _openOutput();
            _rate = output.Rate;
            var mix = new float[_rate * 2];
            output.Start();
            long fadeFrames = (long)(_rate * FadeInSeconds), played = 0;

            while (!_stop)
            {
                output.WaitForRoom();
                SyncSources();

                int free = Math.Min(output.FramesFree(), _rate);
                foreach (var source in _sources.Values) Drain(source);
                RemoveFailedSources();
                if (free <= 0) continue;

                Array.Clear(mix, 0, free * 2);
                foreach (var source in _sources.Values) source.MixInto(mix, free, _gains.GetValueOrDefault(source.Pid));
                for (int i = 0; i < free * 2; i++)
                {
                    float fade = played >= fadeFrames ? 1 : (float)(played + i / 2) / fadeFrames;
                    mix[i] = Math.Clamp(mix[i] * fade, -1f, 1f);
                }
                output.Write(mix, free);
                played += free;
                Interlocked.Add(ref _framesWritten, free);
            }
        }
        catch (Exception ex)
        {
            Failure = ex is NotSupportedException ? ex.Message : "The stream mix stopped because its device went away.";
        }
        finally
        {
            foreach (var source in _sources.Values) source.Dispose();
            _sources.Clear();
            while (_opened.TryDequeue(out var late)) late.Dispose();
            if (mmcss != IntPtr.Zero) NativeAudio.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    void SyncSources()
    {
        while (_opened.TryDequeue(out var source))
        {
            _opening.Remove(source.Pid);
            if (_wanted.Contains(source.Pid) && !_sources.ContainsKey(source.Pid)) _sources[source.Pid] = source;
            else source.Dispose();
        }

        var wanted = _wanted;
        foreach (var pid in _sources.Keys.Where(p => !wanted.Contains(p)).ToList())
        {
            _sources[pid].Dispose();
            _sources.Remove(pid);
        }
        foreach (var pid in wanted)
        {
            if (_sources.ContainsKey(pid) || _opening.Contains(pid)) continue;
            if (_retryAt.TryGetValue(pid, out var at) && at > DateTime.UtcNow) continue;
            _opening.Add(pid);
            int rate = _rate;
            // Opening a capture can take a moment, and the mix can't wait.
            Task.Run(() =>
            {
                try
                {
                    _opened.Enqueue(new Source(pid, rate));
                }
                catch (Exception ex)
                {
                    LastCaptureError = $"{ex.GetType().Name}: {ex.Message}";
                    _retryAt[pid] = DateTime.UtcNow.AddSeconds(10);
                    _opened.Enqueue(Source.Nothing(pid));
                }
            });
        }
    }

    void RemoveFailedSources()
    {
        foreach (var pid in _sources.Where(s => s.Value.Failed).Select(s => s.Key).ToList())
        {
            _sources[pid].Dispose();
            _sources.Remove(pid);
            _retryAt[pid] = DateTime.UtcNow.AddSeconds(10);
        }
    }

    static void Drain(Source source)
    {
        try
        {
            source.Drain();
        }
        catch (COMException)
        {
            source.Failed = true;
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(1000);
    }

    /// <summary>One app's captured audio, with a small buffer between its timing and the output's.</summary>
    sealed class Source : IDisposable
    {
        readonly IAudioClient? _client;
        readonly IAudioCaptureClient? _capture;
        readonly AutoResetEvent? _ready;
        readonly float[] _ring = new float[(MaxBuffered + 4800) * 2];
        int _read, _count;
        bool _primed;
        float _gain;

        public uint Pid { get; }
        public bool Failed { get; set; }

        Source(uint pid) => Pid = pid;

        public Source(uint pid, int rate)
        {
            Pid = pid;
            _client = OpenLoopback(pid);
            var format = Marshal.AllocHGlobal(18);
            try
            {
                Marshal.WriteInt16(format, 0, 3); // 32-bit float
                Marshal.WriteInt16(format, 2, 2);
                Marshal.WriteInt32(format, 4, rate);
                Marshal.WriteInt32(format, 8, rate * 8);
                Marshal.WriteInt16(format, 12, 8);
                Marshal.WriteInt16(format, 14, 32);
                Marshal.WriteInt16(format, 16, 0);
                var session = Guid.Empty;
                const uint flags = 0x00020000 /* loopback */ | 0x00040000 /* event */ | 0x80000000 /* convert */ | 0x08000000 /* good quality */;
                _client.Initialize(0, flags, 2_000_000, 0, format, ref session);
            }
            finally
            {
                Marshal.FreeHGlobal(format);
            }
            _ready = new AutoResetEvent(false);
            _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            var iid = typeof(IAudioCaptureClient).GUID;
            _capture = (IAudioCaptureClient)_client.GetService(ref iid);
            _client.Start();
        }

        /// <summary>A placeholder for a capture that couldn't open, so the mix knows the attempt is over.</summary>
        public static Source Nothing(uint pid) => new(pid) { Failed = true };

        public void Drain()
        {
            if (_capture == null) return;
            int capacity = _ring.Length / 2;
            while (_capture.GetNextPacketSize() > 0)
            {
                _capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _);
                var samples = (float*)data;
                bool silent = (flags & SilentPacket) != 0;
                for (uint f = 0; f < frames; f++)
                {
                    if (_count == capacity)
                    {
                        _read = (_read + 1) % capacity;
                        _count--;
                    }
                    int write = (_read + _count) % capacity;
                    _ring[write * 2] = silent ? 0 : samples[f * 2];
                    _ring[write * 2 + 1] = silent ? 0 : samples[f * 2 + 1];
                    _count++;
                }
                _capture.ReleaseBuffer(frames);
            }
        }

        public void MixInto(float[] mix, int frames, float target)
        {
            int capacity = _ring.Length / 2;
            if (StreamMath.IsGainJump(_gain, target))
            {
                _read = 0;
                _count = 0;
                _primed = false;
                _gain = 0;
            }
            if (_count > MaxBuffered)
            {
                int drop = _count - Primed;
                _read = (_read + drop) % capacity;
                _count -= drop;
            }
            if (!_primed && _count >= Primed) _primed = true;
            if (!_primed) return;

            int take = Math.Min(_count, frames);
            if (take == 0)
            {
                _primed = false;
                return;
            }
            float start = _gain, step = (target - start) / take;
            for (int f = 0; f < take; f++)
            {
                float gain = start + step * f;
                mix[f * 2] += _ring[_read * 2] * gain;
                mix[f * 2 + 1] += _ring[_read * 2 + 1] * gain;
                _read = (_read + 1) % capacity;
            }
            _count -= take;
            _gain = target;
        }

        static IAudioClient OpenLoopback(uint pid)
        {
            var handler = new ActivationHandler();
            var parameters = Marshal.AllocHGlobal(12);
            var variant = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(parameters, 0, 1);          // process loopback
                Marshal.WriteInt32(parameters, 4, (int)pid);
                Marshal.WriteInt32(parameters, 8, 0);          // the process and its children
                for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
                Marshal.WriteInt16(variant, 0, 65);            // VT_BLOB
                Marshal.WriteInt32(variant, 8, 12);
                Marshal.WriteIntPtr(variant, 16, parameters);

                var iid = typeof(IAudioClient).GUID;
                NativeAudio.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, handler, out var operation);
                if (!handler.Done.WaitOne(3000)) throw new TimeoutException();
                operation.GetActivateResult(out int result, out var activated);
                Marshal.ThrowExceptionForHR(result);
                return (IAudioClient)activated;
            }
            finally
            {
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(parameters);
                handler.Done.Dispose();
            }
        }

        public void Dispose()
        {
            try { _client?.Stop(); } catch { }
            AudioEngine.Release(_capture);
            AudioEngine.Release(_client);
            _ready?.Dispose();
        }

        sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new(false);
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
        }
    }

    // ---- Which process started which ----

    /// <summary>Every running process's parent, from a Windows snapshot.</summary>
    public static Dictionary<uint, uint> ReadParents()
    {
        var parents = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return parents;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry)) return parents;
            do parents[entry.ProcessId] = entry.ParentProcessId;
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return parents;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
