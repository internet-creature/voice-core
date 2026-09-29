namespace VoiceCore.Streaming;

/// <summary>
/// Lock-free "latest value" handoff (spec §2) for meters and needles that only
/// care what the value is right now. Drops intermediate values by design; use
/// <see cref="FrameQueue"/> for anything that needs every frame.
/// One writer thread, one reader thread.
/// </summary>
public sealed class TripleBuffer<T> where T : struct
{
    private const int IndexMask = 3;
    private const int FreshBit = 4;

    private readonly T[] _slots = new T[3];
    private int _back;               // writer's slot
    private int _front = 1;          // reader's slot
    private int _middle = 2;         // shared slot index | FreshBit
    private bool _frontHasValue;

    /// <summary>Writer only.</summary>
    public void Publish(in T value)
    {
        _slots[_back] = value;
        // full fence: the slot write is visible before the reader can take it
        _back = Interlocked.Exchange(ref _middle, _back | FreshBit) & IndexMask;
    }

    /// <summary>
    /// Reader only. Gets the most recently published value; false if nothing has
    /// been published yet.
    /// </summary>
    public bool TryGetLatest(out T value)
    {
        if ((Volatile.Read(ref _middle) & FreshBit) != 0)
        {
            _front = Interlocked.Exchange(ref _middle, _front) & IndexMask;
            _frontHasValue = true;
        }
        value = _slots[_front];
        return _frontHasValue;
    }
}
