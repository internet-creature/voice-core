using VoiceProbe.Capture;

namespace VoiceCore.Tests.Probe;

public class PolyphaseResamplerTests
{
    private static float[] Sine(double hz, int rate, int length, float amplitude = 0.5f) =>
        Enumerable.Range(0, length).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    private static float[] Resample(float[] input, int chunk = 441)
    {
        var r = PolyphaseResampler.Create44100To48000();
        var output = new List<float>();
        var buffer = new float[r.MaxOutputFor(chunk)];
        for (int pos = 0; pos < input.Length; pos += chunk)
        {
            int n = r.Process(input.AsSpan(pos, Math.Min(chunk, input.Length - pos)), buffer);
            output.AddRange(buffer.AsSpan(0, n).ToArray());
        }
        return output.ToArray();
    }

    /// <summary>Least-squares sinusoid at <paramref name="hz"/>: (amplitude, residual RMS).</summary>
    private static (double Amplitude, double ResidualRms) Fit(ReadOnlySpan<float> y, double hz, int rate)
    {
        double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
        for (int n = 0; n < y.Length; n++)
        {
            double s = Math.Sin(2 * Math.PI * hz * n / rate), c = Math.Cos(2 * Math.PI * hz * n / rate);
            ss += s * s; cc += c * c; sc += s * c; ys += y[n] * s; yc += y[n] * c;
        }
        double det = ss * cc - sc * sc;
        double a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det;
        double residual = 0;
        for (int n = 0; n < y.Length; n++)
        {
            double e = y[n] - a * Math.Sin(2 * Math.PI * hz * n / rate) - b * Math.Cos(2 * Math.PI * hz * n / rate);
            residual += e * e;
        }
        return (Math.Sqrt(a * a + b * b), Math.Sqrt(residual / y.Length));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10000)]
    [InlineData(15000)]
    [InlineData(19000)]
    public void PassbandToneKeepsItsLevelAndImagesAreBelowMinus80dB(double hz)
    {
        var output = Resample(Sine(hz, 44100, 44100));
        // skip the filter's startup transient and the tail
        var steady = output.AsSpan(4000, 40000);
        var (amplitude, residual) = Fit(steady, hz, 48000);

        Assert.Equal(0, 20 * Math.Log10(amplitude / 0.5), 0.1);           // passband ripple
        Assert.True(20 * Math.Log10(residual / amplitude) < -80,          // images and aliases
            $"residual {20 * Math.Log10(residual / amplitude):0.0} dB");
    }

    [Fact]
    public void OutputRateIsExactly48Over44point1()
    {
        var output = Resample(new float[44100 * 3]);
        Assert.InRange(output.Length, 48000 * 3 - 1, 48000 * 3 + 1);
    }

    [Fact]
    public void ChunkSizeDoesNotChangeOutput()
    {
        var input = Sine(1234, 44100, 20000);
        var reference = Resample(input, chunk: input.Length);
        Assert.Equal(reference, Resample(input, chunk: 1));
        Assert.Equal(reference, Resample(input, chunk: 256));
        Assert.Equal(reference, Resample(input, chunk: 4097));
    }

    [Fact]
    public void ReportedDelayMatchesMeasuredDelay()
    {
        // a single impulse comes out centered on the filter's group delay
        var input = new float[4410];
        input[1000] = 1;
        var output = Resample(input);
        int peak = Array.IndexOf(output, output.Max());
        double expected = 1000 * 48000.0 / 44100 + PolyphaseResampler.Create44100To48000().DelayOutputSamples;
        Assert.InRange(peak, expected - 1, expected + 1);
    }

    [Fact]
    public void SteadyStateDoesNotAllocate()
    {
        var r = PolyphaseResampler.Create44100To48000();
        var input = Sine(440, 44100, 256);
        var output = new float[r.MaxOutputFor(input.Length)];
        Allocation.AssertSteadyStateFree(() => r.Process(input, output), perWindow: 300);
    }
}

public class FormatPolicyTests
{
    private static Func<int, int, bool> Supports(params (int Rate, int Channels)[] formats) =>
        (rate, channels) => formats.Contains((rate, channels));

    [Fact]
    public void Prefers48kMono() =>
        Assert.Equal(new CaptureFormat(48000, 1), FormatPolicy.Choose("mic", 2, Supports((44100, 1), (48000, 2), (48000, 1))));

    [Fact]
    public void Prefers48kStereoOver44k1Mono() =>
        Assert.Equal(new CaptureFormat(48000, 2), FormatPolicy.Choose("mic", 2, Supports((44100, 1), (48000, 2))));

    [Fact]
    public void FallsBackTo44k1WithResampling()
    {
        var format = FormatPolicy.Choose("mic", 1, Supports((44100, 1), (96000, 1)));
        Assert.Equal(new CaptureFormat(44100, 1), format);
        Assert.True(format.Resample);
    }

    [Fact]
    public void OpensAllChannelsWhenThatIsTheOnlyOption() =>
        Assert.Equal(new CaptureFormat(48000, 8), FormatPolicy.Choose("array", 8, Supports((48000, 8))));

    [Fact]
    public void UnsupportedDevicesGetAnExplicitErrorListingNativeFormats()
    {
        var e = Assert.Throws<UnsupportedFormatException>(() => FormatPolicy.Choose("odd mic", 2, Supports((16000, 1), (96000, 2))));
        Assert.Contains("odd mic", e.Message);
        Assert.Contains("16000 Hz × 1 ch", e.Message);
        Assert.Contains("96000 Hz × 2 ch", e.Message);
    }
}

public class CaptureConverterTests
{
    [Fact]
    public void KeepsTheLeftChannel()
    {
        var output = new List<float>();
        var converter = new CaptureConverter(new CaptureFormat(48000, 3), s => output.AddRange(s.ToArray()), maxFramesPerChunk: 4);
        converter.WriteInterleaved([1, 9, 9, 2, 9, 9, 3, 9, 9, 4, 9, 9, 5, 9, 9]);
        Assert.Equal([1f, 2, 3, 4, 5], output);
    }

    [Fact]
    public void ResamplesWhenTheDeviceRunsAt44k1()
    {
        int written = 0;
        var converter = new CaptureConverter(new CaptureFormat(44100, 2), s => written += s.Length);
        converter.WriteInterleaved(new float[44100 * 2]);
        Assert.InRange(written, 47999, 48001);
        Assert.True(converter.ResamplerDelaySamples > 0);
    }

    [Fact]
    public void RejectsRatesItCannotConvert() =>
        Assert.Throws<ArgumentException>(() => new CaptureConverter(new CaptureFormat(96000, 1), _ => { }));

    [Fact]
    public void DeviceNamesAreFlattenedToOneLine() =>
        Assert.Equal("Headset (AirPods Pro)", DeviceCatalog.CleanName("Headset\r\n(AirPods Pro)"));
}

public class LoopbackAnalysisTests
{
    private const int Rate = 48000;
    private const int Buffer = 256;

    /// <summary>
    /// Simulated streams: output and input callbacks every 256 samples, input
    /// delivered <paramref name="pathSamples"/> after output hands samples over.
    /// </summary>
    private static LoopbackResult Simulate(int pathSamples, bool withBursts = true, double noise = 0.001)
    {
        double ticksPerSample = (double)System.Diagnostics.Stopwatch.Frequency / Rate;
        int total = Rate * 3;
        var burst = LoopbackTest.Burst(Rate, 0.5f);
        long[] emitted = [Rate / 2, Rate / 2 + 19200, Rate / 2 + 38400];

        var recorded = new float[total];
        var rng = new Random(1);
        for (int i = 0; i < total; i++)
            recorded[i] = (float)((rng.NextDouble() - 0.5) * 2 * noise);
        if (withBursts)
            foreach (long e in emitted)
                for (int j = 0; j < burst.Length; j++)
                    recorded[e + pathSamples + j] += burst[j];

        // a buffer's callback runs when its last sample exists: output at its start, input at its end
        var outStamps = Enumerable.Range(0, total / Buffer)
            .Select(b => new CallbackStamp((long)(b * Buffer * ticksPerSample), b * Buffer, Buffer)).ToArray();
        var inStamps = Enumerable.Range(0, total / Buffer)
            .Select(b => new CallbackStamp((long)((b + 1) * Buffer * ticksPerSample), b * Buffer, Buffer)).ToArray();

        return LoopbackTest.Analyze(recorded, Rate, inStamps, emitted, Rate, outStamps, intervalSeconds: 0.4);
    }

    [Theory]
    [InlineData(480)]    // 10 ms
    [InlineData(4800)]   // 100 ms
    [InlineData(7777)]   // not buffer-aligned
    public void RecoversTheTrueRoundTrip(int pathSamples)
    {
        var result = Simulate(pathSamples);
        Assert.Equal(3, result.Detected);
        Assert.All(result.RoundTripsMs, rt => Assert.Equal(pathSamples * 1000.0 / Rate, rt, 0.05));
    }

    [Fact]
    public void NoBurstsMeansNoDetections()
    {
        var result = Simulate(480, withBursts: false);
        Assert.Equal(0, result.Detected);
        Assert.Contains("no bursts detected", result.ToString());
    }
}
