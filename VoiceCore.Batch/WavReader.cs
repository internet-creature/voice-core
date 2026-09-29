using System.Text;

namespace VoiceCore.Batch;

/// <summary>
/// Minimal RIFF/WAV reader: PCM 16/24/32-bit and IEEE float 32-bit, any channel
/// count (channel 0 is kept, per the §3 downmix policy). Batch mode's corpus is
/// 48 kHz; other rates are reported, not silently analyzed.
/// </summary>
internal static class WavReader
{
    public sealed record Wav(float[] Samples, int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

    public static Wav Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
        if (Tag(r) != "RIFF")
            throw new InvalidDataException($"{path}: not a RIFF file.");
        r.ReadInt32();
        if (Tag(r) != "WAVE")
            throw new InvalidDataException($"{path}: not a WAVE file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            string id = Tag(r);
            int size = r.ReadInt32();
            long next = r.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                if (format == unchecked((short)0xFFFE) && size >= 40)  // WAVE_FORMAT_EXTENSIBLE: real format in the subformat GUID
                {
                    r.ReadInt16();
                    r.ReadInt16();
                    r.ReadInt32();
                    format = r.ReadInt16();
                }
            }
            else if (id == "data")
            {
                if (channels == 0)
                    throw new InvalidDataException($"{path}: data before fmt.");
                int bytesPerSample = bits / 8;
                long frames = size / (bytesPerSample * channels);
                var samples = new float[frames];
                var frame = new byte[bytesPerSample * channels];
                for (long i = 0; i < frames; i++)
                {
                    r.BaseStream.ReadExactly(frame);
                    samples[i] = Decode(frame, format, bits);  // channel 0
                }
                return new Wav(samples, rate, channels, bits, format == 3);
            }
            r.BaseStream.Position = next;
        }
        throw new InvalidDataException($"{path}: no data chunk.");
    }

    private static float Decode(ReadOnlySpan<byte> b, int format, int bits) => (format, bits) switch
    {
        (3, 32) => BitConverter.ToSingle(b),
        (1, 16) => BitConverter.ToInt16(b) / 32768f,
        (1, 24) => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608f,
        (1, 32) => BitConverter.ToInt32(b) / 2147483648f,
        _ => throw new NotSupportedException($"WAV format {format} with {bits}-bit samples isn't supported."),
    };

    private static string Tag(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(4));
}
