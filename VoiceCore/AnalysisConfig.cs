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
public sealed record AnalysisConfig
{
    public static AnalysisConfig Default { get; } = new();

    public string AnalyzerVersion { get; init; } = "0.1.0";

    /// <summary>
    /// "none" until a calibration table is fit on the corpus (spec §3.9, build
    /// step 5). Tells consumers that confidences are raw scores, not probabilities.
    /// </summary>
    public string ConfidenceCalibration { get; init; } = "none";

    /// <summary>DC-removal one-pole high-pass coefficient (spec §3.2).</summary>
    public float DcFilterPole { get; init; } = 0.995f;

    /// <summary>Peak level above which a hop is flagged as clipping (spec §3.2).</summary>
    public float ClippingThresholdDbfs { get; init; } = -0.1f;

    /// <summary>
    /// SHA-256 over every parameter that affects output, including the fixed
    /// grid constants, so two configs with the same hash produce the same frames.
    /// </summary>
    public string ComputeContentHash()
    {
        var canonical = string.Join(';',
            $"AnalyzerVersion={AnalyzerVersion}",
            $"ConfidenceCalibration={ConfidenceCalibration}",
            $"SampleRate={VoiceAnalyzer.SampleRate}",
            $"HopSamples={VoiceAnalyzer.HopSamples}",
            $"WindowSamples={VoiceAnalyzer.WindowSamples}",
            $"DcFilterPole={DcFilterPole.ToString("R", CultureInfo.InvariantCulture)}",
            $"ClippingThresholdDbfs={ClippingThresholdDbfs.ToString("R", CultureInfo.InvariantCulture)}");
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
    }
}
