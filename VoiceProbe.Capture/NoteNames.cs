namespace VoiceProbe.Capture;

/// <summary>Musical note names for pitch readouts (A4 = 440 Hz, equal temperament).</summary>
public static class NoteNames
{
    private static readonly string[] Names = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    /// <summary>Name of a pitch class 0–11 (C = 0).</summary>
    public static string PitchClass(int pc) => Names[((pc % 12) + 12) % 12];

    /// <summary>"A3 +12¢": the nearest note and the offset from it in cents.</summary>
    public static string Of(double hz)
    {
        double midi = 69 + 12 * Math.Log2(hz / 440);
        int nearest = (int)Math.Round(midi);
        int cents = (int)Math.Round((midi - nearest) * 100);
        return $"{PitchClass(nearest)}{Octave(nearest)} {(cents >= 0 ? "+" : "−")}{Math.Abs(cents)}¢";
    }

    /// <summary>Scientific-pitch octave of a MIDI note (60 = C4).</summary>
    public static int Octave(int midi) => (int)Math.Floor(midi / 12.0) - 1;
}
