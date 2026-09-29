namespace VoiceProbe.Capture;

/// <summary>How a device is opened, and what the probe does to reach 48 kHz mono.</summary>
/// <param name="SampleRate">Rate the device is opened at: 48000, or 44100 with resampling.</param>
/// <param name="Channels">Channels opened; only the first (left) is kept.</param>
public sealed record CaptureFormat(int SampleRate, int Channels)
{
    public bool Resample => SampleRate != VoiceCore.VoiceAnalyzer.SampleRate;

    public override string ToString() =>
        $"{SampleRate} Hz × {Channels} ch → {(Resample ? "resampled to 48000 Hz, " : "")}{(Channels > 1 ? "left channel" : "mono")}";
}

public sealed class UnsupportedFormatException(string message) : Exception(message);

/// <summary>
/// The device format policy (spec §3): prefer 48 kHz; accept 44.1 kHz and
/// resample; open mono if the device allows it, otherwise open more channels
/// and keep the left one (averaging two mics in unknown phase can comb-filter).
/// Anything else is an explicit error listing what the device does support,
/// never silent garbage.
/// </summary>
public static class FormatPolicy
{
    public static readonly int[] AcceptedRates = [48000, 44100];

    /// <summary>Rates listed in the error message when nothing acceptable is supported.</summary>
    public static readonly int[] ProbedRates = [8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000];

    /// <param name="supports">Whether the device can open at (rate, channels), float32.</param>
    /// <param name="maxInputChannels">The device's channel count.</param>
    public static CaptureFormat Choose(string deviceName, int maxInputChannels, Func<int, int, bool> supports)
    {
        var channelOptions = new[] { 1, 2, maxInputChannels }.Where(c => c >= 1 && c <= maxInputChannels).Distinct().ToArray();
        foreach (int rate in AcceptedRates)
            foreach (int channels in channelOptions)
                if (supports(rate, channels))
                    return new CaptureFormat(rate, channels);

        var native = ProbedRates
            .SelectMany(r => channelOptions.Where(c => supports(r, c)).Select(c => $"{r} Hz × {c} ch"))
            .ToList();
        throw new UnsupportedFormatException(
            $"'{deviceName}' supports neither 48000 nor 44100 Hz as float32. " +
            (native.Count > 0 ? $"Native formats found: {string.Join(", ", native)}." : "No common float32 formats were accepted."));
    }
}
