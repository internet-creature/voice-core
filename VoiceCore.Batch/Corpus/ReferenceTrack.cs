using System.Globalization;

namespace VoiceCore.Batch.Corpus;

/// <summary>
/// What a reference says about one moment. <see cref="None"/>: the reference
/// doesn't cover it. <see cref="Exclude"/>: a hand label says annotators disagreed
/// or the frame shouldn't be scored (spec §6: excluded from gates, reported
/// separately).
/// </summary>
internal enum RefState : byte { Silence, Unvoiced, Voiced, Creak, Exclude, None }

/// <param name="Binary">
/// The reference only knows voiced vs not (Praat, laryngograph RAPT). Its
/// <see cref="RefState.Unvoiced"/> then covers silence too, and it can't judge
/// Creak frames.
/// </param>
internal readonly record struct RefPoint(RefState State, float F0Hz, bool Binary)
{
    public static readonly RefPoint Missing = new(RefState.None, float.NaN, true);
}

/// <summary>
/// A reference f0/voicing track (spec §6 reference layers 2 and 4), on its own
/// time grid: <c>time_s,state,f0_hz</c> with '#' provenance lines. Sampled at
/// analyzer frame centers by <see cref="At"/>.
/// </summary>
internal sealed class ReferenceTrack
{
    private readonly double[] _time;
    private readonly RefState[] _state;
    private readonly float[] _f0;
    private readonly double _halfHop;

    public ReferenceTrack(double[] time, RefState[] state, float[] f0, bool binary, IReadOnlyList<string> provenance)
    {
        if (time.Length != state.Length || time.Length != f0.Length)
            throw new ArgumentException("Reference columns differ in length.");
        for (int i = 1; i < time.Length; i++)
            if (!(time[i] > time[i - 1]))
                throw new InvalidDataException("Reference times must be strictly increasing.");
        _time = time;
        _state = state;
        _f0 = f0;
        Binary = binary;
        Provenance = provenance;
        _halfHop = time.Length > 1 ? (time[^1] - time[0]) / (time.Length - 1) / 2 : 0.005;
    }

    public bool Binary { get; }

    /// <summary>The '#' header lines: source, version, settings, time alignment.</summary>
    public IReadOnlyList<string> Provenance { get; }

    /// <summary>The header's "source:" line, used to group references in the run report.</summary>
    public string Source => Provenance.FirstOrDefault(p => p.StartsWith("source:", StringComparison.Ordinal))?["source:".Length..].Trim() ?? "unknown";

    public static ReferenceTrack Load(string path)
    {
        var (comments, rows) = Csv.Read(path);
        bool binary = comments.Any(c => c.StartsWith("states: binary", StringComparison.Ordinal));
        var time = new double[rows.Count];
        var state = new RefState[rows.Count];
        var f0 = new float[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            time[i] = double.Parse(rows[i]["time_s"], CultureInfo.InvariantCulture);
            state[i] = Enum.Parse<RefState>(rows[i]["state"]);
            string hz = rows[i].GetValueOrDefault("f0_hz", "");
            f0[i] = hz.Length > 0 ? float.Parse(hz, CultureInfo.InvariantCulture) : float.NaN;
            if (state[i] == RefState.Voiced && !(f0[i] > 0))
                f0[i] = float.NaN;  // voiced with no f0: scores voicing only
        }
        return new ReferenceTrack(time, state, f0, binary, comments);
    }

    /// <summary>
    /// The reference at time <paramref name="t"/>. Between two voiced points the f0
    /// is interpolated on a log scale; at a voicing boundary the nearer point wins.
    /// Beyond half a hop past either end, the reference doesn't cover the moment.
    /// </summary>
    public RefPoint At(double t)
    {
        if (_time.Length == 0 || t < _time[0] - _halfHop || t > _time[^1] + _halfHop)
            return RefPoint.Missing;
        int hi = Array.BinarySearch(_time, t);
        if (hi >= 0)
            return Point(hi);
        hi = ~hi;
        if (hi == 0)
            return Point(0);
        if (hi == _time.Length)
            return Point(_time.Length - 1);
        int lo = hi - 1;
        if (_state[lo] == RefState.Voiced && _state[hi] == RefState.Voiced && _f0[lo] > 0 && _f0[hi] > 0)
        {
            double w = (t - _time[lo]) / (_time[hi] - _time[lo]);
            float f0 = (float)Math.Exp(Math.Log(_f0[lo]) * (1 - w) + Math.Log(_f0[hi]) * w);
            return new RefPoint(RefState.Voiced, f0, Binary);
        }
        return Point(t - _time[lo] <= _time[hi] - t ? lo : hi);
    }

    private RefPoint Point(int i) => new(_state[i], _f0[i], Binary);
}

/// <summary>
/// Hand labels (spec §6 reference layer 3): <c>start_s,end_s,label</c> intervals
/// with labels Silence, Unvoiced, Voiced, Creak or Exclude. Where a label covers a
/// moment it overrides the reference's voicing state and makes it four-state; the
/// reference's f0 still applies to Voiced labels.
/// </summary>
internal sealed class HandLabels
{
    private readonly (double Start, double End, RefState Label)[] _intervals;

    public HandLabels((double, double, RefState)[] intervals) => _intervals = intervals;

    public static HandLabels Load(string path)
    {
        var rows = Csv.Read(path).Rows;
        return new HandLabels(rows.Select(r => (
            double.Parse(r["start_s"], CultureInfo.InvariantCulture),
            double.Parse(r["end_s"], CultureInfo.InvariantCulture),
            Enum.Parse<RefState>(r["label"]))).ToArray());
    }

    public RefPoint Apply(double t, RefPoint reference)
    {
        foreach (var (start, end, label) in _intervals)
        {
            if (t < start || t >= end)
                continue;
            float f0 = label == RefState.Voiced && reference.State == RefState.Voiced ? reference.F0Hz : float.NaN;
            return new RefPoint(label, f0, Binary: false);
        }
        return reference;
    }
}
