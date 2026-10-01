using VoiceCore.Synthetic;

namespace VoiceCore.Tests;

/// <summary>The §3.3 decision rules, driven with scripted evidence.</summary>
public class VoicingStateMachineTests
{
    private static readonly AnalysisConfig Config = AnalysisConfig.Default;

    private static F0Candidate Periodic(float hz = 200, float ap = 0.05f) => new(hz, ap, F0Range.In, FoundDip: true);
    private static F0Candidate Breathy(float hz = 200) => new(hz, 0.3f, F0Range.In, FoundDip: false);
    private static F0Candidate Noise() => new(3000, 0.8f, F0Range.In, FoundDip: false);

    private static VoicingState Step(VoicingStateMachine m, F0Candidate? c, float zcr = 500) =>
        m.Decide(c is not null, c, zcr, levelAboveGateDb: 10).State;

    [Fact]
    public void LevelGateDecidesSilence()
    {
        var m = new VoicingStateMachine(Config);
        Assert.Equal(VoicingState.Silence, Step(m, null));
        Assert.Equal(VoicingState.Voiced, Step(m, Periodic()));
    }

    [Fact]
    public void OneFrameOfContraryEvidenceIsHeld()
    {
        var m = new VoicingStateMachine(Config);
        Step(m, Periodic());
        var held = m.Decide(false, null, 0, -3);
        Assert.Equal(VoicingState.Voiced, held.State);
        Assert.True(held.Holding);
        Assert.Equal(0.5f, held.Confidence);

        Assert.Equal(VoicingState.Voiced, Step(m, Periodic()));  // flicker over
    }

    [Fact]
    public void TwoFramesOfContraryEvidenceLeaveVoiced()
    {
        var m = new VoicingStateMachine(Config);
        Step(m, Periodic());
        Assert.Equal(VoicingState.Voiced, Step(m, Noise()));
        Assert.Equal(VoicingState.Unvoiced, Step(m, Noise()));
    }

    [Fact]
    public void BreathyNeedsThreeStableCandidatesFirst()
    {
        // §3.3: "a breathy onset therefore publishes Unvoiced for ~30 ms"
        var m = new VoicingStateMachine(Config);
        var states = Enumerable.Range(0, 5).Select(_ => Step(m, Breathy(), zcr: 5000)).ToArray();
        Assert.Equal([VoicingState.Unvoiced, VoicingState.Unvoiced, VoicingState.Unvoiced, VoicingState.Voiced, VoicingState.Voiced], states);
    }

    [Fact]
    public void StableToleratesGlidesUpTo50CentsPerTwoFrames()
    {
        var m = new VoicingStateMachine(Config);
        float hz = 200;
        for (int i = 0; i < 3; i++)
            Step(m, Breathy(hz *= MathF.Pow(2, 20 / 1200f)), zcr: 5000);  // 20 cents per frame = 2000 c/s
        Assert.Equal(VoicingState.Voiced, Step(m, Breathy(hz * MathF.Pow(2, 20 / 1200f)), zcr: 5000));
    }

    [Fact]
    public void OutOfRangeHistoryIsNeverStable()
    {
        var m = new VoicingStateMachine(Config);
        Step(m, new F0Candidate(1100, 0.3f, F0Range.Above, true), zcr: 5000);
        Step(m, Breathy(), zcr: 5000);
        Step(m, Breathy(), zcr: 5000);
        var d = m.Decide(true, Breathy(), 5000, 10);
        Assert.False(d.Stable);
        Assert.Equal(VoicingState.Unvoiced, d.State);
    }

    [Fact]
    public void CreakNeedsThreeOfFiveVotes()
    {
        var m = new VoicingStateMachine(Config);
        float[] jumpy = [70, 95, 60, 110, 65, 90];  // unstable candidates, moderate aperiodicity, low ZCR
        var states = jumpy.Select(hz => Step(m, new F0Candidate(hz, 0.35f, F0Range.In, false), zcr: 800)).ToArray();
        Assert.Equal(VoicingState.Unvoiced, states[0]);
        Assert.Equal(VoicingState.Unvoiced, states[1]);
        Assert.Equal(VoicingState.Creak, states[2]);  // third vote
        Assert.All(states[3..], s => Assert.Equal(VoicingState.Creak, s));
    }

    [Fact]
    public void HighZcrModerateAperiodicityIsUnvoicedNotCreak()
    {
        var m = new VoicingStateMachine(Config);
        float[] jumpy = [70, 95, 60, 110, 65];
        Assert.All(jumpy.Select(hz => Step(m, new F0Candidate(hz, 0.35f, F0Range.In, false), zcr: 6000)),
            s => Assert.Equal(VoicingState.Unvoiced, s));
    }

    [Fact]
    public void ResetForgetsHistory()
    {
        var m = new VoicingStateMachine(Config);
        for (int i = 0; i < 4; i++)
            Step(m, Breathy(), zcr: 5000);
        m.Reset();
        Assert.Equal(VoicingState.Silence, m.State);
        Assert.Equal(VoicingState.Unvoiced, Step(m, Breathy(), zcr: 5000));  // no history → not STABLE
    }

    [Fact]
    public void ConfidenceIsHigherFurtherFromTheThreshold()
    {
        var clear = new VoicingStateMachine(Config).Decide(true, Periodic(ap: 0.02f), 500, 10);
        var marginal = new VoicingStateMachine(Config).Decide(true, Periodic(ap: 0.18f), 500, 10);
        Assert.True(clear.Confidence > marginal.Confidence);
        Assert.InRange(marginal.Confidence, 0.5f, 1f);
    }
}

/// <summary>§3.2 noise floor and the voicing decision through the real analyzer.</summary>
public class AnalyzerVoicingTests
{
    private static List<AnalysisFrame> Run(SyntheticSignal s, float? floor = null)
    {
        var a = new VoiceAnalyzer(AnalysisConfig.Default);
        if (floor is { } f)
            a.CalibratedNoiseFloorDbfs = f;
        return TestSignals.Analyze(a, s.Samples, () => 480);
    }

    [Fact]
    public void SilenceVoiceSilence()
    {
        var s = SyntheticSignal.Generate(new SilentSegment(0.3), new VoicedSegment(0.4, PitchContour.Constant(220)), new SilentSegment(0.3));
        var frames = Run(s).Select(f => Harness.Pair(s, f)).Where(t => t.Steady).ToList();

        Assert.All(frames.Where(t => t.Label == TruthLabel.Silence), t => Assert.Equal(VoicingState.Silence, t.Frame.Voicing));
        Assert.All(frames.Where(t => t.Label == TruthLabel.Voiced), t =>
        {
            Assert.Equal(VoicingState.Voiced, t.Frame.Voicing);
            Assert.Equal(220, t.Frame.F0Hz, 0.5);
        });
    }

    [Fact]
    public void NoiseIsUnvoicedWithNoPitch()
    {
        var s = SyntheticSignal.Generate(new NoiseSegment(0.5, RmsDbfs: -25));
        var frames = Run(s).Skip(3).ToList();
        Assert.All(frames, f =>
        {
            Assert.Equal(VoicingState.Unvoiced, f.Voicing);
            Assert.True(float.IsNaN(f.F0Hz));
            Assert.False(float.IsNaN(f.F0RawHz));  // the candidate is still logged (§3.3 step 2)
        });
    }

    [Fact]
    public void QuietToneBelowTheCalibratedFloorIsSilence()
    {
        var s = Suites.Tone(200, HarmonicProfile.Voice, peakDbfs: -60);  // RMS ≈ −65 dBFS
        Assert.All(Run(s, floor: -60).Skip(3), f => Assert.Equal(VoicingState.Silence, f.Voicing));
        Assert.Contains(Run(s, floor: -90).Skip(3), f => f.Voicing == VoicingState.Voiced);
    }

    [Fact]
    public void FloorAdaptsOnlyDuringSilence()
    {
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default) { CalibratedNoiseFloorDbfs = -70 };
        // loud voicing must not drag the floor up
        TestSignals.Analyze(analyzer, Suites.Tone(200, HarmonicProfile.Voice, peakDbfs: -6).Samples, () => 480);
        Assert.Equal(-70, analyzer.NoiseFloorDbfs, 0.01);

        // room noise under the gate pulls it toward the noise, slowly (τ ≈ 10 s)
        var room = SyntheticSignal.Generate([new SilentSegment(5)], backgroundNoiseRmsDbfs: -66);
        TestSignals.Analyze(analyzer, room.Samples, () => 480);
        Assert.InRange(analyzer.NoiseFloorDbfs, -69, -67);

        analyzer.Reset();
        Assert.Equal(-70, analyzer.NoiseFloorDbfs);
    }

    [Fact]
    public void ChunkInvarianceHoldsWithVoicing()
    {
        var s = SyntheticSignal.Generate(new SilentSegment(0.1), new VoicedSegment(0.5, PitchContour.Glide(150, 400, 1200, 0.05)) { Noise = new NoiseSpec(NoiseKind.Aspiration, 8) }, new NoiseSegment(0.2, -30));
        var rng = new Random(9);
        TestSignals.AssertBitIdentical(TestSignals.Analyze(s.Samples, () => s.Length), TestSignals.Analyze(s.Samples, () => rng.Next(1, 3000)));
    }
}
