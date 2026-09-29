using Godot;

namespace VoiceProbe;

/// <summary>Horizontal RMS and peak bars on a dBFS scale, with a latching clip lamp.</summary>
public partial class LevelMeter : Control
{
    public const float MinDb = -80f;

    private float _rmsDb = MinDb;
    private float _peakDb = MinDb;
    private float _peakHoldDb = MinDb;
    private double _peakHoldAge;
    private bool _clipLatched;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0, 86);
    }

    /// <summary>Feed the latest frame's level; call once per rendered frame.</summary>
    public void Show(float rmsDbfs, float peakDbfs, bool clipping, double delta)
    {
        _rmsDb = Clamp(rmsDbfs);
        _peakDb = Clamp(peakDbfs);
        _peakHoldAge += delta;
        if (_peakDb >= _peakHoldDb || _peakHoldAge > 1.5)
        {
            _peakHoldDb = _peakDb;
            _peakHoldAge = 0;
        }
        _clipLatched |= clipping;
        QueueRedraw();
    }

    public void ResetClip()
    {
        _clipLatched = false;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true })
            ResetClip();  // click to clear the clip lamp
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        const float lamp = 60, barHeight = 22, gap = 8, top = 4;
        float width = Size.X - lamp - 12;

        DrawBar(new Rect2(0, top, width, barHeight), _rmsDb, new Color(0.25f, 0.8f, 0.45f), "RMS");
        DrawBar(new Rect2(0, top + barHeight + gap, width, barHeight), _peakDb, new Color(0.95f, 0.75f, 0.2f), "peak");
        float holdX = X(_peakHoldDb, width);
        DrawLine(new Vector2(holdX, top + barHeight + gap), new Vector2(holdX, top + 2 * barHeight + gap), Colors.White, 2);

        float scaleY = top + 2 * barHeight + gap + 14;
        for (int db = (int)MinDb; db <= 0; db += 10)
        {
            float x = X(db, width);
            DrawLine(new Vector2(x, scaleY - 12), new Vector2(x, scaleY - 8), Colors.Gray);
            DrawString(font, new Vector2(x - 10, scaleY + 6), db.ToString(), HorizontalAlignment.Left, -1, 11, Colors.Gray);
        }

        var lampRect = new Rect2(Size.X - lamp, top, lamp, 2 * barHeight + gap);
        DrawRect(lampRect, _clipLatched ? new Color(0.9f, 0.1f, 0.1f) : new Color(0.2f, 0.2f, 0.2f));
        DrawString(font, lampRect.Position + new Vector2(14, 32), "CLIP", HorizontalAlignment.Left, -1, 14, Colors.White);
    }

    private void DrawBar(Rect2 rect, float db, Color color, string label)
    {
        DrawRect(rect, new Color(0.12f, 0.12f, 0.14f));
        DrawRect(new Rect2(rect.Position, new Vector2(X(db, rect.Size.X), rect.Size.Y)), color);
        DrawString(ThemeDB.FallbackFont, rect.Position + new Vector2(6, 16),
            $"{label} {(db <= MinDb ? "-∞" : db.ToString("0.0"))} dBFS", HorizontalAlignment.Left, -1, 13, Colors.White);
    }

    private static float X(float db, float width) => (Clamp(db) - MinDb) / -MinDb * width;

    private static float Clamp(float db) => float.IsNaN(db) ? MinDb : Mathf.Clamp(db, MinDb, 0);
}
