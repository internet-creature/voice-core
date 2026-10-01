using System.Globalization;
using Parquet;
using VoiceCore.Batch.Corpus;
using VoiceCore.Synthetic;

namespace VoiceCore.Tests;

public class CalibrationMapTests
{
    [Fact]
    public void InterpolatesBetweenKnotsAndIsFlatBeyondThem()
    {
        var map = new CalibrationMap([0.2f, 0.6f], [0.5f, 0.9f]);
        Assert.Equal(0.5f, map.Apply(0f));
        Assert.Equal(0.5f, map.Apply(0.2f));
        Assert.Equal(0.7f, map.Apply(0.4f), 5);
        Assert.Equal(0.9f, map.Apply(0.6f));
        Assert.Equal(0.9f, map.Apply(1f));
        Assert.True(float.IsNaN(map.Apply(float.NaN)));
    }

    [Fact]
    public void RejectsMapsThatArentMonotoneProbabilities()
    {
        Assert.Throws<ArgumentException>(() => new CalibrationMap([0.2f, 0.6f], [0.9f, 0.5f]));
        Assert.Throws<ArgumentException>(() => new CalibrationMap([0.6f, 0.2f], [0.5f, 0.9f]));
        Assert.Throws<ArgumentException>(() => new CalibrationMap([0.2f, 0.2f], [0.5f, 0.9f]));
        Assert.Throws<ArgumentException>(() => new CalibrationMap([0.2f], [1.5f]));
        Assert.Throws<ArgumentException>(() => new CalibrationMap([float.NaN], [0.5f]));
        Assert.Throws<ArgumentException>(() => new CalibrationMap([], []));
    }

    [Fact]
    public void AnalyzerPublishesCalibratedConfidencesAndRawWhereAStateIsUncalibrated()
    {
        var signal = SyntheticSignal.Generate([new SilentSegment(0.3), new VoicedSegment(0.5, PitchContour.Constant(200))], -70);
        var raw = Analyze(AnalysisConfig.Default with { Calibration = null }, signal.Samples);

        var table = new ConfidenceCalibrationTable
        {
            Name = "test",
            F0 = new CalibrationMap([0f], [0.42f]),
            Voiced = new CalibrationMap([0f], [0.77f]),
            // Silence left uncalibrated: raw score passes through
        };
        var calibrated = Analyze(AnalysisConfig.Default with { Calibration = table }, signal.Samples);

        Assert.Contains(calibrated, f => f.Voicing == VoicingState.Voiced);
        Assert.Contains(calibrated, f => f.Voicing == VoicingState.Silence);
        for (int i = 0; i < raw.Length; i++)
        {
            Assert.Equal(raw[i].Voicing, calibrated[i].Voicing);  // calibration never changes a decision
            if (calibrated[i].Voicing == VoicingState.Voiced)
            {
                Assert.Equal(0.77f, calibrated[i].VoicingConfidence);
                if (calibrated[i].F0Range == F0Range.In)
                    Assert.Equal(0.42f, calibrated[i].F0Confidence);
            }
            else
                Assert.Equal(raw[i].VoicingConfidence, calibrated[i].VoicingConfidence);
        }
        Assert.True(table.IsCalibrated(VoicingState.Voiced));
        Assert.False(table.IsCalibrated(VoicingState.Creak));
    }

    [Fact]
    public void DefaultConfigCarriesTheFittedCalibration()
    {
        Assert.Same(FittedCalibration.Table, AnalysisConfig.Default.Calibration);
        Assert.Equal(AnalysisConfig.Default.ComputeContentHash(), new AnalysisConfig().ComputeContentHash());
        if (FittedCalibration.Table is { } table)
        {
            Assert.Equal(table.Name, AnalysisConfig.Default.ConfidenceCalibration);
            Assert.NotEqual("0.2.0", AnalysisConfig.Default.AnalyzerVersion);  // calibrating bumped the version (§3.9)
        }
    }

    private static AnalysisFrame[] Analyze(AnalysisConfig config, float[] samples)
    {
        var analyzer = new VoiceAnalyzer(config) { CalibratedNoiseFloorDbfs = -70 };
        var frames = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(samples.Length)];
        return frames[..analyzer.Process(samples, frames)];
    }
}

public class CalibrationFitTests
{
    [Fact]
    public void RecoversAKnownMonotoneRelationship()
    {
        // P(correct) = 0.5 + 0.5·raw
        var rng = new Random(7);
        var samples = Enumerable.Range(0, 20000).Select(_ =>
        {
            float raw = (float)rng.NextDouble();
            return (raw, rng.NextDouble() < 0.5 + 0.5 * raw);
        }).ToList();
        var map = CalibrationFit.Fit(samples)!;
        foreach (float raw in new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f })
            Assert.Equal(0.5 + 0.5 * raw, map.Apply(raw), 0.05);
    }

    [Fact]
    public void OutputIsMonotoneEvenWhenTheDataIsnt()
    {
        // right more often at low raw scores than in the middle: PAV pools it flat
        var rng = new Random(3);
        var samples = Enumerable.Range(0, 5000).Select(_ =>
        {
            float raw = (float)rng.NextDouble();
            double p = raw < 0.3 ? 0.9 : raw < 0.6 ? 0.6 : 0.95;
            return (raw, rng.NextDouble() < p);
        }).ToList();
        var map = CalibrationFit.Fit(samples)!;
        for (int k = 1; k < map.Probability.Count; k++)
            Assert.True(map.Probability[k] >= map.Probability[k - 1]);
        Assert.All(map.Probability, p => Assert.InRange(p, 0.01f, 0.99f));  // Laplace: never exactly 0 or 1
    }

    [Fact]
    public void TooFewSamplesLeaveTheConfidenceRaw() =>
        Assert.Null(CalibrationFit.Fit(Enumerable.Repeat((0.5f, true), CalibrationFit.MinSamples - 1).ToList()));

    [Fact]
    public void TiedScoresAreNeverSplitAcrossKnots()
    {
        // two discrete raw scores with different rates → exactly two knots
        var samples = Enumerable.Range(0, 1000).Select(i => i < 500 ? (0.5f, i % 2 == 0) : (1f, i % 10 != 0)).ToList();
        var map = CalibrationFit.Fit(samples)!;
        Assert.Equal([0.5f, 1f], map.Raw);
        Assert.Equal(251 / 502.0, map.Probability[0], 4);
        Assert.Equal(451 / 502.0, map.Probability[1], 4);
    }

    [Fact]
    public void GeneratedSourceRoundTripsEveryKnot()
    {
        var table = new ConfidenceCalibrationTable
        {
            Name = "corpus-test",
            F0 = new CalibrationMap([0.1f, 0.10000001f, 0.7f], [0.3f, 0.5f, 0.9f]),
            Unvoiced = new CalibrationMap([0.5f], [0.8f]),
        };
        string source = CalibrationFit.GenerateSource(table, ["fit on test data"]);
        Assert.Contains("0.10000001f", source);  // round-trip format: adjacent knots stay distinct
        Assert.Contains("Creak = null", source);
        Assert.Contains("#nullable enable", source);
        Assert.Contains("// fit on test data", source);
    }
}

public class ReferenceTests
{
    private static ReferenceTrack Track(params (double T, RefState S, float F0)[] points) =>
        new(points.Select(p => p.T).ToArray(), points.Select(p => p.S).ToArray(), points.Select(p => p.F0).ToArray(), binary: true, []);

    [Fact]
    public void InterpolatesF0OnALogScaleBetweenVoicedPoints()
    {
        var track = Track((0.00, RefState.Voiced, 100), (0.01, RefState.Voiced, 400));
        var mid = track.At(0.005);
        Assert.Equal(RefState.Voiced, mid.State);
        Assert.Equal(200, mid.F0Hz, 2);  // geometric mean, not 250
    }

    [Fact]
    public void TheNearerPointWinsAtAVoicingBoundary()
    {
        var track = Track((0.00, RefState.Voiced, 100), (0.01, RefState.Unvoiced, float.NaN));
        Assert.Equal(RefState.Voiced, track.At(0.004).State);
        Assert.Equal(100, track.At(0.004).F0Hz);
        Assert.Equal(RefState.Unvoiced, track.At(0.006).State);
    }

    [Fact]
    public void MomentsBeyondHalfAHopPastEitherEndAreNotCovered()
    {
        var track = Track((1.00, RefState.Voiced, 100), (1.01, RefState.Voiced, 100), (1.02, RefState.Voiced, 100));
        Assert.Equal(RefState.Voiced, track.At(0.996).State);
        Assert.Equal(RefState.None, track.At(0.994).State);
        Assert.Equal(RefState.Voiced, track.At(1.024).State);
        Assert.Equal(RefState.None, track.At(1.026).State);
    }

    [Fact]
    public void HandLabelsOverrideTheStateAndMakeItFourState()
    {
        var track = Track((0.0, RefState.Voiced, 90), (1.0, RefState.Voiced, 90));
        var labels = new HandLabels([(0.2, 0.4, RefState.Creak), (0.4, 0.5, RefState.Exclude), (0.5, 0.6, RefState.Voiced)]);
        var creak = labels.Apply(0.3, track.At(0.3));
        Assert.Equal(RefState.Creak, creak.State);
        Assert.False(creak.Binary);
        Assert.True(float.IsNaN(creak.F0Hz));
        Assert.Equal(RefState.Exclude, labels.Apply(0.45, track.At(0.45)).State);
        Assert.Equal(90, labels.Apply(0.55, track.At(0.55)).F0Hz, 3);  // a Voiced label keeps the reference f0
        Assert.True(labels.Apply(0.8, track.At(0.8)).Binary);          // unlabeled: the reference as is
    }

    [Fact]
    public void LoadsTheReferenceFileFormat()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# source: test reference\n# states: binary (Voiced/Unvoiced)\ntime_s,state,f0_hz\n0.01,Unvoiced,\n0.02,Voiced,150.5\n");
            var track = ReferenceTrack.Load(path);
            Assert.True(track.Binary);
            Assert.Equal("test reference", track.Source);
            Assert.Equal(150.5f, track.At(0.02).F0Hz);
            Assert.Equal(RefState.Unvoiced, track.At(0.01).State);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ScoringTests
{
    private const float Min = 60, Max = 1000;

    private static AnalysisFrame Voiced(float f0, float conf = 0.95f, F0Range range = F0Range.In) => new()
    {
        Voicing = VoicingState.Voiced,
        F0Hz = range == F0Range.In ? f0 : float.NaN,
        F0Range = range,
        F0Confidence = range == F0Range.In ? conf : float.NaN,
        VoicingConfidence = conf,
    };

    private static AnalysisFrame Not(VoicingState state, float conf = 0.9f) => new()
    {
        Voicing = state, F0Hz = float.NaN, F0Confidence = float.NaN, VoicingConfidence = conf,
    };

    private static RefPoint RefVoiced(float f0) => new(RefState.Voiced, f0, Binary: true);
    private static readonly RefPoint RefNotVoiced = new(RefState.Unvoiced, float.NaN, Binary: true);

    [Fact]
    public void GrossFineAndVoicingErrorsFollowTheSpecDefinitions()
    {
        var s = new FrameStats();
        s.Score(Voiced(200), RefVoiced(200), Min, Max);                         // right
        s.Score(Voiced(206), RefVoiced(200), Min, Max);                         // fine error, +51 cents
        s.Score(Voiced(100), RefVoiced(200), Min, Max);                         // octave down: gross
        s.Score(Voiced(0, range: F0Range.Above), RefVoiced(200), Min, Max);     // no pitch on a pitched reference: gross
        s.Score(Not(VoicingState.Unvoiced), RefVoiced(200), Min, Max);          // missed voicing
        s.Score(Voiced(200), RefNotVoiced, Min, Max);                           // false voicing
        s.Score(Not(VoicingState.Silence), RefNotVoiced, Min, Max);             // right
        s.Score(Not(VoicingState.Silence), RefPoint.Missing, Min, Max);         // not covered: ignored

        Assert.Equal(7, s.Frames);
        Assert.Equal(2, s.VoicingMismatch);
        Assert.Equal(4, s.BothVoiced);
        Assert.Equal(2, s.Gross);
        Assert.Equal(50, Metric.Gpe.Compute(s), 6);
        Assert.Equal(100 * 2 / 7.0, Metric.Vde.Compute(s), 6);
        double cents = 1200 * Math.Log2(206 / 200.0);
        Assert.Equal(Math.Sqrt(cents * cents / 2), Metric.Fpe.Compute(s), 6);
        Assert.Equal(1, s.Confusion[(int)VoicingState.Unvoiced, (int)RefState.Voiced]);
        Assert.Equal(1, s.Confusion[(int)VoicingState.Silence, FrameStats.NotVoicedColumn]);
    }

    [Fact]
    public void OutOfRangeReferencesCountFoldsNotGrossErrors()
    {
        var s = new FrameStats();
        s.Score(Voiced(550), RefVoiced(1100), Min, Max);                        // folded into range
        s.Score(Voiced(0, range: F0Range.Above), RefVoiced(1100), Min, Max);    // flagged Above: right
        Assert.Equal(2, s.RefOutOfRange);
        Assert.Equal(1, s.Folds);
        Assert.Equal(0, s.BothVoiced);
    }

    [Fact]
    public void BinaryReferencesCantJudgeCreak()
    {
        Assert.Null(FrameStats.StateRight(Not(VoicingState.Creak), RefNotVoiced));
        Assert.True(FrameStats.StateRight(Not(VoicingState.Silence), RefNotVoiced));
        Assert.True(FrameStats.StateRight(Not(VoicingState.Unvoiced), RefNotVoiced));
        Assert.False(FrameStats.StateRight(Voiced(200), RefNotVoiced));
        var labelledCreak = new RefPoint(RefState.Creak, float.NaN, Binary: false);
        Assert.True(FrameStats.StateRight(Not(VoicingState.Creak), labelledCreak));
        Assert.False(FrameStats.StateRight(Not(VoicingState.Unvoiced), labelledCreak));
    }

    [Fact]
    public void CreakPrecisionCountsOnlyHandLabeledFrames()
    {
        // ChatGPT review: a binary reference's Voiced frame can't judge a published
        // Creak, so one right labeled Creak + one unjudgeable frame is 100%, not 50%
        var s = new FrameStats();
        s.Score(Not(VoicingState.Creak), new RefPoint(RefState.Creak, float.NaN, Binary: false), Min, Max);
        s.Score(Not(VoicingState.Creak), RefVoiced(200), Min, Max);
        var precision = Metric.All.Single(m => m.Name == "Creak precision");
        var recall = Metric.All.Single(m => m.Name == "Creak recall");
        Assert.Equal(100, precision.Compute(s));
        Assert.Equal(100, recall.Compute(s));

        // no hand labels at all: undefined (not reported), not 0%
        var binaryOnly = new FrameStats();
        binaryOnly.Score(Not(VoicingState.Creak), RefVoiced(200), Min, Max);
        Assert.True(double.IsNaN(precision.Compute(binaryOnly)));
        Assert.True(double.IsNaN(recall.Compute(binaryOnly)));
    }

    [Fact]
    public void F0ConfidenceIsJudgedOnlyWhereBothSidesHaveAPitch()
    {
        Assert.True(FrameStats.F0Right(Voiced(200), RefVoiced(210), Min, Max));
        Assert.False(FrameStats.F0Right(Voiced(100), RefVoiced(200), Min, Max));
        Assert.Null(FrameStats.F0Right(Voiced(200), RefNotVoiced, Min, Max));            // a voicing error instead
        Assert.Null(FrameStats.F0Right(Voiced(0, range: F0Range.Above), RefVoiced(200), Min, Max));
        Assert.Null(FrameStats.F0Right(Voiced(200), RefVoiced(1500), Min, Max));
    }

    [Fact]
    public void CoverageNeedsBothConfidencesAtTheFloor()
    {
        var s = new FrameStats();
        s.Score(Voiced(200, conf: 0.95f), RefVoiced(200), Min, Max);
        s.Score(Voiced(200, conf: 0.85f), RefVoiced(200), Min, Max);
        s.Score(Voiced(200, conf: 0.6f), RefVoiced(200), Min, Max);
        s.Score(Not(VoicingState.Unvoiced), RefVoiced(200), Min, Max);
        Assert.Equal(4, s.RefVoicedInRange);
        Assert.Equal(new long[] { 3, 2, 1 }, s.Covered);  // floors 0.5, 0.8, 0.9
    }

    [Fact]
    public void ReliabilityBinsGiveTheExpectedCalibrationError()
    {
        var bins = new ReliabilityBins();
        for (int i = 0; i < 100; i++)
            bins.Add(0.95f, i < 95);  // perfectly calibrated bin
        for (int i = 0; i < 100; i++)
            bins.Add(0.25f, i < 75);  // claims 0.25, observes 0.75
        Assert.Equal(0.25, bins.Ece(), 6);
    }

    [Fact]
    public void FineMedianUsesTheAbsoluteErrorHistogram()
    {
        var s = new FrameStats();
        foreach (float f0 in new[] { 201f, 202f, 210f })  // 8.6, 17.3, 84.5 cents
            s.Score(Voiced(f0), RefVoiced(200), Min, Max);
        Assert.Equal(17.5, Metric.FineMedianAbs.Compute(s));
    }
}

public class ReportTests
{
    private static FileResult File(string speaker, string split, int gross, int both = 100)
    {
        var r = new FileResult
        {
            Entry = new ManifestEntry { SpeakerId = speaker, File = $"audio/{speaker}-{Guid.NewGuid():N}.wav", Source = "public", Task = "read", Condition = "studio" },
            Split = split,
            ReferenceSource = "test",
            NoiseFloorDbfs = -70,
            NoiseFloorSource = "test",
            FrameCount = both,
        };
        foreach (string band in CorpusRunner.Bands)
            r.ByBand[band] = new FrameStats();
        var f = new AnalysisFrame { Voicing = VoicingState.Voiced, F0Range = F0Range.In, F0Confidence = 0.9f, VoicingConfidence = 0.9f };
        for (int i = 0; i < both; i++)
        {
            var frame = f with { F0Hz = i < gross ? 100 : 200 };
            r.Total.Score(frame, new RefPoint(RefState.Voiced, 200, true), 60, 1000);
            r.ByBand["f0 150-250"].Score(frame, new RefPoint(RefState.Voiced, 200, true), 60, 1000);
        }
        return r;
    }

    [Fact]
    public void TargetsGateOnlyQualifyingSlices()
    {
        // 3 speakers × 4 files = 12 files, 5% GPE: qualifies, misses the 2% target
        var big = Enumerable.Range(0, 12).Select(i => File($"s{i % 3}", "dev", gross: 5)).ToList();
        var report = new CorpusReport(big);
        var gpe = report.Rows.Single(r => r.Split == "dev" && r.Slice == "all" && r.Metric == "GPE");
        Assert.True(gpe.Qualifies);
        Assert.Contains("FAIL", gpe.Status);
        Assert.Contains(report.Failures, f => f.Contains("GPE"));

        // 2 speakers: reported with a CI, never gated
        var small = Enumerable.Range(0, 12).Select(i => File($"s{i % 2}", "dev", gross: 5)).ToList();
        var smallReport = new CorpusReport(small);
        Assert.False(smallReport.Rows.Single(r => r.Slice == "all" && r.Metric == "GPE").Qualifies);
        Assert.Empty(smallReport.Failures);
    }

    [Fact]
    public void RegressionBeyondTheCiFailsAndWithinItPasses()
    {
        var files = Enumerable.Range(0, 6).Select(i => File($"s{i % 2}", "heldout", gross: i % 2 == 0 ? 2 : 4)).ToList();
        var report = new CorpusReport(files);
        var gpe = report.Rows.Single(r => r.Slice == "all" && r.Metric == "GPE");
        Assert.True(gpe.CiHigh > gpe.CiLow);

        var near = new Dictionary<(string, string, string), (double, double, double)> { [("heldout", "all", "GPE")] = (gpe.Value - 0.1, 0, 0) };
        Assert.Empty(new CorpusReport(files, near).Failures);
        var far = new Dictionary<(string, string, string), (double, double, double)> { [("heldout", "all", "GPE")] = (gpe.Value - 2.5, 0, 0) };
        Assert.Contains(new CorpusReport(files, far).Failures, f => f.Contains("worsened"));
    }

    [Fact]
    public void OneFileSlicesFailOnAnyWorsening()
    {
        // ChatGPT review: a one-file slice has no CI, and the gate used to skip it
        // entirely (a siren slice could go from 0% to 100% GPE unnoticed)
        var worse = new List<FileResult> { File("s0", "dev", gross: 100) };
        var baseline = new Dictionary<(string, string, string), (double, double, double)> { [("dev", "all", "GPE")] = (0, double.NaN, double.NaN) };
        var report = new CorpusReport(worse, baseline);
        Assert.Contains(report.Failures, f => f.Contains("GPE") && f.Contains("worsened"));
        Assert.Contains("no CI", report.Rows.Single(r => r.Metric == "GPE" && r.Slice == "all").Status);

        var same = new List<FileResult> { File("s0", "dev", gross: 0) };
        Assert.Empty(new CorpusReport(same, baseline).Failures);
    }

    [Fact]
    public void BootstrapCisAreReproducible()
    {
        var files = Enumerable.Range(0, 8).Select(i => File($"s{i % 4}", "dev", gross: i)).ToList();
        var a = new CorpusReport(files).Rows.Single(r => r.Slice == "all" && r.Metric == "GPE");
        var b = new CorpusReport(files).Rows.Single(r => r.Slice == "all" && r.Metric == "GPE");
        Assert.Equal((a.CiLow, a.CiHigh), (b.CiLow, b.CiHigh));
    }

    [Fact]
    public void SummaryCsvRoundTrips()
    {
        var files = Enumerable.Range(0, 4).Select(i => File($"s{i}", "dev", gross: i)).ToList();
        var report = new CorpusReport(files);
        string path = Path.GetTempFileName();
        try
        {
            report.WriteSummaryCsv(path);
            var read = CorpusReport.ReadSummaryCsv(path);
            var gpe = report.Rows.Single(r => r.Slice == "all" && r.Metric == "GPE");
            Assert.Equal(gpe.Value, read[("dev", "all", "GPE")].Value);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}

/// <summary>
/// The whole batch path on a corpus built from a synthetic signal with exact truth
/// (spec §6 reference layer 1): manifest → WAV → streaming analyzer → reference
/// alignment → metrics → Parquet. Catches any time misalignment between frames and
/// references, which would show up as fine error on the glide.
/// </summary>
public class CorpusEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"voicecore-corpus-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SyntheticCorpusScoresCleanAndWritesParquet()
    {
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
        Directory.CreateDirectory(Path.Combine(_root, "reference"));
        var signal = SyntheticSignal.Generate(
        [
            new SilentSegment(0.5),
            new VoicedSegment(2.0, PitchContour.Through((0, 120), (1.0, 360), (2.0, 120))),
            new SilentSegment(0.5),
        ], backgroundNoiseRmsDbfs: -70);
        WavWriter.WriteFloat32(Path.Combine(_root, "audio", "glide.wav"), signal.Samples);

        using (var w = new StreamWriter(Path.Combine(_root, "reference", "glide.csv")))
        {
            w.WriteLine("# source: synthetic truth");
            w.WriteLine("# states: binary");
            w.WriteLine("time_s,state,f0_hz");
            for (long s = 0; s < signal.Length; s += 240)  // a 5 ms grid, deliberately not the analyzer's
            {
                bool voiced = signal.LabelAt(s) == TruthLabel.Voiced;
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{s / 48000.0:0.######},{(voiced ? "Voiced" : "Unvoiced")},{(voiced ? signal.F0At(s).ToString("R", CultureInfo.InvariantCulture) : "")}"));
            }
        }
        CorpusManifest.Append(_root, new ManifestEntry
        {
            SpeakerId = "synthetic", File = "audio/glide.wav", Source = "synthetic", Task = "glide",
            Condition = "clean", NoiseFloorDbfs = -70, Reference = "reference/glide.csv",
        });

        var corpus = CorpusManifest.Load(_root);
        string framesDir = Path.Combine(_root, "frames");
        Directory.CreateDirectory(framesDir);
        var runner = new CorpusRunner(corpus, AnalysisConfig.Default) { FramesDirectory = framesDir };
        var result = runner.Run(corpus.Entries.Single())!;

        Assert.Empty(runner.Warnings);
        Assert.Equal("dev", result.Split);
        Assert.Equal(0, result.Total.Gross);
        Assert.True(Metric.Fpe.Compute(result.Total) < 5, $"FPE {Metric.Fpe.Compute(result.Total):0.00} cents: frames and references misaligned?");
        Assert.True(Metric.Vde.Compute(result.Total) < 2, $"VDE {Metric.Vde.Compute(result.Total):0.00}%");
        Assert.True(result.ByBand["f0 < 150"].BothVoiced > 0 && result.ByBand["f0 > 250"].BothVoiced > 0);

        string parquet = Path.Combine(framesDir, "glide.parquet");
        Assert.True(File.Exists(parquet));
        await using var stream = File.OpenRead(parquet);  // the reader leaves the stream open
        await using var reader = await ParquetReader.CreateAsync(stream);
        Assert.Equal(result.FrameCount, reader.Metadata!.NumRows);
        Assert.Equal(AnalysisConfig.Default.ComputeContentHash(), reader.CustomMetadata["config_hash"]);
        Assert.Contains(reader.Schema.GetDataFields(), f => f.Name == "f0_confidence");
        Assert.Contains(reader.Schema.GetDataFields(), f => f.Name == "ref_f0_hz");
    }

    [Fact]
    public void FilesWithoutAReferenceOrAudioAreSkippedWithAWarning()
    {
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
        WavWriter.WriteFloat32(Path.Combine(_root, "audio", "a.wav"), new float[48000]);
        CorpusManifest.Append(_root, new ManifestEntry { SpeakerId = "x", File = "audio/a.wav" });
        CorpusManifest.Append(_root, new ManifestEntry { SpeakerId = "x", File = "audio/missing.wav", Reference = "reference/r.csv" });
        var corpus = CorpusManifest.Load(_root);
        var runner = new CorpusRunner(corpus, AnalysisConfig.Default);
        Assert.All(corpus.Entries, e => Assert.Null(runner.Run(e)));
        Assert.Equal(2, runner.Warnings.Count);
    }

    [Fact]
    public void TheCorpusFingerprintCoversAudioReferencesAndLabels()
    {
        // ChatGPT review: the hash covered only manifest and splits, so correcting a
        // reference or adding hand labels looked like the same corpus to the gate
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
        Directory.CreateDirectory(Path.Combine(_root, "reference"));
        Directory.CreateDirectory(Path.Combine(_root, "labels"));
        string audio = Path.Combine(_root, "audio", "a.wav");
        string reference = Path.Combine(_root, "reference", "a.csv");
        WavWriter.WriteFloat32(audio, new float[4800]);
        File.WriteAllText(reference, "time_s,state,f0_hz\n0.01,Voiced,200\n");
        CorpusManifest.Append(_root, new ManifestEntry { SpeakerId = "x", File = "audio/a.wav", Reference = "reference/a.csv" });

        string original = CorpusManifest.Load(_root).ManifestHash;
        Assert.Equal(original, CorpusManifest.Load(_root).ManifestHash);  // stable

        File.WriteAllText(reference, "time_s,state,f0_hz\n0.01,Voiced,100\n");
        string correctedReference = CorpusManifest.Load(_root).ManifestHash;
        Assert.NotEqual(original, correctedReference);

        File.WriteAllText(Path.Combine(_root, "labels", "a.csv"), "start_s,end_s,label\n0,1,Exclude\n");
        string labeled = CorpusManifest.Load(_root).ManifestHash;
        Assert.NotEqual(correctedReference, labeled);

        var samples = new float[4800];
        samples[100] = 0.5f;
        WavWriter.WriteFloat32(audio, samples);
        Assert.NotEqual(labeled, CorpusManifest.Load(_root).ManifestHash);
    }

    [Fact]
    public void SplitsAreBySpeakerAndValidated()
    {
        Directory.CreateDirectory(_root);
        CorpusManifest.Append(_root, new ManifestEntry { SpeakerId = "a", File = "audio/1.wav", NoiseFloorDbfs = -63.2f });
        CorpusManifest.Append(_root, new ManifestEntry { SpeakerId = "b", File = "audio/2.wav" });
        File.WriteAllText(Path.Combine(_root, "splits.csv"), "speaker_id,split\na,heldout\n");
        var corpus = CorpusManifest.Load(_root);
        Assert.Equal("heldout", corpus.SplitOf(corpus.Entries[0]));
        Assert.Equal("dev", corpus.SplitOf(corpus.Entries[1]));  // unlisted: dev
        Assert.Equal(-63.2f, corpus.Entries[0].NoiseFloorDbfs);
        Assert.Null(corpus.Entries[1].NoiseFloorDbfs);

        File.WriteAllText(Path.Combine(_root, "splits.csv"), "speaker_id,split\na,test\n");
        Assert.Throws<InvalidDataException>(() => CorpusManifest.Load(_root));
    }
}
