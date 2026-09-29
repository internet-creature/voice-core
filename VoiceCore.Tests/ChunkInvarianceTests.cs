namespace VoiceCore.Tests;

/// <summary>
/// Spec §1.1: feeding the same signal in chunks of 1, 480, 4096, or random sizes
/// must produce bit-identical frames.
/// </summary>
public class ChunkInvarianceTests
{
    private static readonly float[] Signal = TestSignals.Busy(VoiceAnalyzer.SampleRate);  // 1 s

    private static List<AnalysisFrame> Reference() => TestSignals.Analyze(Signal, () => Signal.Length);

    [Theory]
    [InlineData(1)]
    [InlineData(480)]
    [InlineData(4096)]
    [InlineData(2047)]
    [InlineData(2049)]
    public void FixedChunkSizes_ProduceIdenticalFrames(int chunkSize)
    {
        var frames = TestSignals.Analyze(Signal, () => chunkSize);
        TestSignals.AssertBitIdentical(Reference(), frames);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomChunkSizes_ProduceIdenticalFrames(int seed)
    {
        var rng = new Random(seed);
        var frames = TestSignals.Analyze(Signal, () => rng.Next(1, 5000));
        TestSignals.AssertBitIdentical(Reference(), frames);
    }

    [Fact]
    public void ReferenceProducesExpectedFrameCount()
    {
        // frame ends at 2048, 2528, …, ≤ 48000
        int expected = (Signal.Length - VoiceAnalyzer.WindowSamples) / VoiceAnalyzer.HopSamples + 1;
        Assert.Equal(expected, Reference().Count);
    }
}
