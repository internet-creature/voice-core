namespace VoiceCore.Tests;

internal static class Allocation
{
    /// <summary>
    /// Asserts <paramref name="iteration"/> doesn't allocate in steady state. Runs
    /// three measured windows after warm-up and requires at least one to be exactly
    /// zero: a real per-call allocation shows up in every window, while a one-off
    /// runtime event (e.g. tiered JIT promotion landing mid-window) can't fail it.
    /// </summary>
    public static void AssertSteadyStateFree(Action iteration, int warmup = 200, int perWindow = 1000)
    {
        for (int i = 0; i < warmup; i++)
            iteration();

        var windows = new long[3];
        for (int w = 0; w < windows.Length; w++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < perWindow; i++)
                iteration();
            windows[w] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.True(windows.Min() == 0, $"allocated bytes per window: {string.Join(", ", windows)}");
    }
}

public class AllocationCheckTests
{
    [Fact]
    public void CatchesAPerCallAllocation() =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Allocation.AssertSteadyStateFree(() => GC.KeepAlive(new byte[16])));
}
