using System.Numerics;

namespace VoiceCore;

/// <summary>One frame's f0 candidate (spec §3.3 step 2). Always produced for analyzed frames, published only when Voiced.</summary>
/// <param name="F0Hz">Refined estimate; for <see cref="F0Range.Above"/> it's the true out-of-range pitch, never a fold.</param>
/// <param name="Aperiodicity">YIN d′ at the chosen lag.</param>
/// <param name="FoundDip">False when no dip cleared the threshold and the global minimum was used (low confidence).</param>
internal readonly record struct F0Candidate(float F0Hz, float Aperiodicity, F0Range Range, bool FoundDip);

/// <summary>
/// YIN pitch estimator (spec §3.4, de Cheveigné &amp; Kawahara 2002) over the
/// analyzer's 2048-sample window.
/// </summary>
/// <remarks>
/// <para>
/// Difference function: fixed integration length W = 2048 − (τmax + 24), with the
/// compared pair centered on the window for every lag (v2.3, Astra review), so the
/// measured moment is always the window center and the algorithmic delay is fixed.
/// The pair for lag τ is x[h..h+W) against x[h+τ..h+τ+W), h = 1024 − ⌊(W+τ)/2⌋.
/// That per-lag placement rules out the one-FFT cross-correlation shortcut, so d(τ)
/// is a direct sum: W·τmax ≈ 1 M multiply-adds per frame, vectorized with
/// <see cref="Vector{T}"/> (float lanes, double reduction).
/// </para>
/// <para>
/// Lag choice works on a lightly smoothed d′ (moving average over ±1.5% of the lag,
/// about ±26 cents). A clean valley is symmetric, so smoothing leaves its bottom in
/// place; in breathy and noisy frames the bottom is broad and flat, and picking a
/// single raw sample there wandered by 100+ cents from noise alone. Valleys are
/// contiguous runs below a threshold, and each valley's lowest point is used:
/// <list type="number">
/// <item>Reference: the first valley below the absolute threshold (0.15), searched
/// from τ = 2. If none, the global minimum over the search range (low confidence).</item>
/// <item>Octave guard: the answer is the first valley that gets within
/// <see cref="OctaveTolerance"/> of the reference's depth. d′ at 2τ or 3τ is often a
/// little lower than at τ, because the cumulative mean it's normalized by keeps
/// growing, so breathy and noisy frames would otherwise pick a multiple of the true
/// period. On clean signals the reference is near 0 and this is exactly the spec's
/// first-dip rule.</item>
/// </list>
/// </para>
/// <para>
/// Range: the search starts at τ = 2, so a pitch above the ceiling shows up at its
/// true lag and is flagged <see cref="F0Range.Above"/> rather than folding onto an
/// in-range multiple. Lags up to τmax + 24 are evaluated so the floor can be judged
/// on the refined minimum: more than <see cref="BelowRangeCents"/> past τmax is
/// <see cref="F0Range.Below"/>. That margin exceeds the estimator's jitter, so a
/// tone exactly at the floor stays In instead of flipping on one noisy sample.
/// </para>
/// </remarks>
internal sealed class Yin
{
    private const int FirstLag = 2;
    internal const int ExtraLags = 24;
    private const double SmoothingFraction = 0.015;

    /// <summary>How far below the floor (in cents) a refined minimum must be to count as below range.</summary>
    internal const double BelowRangeCents = 25;

    /// <summary>How close (in d′) an earlier valley must get to the reference to win (the octave guard).</summary>
    internal const double OctaveTolerance = 0.1;

    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly int _lastLag;
    private readonly int _integration;
    private readonly float _threshold;
    private readonly double _belowRangeLag;
    private readonly double[] _d;
    private readonly double[] _dPrime;
    private readonly double[] _smooth;
    private readonly double[] _prefix;

    public Yin(AnalysisConfig config)
    {
        _minLag = config.MinLag;
        _maxLag = config.MaxLag;
        _lastLag = _maxLag + ExtraLags;
        _integration = VoiceAnalyzer.WindowSamples - _lastLag;
        _threshold = config.YinThreshold;
        _belowRangeLag = _maxLag * Math.Pow(2, BelowRangeCents / 1200);
        _d = new double[_lastLag + 1];
        _dPrime = new double[_lastLag + 1];
        _smooth = new double[_lastLag + 1];
        _prefix = new double[_lastLag + 2];
    }

    public int IntegrationLength => _integration;

    /// <summary>Estimates f0 from a 2048-sample window (oldest first). No allocation.</summary>
    public F0Candidate Estimate(ReadOnlySpan<float> window)
    {
        Difference(window, _d, _lastLag, _integration);

        // cumulative mean normalized difference: d′(τ) = d(τ)·τ / Σ_{j=1..τ} d(j), d′(0) = 1
        _dPrime[0] = 1;
        double running = 0;
        for (int tau = 1; tau <= _lastLag; tau++)
        {
            running += _d[tau];
            _dPrime[tau] = running > 0 ? _d[tau] * tau / running : 1;
        }
        Smooth();

        // 1. reference: first valley under the absolute threshold, else the global minimum
        int reference = FirstValley(FirstLag, _threshold);
        bool foundDip = reference >= 0;
        int searchFrom = foundDip ? FirstLag : _minLag;
        if (!foundDip)
        {
            reference = _minLag;
            for (int tau = _minLag + 1; tau <= _lastLag; tau++)
                if (_smooth[tau] < _smooth[reference])
                    reference = tau;
        }

        // 2. octave guard: the first valley that gets nearly as deep as the reference
        double guard = Math.Max(foundDip ? _threshold : 0, _smooth[reference] + OctaveTolerance);
        int best = FirstValley(searchFrom, guard);
        if (best < 0 || best > reference)
            best = reference;

        // fine position: the raw minimum near the smoothed one, then a parabola through
        // raw d′. The smoothed curve decides which valley and roughly where; raw values
        // keep sharp valleys (harmonic-rich voices on fast glides) unskewed.
        int width = KernelHalfWidth(best);
        int fine = best;
        for (int tau = Math.Max(1, best - width); tau <= Math.Min(_lastLag, best + width); tau++)
            if (_dPrime[tau] < _dPrime[fine])
                fine = tau;
        best = fine;

        double lag = best;
        if (best - 1 >= 1 && best + 1 <= _lastLag)
        {
            double a = _dPrime[best - 1], b = _dPrime[best], c = _dPrime[best + 1];
            double denominator = a - 2 * b + c;
            if (denominator > 0)
                lag += Math.Clamp(0.5 * (a - c) / denominator, -1, 1);
        }

        var range = best == _lastLag || lag > _belowRangeLag ? F0Range.Below
                  : foundDip && best < _minLag ? F0Range.Above
                  : F0Range.In;

        return new F0Candidate((float)(VoiceAnalyzer.SampleRate / lag), (float)_dPrime[best], range, foundDip);
    }

    /// <summary>
    /// Lowest point of the first contiguous run of d′ below <paramref name="threshold"/>
    /// starting at or after <paramref name="from"/>; −1 if there is none.
    /// </summary>
    private int FirstValley(int from, double threshold)
    {
        for (int tau = from; tau <= _lastLag; tau++)
        {
            if (_smooth[tau] >= threshold)
                continue;
            int lowest = tau;
            for (; tau <= _lastLag && _smooth[tau] < threshold; tau++)
                if (_smooth[tau] < _smooth[lowest])
                    lowest = tau;
            return lowest;
        }
        return -1;
    }

    /// <summary>
    /// Centered moving average of d′ over ±1.5% of the lag, shrunk near the ends of
    /// the array so it stays symmetric. Uses a running prefix sum: O(lags).
    /// </summary>
    private void Smooth()
    {
        // _prefix[i] = Σ_{j<i} d′(j)
        _prefix[0] = 0;
        for (int tau = 0; tau <= _lastLag; tau++)
            _prefix[tau + 1] = _prefix[tau] + _dPrime[tau];
        for (int tau = 0; tau <= _lastLag; tau++)
            _smooth[tau] = SmoothedAt(tau, Math.Min(KernelHalfWidth(tau), Math.Min(tau, _lastLag - tau)));
    }

    private static int KernelHalfWidth(int tau) => (int)(tau * SmoothingFraction);

    private double SmoothedAt(int tau, int halfWidth) =>
        (_prefix[tau + halfWidth + 1] - _prefix[tau - halfWidth]) / (2 * halfWidth + 1);

    /// <summary>
    /// d(τ) = Σ_{j&lt;W} (x[h+j] − x[h+j+τ])² for τ = 1..maxLag, with the pair centered on
    /// the window (h = B/2 − ⌊(W+τ)/2⌋). d[0] = 0. Float SIMD lanes, double reduction.
    /// </summary>
    internal static void Difference(ReadOnlySpan<float> x, Span<double> d, int maxLag, int integration)
    {
        if (x.Length != VoiceAnalyzer.WindowSamples)
            throw new ArgumentException($"Window must be {VoiceAnalyzer.WindowSamples} samples.", nameof(x));
        int width = Vector<float>.Count;
        d[0] = 0;
        for (int tau = 1; tau <= maxLag; tau++)
        {
            int h = VoiceAnalyzer.WindowSamples / 2 - (integration + tau) / 2;
            var a = x.Slice(h, integration);
            var b = x.Slice(h + tau, integration);

            var acc = Vector<float>.Zero;
            int j = 0;
            for (; j <= integration - width; j += width)
            {
                var diff = new Vector<float>(a[j..]) - new Vector<float>(b[j..]);
                acc += diff * diff;
            }
            double sum = 0;
            for (int lane = 0; lane < width; lane++)
                sum += acc[lane];
            for (; j < integration; j++)
            {
                double diff = a[j] - b[j];
                sum += diff * diff;
            }
            d[tau] = sum;
        }
    }
}
