namespace VoiceProbe.Capture;

/// <summary>
/// Streaming rational resampler (spec §3 device format policy): upsample by
/// <see cref="Up"/>, low-pass, decimate by <see cref="Down"/>, done as a polyphase
/// FIR so only the needed outputs are computed. The prototype low-pass is
/// Kaiser-windowed sinc. Stateful across calls and chunk-size invariant;
/// allocation-free after construction.
/// </summary>
public sealed class PolyphaseResampler
{
    private readonly float[] _phases;   // phase p's taps at [p·K, p·K + K), stored oldest-sample first
    private readonly float[] _history;  // last K inputs, written twice so any K-window is contiguous
    private int _historyPos;
    private long _inputCount;
    private long _nextOutputTime;       // in units of 1/Up input samples

    public PolyphaseResampler(int up, int down, double inputRate, double passbandHz, double stopbandHz, double attenuationDb)
    {
        if (up <= 0 || down <= 0)
            throw new ArgumentOutOfRangeException(nameof(up), "Factors must be positive.");
        if (!(passbandHz > 0 && stopbandHz > passbandHz))
            throw new ArgumentException("Need 0 < passband < stopband.");

        Up = up;
        Down = down;

        // Kaiser design (Oppenheim & Schafer) at the upsampled rate
        double upRate = inputRate * up;
        double transition = 2 * Math.PI * (stopbandHz - passbandHz) / upRate;
        double beta = attenuationDb > 50 ? 0.1102 * (attenuationDb - 8.7)
                    : attenuationDb >= 21 ? 0.5842 * Math.Pow(attenuationDb - 21, 0.4) + 0.07886 * (attenuationDb - 21)
                    : 0;
        int length = (int)Math.Ceiling((attenuationDb - 8) / (2.285 * transition)) + 1;
        TapsPerPhase = (length + up - 1) / up;
        length = TapsPerPhase * up;

        double cutoff = (passbandHz + stopbandHz) / 2 / upRate;  // cycles per upsampled sample
        double center = (length - 1) / 2.0;
        double i0Beta = BesselI0(beta);
        var h = new double[length];
        double sum = 0;
        for (int j = 0; j < length; j++)
        {
            double t = j - center;
            double sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
            double r = (j - center) / center;
            h[j] = sinc * BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0Beta;
            sum += h[j];
        }

        // unity passband gain: each phase sums to ~1, so the whole filter sums to Up
        _phases = new float[length];
        for (int p = 0; p < up; p++)
            for (int k = 0; k < TapsPerPhase; k++)
                _phases[p * TapsPerPhase + (TapsPerPhase - 1 - k)] = (float)(h[p + k * up] * up / sum);

        _history = new float[2 * TapsPerPhase];
        DelayOutputSamples = (length - 1) / 2.0 / down;
    }

    /// <summary>44.1 → 48 kHz: 20 kHz passband, input Nyquist stopband, 85 dB design target.</summary>
    public static PolyphaseResampler Create44100To48000() =>
        new(160, 147, 44100, passbandHz: 20_000, stopbandHz: 22_050, attenuationDb: 85);

    public int Up { get; }
    public int Down { get; }
    public int TapsPerPhase { get; }

    /// <summary>Constant group delay, in output samples. Logged; it adds to input latency.</summary>
    public double DelayOutputSamples { get; }

    /// <summary>Upper bound on outputs one <see cref="Process"/> call can write.</summary>
    public int MaxOutputFor(int inputLength) => (int)(((long)inputLength * Up) / Down) + 1;

    /// <summary>Consumes all input and writes the outputs it completes; returns the count.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (output.Length < MaxOutputFor(input.Length))
            throw new ArgumentException($"output needs room for {MaxOutputFor(input.Length)} samples.", nameof(output));

        int k = TapsPerPhase;
        int written = 0;
        foreach (float x in input)
        {
            _history[_historyPos] = x;
            _history[_historyPos + k] = x;
            _historyPos = _historyPos + 1 == k ? 0 : _historyPos + 1;
            long newest = _inputCount++;

            // emit every output whose newest needed input is this sample
            while (_nextOutputTime / Up <= newest)
            {
                int phase = (int)(_nextOutputTime % Up);
                var taps = _phases.AsSpan(phase * k, k);
                var window = _history.AsSpan(_historyPos, k);  // oldest → newest
                float acc = 0;
                for (int i = 0; i < k; i++)
                    acc += taps[i] * window[i];
                output[written++] = acc;
                _nextOutputTime += Down;
            }
        }
        return written;
    }

    private static double BesselI0(double x)
    {
        // power series; converges fast for the β values used here (< 15)
        double sum = 1, term = 1, halfX = x / 2;
        for (int n = 1; n < 50; n++)
        {
            term *= halfX / n;
            double t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-17)
                break;
        }
        return sum;
    }
}
