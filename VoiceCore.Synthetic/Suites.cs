using System.Globalization;

namespace VoiceCore.Synthetic;

/// <summary>A named synthetic test case: how to build it and what it gates.</summary>
public sealed record SuiteCase(string Name, Func<SyntheticSignal> Build, CaseExpectation Expect)
{
    public override string ToString() => Name;
}

/// <summary>
/// The f0 validation suites from spec §3.4, as data. Build step 4 runs YIN
/// through these; step 2 only defines and checks them.
/// </summary>
public static class Suites
{
    private const double ToneSeconds = 0.5;

    private static readonly (string Name, HarmonicProfile Profile)[] Waveforms =
    [
        ("sine", HarmonicProfile.Sine),
        ("voice", HarmonicProfile.Voice),
        ("strongH2", HarmonicProfile.StrongSecond),
        ("weakH1", HarmonicProfile.WeakFundamental),
    ];

    public static IEnumerable<SuiteCase> All() =>
        Smoke().Concat(Sweep()).Concat(Breathy()).Concat(Endpoints()).Concat(CeilingCrossing()).Concat(TimestampAlignment());

    public static SuiteCase Find(string name) =>
        All().FirstOrDefault(c => c.Name == name) ?? throw new KeyNotFoundException($"No suite case named '{name}'.");

    /// <summary>200 Hz sine → within ±2 cents.</summary>
    public static IEnumerable<SuiteCase> Smoke()
    {
        yield return new SuiteCase("smoke/sine/200Hz",
            () => Tone(200, HarmonicProfile.Sine),
            new CaseExpectation { MaxAbsCents = 2 });
    }

    /// <summary>
    /// 60–1000 Hz in 10% steps × {sine, voice, strong H2, weak H1} × 3 phases ×
    /// {−40, −20, −3} dBFS × {clean, 20 dB SNR white noise}. Clean: ±5 cents.
    /// Noisy: no octave or gross errors. 2160 cases.
    /// </summary>
    public static IEnumerable<SuiteCase> Sweep()
    {
        foreach (double hz in SweepFrequencies())
        foreach (var (waveName, profile) in Waveforms)
        foreach (int phaseSeed in new[] { 0, 1, 2 })
        foreach (double dbfs in new[] { -40.0, -20.0, -3.0 })
        foreach (bool noisy in new[] { false, true })
        {
            var noise = noisy ? new NoiseSpec(NoiseKind.White, 20, Seed: phaseSeed + 10) : null;
            yield return new SuiteCase(
                Invariant($"sweep/{waveName}/{hz:0.0}Hz/{dbfs}dBFS/phase{phaseSeed}/{(noisy ? "snr20" : "clean")}"),
                () => Tone(hz, profile, dbfs, noise, phaseSeed),
                new CaseExpectation { MaxAbsCents = noisy ? null : 5 });
        }
    }

    /// <summary>60·1.1^k up to 1000 Hz (30 frequencies, top 953.6 Hz).</summary>
    public static IEnumerable<double> SweepFrequencies()
    {
        for (double hz = 60; hz <= 1000; hz *= 1.1)
            yield return hz;
    }

    /// <summary>
    /// Steep-tilt complexes at 150–400 Hz with aspiration noise, HNR 10 → 0 dB.
    /// Gated at HNR ≥ 5 dB: Voiced once past onset, no octave errors. Below 5 dB
    /// is reported only.
    /// </summary>
    public static IEnumerable<SuiteCase> Breathy()
    {
        foreach (double hz in new[] { 150.0, 200, 250, 300, 400 })
        foreach (double hnr in new[] { 10.0, 7.5, 5, 2.5, 0 })
        foreach (int phaseSeed in new[] { 0, 1 })
        {
            var noise = new NoiseSpec(NoiseKind.Aspiration, hnr, Seed: phaseSeed + 20);
            yield return new SuiteCase(
                Invariant($"breathy/{hz:0}Hz/hnr{hnr}/phase{phaseSeed}"),
                () => Tone(hz, HarmonicProfile.Breathy, -20, noise, phaseSeed),
                new CaseExpectation
                {
                    // "classified Voiced once past onset": onset takes ~30 ms (§3.3), plus margin
                    SettleSeconds = 0.06,
                    Gated = hnr >= 5,
                });
        }
    }

    /// <summary>
    /// Exactly 60 and 1000 Hz (in range, ±5 cents), and just outside: 55 Hz below;
    /// 1100, 1500 and 3000 Hz above. Never a folded value.
    /// </summary>
    public static IEnumerable<SuiteCase> Endpoints()
    {
        foreach (double hz in new[] { 55.0, 60, 1000, 1100, 1500, 3000 })
        foreach (var (waveName, profile) in Waveforms.Take(2))
        {
            yield return new SuiteCase(
                Invariant($"endpoint/{waveName}/{hz:0}Hz"),
                () => Tone(hz, profile),
                new CaseExpectation { MaxAbsCents = 5 });
        }
    }

    /// <summary>
    /// 800 → 1400 → 800 Hz at 1200 and 2400 cents/s. In-range frames within ±5
    /// cents; above-range frames flagged Above; no folds anywhere.
    /// </summary>
    public static IEnumerable<SuiteCase> CeilingCrossing()
    {
        foreach (double rate in new[] { 1200.0, 2400 })
        foreach (var (waveName, profile) in Waveforms.Take(2))
        {
            yield return new SuiteCase(
                Invariant($"ceiling/{waveName}/{rate}cps"),
                () => UpAndBack(800, 1400, rate, profile),
                new CaseExpectation { MaxAbsCents = 5 });
        }
    }

    /// <summary>
    /// Constant-rate glides 100 ↔ 900 Hz at 600, 1200 and 2400 cents/s, both
    /// directions. F0Hz must match the true f0 at WindowCenterSample within ±5
    /// cents; <see cref="ErrorSummary.CentsPerPeriodMsSlope"/> exposes any trend
    /// in error vs period.
    /// </summary>
    public static IEnumerable<SuiteCase> TimestampAlignment()
    {
        foreach (double rate in new[] { 600.0, 1200, 2400 })
        foreach (bool rising in new[] { true, false })
        foreach (var (waveName, profile) in Waveforms.Take(2))
        {
            double from = rising ? 100 : 900, to = rising ? 900 : 100;
            yield return new SuiteCase(
                Invariant($"timestamp/{waveName}/{(rising ? "up" : "down")}/{rate}cps"),
                () => GlideSignal(from, to, rate, profile),
                new CaseExpectation { MaxAbsCents = 5 });
        }
    }

    public static SyntheticSignal Tone(double hz, HarmonicProfile profile, double peakDbfs = -12, NoiseSpec? noise = null, int phaseSeed = 0) =>
        SyntheticSignal.Generate(new VoicedSegment(ToneSeconds, PitchContour.Constant(hz))
        {
            Harmonics = profile,
            PeakDbfs = peakDbfs,
            Noise = noise,
            PhaseSeed = phaseSeed,
        });

    public static SyntheticSignal GlideSignal(double fromHz, double toHz, double centsPerSecond, HarmonicProfile profile)
    {
        var contour = PitchContour.Glide(fromHz, toHz, centsPerSecond);
        return SyntheticSignal.Generate(new VoicedSegment(contour.Seconds, contour) { Harmonics = profile });
    }

    private static SyntheticSignal UpAndBack(double lowHz, double highHz, double centsPerSecond, HarmonicProfile profile)
    {
        const double hold = 0.1;
        double leg = 1200 * Math.Log2(highHz / lowHz) / centsPerSecond;
        var contour = PitchContour.Through(
            (0, lowHz), (hold, lowHz), (hold + leg, highHz), (hold + 2 * leg, lowHz), (2 * hold + 2 * leg, lowHz));
        return SyntheticSignal.Generate(new VoicedSegment(contour.Seconds, contour) { Harmonics = profile });
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
