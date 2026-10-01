namespace VoiceCore.Batch.Corpus;

/// <summary>
/// Reliability bins for one confidence: per bin, how many frames, their mean
/// predicted confidence, and how many were right (spec §6 "confidence
/// calibration"). Ten equal-width bins over 0..1.
/// </summary>
internal sealed class ReliabilityBins
{
    public const int Count = 10;
    public readonly long[] N = new long[Count];
    public readonly long[] Correct = new long[Count];
    public readonly double[] SumPredicted = new double[Count];

    public long Total => N.Sum();

    public void Add(float predicted, bool correct)
    {
        int b = Math.Clamp((int)(predicted * Count), 0, Count - 1);
        N[b]++;
        SumPredicted[b] += predicted;
        if (correct)
            Correct[b]++;
    }

    public void Add(ReliabilityBins other)
    {
        for (int b = 0; b < Count; b++)
        {
            N[b] += other.N[b];
            Correct[b] += other.Correct[b];
            SumPredicted[b] += other.SumPredicted[b];
        }
    }

    /// <summary>Expected calibration error: frame-weighted mean |predicted − observed| over bins.</summary>
    public double Ece()
    {
        long total = Total;
        if (total == 0)
            return double.NaN;
        double sum = 0;
        for (int b = 0; b < Count; b++)
            if (N[b] > 0)
                sum += Math.Abs(SumPredicted[b] / N[b] - Correct[b] / (double)N[b]) * N[b];
        return sum / total;
    }
}

/// <summary>
/// Sufficient statistics for every frame metric in spec §6, so slices are sums of
/// per-file stats and bootstrap resampling by file is cheap.
/// </summary>
internal sealed class FrameStats
{
    public static readonly float[] CoverageFloors = [0.5f, 0.8f, 0.9f];

    /// <summary>Reference columns of the confusion matrix: the four states, plus a binary reference's "not voiced".</summary>
    public const int NotVoicedColumn = 4;

    public long Frames;            // frames the reference covers and doesn't exclude
    public long Excluded;          // frames a hand label excludes (reported, not scored)
    public long VoicingMismatch;   // VDE numerator: voiced vs not disagrees
    public readonly long[,] Confusion = new long[4, 5];  // [published state, reference column]

    /// <summary>
    /// Only frames with a four-state (hand-labeled) reference, [published, reference].
    /// Creak precision and recall come from here: a binary reference's Voiced column
    /// can't say whether a published Creak was right.
    /// </summary>
    public readonly long[,] FourStateConfusion = new long[4, 4];

    public long BothVoiced;        // GPE denominator: reference voiced in range, published Voiced
    public long Gross;
    public long FineN;
    public double FineSumCents;
    public double FineSumSquares;
    public readonly long[] FineAbsHistogram = new long[FineHistogramBins];  // |cents| in 1-cent bins, for the median

    /// <summary>A fine error is under 20% (≈ 316 cents), so 320 one-cent bins hold them all.</summary>
    public const int FineHistogramBins = 320;

    public long RefVoicedInRange;  // coverage denominator
    public readonly long[] Covered = new long[CoverageFloors.Length];
    public long RefOutOfRange;
    public long Folds;             // reference f0 outside the search range, published an in-range F0Hz

    public readonly ReliabilityBins F0Reliability = new();
    public readonly ReliabilityBins[] VoicingReliability = [new(), new(), new(), new()];

    public void Add(FrameStats o)
    {
        Frames += o.Frames;
        Excluded += o.Excluded;
        VoicingMismatch += o.VoicingMismatch;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 5; j++)
                Confusion[i, j] += o.Confusion[i, j];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                FourStateConfusion[i, j] += o.FourStateConfusion[i, j];
        BothVoiced += o.BothVoiced;
        Gross += o.Gross;
        FineN += o.FineN;
        FineSumCents += o.FineSumCents;
        FineSumSquares += o.FineSumSquares;
        for (int k = 0; k < FineHistogramBins; k++)
            FineAbsHistogram[k] += o.FineAbsHistogram[k];
        RefVoicedInRange += o.RefVoicedInRange;
        for (int k = 0; k < Covered.Length; k++)
            Covered[k] += o.Covered[k];
        RefOutOfRange += o.RefOutOfRange;
        Folds += o.Folds;
        F0Reliability.Add(o.F0Reliability);
        for (int s = 0; s < 4; s++)
            VoicingReliability[s].Add(o.VoicingReliability[s]);
    }

    /// <summary>
    /// What <c>VoicingConfidence</c> predicts: whether the published state matches the
    /// reference (§3.9). Null when the reference can't judge it: a binary reference
    /// can't say whether Creak was right, and treats Silence and Unvoiced alike.
    /// </summary>
    public static bool? StateRight(in AnalysisFrame f, RefPoint r)
    {
        if (r.State is RefState.None or RefState.Exclude)
            return null;
        bool refVoiced = r.State == RefState.Voiced;
        if (!r.Binary)
            return (int)f.Voicing == (int)r.State;
        return f.Voicing switch
        {
            VoicingState.Voiced => refVoiced,
            VoicingState.Silence or VoicingState.Unvoiced => !refVoiced,
            _ => null,
        };
    }

    /// <summary>
    /// What <c>F0Confidence</c> predicts: the published f0 is within 20% of the
    /// reference (§3.9). Defined only where both the frame (Voiced, in range, with a
    /// confidence) and the reference (voiced, f0 inside the search range) have a
    /// pitch; a Voiced frame on an unvoiced reference is a voicing error, judged by
    /// <c>VoicingConfidence</c>.
    /// </summary>
    public static bool? F0Right(in AnalysisFrame f, RefPoint r, float searchMinHz, float searchMaxHz)
    {
        if (r.State != RefState.Voiced || !(r.F0Hz >= searchMinHz && r.F0Hz <= searchMaxHz))
            return null;
        if (f.Voicing != VoicingState.Voiced || f.F0Range != F0Range.In || float.IsNaN(f.F0Confidence))
            return null;
        return !IsGross(f, r);
    }

    private static bool IsGross(in AnalysisFrame f, RefPoint r) =>
        float.IsNaN(f.F0Hz) || Math.Abs(f.F0Hz / r.F0Hz - 1) > 0.20;

    /// <summary>
    /// Scores one published frame against the reference at its window center
    /// (spec §6 metric definitions; see docs/corpus.md for the exact rules).
    /// </summary>
    public void Score(in AnalysisFrame f, RefPoint r, float searchMinHz, float searchMaxHz)
    {
        if (r.State == RefState.None)
            return;
        if (r.State == RefState.Exclude)
        {
            Excluded++;
            return;
        }
        Frames++;
        bool oursVoiced = f.Voicing == VoicingState.Voiced;
        bool refVoiced = r.State == RefState.Voiced;
        if (oursVoiced != refVoiced)
            VoicingMismatch++;
        int column = r.Binary && r.State == RefState.Unvoiced ? NotVoicedColumn : (int)r.State;
        Confusion[(int)f.Voicing, column]++;
        if (!r.Binary)
            FourStateConfusion[(int)f.Voicing, (int)r.State]++;

        if (StateRight(f, r) is { } right && !float.IsNaN(f.VoicingConfidence))
            VoicingReliability[(int)f.Voicing].Add(f.VoicingConfidence, right);

        if (!refVoiced || float.IsNaN(r.F0Hz))
            return;
        if (r.F0Hz < searchMinHz || r.F0Hz > searchMaxHz)
        {
            RefOutOfRange++;
            if (oursVoiced && f.F0Range == F0Range.In)
                Folds++;
            return;
        }
        RefVoicedInRange++;
        if (!oursVoiced)
            return;

        // published Voiced on an in-range reference: a NaN F0Hz (Above/Below) is a
        // gross error, since the reference has a pitch the frame didn't give
        BothVoiced++;
        bool gross = IsGross(f, r);
        if (gross)
            Gross++;
        else
        {
            double cents = 1200 * Math.Log2(f.F0Hz / (double)r.F0Hz);
            FineN++;
            FineSumCents += cents;
            FineSumSquares += cents * cents;
            FineAbsHistogram[Math.Min(FineHistogramBins - 1, (int)Math.Abs(cents))]++;
        }
        if (f.F0Range == F0Range.In && !float.IsNaN(f.F0Confidence))
        {
            F0Reliability.Add(f.F0Confidence, !gross);
            for (int k = 0; k < CoverageFloors.Length; k++)
                if (f.F0Confidence >= CoverageFloors[k] && f.VoicingConfidence >= CoverageFloors[k])
                    Covered[k]++;
        }
    }
}

/// <summary>A named metric over <see cref="FrameStats"/>, and which direction is better.</summary>
internal sealed record Metric(string Name, string Unit, bool LowerIsBetter, Func<FrameStats, double> Compute)
{
    private static double Ratio(long num, long den) => den > 0 ? (double)num / den : double.NaN;

    private static double FineMedian(FrameStats s)
    {
        if (s.FineN == 0)
            return double.NaN;
        long half = (s.FineN + 1) / 2, seen = 0;
        for (int k = 0; k < FrameStats.FineHistogramBins; k++)
        {
            seen += s.FineAbsHistogram[k];
            if (seen >= half)
                return k + 0.5;  // bin center: 1-cent resolution
        }
        return double.NaN;
    }

    private static double VoicedPrecision(FrameStats s)
    {
        long published = 0;
        for (int j = 0; j < 5; j++)
            published += s.Confusion[(int)VoicingState.Voiced, j];
        return Ratio(s.Confusion[(int)VoicingState.Voiced, (int)RefState.Voiced], published);
    }

    private static double VoicedRecall(FrameStats s)
    {
        long reference = 0;
        for (int i = 0; i < 4; i++)
            reference += s.Confusion[i, (int)RefState.Voiced];
        return Ratio(s.Confusion[(int)VoicingState.Voiced, (int)RefState.Voiced], reference);
    }

    private static double CreakPrecision(FrameStats s)
    {
        // only four-state (hand-labeled) frames can say whether a Creak frame was right;
        // with none, precision is undefined (NaN), not 0
        long judged = 0;
        for (int j = 0; j < 4; j++)
            judged += s.FourStateConfusion[(int)VoicingState.Creak, j];
        return Ratio(s.FourStateConfusion[(int)VoicingState.Creak, (int)RefState.Creak], judged);
    }

    private static double CreakRecall(FrameStats s)
    {
        long reference = 0;
        for (int i = 0; i < 4; i++)
            reference += s.FourStateConfusion[i, (int)RefState.Creak];
        return Ratio(s.FourStateConfusion[(int)VoicingState.Creak, (int)RefState.Creak], reference);
    }

    public static readonly Metric Gpe = new("GPE", "%", true, s => 100 * Ratio(s.Gross, s.BothVoiced));
    public static readonly Metric Fpe = new("FPE", "cents", true, s => s.FineN > 0 ? Math.Sqrt(s.FineSumSquares / s.FineN) : double.NaN);
    public static readonly Metric FineMedianAbs = new("median |fine|", "cents", true, FineMedian);
    public static readonly Metric FineBias = new("fine bias", "cents", true, s => s.FineN > 0 ? Math.Abs(s.FineSumCents / s.FineN) : double.NaN);
    public static readonly Metric Vde = new("VDE", "%", true, s => 100 * Ratio(s.VoicingMismatch, s.Frames));

    public static readonly Metric[] All =
    [
        Gpe, Fpe, FineMedianAbs, FineBias, Vde,
        new("Voiced precision", "%", false, s => 100 * VoicedPrecision(s)),
        new("Voiced recall", "%", false, s => 100 * VoicedRecall(s)),
        new("Creak precision", "%", false, s => 100 * CreakPrecision(s)),
        new("Creak recall", "%", false, s => 100 * CreakRecall(s)),
        new("F0Confidence ECE", "", true, s => s.F0Reliability.Ece()),
        new("VoicingConfidence ECE (Voiced)", "", true, s => s.VoicingReliability[(int)VoicingState.Voiced].Ece()),
        new("VoicingConfidence ECE (Unvoiced)", "", true, s => s.VoicingReliability[(int)VoicingState.Unvoiced].Ece()),
        new("VoicingConfidence ECE (Silence)", "", true, s => s.VoicingReliability[(int)VoicingState.Silence].Ece()),
        new("coverage@0.5", "%", false, s => 100 * Ratio(s.Covered[0], s.RefVoicedInRange)),
        new("coverage@0.8", "%", false, s => 100 * Ratio(s.Covered[1], s.RefVoicedInRange)),
        new("coverage@0.9", "%", false, s => 100 * Ratio(s.Covered[2], s.RefVoicedInRange)),
        new("out-of-range folds", "%", true, s => 100 * Ratio(s.Folds, s.RefOutOfRange)),
    ];

    /// <summary>
    /// Spec §6 aspirational clean-slice targets; they gate only qualifying slices.
    /// FPE &lt; 15 cents is reported but not gated (v2.5): the only multi-speaker
    /// reference so far (PTDB-TUG's laryngograph RAPT track) scores Praat itself at
    /// 25 cents RMS, so it can't resolve a 15-cent target.
    /// </summary>
    public static readonly Dictionary<string, double> Targets = new()
    {
        ["GPE"] = 2,
        ["VDE"] = 5,
    };
}
