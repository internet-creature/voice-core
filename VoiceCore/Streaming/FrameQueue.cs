namespace VoiceCore.Streaming;

/// <summary>
/// The primary frame output (spec §2): every frame reaches the trace renderer,
/// scoring and logging, unlike <see cref="TripleBuffer{T}"/>, which drops frames
/// by design. If the consumer stalls, the oldest frames are dropped and counted
/// in <see cref="AnalyzerDiagnostics.FrameQueueOverruns"/>; the consumer should
/// treat that gap like a capture gap and score nothing across it.
/// </summary>
public sealed class FrameQueue
{
    public const int DefaultCapacity = 256;  // ≈ 2.5 s of frames

    private readonly SpscOverwriteRing<AnalysisFrame> _ring;
    private readonly AnalyzerDiagnostics? _diagnostics;
    private long _readIndex;

    public FrameQueue(AnalyzerDiagnostics? diagnostics = null, int capacity = DefaultCapacity)
    {
        _ring = new SpscOverwriteRing<AnalysisFrame>(capacity);
        _diagnostics = diagnostics;
    }

    public int Capacity => _ring.Capacity;

    /// <summary>Analysis thread only.</summary>
    public void Enqueue(ReadOnlySpan<AnalysisFrame> frames) => _ring.Write(frames);

    /// <summary>
    /// Consumer only. Copies up to <c>destination.Length</c> frames, oldest first.
    /// <paramref name="droppedFrames"/> is how many frames were lost to overflow
    /// immediately before the first one returned.
    /// </summary>
    public int Drain(Span<AnalysisFrame> destination, out long droppedFrames)
    {
        droppedFrames = 0;
        while (true)
        {
            long published = _ring.PublishedIndex;
            // slots below claimed − Capacity may already be overwritten
            long oldest = _ring.ClaimedIndex - Capacity;
            if (_readIndex < oldest)
            {
                droppedFrames += oldest - _readIndex;
                _readIndex = oldest;
            }

            int count = (int)Math.Min(destination.Length, Math.Max(0, published - _readIndex));
            if (count == 0 || _ring.TryCopy(_readIndex, destination[..count]))
            {
                _readIndex += count;
                if (droppedFrames > 0)
                    _diagnostics?.RecordFrameQueueOverrun(droppedFrames);
                return count;
            }
            // the producer lapped us mid-copy; skip past what it overwrote and retry
        }
    }
}
