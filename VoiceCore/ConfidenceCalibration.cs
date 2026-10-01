using System.Globalization;
using System.Text;

namespace VoiceCore;

/// <summary>
/// Monotone piecewise-linear map from a raw confidence score to a probability
/// (spec §3.9). Fit on the corpus dev split; linear between knots, flat beyond
/// the first and last. Lookup is a binary search over a few dozen knots, so it
/// costs nothing per frame and never allocates.
/// </summary>
public sealed class CalibrationMap
{
    private readonly float[] _raw;
    private readonly float[] _probability;

    /// <param name="raw">Knot positions on the raw score, strictly increasing.</param>
    /// <param name="probability">Probability at each knot, non-decreasing, in [0, 1].</param>
    public CalibrationMap(float[] raw, float[] probability)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(probability);
        if (raw.Length == 0 || raw.Length != probability.Length)
            throw new ArgumentException("A calibration map needs at least one knot and one probability per knot.");
        for (int i = 0; i < raw.Length; i++)
        {
            if (!float.IsFinite(raw[i]) || probability[i] is not (>= 0f and <= 1f))
                throw new ArgumentException($"Knot {i} is invalid: raw {raw[i]}, probability {probability[i]}.");
            if (i > 0 && !(raw[i] > raw[i - 1]))
                throw new ArgumentException("Knot positions must be strictly increasing.");
            if (i > 0 && probability[i] < probability[i - 1])
                throw new ArgumentException("A calibration map must be monotone (non-decreasing).");
        }
        _raw = (float[])raw.Clone();
        _probability = (float[])probability.Clone();
    }

    public IReadOnlyList<float> Raw => _raw;
    public IReadOnlyList<float> Probability => _probability;

    /// <summary>The calibrated probability for a raw score. NaN stays NaN.</summary>
    public float Apply(float raw)
    {
        if (float.IsNaN(raw))
            return float.NaN;
        if (raw <= _raw[0])
            return _probability[0];
        int last = _raw.Length - 1;
        if (raw >= _raw[last])
            return _probability[last];
        int hi = Array.BinarySearch(_raw, raw);
        if (hi >= 0)
            return _probability[hi];
        hi = ~hi;
        int lo = hi - 1;
        float t = (raw - _raw[lo]) / (_raw[hi] - _raw[lo]);
        return _probability[lo] + t * (_probability[hi] - _probability[lo]);
    }

    internal void AppendCanonical(StringBuilder sb)
    {
        for (int i = 0; i < _raw.Length; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{_raw[i]:R}>{_probability[i]:R},");
    }
}

/// <summary>
/// The confidence calibration fit on the corpus (spec §3.9, build step 5): one map
/// for <c>F0Confidence</c> and one per published voicing state for
/// <c>VoicingConfidence</c>. Part of <see cref="AnalysisConfig"/>, so the config
/// hash covers every knot.
/// </summary>
/// <remarks>
/// A state's map is null when the corpus has no reference that can judge that
/// state yet. Creak needs hand labels (step 6): a binary voiced/unvoiced reference
/// can't say whether a Creak frame was right. Those frames publish the raw score,
/// and <see cref="IsCalibrated"/> says so, so the game can treat it as "not a
/// probability" rather than compare it with a floor.
/// </remarks>
public sealed record ConfidenceCalibrationTable
{
    /// <summary>Identifies the fit (corpus, date, run). Stamped as <c>ConfidenceCalibration</c>.</summary>
    public required string Name { get; init; }

    public required CalibrationMap F0 { get; init; }

    public CalibrationMap? Silence { get; init; }
    public CalibrationMap? Unvoiced { get; init; }
    public CalibrationMap? Voiced { get; init; }
    public CalibrationMap? Creak { get; init; }

    public CalibrationMap? VoicingFor(VoicingState state) => state switch
    {
        VoicingState.Silence => Silence,
        VoicingState.Unvoiced => Unvoiced,
        VoicingState.Voiced => Voiced,
        VoicingState.Creak => Creak,
        _ => null,
    };

    /// <summary>Whether <c>VoicingConfidence</c> on a frame in this state is a calibrated probability.</summary>
    public bool IsCalibrated(VoicingState state) => VoicingFor(state) is not null;

    internal string Canonical()
    {
        var sb = new StringBuilder();
        sb.Append("Name=").Append(Name).Append(";F0=");
        F0.AppendCanonical(sb);
        foreach (var state in new[] { VoicingState.Silence, VoicingState.Unvoiced, VoicingState.Voiced, VoicingState.Creak })
        {
            sb.Append(';').Append(state).Append('=');
            if (VoicingFor(state) is { } map)
                map.AppendCanonical(sb);
            else
                sb.Append("raw");
        }
        return sb.ToString();
    }
}
