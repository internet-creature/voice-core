using System.Numerics;

namespace VoiceCore.Streaming;

/// <summary>
/// Lock-free single-producer / single-consumer ring (spec §2) that never blocks
/// or fails the producer: when full, new items overwrite the oldest.
/// </summary>
/// <remarks>
/// Items are addressed by a monotonically increasing absolute index. For audio,
/// that index is the capture clock. The producer announces how far it is about
/// to write (<see cref="ClaimedIndex"/>) before overwriting anything, and
/// publishes (<see cref="PublishedIndex"/>) after. The consumer copies, then
/// checks the claim: if the producer may have overwritten any copied slot during
/// the copy, <see cref="TryCopy"/> returns false and the data must be discarded.
/// Capacity is a power of two so positions use a mask instead of modulo.
/// </remarks>
public sealed class SpscOverwriteRing<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private long _claimed;
    private long _published;

    public SpscOverwriteRing(int capacity)
    {
        if (capacity <= 0 || !BitOperations.IsPow2(capacity))
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be a positive power of two.");
        _buffer = new T[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>Every index below this has been fully written.</summary>
    public long PublishedIndex => Volatile.Read(ref _published);

    /// <summary>The producer may be writing any index below this.</summary>
    public long ClaimedIndex => Volatile.Read(ref _claimed);

    /// <summary>Producer only. No allocation, no locking.</summary>
    public void Write(ReadOnlySpan<T> items)
    {
        long start = _published;  // only the producer writes it
        if (items.Length > Capacity)
        {
            // only the newest Capacity items can survive; the rest still advance the index
            start += items.Length - Capacity;
            items = items[^Capacity..];
        }
        long end = start + items.Length;

        // full fence: the claim must be visible before any slot is overwritten
        Interlocked.Exchange(ref _claimed, end);

        int offset = (int)(start & _mask);
        int firstPart = Math.Min(items.Length, Capacity - offset);
        items[..firstPart].CopyTo(_buffer.AsSpan(offset));
        items[firstPart..].CopyTo(_buffer);

        Volatile.Write(ref _published, end);
    }

    /// <summary>Producer only.</summary>
    public void Write(in T item) => Write(new ReadOnlySpan<T>(in item));

    /// <summary>
    /// Consumer only. Copies <c>destination.Length</c> items starting at absolute
    /// index <paramref name="from"/>, which must end at or before a
    /// <see cref="PublishedIndex"/> the caller already read. Returns false if any
    /// copied slot was, or may have been, overwritten.
    /// </summary>
    public bool TryCopy(long from, Span<T> destination)
    {
        int offset = (int)(from & _mask);
        int firstPart = Math.Min(destination.Length, Capacity - offset);
        _buffer.AsSpan(offset, firstPart).CopyTo(destination);
        _buffer.AsSpan(0, destination.Length - firstPart).CopyTo(destination[firstPart..]);

        // the copy's loads must complete before the claim is read
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _claimed) - from <= Capacity;
    }
}
