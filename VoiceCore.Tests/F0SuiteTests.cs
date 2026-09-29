using System.Collections.Concurrent;
using VoiceCore.Synthetic;
using Xunit.Abstractions;

namespace VoiceCore.Tests;

/// <summary>
/// Spec §3.4 validation: YIN and the voicing decision against every synthetic
/// suite, through the real streaming analyzer. Gated cases must pass; ungated
/// ones (breathy HNR &lt; 5 dB) are reported only.
/// </summary>
public class F0SuiteTests(ITestOutputHelper output)
{
    private static List<(SuiteCase Case, CaseResult Result)> RunSuite(IEnumerable<SuiteCase> cases)
    {
        var results = new ConcurrentBag<(SuiteCase, CaseResult)>();
        Parallel.ForEach(cases, c => results.Add((c, Harness.Evaluate(Harness.Run(c.Build()), c.Expect))));
        return results.OrderBy(r => r.Item1.Name, StringComparer.Ordinal).ToList();
    }

    private void AssertGatedPass(List<(SuiteCase Case, CaseResult Result)> results)
    {
        foreach (var (c, r) in results.Where(r => !r.Case.Expect.Gated))
            output.WriteLine($"[report only] {c.Name}: {r.Report(maxFailures: 2)}");

        var gated = results.Where(r => r.Case.Expect.Gated).ToList();
        var failed = gated.Where(r => !r.Result.Passed).ToList();
        output.WriteLine($"{gated.Count - failed.Count}/{gated.Count} gated cases pass; worst |error| " +
                         $"{gated.Where(r => r.Result.Errors.Compared > 0).Select(r => r.Result.Errors.MaxAbsCents).DefaultIfEmpty(0).Max():0.00} cents");
        Assert.True(failed.Count == 0,
            $"{failed.Count} gated case(s) failed:\n" + string.Join("\n", failed.Take(8).Select(f => $"{f.Case.Name}: {f.Result.Report(3)}")));
    }

    [Fact]
    public void Smoke() => AssertGatedPass(RunSuite(Suites.Smoke()));

    [Fact]
    public void Sweep() => AssertGatedPass(RunSuite(Suites.Sweep()));

    [Fact]
    public void Breathy() => AssertGatedPass(RunSuite(Suites.Breathy()));

    [Fact]
    public void Endpoints() => AssertGatedPass(RunSuite(Suites.Endpoints()));

    [Fact]
    public void CeilingCrossing() => AssertGatedPass(RunSuite(Suites.CeilingCrossing()));

    [Fact]
    public void TimestampAlignment()
    {
        var results = RunSuite(Suites.TimestampAlignment());
        AssertGatedPass(results);

        // no trend in error vs period: an off-center window shows up as a slope
        foreach (var (c, r) in results)
        {
            output.WriteLine($"{c.Name}: max {r.Errors.MaxAbsCents:0.00} c, rms {r.Errors.RmsCents:0.00} c, slope {r.Errors.CentsPerPeriodMsSlope:+0.000;-0.000} c/ms");
            Assert.InRange(r.Errors.CentsPerPeriodMsSlope, -0.2, 0.2);
        }
    }

    [Fact]
    public void PublishedTrackLinesUpWithTruthAtZeroDelay()
    {
        // §3.4: re-check AlgorithmicDelaySamples from outside. Compare the published
        // track with the true contour shifted by s samples; the best s must be ~0.
        var signal = Suites.GlideSignal(100, 900, 2400, HarmonicProfile.Voice);
        var frames = Harness.Run(signal).Where(t => t.Steady && !float.IsNaN(t.Frame.F0Hz)).ToList();

        double Error(int shift) => frames
            .Select(t => t.Frame.WindowCenterSample + shift)
            .Zip(frames, (s, t) => (s, t))
            .Where(p => p.s >= 0 && p.s < signal.Length)
            .Average(p => Math.Pow(1200 * Math.Log2(p.t.Frame.F0Hz / signal.F0At(p.s)), 2));

        int best = Enumerable.Range(-96, 193).Select(i => i * 5).MinBy(Error);
        output.WriteLine($"best alignment shift: {best} samples ({best / 48.0:0.00} ms)");
        Assert.InRange(best, -24, 24);  // within half a millisecond
    }
}
