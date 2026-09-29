namespace VoiceCore;

/// <summary>
/// The §3.2 calibration step: collect hop RMS during about 2 s of instructed
/// silence, then take the 10th percentile. A low percentile, not the mean, so a
/// cough or a door during calibration doesn't raise the floor.
/// </summary>
public sealed class NoiseFloorCalibration
{
    public const double DefaultSeconds = 2.0;
    public const double Percentile = 10;

    private readonly float[] _values;
    private int _count;

    public NoiseFloorCalibration(double seconds = DefaultSeconds)
    {
        if (!(seconds > 0))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        _values = new float[(int)Math.Ceiling(seconds * VoiceAnalyzer.SampleRate / VoiceAnalyzer.HopSamples)];
    }

    public int FramesNeeded => _values.Length;
    public int FramesCollected => _count;
    public bool IsComplete => _count == _values.Length;
    public double Progress => (double)_count / _values.Length;

    /// <summary>Adds one frame's RMS. Ignored once complete, or if not finite.</summary>
    public void Add(in AnalysisFrame frame)
    {
        if (!IsComplete && float.IsFinite(frame.RmsDbfs))
            _values[_count++] = frame.RmsDbfs;
    }

    /// <summary>10th percentile of the collected RMS, in dBFS; clamped to the analyzer's valid range.</summary>
    public float Result()
    {
        if (_count == 0)
            throw new InvalidOperationException("No frames collected.");
        var sorted = _values.AsSpan(0, _count).ToArray();
        Array.Sort(sorted);
        int index = (int)Math.Floor(Percentile / 100 * (sorted.Length - 1));
        return Math.Clamp(sorted[index], -120f, 0f);
    }
}
