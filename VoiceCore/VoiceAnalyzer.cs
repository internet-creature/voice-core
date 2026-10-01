using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VoiceCore;

/// <summary>
/// Stateful streaming analyzer (spec §1.1): push samples in, frames come out.
/// Input is 48 kHz mono float32 in −1..1. Feeding the same signal in any chunking
/// produces identical frames — batch and live use this one path.
/// </summary>
/// <remarks>
/// Build step 4: timestamps, level (§3.2), the voicing decision (§3.3) and YIN f0
/// (§3.4). Octave correction and the display track (§3.5), formants, CPP and the
/// brightness proxy are NaN until their steps land.
/// </remarks>
public sealed class VoiceAnalyzer
{
    public const int SampleRate = 48000;
    public const int HopSamples = 480;
    public const int WindowSamples = 2048;

    private const int HistoryMask = WindowSamples - 1;
    private const float MinDbfs = -120f;
    private const float NoiseFloorLowestDbfs = -100f;  // don't chase digital silence (a muted mic) all the way down

    private readonly float _dcPole;
    private readonly float _clippingLinear;
    private readonly float _floorAlpha;
    private readonly Yin _yin;
    private readonly VoicingStateMachine _voicing;

    // the last WindowSamples samples, indexed by capture sample index & HistoryMask
    private readonly float[] _raw = new float[WindowSamples];
    private readonly float[] _filtered = new float[WindowSamples];
    private readonly float[] _window = new float[WindowSamples];  // the current window, oldest first

    private long _nextSample;    // capture index of the next input sample
    private long _nextFrameEnd;  // exclusive end of the next frame's window
    private double _dcPrevIn;
    private double _dcPrevOut;
    private float _calibratedNoiseFloorDbfs;
    private float _noiseFloorDbfs;

    public VoiceAnalyzer(AnalysisConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        Config = config;
        _dcPole = config.DcFilterPole;
        _clippingLinear = MathF.Pow(10f, config.ClippingThresholdDbfs / 20f);
        _floorAlpha = 1f - MathF.Exp(-(HopSamples / (float)SampleRate) / config.NoiseFloorTimeConstantSeconds);
        _yin = new Yin(config);
        _voicing = new VoicingStateMachine(config);
        _calibratedNoiseFloorDbfs = config.DefaultNoiseFloorDbfs;
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
    /// The session's measured noise floor (spec §3.2: 2 s of instructed silence →
    /// 10th percentile of hop RMS). Setting it also restarts adaptation from it;
    /// <see cref="Reset"/> returns to it. Defaults to
    /// <see cref="AnalysisConfig.DefaultNoiseFloorDbfs"/>. A calibration measurement,
    /// not config: log it with the session, it isn't in the config hash.
    /// </summary>
    public float CalibratedNoiseFloorDbfs
    {
        get => _calibratedNoiseFloorDbfs;
        set
        {
            if (value is not (>= -120f and <= 0f))  // also rejects NaN
                throw new ArgumentOutOfRangeException(nameof(value), value, "Noise floor must be in [-120, 0] dBFS.");
            _calibratedNoiseFloorDbfs = value;
            _noiseFloorDbfs = value;
        }
    }

    /// <summary>The noise floor now, after slow adaptation during Silence frames.</summary>
    public float NoiseFloorDbfs => _noiseFloorDbfs;

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
        _noiseFloorDbfs = _calibratedNoiseFloorDbfs;
        _voicing.Reset();
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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]  // no tier-0 phase in live use
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]  // no tier-0 phase in live use
    private AnalysisFrame AnalyzeFrame()
    {
        long started = Stopwatch.GetTimestamp();

        long end = _nextFrameEnd;
        long center = end - WindowSamples / 2;

        // level and ZCR over the hop centered on the window center, so they describe
        // the same moment as every other measurement. RMS and ZCR use the DC-removed
        // signal; peak and clipping use the raw input, since clipping happens before
        // any filter.
        double sumSquares = 0;
        float peak = 0f;
        int signChanges = 0;
        bool previousNegative = _filtered[(int)((center - HopSamples / 2 - 1) & HistoryMask)] < 0;
        for (long i = center - HopSamples / 2; i < center + HopSamples / 2; i++)
        {
            int slot = (int)(i & HistoryMask);
            float f = _filtered[slot];
            sumSquares += (double)f * f;
            peak = MathF.Max(peak, MathF.Abs(_raw[slot]));
            bool negative = f < 0;
            if (negative != previousNegative)
                signChanges++;
            previousNegative = negative;
        }
        float rmsDbfs = PowerToDbfs(sumSquares / HopSamples);
        float zcrPerSecond = signChanges * (SampleRate / (float)HopSamples);

        // §3.3 step 1: level gate
        float gate = _noiseFloorDbfs + Config.VoicedLevelMarginDb;
        bool hasEnergy = rmsDbfs > gate;

        // step 2: f0 candidate on every frame with energy, and on quiet frames while a
        // hold is pending, so a held Voiced frame can publish its own candidate
        F0Candidate? candidate = null;
        if (hasEnergy || _voicing.NeedsCandidateWhenQuiet)
        {
            CopyWindow(end);
            candidate = _yin.Estimate(_window);
        }

        // steps 3–4: classify, creak votes, hysteresis
        var decision = _voicing.Decide(hasEnergy, hasEnergy ? candidate : null, zcrPerSecond, rmsDbfs - gate);

        if (decision.State == VoicingState.Silence)
            _noiseFloorDbfs = MathF.Max(NoiseFloorLowestDbfs, _noiseFloorDbfs + _floorAlpha * (rmsDbfs - _noiseFloorDbfs));

        var frame = AnalysisFrame.Unmeasured with
        {
            WindowCenterSample = center,
            ResultAvailableSample = end,
            TimeSeconds = center / (double)SampleRate,
            RmsDbfs = rmsDbfs,
            PeakDbfs = AmplitudeToDbfs(peak),
            Clipping = peak > _clippingLinear,
            Voicing = decision.State,
            VoicingConfidence = Config.Calibration?.VoicingFor(decision.State) is { } voicingMap
                ? voicingMap.Apply(decision.Confidence)
                : decision.Confidence,
        };

        if (candidate is { } c)
        {
            frame = frame with { F0RawHz = c.F0Hz, Aperiodicity = c.Aperiodicity };
            if (decision.State == VoicingState.Voiced)
            {
                bool inRange = c.Range == F0Range.In;
                frame = frame with
                {
                    F0Range = c.Range,
                    F0Hz = inRange ? c.F0Hz : float.NaN,
                    F0Cents = inRange ? 1200f * MathF.Log2(c.F0Hz / 55f) : float.NaN,
                    F0Confidence = inRange ? Calibrated(F0Confidence(c, decision, rmsDbfs - gate)) : float.NaN,
                };
            }
        }

        Diagnostics.RecordFrame(Stopwatch.GetTimestamp() - started);
        return frame;
    }

    private float Calibrated(float rawF0Confidence) =>
        Config.Calibration is { } table ? table.F0.Apply(rawF0Confidence) : rawF0Confidence;

    /// <summary>
    /// Raw f0 confidence (§3.9; the config's calibration maps it to a probability): periodicity
    /// (1 − d′, scaled over the voiced range), times factors for candidate stability,
    /// level above the gate, whether a real dip was found, and hysteresis holds.
    /// Monotone in each input, which is all a calibration map needs.
    /// </summary>
    private float F0Confidence(F0Candidate c, VoicingDecision decision, float levelAboveGateDb)
    {
        float periodicity = Math.Clamp(1f - c.Aperiodicity / Config.BreathyAperiodicityMax, 0f, 1f);
        float stability = decision.Stable ? 1f : 0.7f;
        float level = 0.5f + 0.5f * Math.Clamp(levelAboveGateDb / 20f, 0f, 1f);
        float dip = c.FoundDip ? 1f : 0.5f;
        float hold = decision.Holding ? 0.5f : 1f;
        return periodicity * stability * level * dip * hold;
    }

    private void CopyWindow(long end)
    {
        int start = (int)((end - WindowSamples) & HistoryMask);
        _filtered.AsSpan(start).CopyTo(_window);
        _filtered.AsSpan(0, start).CopyTo(_window.AsSpan(WindowSamples - start));
    }

    private static float PowerToDbfs(double meanSquare) =>
        meanSquare > 0 ? MathF.Max(MinDbfs, (float)(10 * Math.Log10(meanSquare))) : MinDbfs;

    private static float AmplitudeToDbfs(float amplitude) =>
        amplitude > 0 ? MathF.Max(MinDbfs, 20f * MathF.Log10(amplitude)) : MinDbfs;
}
