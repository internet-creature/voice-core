using VoiceCore;
using VoiceCore.Streaming;

namespace VoiceProbe.Capture;

/// <summary>
/// The live chain from spec §2, assembled: capture writes 48 kHz mono into the
/// ring, the pump analyzes on its own thread, and consumers read the frame queue
/// (every frame) or the triple buffer (latest frame). Capture-to-result latency
/// is recorded for every frame, and the analyzer resets at every capture gap.
/// </summary>
public sealed class AnalysisPipeline : IDisposable
{
    public const int AudioRingCapacity = 1 << 17;  // ≈ 2.7 s at 48 kHz (§2)

    public AnalysisPipeline(AnalysisConfig config)
    {
        Analyzer = new VoiceAnalyzer(config);
        Frames = new FrameQueue(Analyzer.Diagnostics);
        Pump = new LiveAnalysisPump(Audio, Analyzer, Frames, Latest, Arrivals, Gaps);
    }

    public SpscOverwriteRing<float> Audio { get; } = new(AudioRingCapacity);
    public ArrivalLog Arrivals { get; } = new();
    public CaptureGapLog Gaps { get; } = new();
    public VoiceAnalyzer Analyzer { get; }
    public FrameQueue Frames { get; }
    public TripleBuffer<AnalysisFrame> Latest { get; } = new();
    public LiveAnalysisPump Pump { get; }
    public AnalyzerDiagnostics Diagnostics => Analyzer.Diagnostics;

    /// <summary>Capture thread only. 48 kHz mono in −1..1. No allocation, no locking.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return;
        // log the arrival first: once the audio is published the pump can turn it
        // into frames immediately, and each frame's arrival must already be findable
        Arrivals.Record(Audio.PublishedIndex + samples.Length);
        Audio.Write(samples);
    }

    /// <summary>
    /// Capture thread only: audio was lost (e.g. driver input overflow) just before
    /// the next sample written. The analyzer resets there instead of analyzing
    /// across the discontinuity.
    /// </summary>
    public void MarkGap() => Gaps.Record(Audio.PublishedIndex);

    public void Start() => Pump.Start();

    public void Dispose() => Pump.Stop();
}

/// <summary>
/// Turns a device's native stream into the analyzer's 48 kHz mono (spec §3):
/// keeps channel 0 of interleaved input, and resamples 44.1 kHz with
/// <see cref="PolyphaseResampler"/>. Capture thread only; allocation-free after
/// construction.
/// </summary>
public sealed class CaptureConverter
{
    private readonly int _channels;
    private readonly PolyphaseResampler? _resampler;
    private readonly Action<ReadOnlySpan<float>> _sink;
    private readonly float[] _mono;
    private readonly float[] _resampled;

    public CaptureConverter(CaptureFormat format, Action<ReadOnlySpan<float>> sink, int maxFramesPerChunk = 4096)
    {
        _channels = format.Channels;
        _sink = sink;
        _mono = new float[maxFramesPerChunk];
        if (format.Resample)
        {
            if (format.SampleRate != 44100)
                throw new ArgumentException($"Only 44100 → 48000 Hz resampling is supported, not {format.SampleRate}.");
            _resampler = PolyphaseResampler.Create44100To48000();
            _resampled = new float[_resampler.MaxOutputFor(maxFramesPerChunk)];
        }
        else
        {
            _resampled = [];
        }
    }

    /// <summary>Capture thread only: forget stream history after a gap.</summary>
    public void Reset() => _resampler?.Reset();

    /// <summary>Resampler group delay in 48 kHz samples; 0 when not resampling.</summary>
    public double ResamplerDelaySamples => _resampler?.DelayOutputSamples ?? 0;

    public void WriteInterleaved(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        for (int start = 0; start < frames; start += _mono.Length)
        {
            int n = Math.Min(_mono.Length, frames - start);
            for (int i = 0; i < n; i++)
                _mono[i] = interleaved[(start + i) * _channels];
            Emit(_mono.AsSpan(0, n));
        }
    }

    /// <summary>For sources that already split channels (e.g. Godot's left channel).</summary>
    public void WriteMono(ReadOnlySpan<float> mono)
    {
        for (int start = 0; start < mono.Length; start += _mono.Length)
            Emit(mono.Slice(start, Math.Min(_mono.Length, mono.Length - start)));
    }

    private void Emit(ReadOnlySpan<float> mono)
    {
        if (_resampler is null)
        {
            _sink(mono);
            return;
        }
        int n = _resampler.Process(mono, _resampled);
        _sink(_resampled.AsSpan(0, n));
    }
}
