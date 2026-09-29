using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VoiceCore;

/// <summary>
/// Immutable analyzer configuration (spec §1.1). Changing a parameter means
/// constructing a new <see cref="VoiceAnalyzer"/>. The version string and
/// content hash are stamped into Parquet metadata and session log headers,
/// never into individual frames.
/// </summary>
/// <remarks>
/// Every threshold here is a starting default from the spec, to be retuned against
/// the corpus (§3.3: "expect to retune"). The noise floor is not here: it is a
/// per-session calibration measurement (§3.2), set on the analyzer.
/// </remarks>
public sealed record AnalysisConfig
{
    public static AnalysisConfig Default { get; } = new();

    public string AnalyzerVersion { get; init; } = "0.2.0";

    /// <summary>
    /// "none" until a calibration table is fit on the corpus (spec §3.9, build
    /// step 5). Tells consumers that confidences are raw scores, not probabilities.
    /// </summary>
    public string ConfidenceCalibration { get; init; } = "none";

    // --- preprocessing (§3.2) ---

    /// <summary>DC-removal one-pole high-pass coefficient.</summary>
    public float DcFilterPole { get; init; } = 0.995f;

    /// <summary>Peak level above which a hop is flagged as clipping.</summary>
    public float ClippingThresholdDbfs { get; init; } = -0.1f;

    /// <summary>Noise floor used until the session calibrates one (§3.2).</summary>
    public float DefaultNoiseFloorDbfs { get; init; } = -70f;

    /// <summary>Time constant for adapting the floor, during Silence frames only.</summary>
    public float NoiseFloorTimeConstantSeconds { get; init; } = 10f;

    // --- voicing (§3.3) ---

    /// <summary>Level gate: a frame "has energy" above noise floor + this margin.</summary>
    public float VoicedLevelMarginDb { get; init; } = 8f;

    /// <summary>Aperiodicity below this is Voiced.</summary>
    public float VoicedAperiodicityMax { get; init; } = 0.20f;

    /// <summary>Upper edge of the breathy/creak band: Voiced if STABLE, creak candidate if not.</summary>
    public float BreathyAperiodicityMax { get; init; } = 0.45f;

    /// <summary>STABLE: candidate within this many cents of the median of the previous 3.</summary>
    public float StableCents { get; init; } = 50f;

    /// <summary>"Low" zero-crossing rate for a creak candidate, in sign changes per second.</summary>
    public float LowZcrPerSecond { get; init; } = 2000f;

    /// <summary>Creak is published when this many of the last <see cref="CreakWindowFrames"/> non-silent frames are candidates.</summary>
    public int CreakVotesRequired { get; init; } = 3;
    public int CreakWindowFrames { get; init; } = 5;

    /// <summary>Consecutive contrary frames needed to leave Voiced or Creak.</summary>
    public int HysteresisFrames { get; init; } = 2;

    // --- f0 (§3.4) ---

    public float F0SearchMinHz { get; init; } = 60f;
    public float F0SearchMaxHz { get; init; } = 1000f;

    /// <summary>YIN absolute threshold: the first d′ dip below it wins.</summary>
    public float YinThreshold { get; init; } = 0.15f;

    /// <summary>Longest lag searched, in samples (60 Hz → 800).</summary>
    public int MaxLag => (int)Math.Floor(VoiceAnalyzer.SampleRate / F0SearchMinHz);

    /// <summary>Shortest in-range lag, in samples (1000 Hz → 48). Shorter dips mean "above range".</summary>
    public int MinLag => (int)Math.Ceiling(VoiceAnalyzer.SampleRate / F0SearchMaxHz);

    /// <summary>
    /// SHA-256 over every parameter that affects output, including the fixed
    /// grid constants, so two configs with the same hash produce the same frames.
    /// </summary>
    public string ComputeContentHash()
    {
        string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        var canonical = string.Join(';',
            $"AnalyzerVersion={AnalyzerVersion}",
            $"ConfidenceCalibration={ConfidenceCalibration}",
            $"SampleRate={VoiceAnalyzer.SampleRate}",
            $"HopSamples={VoiceAnalyzer.HopSamples}",
            $"WindowSamples={VoiceAnalyzer.WindowSamples}",
            $"DcFilterPole={F(DcFilterPole)}",
            $"ClippingThresholdDbfs={F(ClippingThresholdDbfs)}",
            $"DefaultNoiseFloorDbfs={F(DefaultNoiseFloorDbfs)}",
            $"NoiseFloorTimeConstantSeconds={F(NoiseFloorTimeConstantSeconds)}",
            $"VoicedLevelMarginDb={F(VoicedLevelMarginDb)}",
            $"VoicedAperiodicityMax={F(VoicedAperiodicityMax)}",
            $"BreathyAperiodicityMax={F(BreathyAperiodicityMax)}",
            $"StableCents={F(StableCents)}",
            $"LowZcrPerSecond={F(LowZcrPerSecond)}",
            $"CreakVotesRequired={CreakVotesRequired}",
            $"CreakWindowFrames={CreakWindowFrames}",
            $"HysteresisFrames={HysteresisFrames}",
            $"F0SearchMinHz={F(F0SearchMinHz)}",
            $"F0SearchMaxHz={F(F0SearchMaxHz)}",
            $"YinThreshold={F(YinThreshold)}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(AnalyzerVersion))
            throw new ArgumentException("AnalyzerVersion must be set.");
        if (!(DcFilterPole > 0f && DcFilterPole < 1f))
            throw new ArgumentException($"DcFilterPole must be in (0, 1), was {DcFilterPole}.");
        if (!(ClippingThresholdDbfs <= 0f))
            throw new ArgumentException($"ClippingThresholdDbfs must be <= 0, was {ClippingThresholdDbfs}.");
        if (!(NoiseFloorTimeConstantSeconds > 0f))
            throw new ArgumentException("NoiseFloorTimeConstantSeconds must be positive.");
        if (!(VoicedAperiodicityMax > 0f && BreathyAperiodicityMax > VoicedAperiodicityMax))
            throw new ArgumentException("Need 0 < VoicedAperiodicityMax < BreathyAperiodicityMax.");
        if (!(YinThreshold > 0f && YinThreshold < 1f))
            throw new ArgumentException("YinThreshold must be in (0, 1).");
        if (CreakWindowFrames < 1 || CreakVotesRequired < 1 || CreakVotesRequired > CreakWindowFrames || CreakWindowFrames > 32)
            throw new ArgumentException("Need 1 ≤ CreakVotesRequired ≤ CreakWindowFrames ≤ 32.");
        if (HysteresisFrames < 1)
            throw new ArgumentException("HysteresisFrames must be at least 1.");
        // the integration window W = 2048 − MaxLag must exceed the longest period (§3.4)
        if (!(F0SearchMinHz > VoiceAnalyzer.SampleRate / (VoiceAnalyzer.WindowSamples / 2.0)))
            throw new ArgumentException($"F0SearchMinHz must be above {VoiceAnalyzer.SampleRate / (VoiceAnalyzer.WindowSamples / 2.0):0.#} Hz so the YIN window outlasts the longest period.");
        if (!(F0SearchMaxHz > F0SearchMinHz && MinLag >= 3))
            throw new ArgumentException("F0SearchMaxHz must exceed F0SearchMinHz and stay below 16 kHz.");
    }
}
