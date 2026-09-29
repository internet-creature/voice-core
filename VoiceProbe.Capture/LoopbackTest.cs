using System.Diagnostics;
using PortAudioSharp;

namespace VoiceProbe.Capture;

/// <summary>A callback's buffer: when it was handed over, and where it sits in the stream.</summary>
/// <param name="StopwatchTimestamp">When the callback ran.</param>
/// <param name="StartSample">Stream index of the buffer's first sample.</param>
/// <param name="Frames">Samples in the buffer.</param>
public readonly record struct CallbackStamp(long StopwatchTimestamp, long StartSample, int Frames);

public sealed record LoopbackResult(int Emitted, IReadOnlyList<double> RoundTripsMs)
{
    public int Detected => RoundTripsMs.Count;
    public double MedianMs => Percentile(50);
    public double MinMs => RoundTripsMs.Count == 0 ? double.NaN : RoundTripsMs.Min();
    public double MaxMs => RoundTripsMs.Count == 0 ? double.NaN : RoundTripsMs.Max();

    private double Percentile(double p)
    {
        if (RoundTripsMs.Count == 0)
            return double.NaN;
        var sorted = RoundTripsMs.Order().ToArray();
        return sorted[(int)Math.Round(p / 100 * (sorted.Length - 1))];
    }

    public override string ToString() => Detected == 0
        ? $"no bursts detected (of {Emitted})"
        : $"round trip median {MedianMs:0.0} ms (min {MinMs:0.0}, max {MaxMs:0.0}), {Detected}/{Emitted} bursts detected";
}

/// <summary>
/// Round-trip latency (spec §3.1, §7 latencyOffsetMs): play tone bursts out of
/// one device and find them in another's input. The result is output buffering +
/// the acoustic or cable path + input buffering, from the app's point of view.
/// Run it speaker → mic, or through a loopback cable or virtual cable. It does
/// not include rendering: user-to-photon needs the camera test.
/// </summary>
public static class LoopbackTest
{
    public const double BurstSeconds = 0.003;
    public const double BurstHz = 1000;

    /// <summary>
    /// Runs the test live. Blocks for about <c>0.5 + bursts × interval</c> seconds.
    /// </summary>
    public static LoopbackResult Run(AudioDevice input, AudioDevice output, int bursts = 10, double intervalSeconds = 0.4, float amplitude = 0.5f)
    {
        DeviceCatalog.EnsureInitialized();
        var inFormat = DeviceCatalog.ChooseFormat(input);
        int outRate = new[] { 48000, 44100 }.FirstOrDefault(r => PortAudioInterop.SupportsOutput(output.Index, Math.Min(2, output.MaxOutputChannels), r), (int)output.DefaultSampleRate);
        int outChannels = Math.Min(2, output.MaxOutputChannels);

        double seconds = 0.5 + bursts * intervalSeconds + 0.3;
        var recorded = new float[(int)(seconds * inFormat.SampleRate) + 1];
        var inStamps = new CallbackStamp[(int)(seconds * inFormat.SampleRate / 32) + 16];
        var outStamps = new CallbackStamp[(int)(seconds * outRate / 32) + 16];
        long inCount = 0, outCount = 0;
        int inStampCount = 0, outStampCount = 0;

        var template = Burst(outRate, amplitude);
        long firstBurst = (long)(0.5 * outRate);
        long burstInterval = (long)(intervalSeconds * outRate);

        unsafe StreamCallbackResult OnInput(IntPtr inPtr, IntPtr _, uint frames, ref StreamCallbackTimeInfo t, StreamCallbackFlags f, IntPtr u)
        {
            long now = Stopwatch.GetTimestamp();
            var src = new ReadOnlySpan<float>((void*)inPtr, (int)frames * inFormat.Channels);
            for (int i = 0; i < frames && inCount + i < recorded.Length; i++)
                recorded[inCount + i] = src[i * inFormat.Channels];
            if (inStampCount < inStamps.Length)
                inStamps[inStampCount++] = new CallbackStamp(now, inCount, (int)frames);
            inCount += frames;
            return StreamCallbackResult.Continue;  // returning Complete would make the later Stop() throw
        }

        unsafe StreamCallbackResult OnOutput(IntPtr _, IntPtr outPtr, uint frames, ref StreamCallbackTimeInfo t, StreamCallbackFlags f, IntPtr u)
        {
            long now = Stopwatch.GetTimestamp();
            var dst = new Span<float>((void*)outPtr, (int)frames * outChannels);
            for (int i = 0; i < frames; i++)
            {
                long s = outCount + i;
                float v = 0;
                long offset = s - firstBurst;
                if (offset >= 0 && offset / burstInterval < bursts)
                {
                    long within = offset % burstInterval;
                    if (within < template.Length)
                        v = template[within];
                }
                for (int c = 0; c < outChannels; c++)
                    dst[i * outChannels + c] = v;
            }
            if (outStampCount < outStamps.Length)
                outStamps[outStampCount++] = new CallbackStamp(now, outCount, (int)frames);
            outCount += frames;
            return StreamCallbackResult.Continue;
        }

        PortAudioSharp.Stream.Callback inCallback = OnInput, outCallback = OnOutput;
        var inParams = new StreamParameters
        {
            device = input.Index, channelCount = inFormat.Channels, sampleFormat = SampleFormat.Float32,
            suggestedLatency = input.DefaultLowInputLatency,
        };
        var outParams = new StreamParameters
        {
            device = output.Index, channelCount = outChannels, sampleFormat = SampleFormat.Float32,
            suggestedLatency = PortAudio.GetDeviceInfo(output.Index).defaultLowOutputLatency,
        };

        using var inStream = new PortAudioSharp.Stream(inParams, null, inFormat.SampleRate, NativeCapture.FramesPerBuffer, StreamFlags.ClipOff, inCallback, null);
        using var outStream = new PortAudioSharp.Stream(null, outParams, outRate, NativeCapture.FramesPerBuffer, StreamFlags.ClipOff, outCallback, null);
        inStream.Start();
        outStream.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds + 0.2));
        outStream.Stop();
        inStream.Stop();
        GC.KeepAlive(inCallback);
        GC.KeepAlive(outCallback);

        var emissions = Enumerable.Range(0, bursts).Select(k => firstBurst + k * burstInterval).ToArray();
        return Analyze(recorded.AsSpan(0, (int)Math.Min(inCount, recorded.Length)), inFormat.SampleRate, inStamps.AsSpan(0, inStampCount),
            emissions, outRate, outStamps.AsSpan(0, outStampCount), intervalSeconds, amplitude);
    }

    /// <summary>
    /// Finds each emitted burst in the recording by cross-correlation and converts
    /// both ends to stopwatch time using the callback stamps. The buffer position of
    /// each sample is taken out, so buffer quantization doesn't bias the result.
    /// </summary>
    public static LoopbackResult Analyze(
        ReadOnlySpan<float> recorded, int inputRate, ReadOnlySpan<CallbackStamp> inputStamps,
        IReadOnlyList<long> emittedAt, int outputRate, ReadOnlySpan<CallbackStamp> outputStamps,
        double intervalSeconds, float amplitude = 0.5f)
    {
        var template = Burst(inputRate, amplitude);
        var roundTrips = new List<double>();
        double tick = Stopwatch.Frequency;

        foreach (long emitted in emittedAt)
        {
            // handoff time of the emitted sample: its buffer's callback, plus its position
            int o = Find(outputStamps, emitted);
            if (o < 0)
                continue;
            double emitSeconds = outputStamps[o].StopwatchTimestamp / tick + (emitted - outputStamps[o].StartSample) / (double)outputRate;

            // search input from the first buffer delivered after the handoff
            int i0 = FirstAfter(inputStamps, (long)(emitSeconds * tick));
            if (i0 < 0)
                continue;
            long searchFrom = Math.Max(0, inputStamps[Math.Max(0, i0 - 1)].StartSample);
            int searchLength = (int)Math.Min(recorded.Length - searchFrom - template.Length, intervalSeconds * 0.9 * inputRate);
            if (searchLength <= 0)
                continue;
            long onset = searchFrom + BestMatch(recorded.Slice((int)searchFrom, searchLength + template.Length), template, out bool clear);
            if (!clear)
                continue;

            // de-quantized arrival: when the onset sample would have arrived on its own
            int r = Find(inputStamps, onset);
            if (r < 0)
                continue;
            var stamp = inputStamps[r];
            double arriveSeconds = stamp.StopwatchTimestamp / tick - (stamp.StartSample + stamp.Frames - onset) / (double)inputRate;
            roundTrips.Add((arriveSeconds - emitSeconds) * 1000);
        }
        return new LoopbackResult(emittedAt.Count, roundTrips);
    }

    /// <summary>Hann-windowed 1 kHz tone burst.</summary>
    internal static float[] Burst(int rate, float amplitude)
    {
        var b = new float[(int)(BurstSeconds * rate)];
        for (int i = 0; i < b.Length; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (b.Length - 1));
            b[i] = (float)(amplitude * w * Math.Sin(2 * Math.PI * BurstHz * i / rate));
        }
        return b;
    }

    /// <summary>
    /// Offset of the best template match. <paramref name="clear"/> is false when the
    /// peak doesn't stand well above the window's typical correlation (no burst).
    /// </summary>
    private static int BestMatch(ReadOnlySpan<float> x, float[] template, out bool clear)
    {
        int lags = x.Length - template.Length + 1;
        var corr = new double[lags];
        int best = 0;
        for (int lag = 0; lag < lags; lag++)
        {
            double c = 0;
            for (int j = 0; j < template.Length; j++)
                c += x[lag + j] * template[j];
            corr[lag] = Math.Abs(c);
            if (corr[lag] > corr[best])
                best = lag;
        }
        Array.Sort(corr);
        double median = corr[lags / 2];
        clear = corr[^1] > 0 && corr[^1] > 8 * median;
        return best;
    }

    private static int Find(ReadOnlySpan<CallbackStamp> stamps, long sample)
    {
        for (int i = 0; i < stamps.Length; i++)
            if (sample >= stamps[i].StartSample && sample < stamps[i].StartSample + stamps[i].Frames)
                return i;
        return -1;
    }

    private static int FirstAfter(ReadOnlySpan<CallbackStamp> stamps, long stopwatchTimestamp)
    {
        for (int i = 0; i < stamps.Length; i++)
            if (stamps[i].StopwatchTimestamp >= stopwatchTimestamp)
                return i;
        return -1;
    }
}
