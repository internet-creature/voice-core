namespace VoiceCore.Synthetic;

public enum TruthLabel : byte { Silence, Unvoiced, Voiced }

/// <summary>
/// A generated 48 kHz mono signal plus its exact ground truth: the label and
/// instantaneous f0 at every sample (spec §6, reference layer 1). Generation is
/// deterministic: the same segments and seeds give identical samples.
/// </summary>
public sealed class SyntheticSignal
{
    public const int SampleRate = VoiceAnalyzer.SampleRate;

    private readonly Placed[] _segments;

    private SyntheticSignal(float[] samples, Placed[] segments)
    {
        Samples = samples;
        _segments = segments;
    }

    public float[] Samples { get; }

    public int Length => Samples.Length;

    public static SyntheticSignal Generate(params Segment[] segments) =>
        Generate(segments, backgroundNoiseRmsDbfs: double.NegativeInfinity);

    /// <param name="backgroundNoiseRmsDbfs">
    /// White noise added across the whole signal, silence included, e.g. to
    /// exercise the level gate. −∞ for none.
    /// </param>
    public static SyntheticSignal Generate(IReadOnlyList<Segment> segments, double backgroundNoiseRmsDbfs, int backgroundSeed = 99)
    {
        var placed = new Placed[segments.Count];
        long start = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            int length = checked((int)Math.Round(segments[i].Seconds * SampleRate));
            if (length <= 0)
                throw new ArgumentException($"Segment {i} is shorter than one sample.", nameof(segments));
            int ramp = segments[i] is VoicedSegment v ? (int)Math.Round(v.RampSeconds * SampleRate) : 0;
            placed[i] = new Placed(segments[i], start, length, ramp);
            start += length;
        }

        var samples = new float[checked((int)start)];
        foreach (var p in placed)
        {
            double[] rendered = p.Segment switch
            {
                VoicedSegment v => RenderVoiced(v, p.Length),
                NoiseSegment n => ScaleToRms(Gaussian(n.Seed, p.Length), n.RmsDbfs),
                SilentSegment => new double[p.Length],
                _ => throw new NotSupportedException(p.Segment.GetType().Name),
            };
            for (int i = 0; i < rendered.Length; i++)
                samples[p.Start + i] = (float)rendered[i];
        }

        if (!double.IsNegativeInfinity(backgroundNoiseRmsDbfs))
        {
            var noise = ScaleToRms(Gaussian(backgroundSeed, samples.Length), backgroundNoiseRmsDbfs);
            for (int i = 0; i < samples.Length; i++)
                samples[i] += (float)noise[i];
        }

        return new SyntheticSignal(samples, placed);
    }

    /// <summary>Ground-truth label at a sample.</summary>
    public TruthLabel LabelAt(long sample) => Find(sample).Segment switch
    {
        VoicedSegment => TruthLabel.Voiced,
        NoiseSegment => TruthLabel.Unvoiced,
        _ => TruthLabel.Silence,
    };

    /// <summary>Instantaneous f0 at a sample; NaN outside voiced segments.</summary>
    public double F0At(long sample)
    {
        var p = Find(sample);
        return p.Segment is VoicedSegment v ? v.Contour.F0At((sample - p.Start) / (double)SampleRate) : double.NaN;
    }

    /// <summary>
    /// Lowest and highest true f0 over <c>[start, end)</c>; NaN if any sample in
    /// the span is not voiced.
    /// </summary>
    public (double Min, double Max) F0Span(long start, long end)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        // labels change only at segment boundaries, which Find resolves exactly, and
        // contours are smooth at this scale: every 8th sample plus the last is plenty
        for (long s = start; s < end + 7; s += 8)
        {
            double f = F0At(Math.Min(s, end - 1));
            if (double.IsNaN(f))
                return (double.NaN, double.NaN);
            min = Math.Min(min, f);
            max = Math.Max(max, f);
        }
        return (min, max);
    }

    /// <summary>
    /// True if <c>[start, end)</c> lies entirely inside one segment's steady part
    /// (clear of ramps and boundaries), so its ground truth is unambiguous.
    /// </summary>
    public bool IsSteady(long start, long end)
    {
        if (start < 0 || end > Length)
            return false;
        var p = Find(start);
        return start >= p.Start + p.Ramp && end <= p.Start + p.Length - p.Ramp;
    }

    /// <summary>
    /// True if f0 is a single straight line in cents across <c>[start, end)</c>: one
    /// voiced segment, no contour breakpoint strictly inside. At a breakpoint the
    /// "true f0 at the center" is not what any windowed estimator measures.
    /// </summary>
    public bool IsF0Linear(long start, long end)
    {
        if (start < 0 || end > Length)
            return false;
        var p = Find(start);
        if (p.Segment is not VoicedSegment v || end > p.Start + p.Length)
            return false;
        foreach (double t in v.Contour.BreakpointSeconds)
        {
            double at = p.Start + t * SampleRate;
            if (at > start && at < end - 1)
                return false;
        }
        return true;
    }

    /// <summary>Start sample of the segment containing <paramref name="sample"/>.</summary>
    public long SegmentStartAt(long sample) => Find(sample).Start;

    private Placed Find(long sample)
    {
        if (sample < 0 || sample >= Length)
            throw new ArgumentOutOfRangeException(nameof(sample), sample, $"Signal has {Length} samples.");
        int lo = 0, hi = _segments.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_segments[mid].Start <= sample) lo = mid; else hi = mid - 1;
        }
        return _segments[lo];
    }

    private static double[] RenderVoiced(VoicedSegment v, int length)
    {
        var profile = v.Harmonics;
        int harmonics = profile.MaxHarmonics;
        if (harmonics < 1)
            throw new ArgumentException("MaxHarmonics must be at least 1.");

        // c_k = a_k·e^{iθ_k}; harmonic k contributes Im(c_k · e^{ikφ})
        var cRe = new double[harmonics + 1];
        var cIm = new double[harmonics + 1];
        var phaseRng = v.PhaseSeed == 0 ? null : new Random(v.PhaseSeed);
        for (int k = 1; k <= harmonics; k++)
        {
            double a = DbToLinear(profile.AmplitudeDb(k));
            double theta = phaseRng is null ? 0 : phaseRng.NextDouble() * 2 * Math.PI;
            cRe[k] = a * Math.Cos(theta);
            cIm[k] = a * Math.Sin(theta);
        }

        var harmonic = new double[length];
        var phase = new double[length];
        double phi = 0;
        double taperStart = 0.9 * profile.MaxFrequencyHz;
        for (int i = 0; i < length; i++)
        {
            double f0 = v.Contour.F0At(i / (double)SampleRate);
            double zRe = Math.Cos(phi), zIm = Math.Sin(phi);
            double pRe = 1, pIm = 0;  // e^{ikφ}, built up by repeated multiplication
            double sum = 0;
            for (int k = 1; k <= harmonics; k++)
            {
                double re = pRe * zRe - pIm * zIm;
                pIm = pRe * zIm + pIm * zRe;
                pRe = re;

                double fk = k * f0;
                if (fk >= profile.MaxFrequencyHz)
                    break;
                double gain = 1;
                if (fk > taperStart)
                {
                    double c = Math.Cos(0.5 * Math.PI * (fk - taperStart) / (profile.MaxFrequencyHz - taperStart));
                    gain = c * c;
                }
                sum += gain * (cRe[k] * pIm + cIm[k] * pRe);
            }
            harmonic[i] = sum;
            phase[i] = phi;
            // advance by the midpoint frequency: second-order accurate for glides
            phi += 2 * Math.PI * v.Contour.F0At((i + 0.5) / SampleRate) / SampleRate;
            if (phi > 2 * Math.PI)
                phi -= 2 * Math.PI;
        }

        var mix = harmonic;
        if (v.Noise is { } spec)
        {
            var noise = Gaussian(spec.Seed, length);
            if (spec.Kind == NoiseKind.Aspiration)
                ShapeAspiration(noise, phase);
            double gain = Rms(harmonic) / DbToLinear(spec.RatioDb) / Rms(noise);
            mix = new double[length];
            for (int i = 0; i < length; i++)
                mix[i] = harmonic[i] + gain * noise[i];
        }

        int ramp = Math.Min((int)Math.Round(v.RampSeconds * SampleRate), length / 2);
        for (int i = 0; i < ramp; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(Math.PI * (i + 0.5) / ramp);
            mix[i] *= w;
            mix[length - 1 - i] *= w;
        }

        double peak = mix.Max(Math.Abs);
        return peak > 0 ? Scale(mix, DbToLinear(v.PeakDbfs) / peak) : mix;
    }

    private static void ShapeAspiration(double[] noise, double[] glottalPhase)
    {
        const double cutoffHz = 500;
        double rc = 1 / (2 * Math.PI * cutoffHz);
        double a = rc / (rc + 1.0 / SampleRate);
        double prevIn = 0, prevOut = 0;
        for (int i = 0; i < noise.Length; i++)
        {
            double y = a * (prevOut + noise[i] - prevIn);
            prevIn = noise[i];
            prevOut = y;
            noise[i] = y * (1 + 0.5 * Math.Cos(glottalPhase[i]));
        }
    }

    /// <summary>Unit-variance Gaussian noise (Box–Muller), deterministic per seed.</summary>
    private static double[] Gaussian(int seed, int length)
    {
        var rng = new Random(seed);
        var x = new double[length];
        for (int i = 0; i < length; i += 2)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double r = Math.Sqrt(-2 * Math.Log(u1));
            x[i] = r * Math.Cos(2 * Math.PI * u2);
            if (i + 1 < length)
                x[i + 1] = r * Math.Sin(2 * Math.PI * u2);
        }
        return x;
    }

    private static double Rms(double[] x) => Math.Sqrt(x.Sum(v => v * v) / x.Length);

    private static double[] ScaleToRms(double[] x, double rmsDbfs) => Scale(x, DbToLinear(rmsDbfs) / Rms(x));

    private static double[] Scale(double[] x, double gain)
    {
        for (int i = 0; i < x.Length; i++)
            x[i] *= gain;
        return x;
    }

    internal static double DbToLinear(double db) => Math.Pow(10, db / 20);

    private readonly record struct Placed(Segment Segment, long Start, int Length, int Ramp);
}
