namespace VoiceCore.Tests.Synthetic;

/// <summary>
/// Independent measurements for checking the generator. Nothing here reuses
/// generator code, so a bug there can't cancel itself out.
/// </summary>
internal static class Measure
{
    private const double Fs = VoiceAnalyzer.SampleRate;

    /// <summary>Upward zero crossings, linearly interpolated, in fractional samples.</summary>
    public static List<double> UpwardCrossings(float[] x, int start, int end)
    {
        var crossings = new List<double>();
        for (int i = Math.Max(start, 1); i < end; i++)
            if (x[i - 1] < 0 && x[i] >= 0)
                crossings.Add(i - 1 + x[i - 1] / (x[i - 1] - x[i]));
        return crossings;
    }

    /// <summary>Mean frequency from the first to last upward crossing.</summary>
    public static double MeanFrequency(float[] x, int start, int end)
    {
        var c = UpwardCrossings(x, start, end);
        return (c.Count - 1) * Fs / (c[^1] - c[0]);
    }

    /// <summary>
    /// Complex amplitude of the component at <paramref name="hz"/> over
    /// <c>[start, start+length)</c>. Exact for harmonics when the span holds a
    /// whole number of periods.
    /// </summary>
    public static (double Re, double Im) Project(float[] x, int start, int length, double hz)
    {
        double re = 0, im = 0;
        for (int n = 0; n < length; n++)
        {
            double w = 2 * Math.PI * hz * (start + n) / Fs;
            re += x[start + n] * Math.Cos(w);
            im -= x[start + n] * Math.Sin(w);
        }
        return (2 * re / length, 2 * im / length);
    }

    public static double AmplitudeDb(float[] x, int start, int length, double hz)
    {
        var (re, im) = Project(x, start, length, hz);
        return 20 * Math.Log10(Math.Sqrt(re * re + im * im));
    }

    /// <summary>
    /// Splits a constant-f0 span into its fitted harmonic part and the residual.
    /// With a whole number of periods the harmonic projections are orthogonal,
    /// so this is the least-squares fit.
    /// </summary>
    public static (double[] Harmonic, double[] Residual) SplitHarmonics(float[] x, int start, int length, double f0, int harmonics)
    {
        var harmonic = new double[length];
        for (int k = 1; k <= harmonics; k++)
        {
            var (re, im) = Project(x, start, length, k * f0);
            for (int n = 0; n < length; n++)
            {
                double w = 2 * Math.PI * k * f0 * (start + n) / Fs;
                harmonic[n] += re * Math.Cos(w) - im * Math.Sin(w);
            }
        }
        var residual = new double[length];
        for (int n = 0; n < length; n++)
            residual[n] = x[start + n] - harmonic[n];
        return (harmonic, residual);
    }

    public static double Rms(ReadOnlySpan<double> x)
    {
        double sum = 0;
        foreach (double v in x)
            sum += v * v;
        return Math.Sqrt(sum / x.Length);
    }

    public static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x)
            sum += (double)v * v;
        return Math.Sqrt(sum / x.Length);
    }

    public static double Db(double ratio) => 20 * Math.Log10(ratio);

    public static double Cents(double hz, double referenceHz) => 1200 * Math.Log2(hz / referenceHz);
}
