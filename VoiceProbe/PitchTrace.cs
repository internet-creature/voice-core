using Godot;
using VoiceCore;
using VoiceProbe.Capture;

namespace VoiceProbe;

/// <summary>
/// Scrolling log-frequency pitch trace (spec §8 step 4), drawn from every frame of
/// the frame queue. Published F0Hz only, the raw tracker output with no display
/// smoothing yet (§3.5 arrives at step 6), so every tracker bug is visible by eye.
/// The line breaks wherever a frame isn't Voiced and across any gap in the frame
/// grid, and fades with F0Confidence. A strip along the bottom shows the voicing
/// state.
/// </summary>
public partial class PitchTrace : Control
{
    private const int Capacity = 600;  // 6 s at 100 frames/s
    private const float StripHeight = 8;

    private readonly float[] _f0 = new float[Capacity];
    private readonly float[] _confidence = new float[Capacity];
    private readonly VoicingState[] _state = new VoicingState[Capacity];
    private readonly bool[] _above = new bool[Capacity];
    private readonly bool[] _breakBefore = new bool[Capacity];
    private int _next;
    private int _count;
    private long _lastCenter = long.MinValue;

    public float MinHz { get; set; } = 60;
    public float MaxHz { get; set; } = 1000;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0, 300);
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public void Add(in AnalysisFrame f)
    {
        bool gap = _lastCenter != long.MinValue && f.WindowCenterSample - _lastCenter != VoiceAnalyzer.HopSamples;
        _lastCenter = f.WindowCenterSample;
        _f0[_next] = f.Voicing == VoicingState.Voiced ? f.F0Hz : float.NaN;
        _confidence[_next] = float.IsNaN(f.F0Confidence) ? 0 : f.F0Confidence;
        _state[_next] = f.Voicing;
        _above[_next] = f.Voicing == VoicingState.Voiced && f.F0Range == F0Range.Above;
        _breakBefore[_next] = gap;
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }

    public void Clear()
    {
        _count = 0;
        _lastCenter = long.MinValue;
        QueueRedraw();
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        float plotHeight = Size.Y - StripHeight - 2;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.07f, 0.07f, 0.09f));

        // note gridlines: every C (stronger) and A, labeled with note and Hz
        for (int midi = 12; midi < 120; midi++)
        {
            int pc = midi % 12;
            if (pc != 0 && pc != 9)
                continue;
            float hz = (float)(440 * Math.Pow(2, (midi - 69) / 12.0));
            if (hz < MinHz || hz > MaxHz)
                continue;
            float y = Y(hz, plotHeight);
            DrawLine(new Vector2(0, y), new Vector2(Size.X, y), new Color(1, 1, 1, pc == 0 ? 0.12f : 0.05f));
            DrawString(font, new Vector2(4, y - 3), $"{NoteNames.PitchClass(pc)}{NoteNames.Octave(midi)}  {hz:0} Hz",
                HorizontalAlignment.Left, -1, 11, new Color(1, 1, 1, pc == 0 ? 0.5f : 0.3f));
        }

        if (_count == 0)
            return;
        float step = Size.X / (Capacity - 1);
        int start = (_next - _count + Capacity) % Capacity;
        float X(int i) => Size.X - (_count - 1 - i) * step;

        for (int i = 0; i < _count; i++)
        {
            int k = (start + i) % Capacity;

            // voicing strip
            var stripColor = _state[k] switch
            {
                VoicingState.Voiced => new Color(0.3f, 0.85f, 0.5f, 0.8f),
                VoicingState.Creak => new Color(1f, 0.6f, 0.15f, 0.9f),
                VoicingState.Unvoiced => new Color(0.6f, 0.6f, 0.65f, 0.5f),
                _ => new Color(0, 0, 0, 0),
            };
            if (stripColor.A > 0)
                DrawRect(new Rect2(X(i) - step / 2, Size.Y - StripHeight, step + 0.5f, StripHeight), stripColor);

            // above the tracker's range: a marker on the top edge, never a folded value
            if (_above[k])
                DrawRect(new Rect2(X(i) - 1, 0, 2.5f, 6), new Color(1f, 0.3f, 0.3f));

            // the line: segment from the previous frame when both are voiced and adjacent
            if (i == 0 || _breakBefore[k])
                continue;
            int p = (start + i - 1) % Capacity;
            if (float.IsNaN(_f0[k]) || float.IsNaN(_f0[p]))
                continue;
            float alpha = 0.3f + 0.7f * Math.Clamp(_confidence[k], 0, 1);
            DrawLine(new Vector2(X(i - 1), Y(_f0[p], plotHeight)), new Vector2(X(i), Y(_f0[k], plotHeight)),
                new Color(0.45f, 0.8f, 1f, alpha), 2.5f, antialiased: true);
        }
    }

    private float Y(float hz, float plotHeight)
    {
        float t = (MathF.Log2(Math.Clamp(hz, MinHz, MaxHz)) - MathF.Log2(MinHz)) / (MathF.Log2(MaxHz) - MathF.Log2(MinHz));
        return (1 - t) * plotHeight;
    }
}
