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

    /// <summary>Times live mode dropped audio backlog and reset the analyzer.</summary>
    public long OverrunCount => Interlocked.Read(ref _overrunCount);

    /// <summary>Audio samples discarded by overruns.</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>Frames lost because the frame-queue consumer stalled.</summary>
    public long FrameQueueOverruns => Interlocked.Read(ref _frameQueueOverruns);

    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    public TimeSpan MaxAnalysisTimePerFrame =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _maxAnalysisTicks) / Stopwatch.Frequency);

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
