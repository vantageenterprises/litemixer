using System.Runtime.InteropServices;

namespace LiteMixer;

/// <summary>Short two-tone cues for mic mute (falling) and unmute (rising), played from memory.</summary>
internal static class Beeper
{
    const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004;
    const int SampleRate = 44100;

    // PlaySound with SND_ASYNC reads the buffer after returning, so it lives in unmanaged memory.
    static IntPtr _muteWave, _unmuteWave;
    static double _builtFor = -1;

    public static void Play(bool muted, double volumePercent)
    {
        if (volumePercent <= 0) return;
        if (Math.Abs(_builtFor - volumePercent) > 0.01) Build(volumePercent);
        PlaySound(muted ? _muteWave : _unmuteWave, IntPtr.Zero, SndAsync | SndNoDefault | SndMemory);
    }

    static void Build(double volumePercent)
    {
        // Stop anything still playing from the old buffers before freeing them.
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (_muteWave != IntPtr.Zero) Marshal.FreeHGlobal(_muteWave);
        if (_unmuteWave != IntPtr.Zero) Marshal.FreeHGlobal(_unmuteWave);

        // 50% on the setting is about -12 dBFS; the default 10% is a soft tick (about -26 dBFS).
        double amplitude = Math.Clamp(volumePercent, 0, 100) / 100 * 0.5;
        _muteWave = ToUnmanaged(Wave(amplitude, 880, 520));
        _unmuteWave = ToUnmanaged(Wave(amplitude, 520, 880));
        _builtFor = volumePercent;
    }

    static byte[] Wave(double amplitude, double firstHz, double secondHz)
    {
        const int toneMs = 70, gapMs = 25;
        int tone = SampleRate * toneMs / 1000, gap = SampleRate * gapMs / 1000;
        var samples = new short[tone * 2 + gap];
        WriteTone(samples, 0, tone, firstHz, amplitude);
        WriteTone(samples, tone + gap, tone, secondHz, amplitude);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);             // PCM
        writer.Write((short)1);             // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);       // byte rate
        writer.Write((short)2);             // block align
        writer.Write((short)16);            // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in samples) writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }

    static void WriteTone(short[] samples, int start, int length, double hz, double amplitude)
    {
        int fade = Math.Min(length / 4, SampleRate / 200);
        for (int i = 0; i < length; i++)
        {
            double envelope = Math.Min(1, Math.Min(i, length - 1 - i) / (double)fade);
            samples[start + i] = (short)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * amplitude * envelope * short.MaxValue);
        }
    }

    static IntPtr ToUnmanaged(byte[] data)
    {
        var buffer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, buffer, data.Length);
        return buffer;
    }

    [DllImport("winmm.dll")]
    static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
