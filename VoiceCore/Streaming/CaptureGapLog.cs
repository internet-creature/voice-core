namespace VoiceCore.Streaming;

/// <summary>
/// Marks where captured audio is discontinuous, e.g. the driver dropped input
/// (spec §2: temporal state is invalid across a gap). The capture callback records
/// the ring index where the gap sits; the analysis thread resets the analyzer
/// exactly there, so no frame's window spans it. Same threading as the audio ring.
/// </summary>
public sealed class CaptureGapLog
{
    public const int DefaultCapacity = 64;

    private readonly SpscOverwriteRing<long> _ring;
    private readonly long[] _one = new long[1];
    private long _readIndex;

    public CaptureGapLog(int capacity = DefaultCapacity) => _ring = new SpscOverwriteRing<long>(capacity);

    /// <summary>
    /// Capture thread only. <paramref name="sampleIndex"/> is the ring index of the
    /// first sample after the gap: call before writing that sample. No allocation.
    /// </summary>
    public void Record(long sampleIndex) => _ring.Write(sampleIndex);

    /// <summary>
    /// Analysis thread only. The next gap at or after <paramref name="position"/>,
    /// discarding any before it (an overrun already reset past them).
    /// <paramref name="lost"/> is true if unread gaps were overwritten; the caller
    /// should then treat <paramref name="position"/> as a gap.
    /// </summary>
    internal bool TryPeek(long position, out long gap, out bool lost)
    {
        lost = false;
        while (true)
        {
            long oldest = _ring.ClaimedIndex - _ring.Capacity;
            if (_readIndex < oldest)
            {
                _readIndex = oldest;
                lost = true;
            }
            if (_readIndex >= _ring.PublishedIndex)
            {
                gap = 0;
                return false;
            }
            if (!_ring.TryCopy(_readIndex, _one))
                continue;
            if (_one[0] >= position)
            {
                gap = _one[0];
                return true;
            }
            _readIndex++;  // behind us: already covered by a reset
        }
    }

    /// <summary>Analysis thread only: the gap returned by <see cref="TryPeek"/> has been handled.</summary>
    internal void Consume() => _readIndex++;
}
