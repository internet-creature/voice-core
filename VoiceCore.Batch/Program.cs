using System.Diagnostics;
using System.Globalization;
using VoiceCore;
using VoiceCore.Synthetic;

// batch mode proper lands at build step 5 (spec §6): it runs VoiceCore over a WAV
// corpus through the same streaming path and emits per-frame Parquet plus a
// summary CSV. until then this only exports synthetic suite signals.
switch (args)
{
    case ["synth", "list", .. var filter]:
        foreach (var c in Suites.All().Where(c => filter.Length == 0 || c.Name.StartsWith(filter[0], StringComparison.Ordinal)))
            Console.WriteLine(c.Name);
        return 0;

    case ["synth", var name, var outPath]:
        var match = Suites.All().FirstOrDefault(c => c.Name == name);
        if (match is null)
        {
            Console.Error.WriteLine($"no suite case named '{name}'; see 'synth list'.");
            return 1;
        }
        var signal = match.Build();
        WavWriter.WriteFloat32(outPath, signal.Samples);
        Console.WriteLine($"wrote {signal.Length} samples ({signal.Length / (double)SyntheticSignal.SampleRate:0.###} s) to {outPath}");
        return 0;

    case ["bench", .. var rest]:
        return Bench(rest.Length > 0 ? double.Parse(rest[0], CultureInfo.InvariantCulture) : 20);

    default:
        Console.Error.WriteLine("""
            usage:
              VoiceCore.Batch synth list [prefix]      list synthetic suite cases
              VoiceCore.Batch synth <case> <out.wav>   write one case as 48 kHz float WAV
              VoiceCore.Batch bench [seconds]          per-frame analysis cost (spec §3.4: measure on Steam Deck)
            corpus batch mode arrives at build step 5.
            """);
        return 1;
}

// feeds a voice-like signal one hop at a time (as live capture would) and times each
// frame. Every frame runs YIN, the expensive part (~1 M multiply-adds).
static int Bench(double seconds)
{
    var contour = PitchContour.Through((0, 110), (seconds / 2, 440), (seconds, 110));
    var signal = SyntheticSignal.Generate(new VoicedSegment(seconds, contour) { Noise = new NoiseSpec(NoiseKind.Aspiration, 12) });
    var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
    var output = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(VoiceAnalyzer.HopSamples)];

    // warm-up pass (JIT tiering), then the measured pass
    for (int pos = 0; pos + VoiceAnalyzer.HopSamples <= Math.Min(signal.Length, 48000); pos += VoiceAnalyzer.HopSamples)
        analyzer.Process(signal.Samples.AsSpan(pos, VoiceAnalyzer.HopSamples), output);
    analyzer.Reset();

    var perFrame = new List<double>();
    for (int pos = 0; pos + VoiceAnalyzer.HopSamples <= signal.Length; pos += VoiceAnalyzer.HopSamples)
    {
        long t0 = Stopwatch.GetTimestamp();
        int n = analyzer.Process(signal.Samples.AsSpan(pos, VoiceAnalyzer.HopSamples), output);
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (n > 0)
            perFrame.Add(ms);
    }
    perFrame.Sort();
    double P(double p) => perFrame[(int)Math.Min(perFrame.Count - 1, Math.Round(p / 100 * (perFrame.Count - 1)))];
    double hopMs = 1000.0 * VoiceAnalyzer.HopSamples / VoiceAnalyzer.SampleRate;
    Console.WriteLine($"{Environment.ProcessorCount} logical cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, SIMD width {System.Numerics.Vector<float>.Count} floats");
    Console.WriteLine($"{perFrame.Count} frames: mean {perFrame.Average():0.000} ms, p50 {P(50):0.000}, p99 {P(99):0.000}, max {perFrame[^1]:0.000} ms per frame");
    Console.WriteLine($"= {perFrame.Average() / hopMs * 100:0.0}% of one core in real time (a frame every {hopMs:0} ms)");
    return 0;
}
