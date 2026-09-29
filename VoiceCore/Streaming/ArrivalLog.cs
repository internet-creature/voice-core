using System.Diagnostics;

namespace VoiceCore.Streaming;

/// <summary>When a capture write landed in the audio ring.</summary>
/// <param name="EndSample">Capture index just past the last sample of the write.</param>
/// <param name="StopwatchTimestamp"><see cref="Stopwatch.GetTimestamp"/> after the write was published.</param>
public readonly record struct Arrival(long EndSample, long StopwatchTimestamp);

/// <summary>
/// Records when each capture write arrived, so the analysis thread can measure
/// capture-to-result latency (spec §3.1): from the arrival of the last sample a
/// frame depends on to the frame being readable. Same threading as the audio
/// ring: the capture callback writes, the analysis thread reads.
/// </summary>
public sealed class ArrivalLog
{
    public const int DefaultCapacity = 1024;  // ≈ 5 s of 256-sample callbacks

    private readonly SpscOverwriteRing<Arrival> _ring;
    private readonly Arrival[] _one = new Arrival[1];
    private long _readIndex;
    private Arrival _current;
    private bool _hasCurrent;

    public ArrivalLog(int capacity = DefaultCapacity) => _ring = new SpscOverwriteRing<Arrival>(capacity);

    /// <summary>
    /// Capture thread only: call right after writing audio to the ring, with the
    /// ring's new <see cref="SpscOverwriteRing{T}.PublishedIndex"/>. No allocation.
    /// </summary>
    public void Record(long endSample) => _ring.Write(new Arrival(endSample, Stopwatch.GetTimestamp()));

    /// <summary>
    /// Analysis thread only. The arrival timestamp of the capture write that
    /// delivered <paramref name="sampleIndex"/>. Queries must not decrease. False
    /// if that write hasn't been logged yet or its record was overwritten.
    /// </summary>
    public bool TryGetArrival(long sampleIndex, out long stopwatchTimestamp)
    {
        while (true)
        {
            if (_hasCurrent && _current.EndSample > sampleIndex)
            {
                stopwatchTimestamp = _current.StopwatchTimestamp;
                return true;
            }

            long oldest = _ring.ClaimedIndex - _ring.Capacity;
            if (_readIndex < oldest)
                _readIndex = oldest;  // lapped: the skipped records are gone
            if (_readIndex >= _ring.PublishedIndex)
            {
                stopwatchTimestamp = 0;
                return false;
            }
            if (!_ring.TryCopy(_readIndex, _one))
                continue;
            _current = _one[0];
            _hasCurrent = true;
            _readIndex++;
        }
    }
}
