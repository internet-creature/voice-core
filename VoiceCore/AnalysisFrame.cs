namespace VoiceCore;

public enum VoicingState : byte { Silence, Unvoiced, Voiced, Creak }

public enum F0Range : byte { In, Above, Below }

/// <summary>
/// One analysis frame on the 10 ms grid (spec §4). NaN means "not measured",
/// never 0 — a zero silently poisons every downstream average.
/// </summary>
public readonly record struct AnalysisFrame
{
    // timing (§3.1)
    public long   WindowCenterSample   { get; init; }
    public long   ResultAvailableSample{ get; init; }
    public double TimeSeconds          { get; init; }  // WindowCenterSample / 48000.0

    // voicing. until the classifier exists (build step 4) Voicing is left at its
    // default and VoicingConfidence is NaN, meaning no voicing decision was made.
    public VoicingState Voicing        { get; init; }
    public float  VoicingConfidence    { get; init; }  // calibrated probability, §3.9

    // pitch
    public float  F0RawHz              { get; init; }  // internal candidate, always logged
    public float  F0Hz                 { get; init; }  // published; NaN unless Voiced
    public float  F0DisplayHz          { get; init; }  // causal median + slew; NaN unless Voiced
    public float  F0Cents              { get; init; }  // re 55 Hz, from F0Hz
    public float  F0Confidence         { get; init; }  // calibrated probability, §3.9; NaN unless Voiced and F0Range == In
    public F0Range F0Range             { get; init; }  // In | Above | Below, §3.4
    public float  Aperiodicity         { get; init; }  // YIN d′ at chosen lag

    // level
    public float  RmsDbfs              { get; init; }
    public float  PeakDbfs             { get; init; }
    public bool   Clipping             { get; init; }

    // formants — individually NaN when invalid (§3.6)
    public float  F1Hz { get; init; }  public float B1Hz { get; init; }
    public float  F2Hz { get; init; }  public float B2Hz { get; init; }
    public float  F3Hz { get; init; }  public float B3Hz { get; init; }
    public float  F4Hz { get; init; }  public float B4Hz { get; init; }
    public float  FormantConfidence    { get; init; }

    // voice quality
    public float  CppDb                { get; init; }

    // experimental (§3.7b) — NaN unless Voiced
    public float  BrightnessProxy      { get; init; }  // variant set by AnalysisConfig
    public float  SpectralTiltDbPerKhz { get; init; }

    /// <summary>Every measurement NaN; the analyzer fills in what it computes.</summary>
    internal static readonly AnalysisFrame Unmeasured = new()
    {
        VoicingConfidence = float.NaN,
        F0RawHz = float.NaN, F0Hz = float.NaN, F0DisplayHz = float.NaN,
        F0Cents = float.NaN, F0Confidence = float.NaN, Aperiodicity = float.NaN,
        RmsDbfs = float.NaN, PeakDbfs = float.NaN,
        F1Hz = float.NaN, B1Hz = float.NaN, F2Hz = float.NaN, B2Hz = float.NaN,
        F3Hz = float.NaN, B3Hz = float.NaN, F4Hz = float.NaN, B4Hz = float.NaN,
        FormantConfidence = float.NaN,
        CppDb = float.NaN,
        BrightnessProxy = float.NaN, SpectralTiltDbPerKhz = float.NaN,
    };
}
