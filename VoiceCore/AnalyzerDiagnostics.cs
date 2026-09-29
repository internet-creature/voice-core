using System.Diagnostics;

namespace VoiceCore;

/// <summary>
/// Session counters (spec §2). Written by the analysis thread and the frame
/// consumer, readable from any thread. Logged per session; overruns > 0 on
/// target hardware is a bug. <see cref="VoiceAnalyzer.Reset"/> does not clear them.
/// </summary>
public sealed class AnalyzerDiagnostics
{
    private long _overrunCount;
    private long _droppedSamples;
    private long _frameQueueOverruns;
    private long _framesProduced;
    private long _maxAnalysisTicks;

    // capture-to-result histogram (spec §3.1, Gate B): 10 µs buckets up to 100 ms,
    // plus one overflow bucket. fixed size so recording never allocates.
    private const double BucketSeconds = 10e-6;
    private readonly long[] _captureToResult = new long[10_001];
    private long _captureToResultMaxTicks;

    /// <summary>Times live mode dropped audio backlog and reset the analyzer.</summary>
    public long OverrunCount => Interlocked.Read(ref _overrunCount);

    /// <summary>Audio samples discarded by overruns.</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>Frames lost because the frame-queue consumer stalled.</summary>
    public long FrameQueueOverruns => Interlocked.Read(ref _frameQueueOverruns);

    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    public TimeSpan MaxAnalysisTimePerFrame =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _maxAnalysisTicks) / Stopwatch.Frequency);

    /// <summary>Frames with a capture-to-result measurement (live mode with an <see cref="Streaming.ArrivalLog"/>).</summary>
    public long CaptureToResultCount
    {
        get
        {
            long n = 0;
            foreach (ref readonly long b in _captureToResult.AsSpan())
                n += Volatile.Read(in b);
            return n;
        }
    }

    public TimeSpan CaptureToResultMax =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _captureToResultMaxTicks) / Stopwatch.Frequency);

    /// <summary>
    /// Capture-to-result latency at percentile <paramref name="p"/> (0–100), as the
    /// upper edge of its 10 µs bucket; <see cref="TimeSpan.MaxValue"/> if it falls
    /// past 100 ms. Zero when nothing has been measured. Gate B gates p95 &lt; 10 ms.
    /// </summary>
    public TimeSpan CaptureToResultPercentile(double p)
    {
        if (p is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(p));
        long total = CaptureToResultCount;
        if (total == 0)
            return TimeSpan.Zero;
        long rank = Math.Max(1, (long)Math.Ceiling(p / 100 * total));
        long seen = 0;
        for (int i = 0; i < _captureToResult.Length; i++)
        {
            seen += Volatile.Read(ref _captureToResult[i]);
            if (seen >= rank)
                return i == _captureToResult.Length - 1 ? TimeSpan.MaxValue : TimeSpan.FromSeconds((i + 1) * BucketSeconds);
        }
        return TimeSpan.MaxValue;
    }

    /// <summary>
    /// Clears the capture-to-result statistics, e.g. after start-up. Only
    /// approximate if frames are being recorded at the same moment.
    /// </summary>
    public void ResetCaptureToResult()
    {
        for (int i = 0; i < _captureToResult.Length; i++)
            Interlocked.Exchange(ref _captureToResult[i], 0);
        Interlocked.Exchange(ref _captureToResultMaxTicks, 0);
    }

    internal void RecordCaptureToResult(long elapsedStopwatchTicks)
    {
        double seconds = (double)Math.Max(0, elapsedStopwatchTicks) / Stopwatch.Frequency;
        int bucket = (int)Math.Min(_captureToResult.Length - 1, seconds / BucketSeconds);
        Interlocked.Increment(ref _captureToResult[bucket]);
        if (elapsedStopwatchTicks > Interlocked.Read(ref _captureToResultMaxTicks))
            Interlocked.Exchange(ref _captureToResultMaxTicks, elapsedStopwatchTicks);
    }

    internal void RecordFrame(long elapsedStopwatchTicks)
    {
        Interlocked.Increment(ref _framesProduced);
        // only the analysis thread writes the max, so no CAS loop is needed
        if (elapsedStopwatchTicks > Interlocked.Read(ref _maxAnalysisTicks))
            Interlocked.Exchange(ref _maxAnalysisTicks, elapsedStopwatchTicks);
    }

    internal void RecordOverrun(long droppedSamples)
    {
        Interlocked.Increment(ref _overrunCount);
        Interlocked.Add(ref _droppedSamples, droppedSamples);
    }

    internal void RecordFrameQueueOverrun(long droppedFrames) =>
        Interlocked.Add(ref _frameQueueOverruns, droppedFrames);
}
