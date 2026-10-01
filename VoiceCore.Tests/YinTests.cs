using VoiceCore.Synthetic;

namespace VoiceCore.Tests;

public class YinTests
{
    private static readonly AnalysisConfig Config = AnalysisConfig.Default;

    /// <summary>The §3.4 test oracle: the same centered difference function, scalar, in float64.</summary>
    private static double[] OracleDifference(ReadOnlySpan<float> x, int maxLag, int integration)
    {
        var d = new double[maxLag + 1];
        for (int tau = 1; tau <= maxLag; tau++)
        {
            int h = VoiceAnalyzer.WindowSamples / 2 - (integration + tau) / 2;
            double sum = 0;
            for (int j = 0; j < integration; j++)
            {
                double diff = (double)x[h + j] - x[h + j + tau];
                sum += diff * diff;
            }
            d[tau] = sum;
        }
        return d;
    }

    public static TheoryData<string> Windows => ["noise", "sine", "voice", "breathy", "silence"];

    private static float[] Window(string kind)
    {
        var rng = new Random(kind.GetHashCode(StringComparison.Ordinal) & 0xffff);
        return kind switch
        {
            "noise" => Enumerable.Range(0, 2048).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray(),
            "silence" => new float[2048],
            "sine" => Suites.Tone(233, HarmonicProfile.Sine).Samples[5000..7048],
            "voice" => Suites.Tone(151, HarmonicProfile.Voice).Samples[5000..7048],
            _ => Suites.Tone(200, HarmonicProfile.Breathy, noise: new NoiseSpec(NoiseKind.Aspiration, 5)).Samples[5000..7048],
        };
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void SimdDifferenceMatchesFloat64Oracle(string kind)
    {
        var x = Window(kind);
        var yin = new Yin(Config);
        int maxLag = Config.MaxLag + 24;
        var simd = new double[maxLag + 1];
        Yin.Difference(x, simd, maxLag, yin.IntegrationLength);
        var oracle = OracleDifference(x, maxLag, yin.IntegrationLength);

        // float lanes vs double: relative to the window's energy scale
        double scale = Math.Max(1e-12, oracle.Max());
        for (int tau = 1; tau <= maxLag; tau++)
            Assert.True(Math.Abs(simd[tau] - oracle[tau]) <= 1e-5 * scale, $"τ={tau}: {simd[tau]} vs {oracle[tau]}");
    }

    [Fact]
    public void ComparedPairIsCenteredForEveryLag()
    {
        // the §3.4 v2.3 fix: the pair [h, h+τ+W) is centered on the window at every τ
        int integration = new Yin(Config).IntegrationLength;
        for (int tau = 1; tau <= Config.MaxLag + 24; tau++)
        {
            int h = VoiceAnalyzer.WindowSamples / 2 - (integration + tau) / 2;
            double center = h + (tau + integration) / 2.0;
            Assert.InRange(center, 1023.5, 1024.5);
            Assert.True(h >= 0 && h + tau + integration <= VoiceAnalyzer.WindowSamples);
        }
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(1500)]
    [InlineData(3000)]
    public void AboveRangeToneIsFlaggedAtItsTrueLag(double hz)
    {
        // Astra's case: 1100 Hz must not come back as 550 Hz
        var c = new Yin(Config).Estimate(Suites.Tone(hz, HarmonicProfile.Sine).Samples.AsSpan(5000, 2048));
        Assert.Equal(F0Range.Above, c.Range);
        Assert.Equal(hz, c.F0Hz, hz * 0.002);
    }

    [Fact]
    public void BelowRangeToneIsFlaggedNotFolded()
    {
        var c = new Yin(Config).Estimate(Suites.Tone(55, HarmonicProfile.Sine).Samples.AsSpan(5000, 2048));
        Assert.Equal(F0Range.Below, c.Range);
    }

    [Fact]
    public void ToneExactlyAtTheFloorStaysInRange()
    {
        var c = new Yin(Config).Estimate(Suites.Tone(60, HarmonicProfile.Voice, noise: new NoiseSpec(NoiseKind.White, 20)).Samples.AsSpan(5000, 2048));
        Assert.Equal(F0Range.In, c.Range);
        Assert.Equal(60, c.F0Hz, 0.5);
    }

    [Fact]
    public void EstimateDoesNotAllocate()
    {
        var yin = new Yin(Config);
        var x = Window("voice");
        Allocation.AssertSteadyStateFree(() => yin.Estimate(x), warmup: 20, perWindow: 50);
    }
}
