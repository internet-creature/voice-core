using VoiceCore.Synthetic;

namespace VoiceCore.Tests.Synthetic;

public class SuiteTests
{
    [Fact]
    public void SuitesHaveTheSpecifiedShape()
    {
        Assert.Single(Suites.Smoke());
        Assert.Equal(30, Suites.SweepFrequencies().Count());
        Assert.Equal(60, Suites.SweepFrequencies().First());
        Assert.InRange(Suites.SweepFrequencies().Last(), 950, 1000);
        Assert.Equal(30 * 4 * 3 * 3 * 2, Suites.Sweep().Count());
        Assert.Equal(5 * 5 * 2, Suites.Breathy().Count());
        Assert.Equal(12, Suites.Endpoints().Count());
        Assert.Equal(4, Suites.CeilingCrossing().Count());
        Assert.Equal(12, Suites.TimestampAlignment().Count());
    }

    [Fact]
    public void NamesAreUnique()
    {
        var names = Suites.All().Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void BreathyGatesOnlyAtHnrFiveAndAbove()
    {
        Assert.All(Suites.Breathy(), c =>
            Assert.Equal(!c.Name.Contains("hnr2.5") && !c.Name.Contains("hnr0/"), c.Expect.Gated));
    }

    [Fact]
    public void NoisySweepCasesGateOctaveErrorsNotCents()
    {
        Assert.All(Suites.Sweep(), c =>
        {
            Assert.True(c.Expect.NoOctaveErrors);
            Assert.Equal(c.Name.EndsWith("/clean") ? 5 : null, c.Expect.MaxAbsCents);
        });
    }

    [Fact]
    public void EveryCaseBuildsAValidSignal()
    {
        // every non-sweep case, plus every 37th sweep case (the full sweep is
        // ~1 M samples per 20 cases; step 4 runs all of it)
        var sample = Suites.All().Where(c => !c.Name.StartsWith("sweep/"))
            .Concat(Suites.Sweep().Where((_, i) => i % 37 == 0));

        Assert.All(sample, c =>
        {
            var signal = c.Build();
            Assert.True(signal.Length >= VoiceAnalyzer.WindowSamples * 4, $"{c.Name}: too short");
            Assert.True(signal.Samples.All(float.IsFinite), $"{c.Name}: non-finite sample");
            Assert.True(signal.Samples.Max(MathF.Abs) <= 1f, $"{c.Name}: clips");
            var frames = Harness.Run(signal);
            Assert.Contains(frames, t => t.Steady && t.Label == TruthLabel.Voiced);
        });
    }

    [Fact]
    public void TimestampGlidesCoverTheRequestedSpan()
    {
        foreach (var c in Suites.TimestampAlignment())
        {
            var frames = Harness.Run(c.Build()).Where(t => t.Steady).ToList();
            Assert.InRange(frames.Min(t => t.TrueF0Hz), 99, 101);
            Assert.InRange(frames.Max(t => t.TrueF0Hz), 899, 901);
        }
    }
}
