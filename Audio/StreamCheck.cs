using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteMixer.Audio;

/// <summary>
/// Build-time check (--stream-check &lt;report&gt;) of the stream mix with real audio capture and no sound. A throwaway copy of
/// this program plays a -60 dB tone with its own session at 1% or less, or muted, set before it starts and held there,
/// and the mix goes to memory. A second part opens a real output with its own session muted. No other app, session,
/// device or setting is touched.
/// </summary>
internal static class StreamCheck
{
    const float ToneLevel = 0.001f;
    const float QuietestAllowed = 0.01f;

    /// <summary>The mix, measured instead of played.</summary>
    sealed class MemoryOutput : IStreamOutput
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        long _given;
        double _peak;

        public int Rate => 48000;
        public void Start() { }
        public void WaitForRoom() => Thread.Sleep(10);
        public int FramesFree() => (int)(_clock.Elapsed.TotalSeconds * Rate - _given);

        public void Write(float[] stereo, int frames)
        {
            _given += frames;
            for (int i = 0; i < frames * 2; i++) _peak = Math.Max(_peak, Math.Abs(stereo[i]));
        }

        public double TakePeak()
        {
            double peak = _peak;
            _peak = 0;
            return peak;
        }

        public void Dispose() { }
    }

    /// <summary>Read-only snapshot of the audio setup, to compare before and after testing.</summary>
    public static void WriteState(string path)
    {
        using var engine = new AudioEngine();
        var text = new StringBuilder();
        var outputs = engine.GetDevices(EDataFlow.Render);
        var inputs = engine.GetDevices(EDataFlow.Capture);
        string NameOf(IEnumerable<DeviceInfo> list, string? id) => list.FirstOrDefault(d => d.Id == id)?.Name ?? "(none)";
        text.AppendLine($"default output: {NameOf(outputs, engine.GetDefaultId(EDataFlow.Render))}");
        text.AppendLine($"default input: {NameOf(inputs, engine.GetDefaultId(EDataFlow.Capture))}");
        foreach (var device in outputs.Concat(inputs))
            text.AppendLine($"device: {device.Name} | volume {engine.GetEndpointVolume(device.Id):0.000} | muted {engine.GetEndpointMute(device.Id)}");
        var sessions = engine.GetSessions(outputs, includeSystemSounds: true);
        foreach (var session in sessions.Where(s => s.Key is not ("litemixer" or "looptest")).OrderBy(s => s.Key))
            text.AppendLine($"app: {session.Key} | volume {session.Volume?.GetMasterVolume():0.0000} | muted {session.Volume?.GetMute()} | pinned {(AppRouting.GetAppDevice(session.Pid, EDataFlow.Render) == null ? "no" : "yes")}");
        AudioEngine.Release(sessions);
        File.WriteAllText(path, text.ToString());
    }

    /// <summary>Listens to the far end of a virtual cable, as a streaming app would, and measures what arrives.</summary>
    sealed unsafe class CableListener : IDisposable
    {
        const uint EventCallback = 0x00040000;
        readonly Thread _thread;
        readonly string _deviceId;
        volatile bool _stop;
        double _peak;
        long _frames, _dropouts, _zeroRun;
        long _firstLoudTicks;
        volatile float _loudAbove = float.MaxValue;

        public string? Error { get; private set; }

        public CableListener(string deviceId)
        {
            _deviceId = deviceId;
            _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public long Frames => Interlocked.Read(ref _frames);

        /// <summary>The peak and the number of gaps since the last reset.</summary>
        public (double Peak, long Dropouts) TakeAndReset()
        {
            var result = (_peak, Interlocked.Read(ref _dropouts));
            _peak = 0;
            Interlocked.Exchange(ref _dropouts, 0);
            _zeroRun = 0;
            return result;
        }

        public void WatchFor(float level)
        {
            Interlocked.Exchange(ref _firstLoudTicks, 0);
            _loudAbove = level;
        }

        public long FirstLoudTicks => Interlocked.Read(ref _firstLoudTicks);

        void Run()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                var device = enumerator.GetDevice(_deviceId);
                var iid = typeof(IAudioClient).GUID;
                var client = (IAudioClient)device.Activate(ref iid, 1, IntPtr.Zero);
                var format = client.GetMixFormat();
                int channels = Marshal.ReadInt16(format, 2);
                var session = Guid.Empty;
                client.Initialize(0, EventCallback, 200_000, 0, format, ref session);
                Marshal.FreeCoTaskMem(format);
                using var ready = new AutoResetEvent(false);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                var captureIid = typeof(IAudioCaptureClient).GUID;
                var capture = (IAudioCaptureClient)client.GetService(ref captureIid);
                client.Start();
                while (!_stop)
                {
                    ready.WaitOne(100);
                    while (capture.GetNextPacketSize() > 0)
                    {
                        capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _);
                        var samples = (float*)data;
                        bool silent = (flags & 2) != 0;
                        for (uint f = 0; f < frames; f++)
                        {
                            float value = silent ? 0 : Math.Abs(samples[f * channels]);
                            if (value > _peak) _peak = value;
                            if (value > _loudAbove && Interlocked.Read(ref _firstLoudTicks) == 0) Interlocked.Exchange(ref _firstLoudTicks, Stopwatch.GetTimestamp());
                            // A 440 Hz tone never stays at exactly zero, so a run of them is a gap in the audio.
                            if (value == 0)
                            {
                                if (++_zeroRun == 8) Interlocked.Increment(ref _dropouts);
                            }
                            else
                            {
                                _zeroRun = 0;
                            }
                        }
                        capture.ReleaseBuffer(frames);
                        Interlocked.Add(ref _frames, frames);
                    }
                }
                client.Stop();
                AudioEngine.Release(capture);
                AudioEngine.Release(client);
                AudioEngine.Release(device);
            }
            catch (Exception ex)
            {
                Error = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                AudioEngine.Release(enumerator);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(1000);
        }
    }

    /// <summary>The whole path through a real virtual cable, still with the silent tone. Nothing here plays on a device anyone hears.</summary>
    static void RunCable(AudioEngine engine, Action<string, bool> check)
    {
        var outputs = engine.GetDevices(EDataFlow.Render);
        var inputs = engine.GetDevices(EDataFlow.Capture);
        var cableIn = outputs.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        var cableOut = inputs.FirstOrDefault(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        if (cableIn == null || cableOut == null)
        {
            check("no virtual cable here, so the cable part was skipped", true);
            return;
        }

        var defaultOutput = engine.GetDefaultId(EDataFlow.Render);
        var problem = StreamMath.DeviceProblem(cableIn.Id, outputs.Select(d => d.Id), defaultOutput == null ? [] : [defaultOutput]);
        check($"the cable is usable, not the default output ({cableIn.Name}; {problem ?? "no problem"})", problem == null);
        if (problem != null) return;
        check("the cable is recognized as a cable", StreamMath.LooksLikeCable(cableIn.Name));
        check("the listening device is refused", StreamMath.DeviceProblem(defaultOutput, outputs.Select(d => d.Id), [defaultOutput!]) != null);

        static bool Near(double value, double expected) => Math.Abs(value - expected) <= expected * 0.05;

        using var listener = new CableListener(cableOut.Id);
        using var mix = new StreamMix(() => new DeviceOutput(cableIn.Id));
        using var tone = StartTone(QuietestAllowed, mute: false);
        uint pid = (uint)tone.Id;
        float full = StreamMath.Gain(1, QuietestAllowed);
        mix.SetGain(pid, full);
        mix.SetSources([pid]);
        mix.Start();
        Thread.Sleep(2500); // Past the fade-in.

        check($"audio comes out of the far end of the cable ({listener.Frames} frames, {listener.Error ?? "no problem"})", listener.Frames > 0 && listener.Error == null);
        listener.TakeAndReset();
        Thread.Sleep(10000);
        var (peak, dropouts) = listener.TakeAndReset();
        check($"the level arrives exact (tone {ToneLevel:0.000000}, got {peak:0.000000})", Near(peak, ToneLevel));
        check($"no gaps in 10 seconds ({dropouts} found)", dropouts == 0);

        // Delay: silence, then see how long the tone takes to reach the far end.
        mix.SetGain(pid, 0);
        Thread.Sleep(700);
        listener.TakeAndReset();
        listener.WatchFor(ToneLevel / 4);
        long started = Stopwatch.GetTimestamp();
        mix.SetGain(pid, full);
        Thread.Sleep(800);
        double delay = (listener.FirstLoudTicks - started) * 1000.0 / Stopwatch.Frequency;
        check($"delay through the mix and cable: {delay:0} ms", listener.FirstLoudTicks > 0 && delay < 150);
        listener.TakeAndReset();

        // Mute: the session drops to the floor as the gain jumps. Without protection this is about 1000 times too loud.
        Thread.Sleep(300);
        listener.TakeAndReset();
        mix.SetGain(pid, StreamMath.Gain(1, StreamMath.Floor));
        tone.StandardInput.WriteLine(StreamMath.Floor.ToString(CultureInfo.InvariantCulture));
        tone.StandardInput.Flush();
        Thread.Sleep(1000);
        var (jumpPeak, _) = listener.TakeAndReset();
        check($"muting a channel makes no pop on stream (peak {jumpPeak:0.000000}, tone {ToneLevel:0.000000})", jumpPeak <= ToneLevel * 1.5);
        Thread.Sleep(600);
        var (afterMute, _) = listener.TakeAndReset();
        check($"after muting, the stream carries on at full level ({afterMute:0.000000})", Near(afterMute, ToneLevel));

        // Unmute: the gain falls first, then the session rises.
        mix.SetGain(pid, full);
        tone.StandardInput.WriteLine(QuietestAllowed.ToString(CultureInfo.InvariantCulture));
        tone.StandardInput.Flush();
        Thread.Sleep(1000);
        var (unmutePeak, _) = listener.TakeAndReset();
        check($"unmuting makes no pop either (peak {unmutePeak:0.000000})", unmutePeak <= ToneLevel * 1.5);
        Thread.Sleep(600);
        var (afterUnmute, _) = listener.TakeAndReset();
        check($"after unmuting, the level is back ({afterUnmute:0.000000})", Near(afterUnmute, ToneLevel));

        check($"every capture opened ({mix.LastCaptureError ?? "no errors"})", mix.LastCaptureError == null);
        check($"the mix ran throughout ({mix.Failure ?? "no problem"})", mix.IsAlive);
        tone.Kill();
    }

    static Process StartTone(float volume, bool mute)
    {
        var arguments = string.Create(CultureInfo.InvariantCulture, $"--stream-tone {volume} {(mute ? "mute" : "open")}");
        var tone = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true })!;
        var ready = tone.StandardOutput.ReadLine();
        if (ready != "ready") throw new InvalidOperationException($"The silent tone didn't start: {ready}");
        return tone;
    }

    public static void Run(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string name, bool ok) => report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        // Tight, because a stray factor is what this is here to catch.
        static bool Near(double value, double expected) => Math.Abs(value - expected) <= expected * 0.03;

        // If LiteMixer is installed and running, it treats this check as a new app and turns it down, which scales captures.
        using var engine = new AudioEngine();
        var devices = engine.GetDevices(EDataFlow.Render);
        using var hold = new System.Threading.Timer(_ => engine.HoldOwnSessionsAtFull(devices), null, 0, 50);

        try
        {
            var memory = new MemoryOutput();
            using var mix = new StreamMix(() => memory);
            mix.Start();

            double Measure(Process tone, float gain)
            {
                mix.SetGain((uint)tone.Id, gain);
                mix.SetSources([(uint)tone.Id]);
                Thread.Sleep(1200); // Past the fade-in and the capture opening.
                memory.TakePeak();
                Thread.Sleep(500);
                return memory.TakePeak();
            }

            Thread.Sleep(1100);

            using (var tone = StartTone(QuietestAllowed, mute: false))
            {
                double full = Measure(tone, StreamMath.Gain(1, QuietestAllowed));
                Check($"a stream level of 100% exactly undoes a personal level of 1% (tone {ToneLevel:0.000000}, got {full:0.000000})", Near(full, ToneLevel));
                double half = Measure(tone, StreamMath.Gain(0.5, QuietestAllowed));
                Check($"a stream level of 50% is half ({half:0.000000})", Near(half, ToneLevel / 2));
                double off = Measure(tone, 0);
                Check($"a stream level of 0% is silent ({off:0.000000})", off == 0);
                tone.Kill();
            }

            using (var tone = StartTone((float)StreamMath.Floor, mute: false))
            {
                double floor = Measure(tone, StreamMath.Gain(1, StreamMath.Floor));
                Check($"a channel silent for you at {StreamMath.Floor * 100:0.#####}% still reaches the stream in full ({floor:0.000000})", Near(floor, ToneLevel));
                tone.Kill();
            }

            using (var tone = StartTone(QuietestAllowed, mute: false))
            {
                // Mute: gain first, then the session, as the mixer does it. Without protection this is 1000 times too loud.
                double before = Measure(tone, StreamMath.Gain(1, QuietestAllowed));
                memory.TakePeak();
                mix.SetGain((uint)tone.Id, StreamMath.Gain(1, StreamMath.Floor));
                tone.StandardInput.WriteLine(StreamMath.Floor.ToString(CultureInfo.InvariantCulture));
                tone.StandardInput.Flush();
                Thread.Sleep(1000);
                double jump = memory.TakePeak();
                Check($"muting a channel makes no pop on stream (before {before:0.000000}, during {jump:0.000000})", jump <= ToneLevel * 1.5);
                Thread.Sleep(600);
                double after = memory.TakePeak();
                Check($"after muting, the stream carries on at full level ({after:0.000000})", Near(after, ToneLevel));

                mix.SetGain((uint)tone.Id, StreamMath.Gain(1, QuietestAllowed));
                tone.StandardInput.WriteLine(QuietestAllowed.ToString(CultureInfo.InvariantCulture));
                tone.StandardInput.Flush();
                Thread.Sleep(1000);
                double unmute = memory.TakePeak();
                Check($"unmuting makes no pop either ({unmute:0.000000})", unmute <= ToneLevel * 1.5);
                Thread.Sleep(600);
                double back = memory.TakePeak();
                Check($"after unmuting, the level is back ({back:0.000000})", Near(back, ToneLevel));
                tone.Kill();
            }

            using (var tone = StartTone(QuietestAllowed, mute: true))
            {
                double muted = Measure(tone, StreamMath.Gain(1, QuietestAllowed));
                Check($"a truly muted session captures nothing, which is why the floor exists ({muted:0.000000})", muted == 0);
                tone.Kill();
            }

            Check($"every capture opened ({mix.LastCaptureError ?? "no errors"})", mix.LastCaptureError == null);
            Check("the mix is still running", mix.IsAlive);
            mix.SetSources([]);
            Thread.Sleep(200);
            Check("a source can be removed while running", mix.IsAlive);
        }
        catch (Exception ex)
        {
            Check($"capture check finished ({ex.GetType().Name}: {ex.Message})", false);
        }

        try
        {
            using var mix = new StreamMix(() => new DeviceOutput(null, silentForCheck: true));
            mix.SetSources([]);
            mix.Start();
            Thread.Sleep(500);
            Check($"a real output opens and keeps taking audio, muted ({mix.FramesWritten} frames, {mix.Failure ?? "no problem"})", mix.IsAlive && mix.FramesWritten > 1000);
        }
        catch (Exception ex)
        {
            Check($"output check finished ({ex.GetType().Name}: {ex.Message})", false);
        }

        try
        {
            RunCable(engine, Check);
        }
        catch (Exception ex)
        {
            Check($"cable check finished ({ex.GetType().Name}: {ex.Message})", false);
        }

        File.WriteAllText(reportPath, report.ToString());
    }

    /// <summary>
    /// The throwaway process: inaudible by construction. It refuses a level above 1% unless muted, confirms its session
    /// before starting, and keeps re-applying it, because a running mixer may treat it as a new app and change it.
    /// </summary>
    public static unsafe void PlayTone(float level, bool mute)
    {
        if (level > QuietestAllowed)
        {
            Console.WriteLine("level too high, not starting");
            return;
        }
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        var device = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console);
        var iid = typeof(IAudioClient).GUID;
        var client = (IAudioClient)device.Activate(ref iid, 1, IntPtr.Zero);
        var format = client.GetMixFormat();
        int channels = Marshal.ReadInt16(format, 2), rate = Marshal.ReadInt32(format, 4), bits = Marshal.ReadInt16(format, 14);
        if (bits != 32)
        {
            Console.WriteLine($"unexpected format, not starting ({bits} bit)");
            return;
        }
        var session = Guid.Empty;
        client.Initialize(0, 0, 2_000_000, 0, format, ref session);
        var volumeIid = typeof(ISimpleAudioVolume).GUID;
        var volume = (ISimpleAudioVolume)client.GetService(ref volumeIid);
        var context = Guid.NewGuid();
        volume.SetMute(mute, ref context);
        volume.SetMasterVolume(level, ref context);
        if (volume.GetMute() != mute || volume.GetMasterVolume() > level + 0.0001f)
        {
            Console.WriteLine("couldn't confirm the session is silent, not starting");
            return;
        }

        var renderIid = typeof(IAudioRenderClient).GUID;
        var render = (IAudioRenderClient)client.GetService(ref renderIid);
        uint size = client.GetBufferSize();
        client.Start();
        Console.WriteLine("ready");
        Console.Out.Flush();

        // The check can lower or raise the level while running, but never past the limit above.
        var reader = new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
                if (float.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var next) && next <= QuietestAllowed)
                    level = next;
        }) { IsBackground = true };
        reader.Start();

        double phase = 0, step = 2 * Math.PI * 440 / rate;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            uint free = size - client.GetCurrentPadding();
            if (free > 0)
            {
                var samples = (float*)render.GetBuffer(free);
                for (uint f = 0; f < free; f++)
                {
                    float value = (float)(Math.Sin(phase) * ToneLevel);
                    phase += step;
                    for (int c = 0; c < channels; c++) samples[f * channels + c] = value;
                }
                render.ReleaseBuffer(free, 0);
            }
            volume.SetMute(mute, ref context);
            volume.SetMasterVolume(level, ref context);
            Thread.Sleep(10);
        }
        client.Stop();
    }
}
