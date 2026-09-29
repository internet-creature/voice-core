using System.Diagnostics;

namespace VoiceCore.Streaming;

/// <summary>
/// The live analysis thread (spec §2): drains captured audio from the ring into
/// the analyzer and hands frames to the <see cref="FrameQueue"/> and the
/// latest-frame <see cref="TripleBuffer{T}"/>. Batch mode doesn't use this; it
/// calls <see cref="VoiceAnalyzer.Process"/> directly and never drops audio.
/// </summary>
/// <remarks>
/// Overflow policy: a full ring must never become seconds of stale feedback. If
/// the backlog exceeds <see cref="MaxBacklogSamples"/>, or the producer laps the
/// read position, the oldest audio is dropped, the analyzer is reset (temporal
/// state is invalid across the gap), and the overrun is counted. One window of
/// audio is kept so a frame can come out as soon as analysis resumes.
/// </remarks>
public sealed class LiveAnalysisPump : IDisposable
{
    public const int MaxBacklogSamples = VoiceAnalyzer.SampleRate / 10;  // 100 ms

    private readonly SpscOverwriteRing<float> _audio;
    private readonly VoiceAnalyzer _analyzer;
    private readonly FrameQueue _frames;
    private readonly TripleBuffer<AnalysisFrame> _latest;
    private readonly ArrivalLog? _arrivals;
    private readonly CaptureGapLog? _gaps;
    private readonly float[] _chunk = new float[MaxBacklogSamples];
    private readonly AnalysisFrame[] _frameScratch = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(MaxBacklogSamples)];

    private long _position;  // capture index of the next sample to analyze
    private Thread? _thread;
    private volatile bool _stopRequested;

    /// <summary>Test seam: runs after each analyzed chunk, e.g. to simulate capture arriving mid-pump.</summary>
    internal Action? AfterChunk;

    /// <summary>
    /// Starts analysis at the ring's current write position; audio already in the
    /// ring is ignored.
    /// </summary>
    /// <param name="arrivals">
    /// Where the capture callback logs its writes. When given, every frame's
    /// capture-to-result latency is recorded in <see cref="AnalyzerDiagnostics"/>.
    /// </param>
    /// <param name="gaps">
    /// Where the capture callback marks discontinuities (e.g. driver input
    /// overflow). The analyzer is reset exactly at each one.
    /// </param>
    public LiveAnalysisPump(
        SpscOverwriteRing<float> audio,
        VoiceAnalyzer analyzer,
        FrameQueue frames,
        TripleBuffer<AnalysisFrame> latest,
        ArrivalLog? arrivals = null,
        CaptureGapLog? gaps = null)
    {
        if (audio.Capacity <= MaxBacklogSamples)
            throw new ArgumentException($"Audio ring must hold more than {MaxBacklogSamples} samples.", nameof(audio));
        if (frames.Diagnostics != analyzer.Diagnostics)
            throw new ArgumentException("Frame queue must report to the analyzer's diagnostics, or its overruns go uncounted.", nameof(frames));
        _audio = audio;
        _analyzer = analyzer;
        _frames = frames;
        _latest = latest;
        _arrivals = arrivals;
        _gaps = gaps;
        _position = audio.PublishedIndex;
        analyzer.Reset(_position);
    }

    /// <summary>
    /// Analyzes the audio that had arrived when the call began, then returns, even
    /// while capture keeps writing, so the thread loop can see a stop request.
    /// Returns the number of frames produced. Call from one thread only: the
    /// analysis thread, or a test.
    /// </summary>
    public int PumpOnce()
    {
        int total = 0;
        long target = _audio.PublishedIndex;
        while (_position < target && !_stopRequested)
        {
            long published = _audio.PublishedIndex;
            if (published - _position > MaxBacklogSamples)
            {
                target = DropBacklog(published);
                continue;
            }

            long chunkEnd = Math.Min(target, _position + _chunk.Length);
            if (_gaps is not null)
            {
                bool hasGap = _gaps.TryPeek(_position, out long gap, out bool lost);
                if (lost)
                    ResetAtGap(_position);  // gap records were overwritten: assume one here
                if (hasGap && gap == _position)
                {
                    ResetAtGap(gap);
                    _gaps.Consume();
                    continue;
                }
                if (hasGap && gap < chunkEnd)
                    chunkEnd = gap;  // analyze up to the gap, reset, then go on
            }

            var chunk = _chunk.AsSpan(0, (int)(chunkEnd - _position));
            if (!_audio.TryCopy(_position, chunk))
            {
                target = DropBacklog(_audio.PublishedIndex);
                continue;
            }
            _position += chunk.Length;

            int produced = _analyzer.Process(chunk, _frameScratch);
            if (produced > 0)
            {
                _frames.Enqueue(_frameScratch.AsSpan(0, produced));
                _latest.Publish(in _frameScratch[produced - 1]);
                RecordCaptureToResult(produced);
                total += produced;
            }
            AfterChunk?.Invoke();
        }
        return total;
    }

    /// <summary>Starts the dedicated analysis thread (not the thread pool).</summary>
    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("Already started.");
        _stopRequested = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "VoiceCore analysis", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null)
            return;
        _stopRequested = true;
        _thread.Join();
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        while (!_stopRequested)
        {
            long before = _position;
            PumpOnce();
            // idle wait when no audio arrived. capture-to-result includes this
            // wakeup latency, so it is measured at build step 3 (Gate B).
            if (_position == before)
                Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Frames are readable now; latency runs from the arrival of the last sample
    /// each one depends on (spec §3.1).
    /// </summary>
    private void RecordCaptureToResult(int produced)
    {
        if (_arrivals is null)
            return;
        long now = Stopwatch.GetTimestamp();
        for (int i = 0; i < produced; i++)
        {
            if (_arrivals.TryGetArrival(_frameScratch[i].ResultAvailableSample - 1, out long arrived))
                _analyzer.Diagnostics.RecordCaptureToResult(now - arrived);
            else
                _analyzer.Diagnostics.RecordCaptureToResultMissed();
        }
    }

    private void ResetAtGap(long sampleIndex)
    {
        _analyzer.Reset(sampleIndex);
        _analyzer.Diagnostics.RecordCaptureGap();
    }

    /// <summary>Returns the new pump target: the published index it dropped against.</summary>
    private long DropBacklog(long published)
    {
        long resume = Math.Max(_position, published - VoiceAnalyzer.WindowSamples);
        _analyzer.Diagnostics.RecordOverrun(resume - _position);
        _position = resume;
        _analyzer.Reset(resume);
        return published;
    }
}
