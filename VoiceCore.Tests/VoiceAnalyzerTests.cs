namespace VoiceCore.Tests;

public class VoiceAnalyzerTests
{
    private static VoiceAnalyzer NewAnalyzer() => new(AnalysisConfig.Default);

    // --- timing model (§3.1) ---

    [Fact]
    public void FirstFrameAppearsWhenWindowFills()
    {
        var analyzer = NewAnalyzer();
        var output = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(VoiceAnalyzer.WindowSamples)];

        Assert.Equal(0, analyzer.Process(new float[VoiceAnalyzer.WindowSamples - 1], output));
        Assert.Equal(1, analyzer.Process(new float[1], output));
    }

    [Fact]
    public void FramesSitOnTheHopGridWithFixedDelay()
    {
        const long start = 1_000_000;
        var frames = TestSignals.Analyze(TestSignals.Busy(20_000), () => 333, startIndex: start);
        var analyzer = NewAnalyzer();

        Assert.Equal(1024, analyzer.AlgorithmicDelaySamples);
        for (int k = 0; k < frames.Count; k++)
        {
            long center = start + VoiceAnalyzer.WindowSamples / 2 + (long)k * VoiceAnalyzer.HopSamples;
            Assert.Equal(center, frames[k].WindowCenterSample);
            Assert.Equal(center + analyzer.AlgorithmicDelaySamples, frames[k].ResultAvailableSample);
            Assert.Equal(center / 48000.0, frames[k].TimeSeconds);
        }
    }

    [Fact]
    public void ResetClearsStateAndRestartsTheGrid()
    {
        var first = TestSignals.Busy(10_000, seed: 1);
        var second = TestSignals.Busy(10_000, seed: 2);

        var reused = NewAnalyzer();
        TestSignals.Analyze(reused, first, () => 700);
        reused.Reset(500_000);
        var afterReset = TestSignals.Analyze(reused, second, () => 700);

        var fresh = TestSignals.Analyze(second, () => 700, startIndex: 500_000);
        TestSignals.AssertBitIdentical(fresh, afterReset);
    }

    // --- output-capacity contract (§1.1) ---

    [Fact]
    public void MaxFramesForIsReachable()
    {
        var analyzer = NewAnalyzer();
        var output = new AnalysisFrame[8];
        analyzer.Process(new float[VoiceAnalyzer.WindowSamples - 1], output);  // next frame end is 1 sample away

        int input = 3 * VoiceAnalyzer.HopSamples + 1;
        Assert.Equal(VoiceAnalyzer.MaxFramesFor(input), analyzer.Process(new float[input], output));
    }

    [Fact]
    public void MaxFramesForIsNeverExceeded()
    {
        var analyzer = NewAnalyzer();
        var rng = new Random(7);
        var signal = TestSignals.Busy(200_000);
        int pos = 0;
        while (pos < signal.Length)
        {
            int n = Math.Min(rng.Next(0, 3000), signal.Length - pos);
            var output = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(n)];
            Assert.InRange(analyzer.Process(signal.AsSpan(pos, n), output), 0, output.Length);
            pos += n;
        }
    }

    [Fact]
    public void UndersizedOutputThrows()
    {
        var analyzer = NewAnalyzer();
        var input = new float[4096];
        var output = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(input.Length) - 1];
        Assert.Throws<ArgumentException>(() => analyzer.Process(input, output));
    }

    // --- level (§3.2) ---

    [Fact]
    public void RmsIsMeasuredAfterDcRemoval()
    {
        // 1 kHz: exactly 10 cycles per hop, so the hop RMS is exact
        var frames = TestSignals.Analyze(TestSignals.Sine(1000, 0.5f, 48_000, dcOffset: 0.3f), () => 256);
        float expected = 20f * MathF.Log10(0.5f / MathF.Sqrt(2));

        foreach (var f in frames.Skip(10))  // DC filter has settled after ~0.1 s
            Assert.Equal(expected, f.RmsDbfs, 0.05f);
    }

    [Fact]
    public void PeakAndClippingUseRawInput()
    {
        var withDc = TestSignals.Analyze(TestSignals.Sine(1000, 0.5f, 10_000, dcOffset: 0.3f), () => 480);
        Assert.All(withDc, f =>
        {
            Assert.Equal(20f * MathF.Log10(0.8f), f.PeakDbfs, 0.01f);
            Assert.False(f.Clipping);
        });

        var fullScale = TestSignals.Analyze(TestSignals.Sine(1000, 1.0f, 10_000), () => 480);
        Assert.All(fullScale, f => Assert.True(f.Clipping));
    }

    [Fact]
    public void DigitalSilenceFloorsAtMinus120()
    {
        var frames = TestSignals.Analyze(new float[10_000], () => 480);
        Assert.All(frames, f =>
        {
            Assert.Equal(-120f, f.RmsDbfs);
            Assert.Equal(-120f, f.PeakDbfs);
            Assert.False(f.Clipping);
        });
    }

    [Fact]
    public void UnimplementedMeasurementsAreNaN()
    {
        // pitch and voicing arrive in step 4; the display track (§3.5), formants,
        // CPP and the brightness proxy are later steps
        var f = TestSignals.Analyze(TestSignals.Busy(5000), () => 5000)[0];
        Assert.Equal(VoicingState.Voiced, f.Voicing);
        Assert.False(float.IsNaN(f.F0Hz));
        float[] notYet =
        [
            f.F0DisplayHz,
            f.F1Hz, f.B1Hz, f.F2Hz, f.B2Hz, f.F3Hz, f.B3Hz, f.F4Hz, f.B4Hz, f.FormantConfidence,
            f.CppDb, f.BrightnessProxy, f.SpectralTiltDbPerKhz,
        ];
        Assert.All(notYet, v => Assert.True(float.IsNaN(v)));
    }

    // --- diagnostics and allocation (§1.1, §2) ---

    [Fact]
    public void DiagnosticsCountFramesAndSurviveReset()
    {
        var analyzer = NewAnalyzer();
        var frames = TestSignals.Analyze(analyzer, TestSignals.Busy(10_000), () => 480);
        analyzer.Reset();

        Assert.Equal(frames.Count, analyzer.Diagnostics.FramesProduced);
        Assert.True(analyzer.Diagnostics.MaxAnalysisTimePerFrame > TimeSpan.Zero);
    }

    [Fact]
    public void SteadyStateProcessDoesNotAllocate()
    {
        var analyzer = NewAnalyzer();
        var input = TestSignals.Busy(256);
        var output = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(input.Length)];
        Allocation.AssertSteadyStateFree(() => analyzer.Process(input, output));
    }

    // --- config (§1.1) ---

    [Fact]
    public void ContentHashTracksEveryParameter()
    {
        var baseline = AnalysisConfig.Default;
        Assert.Equal(baseline.ComputeContentHash(), new AnalysisConfig().ComputeContentHash());
        Assert.NotEqual(baseline.ComputeContentHash(), (baseline with { DcFilterPole = 0.99f }).ComputeContentHash());
        Assert.NotEqual(baseline.ComputeContentHash(), (baseline with { AnalyzerVersion = "0.1.1" }).ComputeContentHash());
        var table = new ConfidenceCalibrationTable { Name = "t", F0 = new CalibrationMap([0f, 1f], [0.2f, 0.9f]) };
        var calibrated = baseline with { Calibration = table };
        Assert.NotEqual(baseline.ComputeContentHash(), calibrated.ComputeContentHash());
        // every knot is covered, not just the name
        Assert.NotEqual(calibrated.ComputeContentHash(),
            (baseline with { Calibration = table with { F0 = new CalibrationMap([0f, 1f], [0.2f, 0.95f]) } }).ComputeContentHash());
        Assert.NotEqual(calibrated.ComputeContentHash(),
            (baseline with { Calibration = table with { Voiced = new CalibrationMap([0.5f], [0.9f]) } }).ComputeContentHash());
    }

    [Fact]
    public void InvalidConfigIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new VoiceAnalyzer(AnalysisConfig.Default with { DcFilterPole = 1f }));
        Assert.Throws<ArgumentException>(() => new VoiceAnalyzer(AnalysisConfig.Default with { ClippingThresholdDbfs = 1f }));
    }

    public static TheoryData<string, Func<AnalysisConfig, AnalysisConfig>> BadConfigs => new()
    {
        // regression (Sol review of 4a): NaN noise floor was accepted and silenced every frame
        { "noise floor NaN", c => c with { DefaultNoiseFloorDbfs = float.NaN } },
        { "noise floor +∞", c => c with { DefaultNoiseFloorDbfs = float.PositiveInfinity } },
        { "noise floor above 0 dBFS", c => c with { DefaultNoiseFloorDbfs = 5 } },
        { "floor time constant NaN", c => c with { NoiseFloorTimeConstantSeconds = float.NaN } },
        { "level margin NaN", c => c with { VoicedLevelMarginDb = float.NaN } },
        { "level margin negative", c => c with { VoicedLevelMarginDb = -1 } },
        { "voiced aperiodicity NaN", c => c with { VoicedAperiodicityMax = float.NaN } },
        { "breathy aperiodicity +∞", c => c with { BreathyAperiodicityMax = float.PositiveInfinity } },
        { "stable cents NaN", c => c with { StableCents = float.NaN } },
        { "stable cents zero", c => c with { StableCents = 0 } },
        { "low ZCR NaN", c => c with { LowZcrPerSecond = float.NaN } },
        { "YIN threshold NaN", c => c with { YinThreshold = float.NaN } },
        { "f0 floor NaN", c => c with { F0SearchMinHz = float.NaN } },
        { "f0 ceiling NaN", c => c with { F0SearchMaxHz = float.NaN } },
        { "f0 ceiling above fs/3", c => c with { F0SearchMaxHz = 20_000 } },
        { "f0 floor too low for the window", c => c with { F0SearchMinHz = 47 } },
        { "clipping threshold NaN", c => c with { ClippingThresholdDbfs = float.NaN } },
    };

    [Theory]
    [MemberData(nameof(BadConfigs))]
    public void NonFiniteOrOutOfRangeSettingsAreRejected(string _, Func<AnalysisConfig, AnalysisConfig> bad) =>
        Assert.Throws<ArgumentException>(() => new VoiceAnalyzer(bad(AnalysisConfig.Default)));

    [Fact]
    public void NaNCalibratedNoiseFloorIsRejected()
    {
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.CalibratedNoiseFloorDbfs = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.CalibratedNoiseFloorDbfs = 3);
    }

    [Fact]
    public void LowestValidF0FloorStillWorks()
    {
        // 48 Hz → MaxLag 1000, lags to 1024, W = 1024: just outlasts the longest period
        var config = AnalysisConfig.Default with { F0SearchMinHz = 48 };
        var frames = TestSignals.Analyze(new VoiceAnalyzer(config), Synthetic_Tone(55), () => 480);
        Assert.Contains(frames, f => f.Voicing == VoicingState.Voiced && Math.Abs(f.F0Hz - 55) < 0.5);
    }

    private static float[] Synthetic_Tone(double hz) => VoiceCore.Synthetic.Suites.Tone(hz, VoiceCore.Synthetic.HarmonicProfile.Voice).Samples;
}
