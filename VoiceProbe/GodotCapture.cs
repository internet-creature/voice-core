using Godot;
using VoiceProbe.Capture;

namespace VoiceProbe;

/// <summary>
/// The Godot capture path for the phase 1a comparison: microphone →
/// AudioStreamMicrophone → a muted bus with an AudioEffectCapture, pulled once
/// per rendered frame. Godot resamples input to its mix rate and may hand over
/// an OS-processed stream; this path can't request raw capture.
/// </summary>
/// <remarks>
/// "Arrival" here is when <see cref="_Process"/> pulls the buffer, so
/// capture-to-result doesn't see Godot's internal buffering or the render-rate
/// pull cadence. Only the camera test compares the two paths end to end.
/// </remarks>
public partial class GodotCapture : Node
{
    private const string BusName = "ProbeMic";

    private AudioEffectCapture? _effect;
    private AudioStreamPlayer? _player;
    private CaptureConverter? _converter;
    private AnalysisPipeline? _pipeline;
    private long _discarded;
    private float[] _left = new float[4096];

    public CaptureFormat? Format { get; private set; }
    public string DeviceName { get; private set; } = "";
    public long Pulls { get; private set; }
    public long LargestPullFrames { get; private set; }

    /// <summary>Times Godot's capture buffer overflowed and dropped audio; each is marked as a gap.</summary>
    public long Overflows { get; private set; }

    public static string[] InputDevices() => AudioServer.GetInputDeviceList();

    /// <summary>Starts capturing <paramref name="device"/> into <paramref name="pipeline"/>.</summary>
    public void Begin(string device, AnalysisPipeline pipeline)
    {
        AudioServer.InputDevice = device;
        DeviceName = AudioServer.InputDevice;

        int bus = AudioServer.GetBusIndex(BusName);
        if (bus < 0)
        {
            AudioServer.AddBus();
            bus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(bus, BusName);
            AudioServer.AddBusEffect(bus, new AudioEffectCapture { BufferLength = 0.5f });
            AudioServer.SetBusMute(bus, true);  // never play the mic back out
        }
        _effect = (AudioEffectCapture)AudioServer.GetBusEffect(bus, 0);
        _effect.ClearBuffer();
        _discarded = _effect.GetDiscardedFrames();
        _pipeline = pipeline;
        Overflows = 0;

        // Godot delivers stereo at its mix rate; the converter keeps the left channel
        Format = new CaptureFormat((int)AudioServer.GetMixRate(), 2);
        _converter = new CaptureConverter(Format, pipeline.Write);

        _player = new AudioStreamPlayer { Stream = new AudioStreamMicrophone(), Bus = BusName };
        AddChild(_player);
        _player.Play();
    }

    public void End()
    {
        _player?.Stop();
        _player?.QueueFree();
        _player = null;
        _converter = null;
        _pipeline = null;
        _effect = null;
    }

    public override void _Process(double delta)
    {
        if (_effect is null || _converter is null || _pipeline is null)
            return;

        // Godot drops the oldest audio when its buffer fills (e.g. a long frame
        // hitch); don't analyze across that discontinuity
        long discarded = _effect.GetDiscardedFrames();
        if (discarded != _discarded)
        {
            _discarded = discarded;
            Overflows++;
            _converter.Reset();
            _pipeline.MarkGap();
        }

        int available = _effect.GetFramesAvailable();
        if (available == 0)
            return;

        Vector2[] frames = _effect.GetBuffer(available);  // allocates; acceptable in the disposable probe
        if (_left.Length < frames.Length)
            _left = new float[frames.Length];
        for (int i = 0; i < frames.Length; i++)
            _left[i] = frames[i].X;
        _converter.WriteMono(_left.AsSpan(0, frames.Length));

        Pulls++;
        LargestPullFrames = Math.Max(LargestPullFrames, frames.Length);
    }
}
