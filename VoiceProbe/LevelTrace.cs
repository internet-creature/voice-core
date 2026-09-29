using Godot;

namespace VoiceProbe;

/// <summary>
/// Scrolling RMS history drawn from every frame of the frame queue, not the
/// render-rate latest value, so short events aren't lost between redraws.
/// </summary>
public partial class LevelTrace : Control
{
    private const int Capacity = 600;  // 6 s at 100 frames/s
    private readonly float[] _values = new float[Capacity];
    private int _next;
    private int _count;
    private readonly Vector2[] _points = new Vector2[Capacity];

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0, 140);
    }

    public void Add(float rmsDbfs)
    {
        _values[_next] = float.IsNaN(rmsDbfs) ? LevelMeter.MinDb : Mathf.Clamp(rmsDbfs, LevelMeter.MinDb, 0);
        _next = (_next + 1) % Capacity;
        _count = Mathf.Min(_count + 1, Capacity);
    }

    /// <summary>Marks a gap (overrun or queue overflow): nothing is drawn across it.</summary>
    public void Clear()
    {
        _count = 0;
        QueueRedraw();
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.08f, 0.08f, 0.1f));
        for (int db = -70; db < 0; db += 10)
        {
            float y = Y(db);
            DrawLine(new Vector2(0, y), new Vector2(Size.X, y), new Color(1, 1, 1, 0.07f));
            DrawString(ThemeDB.FallbackFont, new Vector2(4, y - 2), db.ToString(), HorizontalAlignment.Left, -1, 10, new Color(1, 1, 1, 0.35f));
        }

        if (_count < 2)
            return;
        float step = Size.X / (Capacity - 1);
        int start = (_next - _count + Capacity) % Capacity;
        for (int i = 0; i < _count; i++)
            _points[i] = new Vector2(Size.X - (_count - 1 - i) * step, Y(_values[(start + i) % Capacity]));
        DrawPolyline(_points.AsSpan(0, _count).ToArray(), new Color(0.3f, 0.85f, 0.5f), 1.5f, antialiased: true);
    }

    private float Y(float db) => (1 - (db - LevelMeter.MinDb) / -LevelMeter.MinDb) * Size.Y;
}
