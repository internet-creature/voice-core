using System.Globalization;

namespace VoiceCore.Batch.Corpus;

/// <summary>One corpus file's analysis, scored against its reference.</summary>
internal sealed class FileResult
{
    public required ManifestEntry Entry { get; init; }
    public required string Split { get; init; }
    public required string ReferenceSource { get; init; }
    public required float NoiseFloorDbfs { get; init; }
    public required string NoiseFloorSource { get; init; }
    public required int FrameCount { get; init; }
    public FrameStats Total { get; } = new();

    /// <summary>Per f0 band (spec §6): voiced frames by their reference f0, others by the file's median reference f0.</summary>
    public Dictionary<string, FrameStats> ByBand { get; } = [];
}

/// <summary>
/// Batch mode (spec §6): every corpus file through the same streaming analyzer as
/// live capture, scored frame by frame against its reference.
/// </summary>
internal sealed class CorpusRunner(CorpusManifest corpus, AnalysisConfig config)
{
    public static readonly string[] Bands = ["f0 < 150", "f0 150-250", "f0 > 250"];

    public static string BandOf(float hz) => hz < 150 ? Bands[0] : hz <= 250 ? Bands[1] : Bands[2];

    /// <summary>Called for every scored frame (calibration fitting collects raw scores here).</summary>
    public Action<FileResult, AnalysisFrame, RefPoint>? OnFrame { get; init; }

    /// <summary>Directory for per-file Parquet output, or null to skip writing frames.</summary>
    public string? FramesDirectory { get; init; }

    public List<string> Warnings { get; } = [];

    public FileResult? Run(ManifestEntry e)
    {
        string wavPath = corpus.PathOf(e.File);
        if (!File.Exists(wavPath))
        {
            Warnings.Add($"{e.File}: missing audio, skipped.");
            return null;
        }
        if (e.Reference.Length == 0 || !File.Exists(corpus.PathOf(e.Reference)))
        {
            Warnings.Add($"{e.File}: no reference (run tools/praat_reference.py), skipped.");
            return null;
        }
        var wav = WavReader.Read(wavPath);
        if (wav.SampleRate != VoiceAnalyzer.SampleRate)
        {
            // §6 accepts 44.1 kHz with a logged resample; no corpus file needs it yet
            Warnings.Add($"{e.File}: {wav.SampleRate} Hz, skipped (batch mode needs 48 kHz; resample first).");
            return null;
        }

        var reference = ReferenceTrack.Load(corpus.PathOf(e.Reference));
        string labelsPath = corpus.PathOf(Path.Combine("labels", e.Stem + ".csv"));
        var labels = File.Exists(labelsPath) ? HandLabels.Load(labelsPath) : null;

        var (floor, floorSource) = e.NoiseFloorDbfs is { } f ? (f, "manifest") : (EstimateNoiseFloor(wav.Samples), "estimated from file");
        var analyzer = new VoiceAnalyzer(config) { CalibratedNoiseFloorDbfs = floor };
        var frames = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(wav.Samples.Length)];
        int n = analyzer.Process(wav.Samples, frames);

        var refs = new RefPoint[n];
        for (int i = 0; i < n; i++)
        {
            var r = reference.At(frames[i].TimeSeconds);
            refs[i] = labels?.Apply(frames[i].TimeSeconds, r) ?? r;
        }

        var voicedRef = refs.Where(r => r.State == RefState.Voiced && r.F0Hz > 0).Select(r => r.F0Hz).Order().ToArray();
        string fileBand = voicedRef.Length > 0 ? BandOf(voicedRef[voicedRef.Length / 2]) : Bands[1];

        var result = new FileResult
        {
            Entry = e,
            Split = corpus.SplitOf(e),
            ReferenceSource = reference.Source,
            NoiseFloorDbfs = floor,
            NoiseFloorSource = floorSource,
            FrameCount = n,
        };
        foreach (string band in Bands)
            result.ByBand[band] = new FrameStats();
        for (int i = 0; i < n; i++)
        {
            var r = refs[i];
            string band = r.State == RefState.Voiced && r.F0Hz > 0 ? BandOf(r.F0Hz) : fileBand;
            result.Total.Score(frames[i], r, config.F0SearchMinHz, config.F0SearchMaxHz);
            result.ByBand[band].Score(frames[i], r, config.F0SearchMinHz, config.F0SearchMaxHz);
            OnFrame?.Invoke(result, frames[i], r);
        }

        if (FramesDirectory is not null)
            ParquetFrames.Write(Path.Combine(FramesDirectory, e.Stem + ".parquet"), frames.AsSpan(0, n), refs, config,
                new Dictionary<string, string>
                {
                    ["source_file"] = e.File,
                    ["reference"] = reference.Source,
                    ["noise_floor_dbfs"] = floor.ToString("0.0", CultureInfo.InvariantCulture) + " (" + floorSource + ")",
                });
        return result;
    }

    /// <summary>
    /// Files without a calibrated floor (public corpora): the live calibration's
    /// statistic, the 10th percentile of hop RMS (§3.2), over the whole file
    /// instead of 2 s of instructed silence.
    /// </summary>
    public static float EstimateNoiseFloor(float[] samples)
    {
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default with { Calibration = null });
        var frames = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(samples.Length)];
        int n = analyzer.Process(samples, frames);
        var calibration = new NoiseFloorCalibration(seconds: Math.Max(0.01, n * VoiceAnalyzer.HopSamples / (double)VoiceAnalyzer.SampleRate));
        for (int i = 0; i < n; i++)
            calibration.Add(frames[i]);
        return Math.Clamp(calibration.Result(), -100f, 0f);
    }
}
