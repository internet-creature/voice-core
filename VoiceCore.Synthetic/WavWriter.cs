using System.Text;

namespace VoiceCore.Synthetic;

/// <summary>Writes mono 32-bit float WAV, for listening and for Praat comparisons.</summary>
public static class WavWriter
{
    public static void WriteFloat32(string path, ReadOnlySpan<float> samples, int sampleRate = VoiceAnalyzer.SampleRate)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        int dataBytes = checked(samples.Length * sizeof(float));

        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);

        w.Write("fmt "u8);
        w.Write(16);                          // chunk size
        w.Write((short)3);                    // WAVE_FORMAT_IEEE_FLOAT
        w.Write((short)1);                    // mono
        w.Write(sampleRate);
        w.Write(sampleRate * sizeof(float));  // byte rate
        w.Write((short)sizeof(float));        // block align
        w.Write((short)32);                   // bits per sample

        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (float s in samples)
            w.Write(s);
    }
}
