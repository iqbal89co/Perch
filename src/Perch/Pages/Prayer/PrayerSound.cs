using System.Text;

namespace Perch.Pages.Prayer;

/// <summary>An original, soft two-note chime synthesised in code, so Perch ships no sound files.</summary>
public static class PrayerSound
{
    public const int SampleRate = 22050;
    public const double DurationSeconds = 1.6;

    /// <summary>
    /// 16-bit mono PCM WAV: a 660 Hz note, then an 880 Hz note 0.42 s later, both with exponential decay.
    /// Returns the complete file, RIFF header included.
    /// </summary>
    public static byte[] BuildChimeWav(int sampleRate = SampleRate)
    {
        var count = (int)Math.Round(sampleRate * DurationSeconds);
        var pcm = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            var value = 0.5 * Note(t, 0.00, 660, decay: 3.6)
                        + 0.42 * Note(t, 0.42, 880, decay: 4.2);
            var sample = (short)Math.Clamp(Math.Round(value * 0.6 * short.MaxValue), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                    // PCM fmt chunk size
        w.Write((short)1);              // PCM
        w.Write((short)1);              // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);        // byte rate
        w.Write((short)2);              // block align
        w.Write((short)16);             // bits per sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>One note: a sine with a short attack, a soft second partial and exponential decay.</summary>
    private static double Note(double t, double start, double frequency, double decay)
    {
        if (t < start) return 0;
        var u = t - start;
        var envelope = Math.Exp(-decay * u) * Math.Min(1, u / 0.008);
        var wave = Math.Sin(2 * Math.PI * frequency * u) + 0.2 * Math.Sin(2 * Math.PI * 2 * frequency * u);
        return envelope * wave / 1.2;
    }
}
