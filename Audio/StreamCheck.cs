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

    static Process StartTone(float volume, bool mute)
    {
        var arguments = string.Create(CultureInfo.InvariantCulture, $"--stream-tone {volume} {(mute ? "mute" : "open")}");
        var tone = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
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

        double phase = 0, step = 2 * Math.PI * 440 / rate;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
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
