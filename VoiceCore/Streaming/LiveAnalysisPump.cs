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
    private readonly float[] _chunk = new float[MaxBacklogSamples];
    private readonly AnalysisFrame[] _frameScratch = new AnalysisFrame[VoiceAnalyzer.MaxFramesFor(MaxBacklogSamples)];

    private long _position;  // capture index of the next sample to analyze
    private Thread? _thread;
    private volatile bool _stopRequested;

    /// <summary>
    /// Starts analysis at the ring's current write position; audio already in the
    /// ring is ignored.
    /// </summary>
    public LiveAnalysisPump(
        SpscOverwriteRing<float> audio,
        VoiceAnalyzer analyzer,
        FrameQueue frames,
        TripleBuffer<AnalysisFrame> latest)
    {
        if (audio.Capacity <= MaxBacklogSamples)
            throw new ArgumentException($"Audio ring must hold more than {MaxBacklogSamples} samples.", nameof(audio));
        _audio = audio;
        _analyzer = analyzer;
        _frames = frames;
        _latest = latest;
        _position = audio.PublishedIndex;
        analyzer.Reset(_position);
    }

    /// <summary>
    /// Analyzes all audio available now. Returns the number of frames produced.
    /// Call from one thread only: the analysis thread, or a test.
    /// </summary>
    public int PumpOnce()
    {
        int total = 0;
        while (true)
        {
            long published = _audio.PublishedIndex;
            long backlog = published - _position;
            if (backlog > MaxBacklogSamples)
            {
                DropBacklog(published);
                continue;
            }
            if (backlog == 0)
                return total;

            var chunk = _chunk.AsSpan(0, (int)backlog);
            if (!_audio.TryCopy(_position, chunk))
            {
                DropBacklog(_audio.PublishedIndex);
                continue;
            }
            _position += chunk.Length;

            int produced = _analyzer.Process(chunk, _frameScratch);
            if (produced > 0)
            {
                _frames.Enqueue(_frameScratch.AsSpan(0, produced));
                _latest.Publish(in _frameScratch[produced - 1]);
                total += produced;
            }
        }
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

    private void DropBacklog(long published)
    {
        long resume = Math.Max(_position, published - VoiceAnalyzer.WindowSamples);
        _analyzer.Diagnostics.RecordOverrun(resume - _position);
        _position = resume;
        _analyzer.Reset(resume);
    }
}
