using System.Diagnostics;
using System.Globalization;
using VoiceCore;
using VoiceCore.Batch;
using VoiceCore.Batch.Corpus;
using VoiceCore.Synthetic;

// batch mode (spec §6): synthetic suite export, single-file analysis, and the
// corpus commands (build step 5), which run every corpus file through the same
// streaming path as live capture and score it against its reference.
switch (args)
{
    case ["corpus", .. var rest]:
        return CorpusCommands.Run(rest);

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

    case ["analyze", var inPath, var outPath, .. var options]:
        return Analyze(inPath, outPath, options);

    case ["bench", .. var rest]:
        return Bench(rest.Length > 0 ? double.Parse(rest[0], CultureInfo.InvariantCulture) : 20);

    default:
        Console.Error.WriteLine("""
            usage:
              VoiceCore.Batch synth list [prefix]      list synthetic suite cases
              VoiceCore.Batch synth <case> <out.wav>   write one case as 48 kHz float WAV
              VoiceCore.Batch bench [seconds]          per-frame analysis cost (spec §3.4: measure on Steam Deck)
              VoiceCore.Batch analyze <in.wav> <out.csv> [--floor <dBFS>]
                                                       per-frame CSV through the streaming path; the noise
                                                       floor comes from --floor, else the recording's
                                                       .txt sidecar, else the config default
              VoiceCore.Batch corpus ...               debug corpus: add, run, accept, calibrate (docs/corpus.md)
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

// runs a 48 kHz recording through the same streaming path as live capture and
// writes every frame. Header lines start with '#'.
static int Analyze(string inPath, string outPath, string[] options)
{
    var wav = WavReader.Read(inPath);
    if (wav.SampleRate != VoiceAnalyzer.SampleRate)
    {
        Console.Error.WriteLine($"{inPath}: {wav.SampleRate} Hz; batch mode needs 48000 Hz (resample first).");
        return 1;
    }

    var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
    string floorSource = "config default";
    int floorArg = Array.IndexOf(options, "--floor");
    if (floorArg >= 0 && floorArg + 1 < options.Length)
    {
        analyzer.CalibratedNoiseFloorDbfs = float.Parse(options[floorArg + 1], CultureInfo.InvariantCulture);
        floorSource = "--floor";
    }
    else if (SidecarFloor(inPath) is { } sidecar)
    {
        analyzer.CalibratedNoiseFloorDbfs = sidecar;
        floorSource = "sidecar";
    }

    var frames = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(wav.Samples.Length)];
    int n = analyzer.Process(wav.Samples, frames);

    using var w = new StreamWriter(outPath);
    w.WriteLine($"# source={Path.GetFileName(inPath)} channels={wav.Channels} bits={wav.BitsPerSample}{(wav.IsFloat ? "f" : "")} (channel 0 analyzed)");
    w.WriteLine($"# analyzer_version={analyzer.Config.AnalyzerVersion} config_hash={analyzer.Config.ComputeContentHash()} confidence_calibration={analyzer.Config.ConfidenceCalibration}");
    w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# noise_floor_dbfs={analyzer.CalibratedNoiseFloorDbfs} ({floorSource})"));
    w.WriteLine("time_s,voicing,voicing_conf,f0_hz,f0_raw_hz,f0_conf,f0_range,aperiodicity,rms_dbfs,peak_dbfs,clipping");
    string F(float v) => float.IsNaN(v) ? "" : v.ToString("0.#####", CultureInfo.InvariantCulture);
    for (int i = 0; i < n; i++)
    {
        var f = frames[i];
        w.WriteLine(string.Join(',',
            f.TimeSeconds.ToString("0.#####", CultureInfo.InvariantCulture), f.Voicing, F(f.VoicingConfidence),
            F(f.F0Hz), F(f.F0RawHz), F(f.F0Confidence), f.F0Range, F(f.Aperiodicity),
            F(f.RmsDbfs), F(f.PeakDbfs), f.Clipping ? 1 : 0));
    }
    int voiced = frames.AsSpan(0, n).ToArray().Count(f => f.Voicing == VoicingState.Voiced);
    Console.WriteLine($"{n} frames ({voiced} voiced) -> {outPath}");
    return 0;
}

// recordings from the probe carry a "noise_floor_dbfs: -63.2" line in <name>.txt
static float? SidecarFloor(string wavPath)
{
    string sidecar = Path.ChangeExtension(wavPath, ".txt");
    if (!File.Exists(sidecar))
        return null;
    foreach (string line in File.ReadLines(sidecar))
        if (line.StartsWith("noise_floor_dbfs:", StringComparison.Ordinal)
            && float.TryParse(line["noise_floor_dbfs:".Length..].Trim(), CultureInfo.InvariantCulture, out float v))
            return v;
    return null;
}
