using System.Globalization;

namespace VoiceCore.Synthetic;

/// <summary>
/// f0 over time within one voiced segment: piecewise linear in cents (so a
/// straight segment is a constant-rate glide), held flat before the first
/// point and after the last. This is the ground truth the analyzer is scored
/// against.
/// </summary>
public sealed class PitchContour
{
    private readonly double[] _seconds;
    private readonly double[] _log2Hz;

    private PitchContour(double[] seconds, double[] log2Hz)
    {
        _seconds = seconds;
        _log2Hz = log2Hz;
    }

    public static PitchContour Constant(double hz) => Through((0, hz));

    /// <summary>
    /// Hold <paramref name="fromHz"/>, glide at a constant rate to
    /// <paramref name="toHz"/>, then hold again. The holds keep the glide away
    /// from segment edges.
    /// </summary>
    public static PitchContour Glide(double fromHz, double toHz, double centsPerSecond, double holdSeconds = 0.1)
    {
        if (!(centsPerSecond > 0))
            throw new ArgumentOutOfRangeException(nameof(centsPerSecond), "Rate is a magnitude; direction comes from the endpoints.");
        double glideSeconds = Math.Abs(1200 * Math.Log2(toHz / fromHz)) / centsPerSecond;
        return Through(
            (0, fromHz),
            (holdSeconds, fromHz),
            (holdSeconds + glideSeconds, toHz),
            (2 * holdSeconds + glideSeconds, toHz));
    }

    public static PitchContour Through(params (double Seconds, double Hz)[] points)
    {
        if (points.Length == 0)
            throw new ArgumentException("A contour needs at least one point.", nameof(points));
        for (int i = 0; i < points.Length; i++)
        {
            if (!(points[i].Hz > 0))
                throw new ArgumentOutOfRangeException(nameof(points), $"Point {i}: f0 must be positive.");
            if (i > 0 && !(points[i].Seconds > points[i - 1].Seconds))
                throw new ArgumentException($"Point {i}: times must strictly increase.", nameof(points));
        }
        return new PitchContour(
            points.Select(p => p.Seconds).ToArray(),
            points.Select(p => Math.Log2(p.Hz)).ToArray());
    }

    /// <summary>Times where the contour changes slope (every point, including hold edges).</summary>
    public IReadOnlyList<double> BreakpointSeconds => _seconds;

    /// <summary>Time of the last point; the contour holds after it.</summary>
    public double Seconds => _seconds[^1];

    public double MinHz => Math.Pow(2, _log2Hz.Min());
    public double MaxHz => Math.Pow(2, _log2Hz.Max());

    public double F0At(double seconds)
    {
        if (seconds <= _seconds[0])
            return Math.Pow(2, _log2Hz[0]);
        for (int i = 1; i < _seconds.Length; i++)
        {
            if (seconds <= _seconds[i])
            {
                double u = (seconds - _seconds[i - 1]) / (_seconds[i] - _seconds[i - 1]);
                return Math.Pow(2, _log2Hz[i - 1] + u * (_log2Hz[i] - _log2Hz[i - 1]));
            }
        }
        return Math.Pow(2, _log2Hz[^1]);
    }

    public override string ToString() => string.Join(" → ",
        _log2Hz.Select((l, i) => string.Create(CultureInfo.InvariantCulture, $"{Math.Pow(2, l):0.#}Hz@{_seconds[i]:0.###}s")));
}
