using System.Reflection;

namespace VoiceCore.Tests;

internal static class TestSignals
{
    public static float[] Sine(double hz, float amplitude, int length, float dcOffset = 0f)
    {
        var x = new float[length];
        for (int i = 0; i < length; i++)
            x[i] = dcOffset + amplitude * (float)Math.Sin(2 * Math.PI * hz * i / VoiceAnalyzer.SampleRate);
        return x;
    }

    /// <summary>Sine + noise + DC offset: exercises every piece of temporal state.</summary>
    public static float[] Busy(int length, int seed = 1)
    {
        var rng = new Random(seed);
        var x = Sine(220, 0.4f, length, dcOffset: 0.1f);
        for (int i = 0; i < length; i++)
            x[i] += (float)(rng.NextDouble() - 0.5) * 0.2f;
        return x;
    }

    /// <summary>Runs a fresh analyzer over the signal, split by chunkSizes (cycled).</summary>
    public static List<AnalysisFrame> Analyze(float[] signal, Func<int> nextChunkSize, long startIndex = 0)
    {
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
        analyzer.Reset(startIndex);
        return Analyze(analyzer, signal, nextChunkSize);
    }

    public static List<AnalysisFrame> Analyze(VoiceAnalyzer analyzer, float[] signal, Func<int> nextChunkSize)
    {
        var frames = new List<AnalysisFrame>();
        int pos = 0;
        while (pos < signal.Length)
        {
            int n = Math.Min(nextChunkSize(), signal.Length - pos);
            var output = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(n)];
            int written = analyzer.Process(signal.AsSpan(pos, n), output);
            frames.AddRange(output.AsSpan(0, written).ToArray());
            pos += n;
        }
        return frames;
    }

    /// <summary>
    /// Field-by-field comparison on raw bits, so NaN payloads and ±0 count and
    /// "close enough" doesn't.
    /// </summary>
    public static void AssertBitIdentical(IReadOnlyList<AnalysisFrame> expected, IReadOnlyList<AnalysisFrame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        var properties = typeof(AnalysisFrame).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < expected.Count; i++)
        {
            foreach (var p in properties)
            {
                object? e = p.GetValue(expected[i]);
                object? a = p.GetValue(actual[i]);
                bool same = (e, a) switch
                {
                    (float fe, float fa) => BitConverter.SingleToInt32Bits(fe) == BitConverter.SingleToInt32Bits(fa),
                    (double de, double da) => BitConverter.DoubleToInt64Bits(de) == BitConverter.DoubleToInt64Bits(da),
                    _ => Equals(e, a),
                };
                Assert.True(same, $"frame {i}, {p.Name}: expected {e}, got {a}");
            }
        }
    }
}
