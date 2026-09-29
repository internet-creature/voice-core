namespace VoiceCore.Synthetic;

/// <summary>An analyzer frame paired with the ground truth for the moment it describes.</summary>
public readonly record struct FrameTruth
{
    public AnalysisFrame Frame { get; init; }

    /// <summary>True f0 at <see cref="AnalysisFrame.WindowCenterSample"/>; NaN unless voiced there.</summary>
    public double TrueF0Hz { get; init; }

    public TruthLabel Label { get; init; }

    /// <summary>
    /// True f0 span over the 2048-sample analysis window; NaN if any of the window
    /// is not voiced. Used to decide whether the window is in, above or below range.
    /// </summary>
    public double WindowMinF0Hz { get; init; }
    public double WindowMaxF0Hz { get; init; }

    /// <summary>The window sits inside one segment's steady part, clear of edges and ramps.</summary>
    public bool Steady { get; init; }

    /// <summary>Seconds from the start of the containing segment to the window center.</summary>
    public double SecondsIntoSegment { get; init; }

    /// <summary>Published F0Hz vs truth, in cents; NaN if either is missing.</summary>
    public double CentsError => 1200 * Math.Log2(Frame.F0Hz / TrueF0Hz);

    /// <summary>Published F0Hz off by more than 20% (the GPE definition, spec §6).</summary>
    public bool IsGrossError => !float.IsNaN(Frame.F0Hz) && Math.Abs(Frame.F0Hz / TrueF0Hz - 1) > 0.20;

    /// <summary>Gross error within 100 cents of a whole number of octaves.</summary>
    public bool IsOctaveError
    {
        get
        {
            if (!IsGrossError)
                return false;
            double octaves = Math.Round(CentsError / 1200);
            return octaves != 0 && Math.Abs(CentsError - 1200 * octaves) < 100;
        }
    }
}

/// <summary>Where a frame's true f0 sits relative to the analyzer's search range.</summary>
public enum TruthRange { NotVoiced, In, Above, Below, Straddling }

/// <summary>
/// Pitch-error summary over frames that have both a published and a true f0.
/// </summary>
public sealed record ErrorSummary(int Compared, double MaxAbsCents, double RmsCents, int GrossErrors, int OctaveErrors, double CentsPerPeriodMsSlope)
{
    public static ErrorSummary Of(IEnumerable<FrameTruth> frames)
    {
        var both = frames.Where(f => !double.IsNaN(f.CentsError)).ToList();
        if (both.Count == 0)
            return new ErrorSummary(0, double.NaN, double.NaN, 0, 0, double.NaN);
        var fine = both.Where(f => !f.IsGrossError).ToList();
        return new ErrorSummary(
            both.Count,
            both.Max(f => Math.Abs(f.CentsError)),
            fine.Count == 0 ? double.NaN : Math.Sqrt(fine.Average(f => f.CentsError * f.CentsError)),
            both.Count(f => f.IsGrossError),
            both.Count(f => f.IsOctaveError),
            Slope(fine.Select(f => (1000 / f.TrueF0Hz, f.CentsError)).ToList()));
    }

    /// <summary>
    /// Least-squares slope of cents error against true period (ms). An off-center
    /// analysis window shows up as a trend here (spec §3.4 timestamp alignment).
    /// </summary>
    private static double Slope(List<(double X, double Y)> points)
    {
        if (points.Count < 2)
            return double.NaN;
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y);
        double sxx = points.Sum(p => (p.X - mx) * (p.X - mx));
        return sxx == 0 ? double.NaN : points.Sum(p => (p.X - mx) * (p.Y - my)) / sxx;
    }
}

public static class Harness
{
    /// <summary>Search range the §3.4 suites assume (spec §3.4, §7 defaults).</summary>
    public const double SearchMinHz = 60;
    public const double SearchMaxHz = 1000;

    /// <summary>Runs a fresh analyzer over the signal and pairs every frame with its truth.</summary>
    public static FrameTruth[] Run(SyntheticSignal signal, AnalysisConfig? config = null)
    {
        var analyzer = new VoiceAnalyzer(config ?? AnalysisConfig.Default);
        var output = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(signal.Length)];
        int n = analyzer.Process(signal.Samples, output);
        return output.AsSpan(0, n).ToArray().Select(f => Pair(signal, f)).ToArray();
    }

    public static FrameTruth Pair(SyntheticSignal signal, AnalysisFrame frame)
    {
        long center = frame.WindowCenterSample;
        long start = center - VoiceAnalyzer.WindowSamples / 2;
        long end = center + VoiceAnalyzer.WindowSamples / 2;
        var (min, max) = signal.F0Span(start, end);
        return new FrameTruth
        {
            Frame = frame,
            TrueF0Hz = signal.F0At(center),
            Label = signal.LabelAt(center),
            WindowMinF0Hz = min,
            WindowMaxF0Hz = max,
            Steady = signal.IsSteady(start, end),
            SecondsIntoSegment = (center - signal.SegmentStartAt(center)) / (double)SyntheticSignal.SampleRate,
        };
    }

    public static TruthRange RangeOf(FrameTruth t, double minHz = SearchMinHz, double maxHz = SearchMaxHz)
    {
        if (double.IsNaN(t.WindowMinF0Hz))
            return TruthRange.NotVoiced;
        if (t.WindowMinF0Hz > maxHz)
            return TruthRange.Above;
        if (t.WindowMaxF0Hz < minHz)
            return TruthRange.Below;
        if (t.WindowMinF0Hz >= minHz && t.WindowMaxF0Hz <= maxHz)
            return TruthRange.In;
        return TruthRange.Straddling;
    }

    /// <summary>
    /// Applies a case's expectation to every scoreable frame. Frames that aren't
    /// <see cref="FrameTruth.Steady"/>, or that are earlier than
    /// <see cref="CaseExpectation.SettleSeconds"/> into their segment, are not
    /// scored. Per-frame rules by true range (spec §3.4):
    /// <list type="bullet">
    /// <item>In: must be Voiced if required, within the cents tolerance if set, and never a gross error if octave errors are banned.</item>
    /// <item>Above: <c>F0Range = Above</c> with <c>F0Hz</c> NaN. Never a folded value.</item>
    /// <item>Below: <c>F0Range = Below</c>, not Voiced, <c>F0Hz</c> NaN, or F0Confidence below the floor.</item>
    /// <item>Straddling the range edge: may abstain, but a published F0Hz must not be a gross error.</item>
    /// </list>
    /// A run with no scoreable frames fails, so a case can't pass vacuously.
    /// </summary>
    public static CaseResult Evaluate(IReadOnlyList<FrameTruth> frames, CaseExpectation expect)
    {
        var failures = new List<string>();
        var scored = frames.Where(f => f.Steady && f.SecondsIntoSegment >= expect.SettleSeconds).ToList();
        if (scored.Count == 0)
            failures.Add("no scoreable frames");

        foreach (var t in scored)
        {
            var f = t.Frame;
            string? problem = RangeOf(t) switch
            {
                TruthRange.In when expect.RequireVoiced && f.Voicing != VoicingState.Voiced
                    => $"expected Voiced, got {f.Voicing}",
                TruthRange.In when expect.MaxAbsCents is { } tol && !(Math.Abs(t.CentsError) <= tol)
                    => $"error {t.CentsError:+0.0;-0.0} cents exceeds ±{tol}",
                TruthRange.In when expect.NoOctaveErrors && t.IsGrossError
                    => $"gross error ({t.CentsError:+0;-0} cents)",
                TruthRange.Above when f.F0Range != F0Range.Above || !float.IsNaN(f.F0Hz)
                    => $"above range: expected F0Range=Above and F0Hz=NaN, got {f.F0Range} / {f.F0Hz:0.0} Hz",
                TruthRange.Below when !(f.F0Range == F0Range.Below || f.Voicing != VoicingState.Voiced
                                        || float.IsNaN(f.F0Hz) || f.F0Confidence < expect.LowConfidenceFloor)
                    => $"below range: published {f.F0Hz:0.0} Hz as confident Voiced",
                TruthRange.Straddling when t.IsGrossError
                    => $"range-edge frame published a gross error ({f.F0Hz:0.0} Hz)",
                _ => null,
            };
            if (problem is not null)
                failures.Add($"t={f.TimeSeconds:0.000}s true={t.TrueF0Hz:0.0}Hz: {problem}");
        }

        return new CaseResult(failures.Count == 0, scored.Count, ErrorSummary.Of(scored), failures);
    }
}

/// <summary>What a suite case requires of the analyzer; see <see cref="Harness.Evaluate"/>.</summary>
public sealed record CaseExpectation
{
    public double? MaxAbsCents { get; init; }
    public bool NoOctaveErrors { get; init; } = true;
    public bool RequireVoiced { get; init; } = true;

    /// <summary>Frames closer than this to their segment's start aren't scored (onset rules, §3.3).</summary>
    public double SettleSeconds { get; init; }

    /// <summary>
    /// False for cases the spec says to report but not gate, e.g. breathy HNR &lt; 5 dB.
    /// </summary>
    public bool Gated { get; init; } = true;

    /// <summary>Below-range frames may publish with confidence under this (spec §3.4 "low confidence").</summary>
    public float LowConfidenceFloor { get; init; } = 0.5f;
}

public sealed record CaseResult(bool Passed, int ScoredFrames, ErrorSummary Errors, IReadOnlyList<string> Failures)
{
    public string Report(int maxFailures = 10) =>
        $"{(Passed ? "PASS" : "FAIL")}: {ScoredFrames} frames scored, {Errors}"
        + string.Concat(Failures.Take(maxFailures).Select(f => "\n  " + f))
        + (Failures.Count > maxFailures ? $"\n  … {Failures.Count - maxFailures} more" : "");
}
