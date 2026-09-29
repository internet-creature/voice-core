using VoiceCore.Synthetic;

namespace VoiceCore.Tests.Synthetic;

public class GeneratorTests
{
    private const int Fs = VoiceAnalyzer.SampleRate;

    private static SyntheticSignal Tone(double hz, HarmonicProfile profile, double peakDbfs = -12, NoiseSpec? noise = null, int phaseSeed = 0, double seconds = 0.5) =>
        SyntheticSignal.Generate(new VoicedSegment(seconds, PitchContour.Constant(hz))
        {
            Harmonics = profile, PeakDbfs = peakDbfs, Noise = noise, PhaseSeed = phaseSeed,
        });

    // --- frequency and phase ---

    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    [InlineData(1000)]
    [InlineData(3000)]
    public void SineHasTheRequestedFrequency(double hz)
    {
        var x = Tone(hz, HarmonicProfile.Sine, seconds: 1).Samples;
        Assert.Equal(0, Measure.Cents(Measure.MeanFrequency(x, 480, x.Length - 480), hz), 0.01);
    }

    [Theory]
    [InlineData(100, 900, 2400)]
    [InlineData(900, 100, 2400)]
    [InlineData(800, 1400, 1200)]
    public void GlideTruthMatchesMeasuredInstantaneousFrequency(double from, double to, double rate)
    {
        // period between successive upward crossings, assigned to their midpoint,
        // must match the contour the harness scores against
        var contour = PitchContour.Glide(from, to, rate);
        var signal = SyntheticSignal.Generate(new VoicedSegment(contour.Seconds, contour) { Harmonics = HarmonicProfile.Sine });
        var crossings = Measure.UpwardCrossings(signal.Samples, 480, signal.Length - 480);

        double worst = 0;
        for (int i = 1; i < crossings.Count; i++)
        {
            double measured = Fs / (crossings[i] - crossings[i - 1]);
            double mid = (crossings[i] + crossings[i - 1]) / 2;
            double truth = contour.F0At(mid / Fs);
            worst = Math.Max(worst, Math.Abs(Measure.Cents(measured, truth)));
        }
        Assert.True(worst < 0.5, $"worst deviation {worst:0.000} cents");
    }

    [Fact]
    public void PhaseSeedChangesPhaseNotSpectrum()
    {
        var a = Tone(200, HarmonicProfile.Voice, phaseSeed: 0).Samples;
        var b = Tone(200, HarmonicProfile.Voice, phaseSeed: 7).Samples;
        Assert.NotEqual(a, b);
        // levels relative to H1 match (absolute levels differ with the crest factor)
        for (int k = 2; k <= 6; k++)
        {
            double relA = Measure.AmplitudeDb(a, 2400, 19200, k * 200) - Measure.AmplitudeDb(a, 2400, 19200, 200);
            double relB = Measure.AmplitudeDb(b, 2400, 19200, k * 200) - Measure.AmplitudeDb(b, 2400, 19200, 200);
            Assert.Equal(relA, relB, 0.01);
        }
    }

    // --- level and spectrum ---

    [Theory]
    [InlineData(-40)]
    [InlineData(-12)]
    [InlineData(-3)]
    public void PeakMatchesRequestedLevel(double dbfs)
    {
        var x = Tone(311, HarmonicProfile.StrongSecond, dbfs, new NoiseSpec(NoiseKind.White, 20)).Samples;
        Assert.Equal(dbfs, Measure.Db(x.Max(MathF.Abs)), 0.001);
    }

    [Theory]
    [InlineData("voice", 4, -12.0)]      // −6 dB/octave × 2 octaves
    [InlineData("strongH2", 2, 6.0)]     // −6 + 12
    [InlineData("weakH1", 1, -14.0)]     // H1 −20 vs H2 at −6
    [InlineData("breathy", 2, -15.0)]
    public void HarmonicLevelsFollowTheProfile(string name, int harmonic, double expectedDbReReference)
    {
        var profile = name switch
        {
            "voice" => HarmonicProfile.Voice,
            "strongH2" => HarmonicProfile.StrongSecond,
            "weakH1" => HarmonicProfile.WeakFundamental,
            _ => HarmonicProfile.Breathy,
        };
        // 200 Hz: 19200 samples is exactly 80 periods, clear of the edge ramps
        var x = Tone(200, profile).Samples;
        int reference = harmonic == 1 ? 2 : 1;
        double measured = Measure.AmplitudeDb(x, 2400, 19200, harmonic * 200) - Measure.AmplitudeDb(x, 2400, 19200, reference * 200);
        Assert.Equal(expectedDbReReference, measured, 0.01);
    }

    [Fact]
    public void HarmonicsStopAtMaxFrequency()
    {
        var profile = HarmonicProfile.Voice with { TiltDbPerOctave = 0, MaxFrequencyHz = 3000 };
        var x = Tone(200, profile).Samples;
        double h1 = Measure.AmplitudeDb(x, 2400, 19200, 200);

        Assert.Equal(0, Measure.AmplitudeDb(x, 2400, 19200, 2600) - h1, 0.01);  // below the taper
        Assert.InRange(Measure.AmplitudeDb(x, 2400, 19200, 2800) - h1, -20, -0.5);  // inside it
        Assert.True(Measure.AmplitudeDb(x, 2400, 19200, 3200) - h1 < -100);  // past the cap
    }

    // --- noise ---

    [Theory]
    [InlineData(NoiseKind.White, 20.0)]
    [InlineData(NoiseKind.Aspiration, 10.0)]
    [InlineData(NoiseKind.Aspiration, 5.0)]
    [InlineData(NoiseKind.Aspiration, 0.0)]
    public void NoiseRatioIsHarmonicRmsOverResidualRms(NoiseKind kind, double ratioDb)
    {
        var x = Tone(200, HarmonicProfile.Breathy, noise: new NoiseSpec(kind, ratioDb)).Samples;
        var (harmonic, residual) = Measure.SplitHarmonics(x, 2400, 19200, 200, 40);
        Assert.Equal(ratioDb, Measure.Db(Measure.Rms(harmonic) / Measure.Rms(residual)), 0.3);
    }

    [Fact]
    public void AspirationIsHighPassedAndPitchSynchronous()
    {
        const double f0 = 200;
        var x = Tone(f0, HarmonicProfile.Breathy, noise: new NoiseSpec(NoiseKind.Aspiration, 0)).Samples;
        var (_, residual) = Measure.SplitHarmonics(x, 2400, 19200, f0, 40);

        // high-passed at 500 Hz: well below cutoff has much less energy than above it
        // (averaged over ~60 bins, since one noise bin's power varies by several dB)
        float[] r = residual.Select(v => (float)v).ToArray();
        double BandPower(double fromHz) => Enumerable.Range(0, 60)
            .Average(i => Math.Pow(10, Measure.AmplitudeDb(r, 0, r.Length, fromHz + 2.5 * i) / 10));
        Assert.True(Measure.Db(Math.Sqrt(BandPower(2000) / BandPower(10))) > 10);

        // modulated by 1 + 0.5·cos φ: power near φ = 0 is ~9× power near φ = π
        double near0 = 0, nearPi = 0;
        int n0 = 0, nPi = 0;
        for (int n = 0; n < residual.Length; n++)
        {
            double phase = (2 * Math.PI * f0 * (2400 + n) / Fs) % (2 * Math.PI);
            if (phase < 0.5 || phase > 2 * Math.PI - 0.5) { near0 += residual[n] * residual[n]; n0++; }
            else if (Math.Abs(phase - Math.PI) < 0.5) { nearPi += residual[n] * residual[n]; nPi++; }
        }
        Assert.InRange(near0 / n0 / (nearPi / nPi), 6, 12);
    }

    // --- segments, labels and determinism ---

    [Fact]
    public void SegmentsCarryLabelsAndTruth()
    {
        var contour = PitchContour.Constant(220);
        var signal = SyntheticSignal.Generate(
            new SilentSegment(0.1),
            new VoicedSegment(0.3, contour),
            new NoiseSegment(0.1, RmsDbfs: -30));

        Assert.Equal((int)(0.5 * Fs), signal.Length);
        Assert.Equal(TruthLabel.Silence, signal.LabelAt(0));
        Assert.Equal(TruthLabel.Voiced, signal.LabelAt(4800));
        Assert.Equal(TruthLabel.Unvoiced, signal.LabelAt(19200));
        Assert.True(double.IsNaN(signal.F0At(100)));
        Assert.Equal(220, signal.F0At(10_000), 9);
        Assert.All(signal.Samples.AsSpan(0, 4800).ToArray(), s => Assert.Equal(0f, s));
        Assert.Equal(-30, Measure.Db(Measure.Rms(signal.Samples.AsSpan(19200))), 0.001);
    }

    [Fact]
    public void SteadyExcludesRampsAndBoundaries()
    {
        var signal = SyntheticSignal.Generate(new SilentSegment(0.1), new VoicedSegment(0.3, PitchContour.Constant(220)));
        const int voicedStart = 4800, ramp = 240;

        Assert.True(signal.IsSteady(0, voicedStart));
        Assert.False(signal.IsSteady(voicedStart - 10, voicedStart + 10));
        Assert.False(signal.IsSteady(voicedStart, voicedStart + 1000));
        Assert.True(signal.IsSteady(voicedStart + ramp, voicedStart + ramp + 2048));
        Assert.False(signal.IsSteady(signal.Length - 2048, signal.Length));
    }

    [Fact]
    public void BackgroundNoiseCoversSilence()
    {
        var signal = SyntheticSignal.Generate([new SilentSegment(0.5)], backgroundNoiseRmsDbfs: -60);
        Assert.Equal(-60, Measure.Db(Measure.Rms(signal.Samples)), 0.001);
        Assert.Equal(TruthLabel.Silence, signal.LabelAt(1000));
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        var noise = new NoiseSpec(NoiseKind.Aspiration, 5, Seed: 3);
        Assert.Equal(Tone(250, HarmonicProfile.Breathy, noise: noise, phaseSeed: 4).Samples,
                     Tone(250, HarmonicProfile.Breathy, noise: noise, phaseSeed: 4).Samples);
        Assert.NotEqual(Tone(250, HarmonicProfile.Breathy, noise: noise).Samples,
                        Tone(250, HarmonicProfile.Breathy, noise: noise with { Seed = 4 }).Samples);
    }

    // --- contours ---

    [Fact]
    public void ContoursInterpolateInCents()
    {
        var glide = PitchContour.Glide(100, 400, centsPerSecond: 1200, holdSeconds: 0.1);
        Assert.Equal(2.2, glide.Seconds, 9);        // 0.1 hold + 2 octaves at 1 octave/s + 0.1 hold
        Assert.Equal(100, glide.F0At(0.05), 9);
        Assert.Equal(200, glide.F0At(1.1), 9);      // one octave into the glide
        Assert.Equal(400, glide.F0At(5), 9);        // held after the last point
        Assert.Equal(100, glide.MinHz, 9);
        Assert.Equal(400, glide.MaxHz, 9);
    }

    [Fact]
    public void InvalidContoursAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PitchContour.Constant(0));
        Assert.Throws<ArgumentException>(() => PitchContour.Through((0, 100), (0, 200)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PitchContour.Glide(100, 200, -600));
    }

    // --- WAV export ---

    [Fact]
    public void WavRoundTrips()
    {
        var x = Tone(440, HarmonicProfile.Voice).Samples;
        string path = Path.Combine(Path.GetTempPath(), $"voicecore-{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WriteFloat32(path, x);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
            Assert.Equal(3, BitConverter.ToInt16(bytes, 20));       // IEEE float
            Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));
            Assert.Equal(x.Length * 4, BitConverter.ToInt32(bytes, 40));
            var back = new float[x.Length];
            Buffer.BlockCopy(bytes, 44, back, 0, x.Length * 4);
            Assert.Equal(x, back);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
