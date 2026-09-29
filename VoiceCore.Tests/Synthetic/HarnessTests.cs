using VoiceCore.Synthetic;

namespace VoiceCore.Tests.Synthetic;

/// <summary>
/// The harness must catch real failures, not just pass. These tests feed it
/// fabricated "oracle" frames — perfect, slightly off, folded, off-center —
/// and check each verdict.
/// </summary>
public class HarnessTests
{
    /// <summary>
    /// Real frame timing from the analyzer, with pitch fields replaced by
    /// <paramref name="publish"/>(truth).
    /// </summary>
    private static FrameTruth[] Oracle(SyntheticSignal signal, Func<FrameTruth, AnalysisFrame> publish) =>
        Harness.Run(signal).Select(t => Harness.Pair(signal, publish(t))).ToArray();

    private static AnalysisFrame Perfect(FrameTruth t) => t.Frame with
    {
        Voicing = VoicingState.Voiced,
        F0Hz = (float)t.TrueF0Hz,
        F0Confidence = 0.99f,
    };

    private static AnalysisFrame ScaledBy(FrameTruth t, double ratio) => Perfect(t) with { F0Hz = (float)(t.TrueF0Hz * ratio) };

    private static AnalysisFrame RangeAware(FrameTruth t) => Harness.RangeOf(t) == TruthRange.Above || t.TrueF0Hz > Harness.SearchMaxHz
        ? t.Frame with { Voicing = VoicingState.Voiced, F0Range = F0Range.Above, F0Hz = float.NaN }
        : Perfect(t);

    private static readonly SuiteCase Smoke = Suites.Smoke().Single();

    [Fact]
    public void PerfectFramesPass()
    {
        var result = Harness.Evaluate(Oracle(Smoke.Build(), Perfect), Smoke.Expect);
        Assert.True(result.Passed, result.Report());
        Assert.True(result.ScoredFrames > 30);
        Assert.Equal(0, result.Errors.MaxAbsCents, 0.001);
    }

    [Fact]
    public void ToleranceIsEnforced()
    {
        double threeCents = Math.Pow(2, 3 / 1200.0);
        var frames = Oracle(Smoke.Build(), t => ScaledBy(t, threeCents));

        Assert.False(Harness.Evaluate(frames, Smoke.Expect).Passed);                          // ±2
        Assert.True(Harness.Evaluate(frames, Smoke.Expect with { MaxAbsCents = 5 }).Passed);  // ±5
    }

    [Fact]
    public void OctaveErrorsAreCaughtEvenWithoutATolerance()
    {
        var noisy = Suites.Find("sweep/strongH2/250.6Hz/-20dBFS/phase1/snr20");
        Assert.Null(noisy.Expect.MaxAbsCents);

        var octaveUp = Oracle(noisy.Build(), t => ScaledBy(t, 2));
        var result = Harness.Evaluate(octaveUp, noisy.Expect);

        Assert.False(result.Passed);
        Assert.Equal(result.ScoredFrames, result.Errors.OctaveErrors);
    }

    [Fact]
    public void MissingVoicingFails()
    {
        var unvoiced = Oracle(Smoke.Build(), t => Perfect(t) with { Voicing = VoicingState.Unvoiced, F0Hz = float.NaN });
        var result = Harness.Evaluate(unvoiced, Smoke.Expect);
        Assert.False(result.Passed);
        Assert.Contains("expected Voiced", result.Failures[0]);
    }

    [Fact]
    public void AboveRangeMustBeFlaggedNotFolded()
    {
        var c = Suites.Find("endpoint/sine/1100Hz");

        var flagged = Oracle(c.Build(), RangeAware);
        Assert.True(Harness.Evaluate(flagged, c.Expect).Passed);

        // the fold Astra found: 1100 Hz published as 550 Hz Voiced
        var folded = Oracle(c.Build(), t => ScaledBy(t, 0.5));
        var result = Harness.Evaluate(folded, c.Expect);
        Assert.False(result.Passed);
        Assert.Contains("above range", result.Failures[0]);
    }

    [Fact]
    public void BelowRangeMayAbstainButNotPublishConfidently()
    {
        var c = Suites.Find("endpoint/voice/55Hz");

        var flagged = Oracle(c.Build(), t => t.Frame with { Voicing = VoicingState.Voiced, F0Range = F0Range.Below, F0Hz = float.NaN });
        Assert.True(Harness.Evaluate(flagged, c.Expect).Passed);

        var lowConfidence = Oracle(c.Build(), t => ScaledBy(t, 2) with { F0Confidence = 0.2f });
        Assert.True(Harness.Evaluate(lowConfidence, c.Expect).Passed);

        var confidentFold = Oracle(c.Build(), t => ScaledBy(t, 2));
        Assert.False(Harness.Evaluate(confidentFold, c.Expect).Passed);
    }

    [Fact]
    public void CeilingCrossingScoresEachFrameByItsOwnRange()
    {
        var c = Suites.Find("ceiling/voice/2400cps");
        var frames = Oracle(c.Build(), RangeAware);

        Assert.Contains(frames, t => Harness.RangeOf(t) == TruthRange.In);
        Assert.Contains(frames, t => Harness.RangeOf(t) == TruthRange.Above);
        Assert.Contains(frames, t => Harness.RangeOf(t) == TruthRange.Straddling);
        Assert.True(Harness.Evaluate(frames, c.Expect).Passed);

        // folding the out-of-range stretch down an octave must fail
        var folding = Oracle(c.Build(), t => t.TrueF0Hz > Harness.SearchMaxHz ? ScaledBy(t, 0.5) : Perfect(t));
        Assert.False(Harness.Evaluate(folding, c.Expect).Passed);
    }

    [Fact]
    public void OffCenterTimestampsAreCaught()
    {
        // an analyzer whose measured moment sits 320 samples after the window
        // center (the first v2.3 YIN draft at 300 Hz) reads the glide late
        var c = Suites.Find("timestamp/sine/up/2400cps");
        var signal = c.Build();
        var late = Oracle(signal, t => Perfect(t) with
        {
            F0Hz = (float)signal.F0At(Math.Min(t.Frame.WindowCenterSample + 320, signal.Length - 1)),
        });

        var result = Harness.Evaluate(late, c.Expect);
        Assert.False(result.Passed);
        Assert.InRange(result.Errors.MaxAbsCents, 15, 17);  // 320 samples × 2400 c/s ≈ 16 cents

        Assert.True(Harness.Evaluate(Oracle(signal, Perfect), c.Expect).Passed);
    }

    [Fact]
    public void OnsetFramesInsideTheSettleWindowAreNotScored()
    {
        var c = Suites.Find("breathy/200Hz/hnr5/phase0");
        var slowOnset = Oracle(c.Build(), t => t.SecondsIntoSegment < 0.05
            ? t.Frame with { Voicing = VoicingState.Unvoiced }
            : Perfect(t));

        Assert.True(Harness.Evaluate(slowOnset, c.Expect).Passed);
        Assert.False(Harness.Evaluate(slowOnset, c.Expect with { SettleSeconds = 0 }).Passed);
    }

    [Fact]
    public void NoScoreableFramesFails()
    {
        var result = Harness.Evaluate([], Smoke.Expect);
        Assert.False(result.Passed);
        Assert.Contains("no scoreable frames", result.Failures);
    }

    [Fact]
    public void TodaysPassthroughAnalyzerFailsTheSmokeGate()
    {
        // end to end through the real VoiceAnalyzer: no pitch yet, so no pass
        var result = Harness.Evaluate(Harness.Run(Smoke.Build()), Smoke.Expect);
        Assert.False(result.Passed);
    }

    [Fact]
    public void FramesArePairedWithTruthAtTheirCenter()
    {
        var signal = SyntheticSignal.Generate(new SilentSegment(0.1), new VoicedSegment(0.4, PitchContour.Glide(200, 400, 2400, 0.05)));
        var frames = Harness.Run(signal);

        foreach (var t in frames)
        {
            long center = t.Frame.WindowCenterSample;
            Assert.Equal(signal.LabelAt(center), t.Label);
            Assert.Equal(signal.F0At(center), t.TrueF0Hz);
            if (t.Label == TruthLabel.Voiced && t.Steady)
                Assert.InRange(t.TrueF0Hz, t.WindowMinF0Hz, t.WindowMaxF0Hz);
        }
        Assert.Contains(frames, t => !t.Steady);  // the silence → voice boundary
        Assert.Equal(TruthRange.NotVoiced, Harness.RangeOf(frames[0]));
    }

    [Fact]
    public void LevelMatchesAcrossHarnessAndAnalyzer()
    {
        // the one measurement the step 1 analyzer makes: −12 dBFS-peak sine → −15 dBFS RMS
        var frames = Harness.Run(Suites.Tone(1000, HarmonicProfile.Sine));
        Assert.All(frames.Where(t => t.Steady), t => Assert.Equal(-12 - 3.0103, t.Frame.RmsDbfs, 0.02));
    }
}
