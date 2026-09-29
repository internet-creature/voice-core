using System.Diagnostics;

namespace VoiceCore;

/// <summary>
/// Stateful streaming analyzer (spec §1.1): push samples in, frames come out.
/// Input is 48 kHz mono float32 in −1..1. Feeding the same signal in any chunking
/// produces identical frames — batch and live use this one path.
/// </summary>
/// <remarks>
/// Build step 1: the skeleton. Frames carry real timestamps and level (§3.2);
/// every other measurement is NaN until its build step lands.
/// </remarks>
public sealed class VoiceAnalyzer
{
    public const int SampleRate = 48000;
    public const int HopSamples = 480;
    public const int WindowSamples = 2048;

    private const int HistoryMask = WindowSamples - 1;
    private const float MinDbfs = -120f;

    private readonly float _dcPole;
    private readonly float _clippingLinear;

    // the last WindowSamples samples, indexed by capture sample index & HistoryMask
    private readonly float[] _raw = new float[WindowSamples];
    private readonly float[] _filtered = new float[WindowSamples];

    private long _nextSample;    // capture index of the next input sample
    private long _nextFrameEnd;  // exclusive end of the next frame's window
    private double _dcPrevIn;
    private double _dcPrevOut;

    public VoiceAnalyzer(AnalysisConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        Config = config;
        _dcPole = config.DcFilterPole;
        _clippingLinear = MathF.Pow(10f, config.ClippingThresholdDbfs / 20f);
        Reset();
    }

    public AnalysisConfig Config { get; }

    /// <summary>
    /// Samples of delay between an acoustic event and the frame that reflects it,
    /// measured at the window center. Fixed per config.
    /// </summary>
    public int AlgorithmicDelaySamples => WindowSamples / 2;

    public AnalyzerDiagnostics Diagnostics { get; } = new();

    /// <summary>
    /// Clears all temporal state. Call on stream start and after any capture gap.
    /// The frame grid restarts at <paramref name="nextSampleIndex"/>: the first
    /// frame's window begins there.
    /// </summary>
    public void Reset(long nextSampleIndex = 0)
    {
        Array.Clear(_raw);
        Array.Clear(_filtered);
        _dcPrevIn = 0;
        _dcPrevOut = 0;
        _nextSample = nextSampleIndex;
        _nextFrameEnd = nextSampleIndex + WindowSamples;
    }

    /// <summary>
    /// Upper bound on frames one <see cref="Process"/> call can emit for an input
    /// of this length: frame ends are <see cref="HopSamples"/> apart.
    /// </summary>
    public static int MaxFramesFor(int inputLength) => inputLength / HopSamples + 1;

    /// <summary>
    /// Consumes input, writes any completed frames to output, returns the number
    /// written. Zero-allocation in steady state.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="output"/> is shorter than <see cref="MaxFramesFor"/>
    /// of the input length (a caller bug, not a runtime state).
    /// </exception>
    public int Process(ReadOnlySpan<float> input, Span<AnalysisFrame> output)
    {
        if (output.Length < MaxFramesFor(input.Length))
            throw new ArgumentException(
                $"output holds {output.Length} frames; input of {input.Length} samples needs {MaxFramesFor(input.Length)}.",
                nameof(output));

        int written = 0;
        foreach (float x in input)
        {
            // DC removal (§3.2): y[n] = x[n] − x[n−1] + pole·y[n−1]
            double y = x - _dcPrevIn + _dcPole * _dcPrevOut;
            _dcPrevIn = x;
            _dcPrevOut = y;

            int slot = (int)(_nextSample & HistoryMask);
            _raw[slot] = x;
            _filtered[slot] = (float)y;
            _nextSample++;

            if (_nextSample == _nextFrameEnd)
            {
                output[written++] = AnalyzeFrame();
                _nextFrameEnd += HopSamples;
            }
        }
        return written;
    }

    private AnalysisFrame AnalyzeFrame()
    {
        long started = Stopwatch.GetTimestamp();

        long end = _nextFrameEnd;
        long center = end - WindowSamples / 2;

        // level over the hop centered on the window center, so it describes the
        // same moment as every other measurement. RMS uses the DC-removed signal;
        // peak and clipping use the raw input, since clipping happens before any filter.
        double sumSquares = 0;
        float peak = 0f;
        for (long i = center - HopSamples / 2; i < center + HopSamples / 2; i++)
        {
            int slot = (int)(i & HistoryMask);
            double f = _filtered[slot];
            sumSquares += f * f;
            peak = MathF.Max(peak, MathF.Abs(_raw[slot]));
        }

        var frame = AnalysisFrame.Unmeasured with
        {
            WindowCenterSample = center,
            ResultAvailableSample = end,
            TimeSeconds = center / (double)SampleRate,
            RmsDbfs = PowerToDbfs(sumSquares / HopSamples),
            PeakDbfs = AmplitudeToDbfs(peak),
            Clipping = peak > _clippingLinear,
        };

        Diagnostics.RecordFrame(Stopwatch.GetTimestamp() - started);
        return frame;
    }

    private static float PowerToDbfs(double meanSquare) =>
        meanSquare > 0 ? MathF.Max(MinDbfs, (float)(10 * Math.Log10(meanSquare))) : MinDbfs;

    private static float AmplitudeToDbfs(float amplitude) =>
        amplitude > 0 ? MathF.Max(MinDbfs, 20f * MathF.Log10(amplitude)) : MinDbfs;
}
