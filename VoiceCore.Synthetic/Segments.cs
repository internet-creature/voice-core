namespace VoiceCore.Synthetic;

/// <summary>Spectral shape of a harmonic complex, relative to the fundamental.</summary>
public sealed record HarmonicProfile
{
    /// <summary>Harmonic k sits at <c>TiltDbPerOctave · log2(k)</c> dB re H1.</summary>
    public double TiltDbPerOctave { get; init; } = -6;

    /// <summary>Extra gain on H1. Negative values make a weak-fundamental (octave-up bait) complex.</summary>
    public double H1OffsetDb { get; init; }

    /// <summary>Extra gain on H2. Positive values make a strong-2nd-harmonic (octave-up bait) complex.</summary>
    public double H2OffsetDb { get; init; }

    public int MaxHarmonics { get; init; } = 40;

    /// <summary>Harmonics fade out (cos² taper over the top 10%) and stop here.</summary>
    public double MaxFrequencyHz { get; init; } = 8000;

    public static HarmonicProfile Sine { get; } = new() { MaxHarmonics = 1 };

    /// <summary>−6 dB/octave: glottal source (−12) plus lip radiation (+6).</summary>
    public static HarmonicProfile Voice { get; } = new();

    /// <summary>H2 6 dB above H1.</summary>
    public static HarmonicProfile StrongSecond { get; } = new() { H2OffsetDb = 12 };

    /// <summary>H1 20 dB below where the tilt would put it.</summary>
    public static HarmonicProfile WeakFundamental { get; } = new() { H1OffsetDb = -20 };

    /// <summary>Steep tilt, as in breathy phonation.</summary>
    public static HarmonicProfile Breathy { get; } = new() { TiltDbPerOctave = -15 };

    public double AmplitudeDb(int harmonic) =>
        TiltDbPerOctave * Math.Log2(harmonic)
        + (harmonic == 1 ? H1OffsetDb : 0)
        + (harmonic == 2 ? H2OffsetDb : 0);
}

public enum NoiseKind
{
    /// <summary>Gaussian white noise, as additive room or mic noise.</summary>
    White,

    /// <summary>
    /// Aspiration-like: white noise, high-passed at 500 Hz (one pole), and
    /// amplitude-modulated in step with the glottal cycle by <c>1 + 0.5·cos φ</c>.
    /// </summary>
    Aspiration,
}

/// <summary>
/// Noise mixed into a voiced segment. <see cref="RatioDb"/> is the RMS ratio of
/// the harmonic part to the noise over the segment: SNR for white noise, HNR for
/// aspiration.
/// </summary>
public sealed record NoiseSpec(NoiseKind Kind, double RatioDb, int Seed = 1);

/// <summary>One stretch of a synthetic signal with a single ground-truth label.</summary>
public abstract record Segment(double Seconds);

public sealed record VoicedSegment(double Seconds, PitchContour Contour) : Segment(Seconds)
{
    public HarmonicProfile Harmonics { get; init; } = HarmonicProfile.Voice;

    /// <summary>Peak of the segment after noise is mixed in.</summary>
    public double PeakDbfs { get; init; } = -12;

    public NoiseSpec? Noise { get; init; }

    /// <summary>0: every harmonic starts in sine phase. Otherwise: seeded random phases.</summary>
    public int PhaseSeed { get; init; }

    /// <summary>Raised-cosine fade at each end, so segment edges don't click.</summary>
    public double RampSeconds { get; init; } = 0.005;
}

/// <summary>Digital silence (plus any background noise the signal adds).</summary>
public sealed record SilentSegment(double Seconds) : Segment(Seconds);

/// <summary>Unvoiced sound: white noise at a fixed RMS level, no f0.</summary>
public sealed record NoiseSegment(double Seconds, double RmsDbfs, int Seed = 1) : Segment(Seconds);
