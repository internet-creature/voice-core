using VoiceCore.Streaming;

namespace VoiceCore.Tests;

public class SpscOverwriteRingTests
{
    [Fact]
    public void RoundTripsAcrossTheWrap()
    {
        var ring = new SpscOverwriteRing<long>(8);
        var dest = new long[5];
        long next = 0;
        for (int round = 0; round < 10; round++)
        {
            ring.Write([next, next + 1, next + 2, next + 3, next + 4]);
            Assert.True(ring.TryCopy(next, dest));
            Assert.Equal([next, next + 1, next + 2, next + 3, next + 4], dest);
            next += 5;
        }
        Assert.Equal(next, ring.PublishedIndex);
    }

    [Fact]
    public void OverwrittenDataIsRejected()
    {
        var ring = new SpscOverwriteRing<long>(8);
        ring.Write(Enumerable.Range(0, 20).Select(i => (long)i).ToArray());

        Assert.Equal(20, ring.PublishedIndex);
        Assert.False(ring.TryCopy(0, new long[8]));
        Assert.False(ring.TryCopy(11, new long[8]));

        var dest = new long[8];
        Assert.True(ring.TryCopy(12, dest));
        Assert.Equal(Enumerable.Range(12, 8).Select(i => (long)i), dest);
    }

    [Fact]
    public void RejectsNonPowerOfTwoCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpscOverwriteRing<float>(1000));

    [Fact]
    public void ConcurrentReadsNeverReturnTornOrOverwrittenData()
    {
        // each slot holds its own absolute index, so any accepted copy is checkable
        var ring = new SpscOverwriteRing<long>(64);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var producer = new Thread(() =>
        {
            var chunk = new long[7];
            long next = 0;
            while (!stop.IsCancellationRequested)
            {
                for (int i = 0; i < chunk.Length; i++)
                    chunk[i] = next + i;
                ring.Write(chunk);
                next += chunk.Length;
            }
        });
        producer.Start();

        var rng = new Random(3);
        var dest = new long[16];
        long accepted = 0;
        while (!stop.IsCancellationRequested)
        {
            long published = ring.PublishedIndex;
            if (published < dest.Length)
                continue;
            long from = published - dest.Length - rng.Next(0, 64);
            if (from < 0 || !ring.TryCopy(from, dest))
                continue;
            for (int i = 0; i < dest.Length; i++)
                Assert.Equal(from + i, dest[i]);
            accepted++;
        }
        producer.Join();
        Assert.True(accepted > 0);
    }
}

public class FrameQueueTests
{
    private static AnalysisFrame Frame(long center) => new() { WindowCenterSample = center };

    [Fact]
    public void DeliversEveryFrameInOrder()
    {
        var queue = new FrameQueue(new AnalyzerDiagnostics());
        for (int i = 0; i < 100; i++)
            queue.Enqueue([Frame(i)]);

        var dest = new AnalysisFrame[256];
        int n = queue.Drain(dest, out long dropped);

        Assert.Equal(100, n);
        Assert.Equal(0, dropped);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (long)i), dest.Take(n).Select(f => f.WindowCenterSample));
        Assert.Equal(0, queue.Drain(dest, out _));
    }

    [Fact]
    public void OverflowDropsOldestAndCounts()
    {
        var diagnostics = new AnalyzerDiagnostics();
        var queue = new FrameQueue(diagnostics);
        for (int i = 0; i < 300; i++)
            queue.Enqueue([Frame(i)]);

        var dest = new AnalysisFrame[512];
        int n = queue.Drain(dest, out long dropped);

        Assert.Equal(44, dropped);
        Assert.Equal(256, n);
        Assert.Equal(44, dest[0].WindowCenterSample);
        Assert.Equal(299, dest[n - 1].WindowCenterSample);
        Assert.Equal(44, diagnostics.FrameQueueOverruns);
    }

    [Fact]
    public void PartialDrainsResumeWhereTheyLeftOff()
    {
        var queue = new FrameQueue(new AnalyzerDiagnostics());
        for (int i = 0; i < 10; i++)
            queue.Enqueue([Frame(i)]);

        var dest = new AnalysisFrame[4];
        Assert.Equal(4, queue.Drain(dest, out _));
        Assert.Equal(4, queue.Drain(dest, out _));
        Assert.Equal(4, dest[0].WindowCenterSample);
        Assert.Equal(2, queue.Drain(dest, out _));
    }
}

public class TripleBufferTests
{
    [Fact]
    public void ReturnsLatestValue()
    {
        var buffer = new TripleBuffer<int>();
        Assert.False(buffer.TryGetLatest(out _));

        buffer.Publish(1);
        buffer.Publish(2);
        buffer.Publish(3);
        Assert.True(buffer.TryGetLatest(out int v));
        Assert.Equal(3, v);
        Assert.True(buffer.TryGetLatest(out v));  // still there with nothing new
        Assert.Equal(3, v);
    }

    private readonly record struct Pair(long A, long B);

    [Fact]
    public void ConcurrentReadsAreNeverTornOrStale()
    {
        var buffer = new TripleBuffer<Pair>();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var writer = new Thread(() =>
        {
            for (long i = 1; !stop.IsCancellationRequested; i++)
                buffer.Publish(new Pair(i, -i));
        });
        writer.Start();

        long last = 0;
        while (!stop.IsCancellationRequested)
        {
            if (!buffer.TryGetLatest(out var p))
                continue;
            Assert.Equal(-p.A, p.B);
            Assert.True(p.A >= last);
            last = p.A;
        }
        writer.Join();
        Assert.True(last > 0);
    }
}

public class LiveAnalysisPumpTests
{
    private sealed class Rig
    {
        public readonly SpscOverwriteRing<float> Audio = new(1 << 17);
        public readonly VoiceAnalyzer Analyzer = new(AnalysisConfig.Default);
        public readonly FrameQueue Frames;
        public readonly TripleBuffer<AnalysisFrame> Latest = new();
        public readonly LiveAnalysisPump Pump;

        public Rig()
        {
            Frames = new FrameQueue(Analyzer.Diagnostics);
            Pump = new LiveAnalysisPump(Audio, Analyzer, Frames, Latest);
        }

        public List<AnalysisFrame> DrainAll()
        {
            var dest = new AnalysisFrame[Frames.Capacity];
            int n = Frames.Drain(dest, out _);
            return dest.Take(n).ToList();
        }
    }

    [Fact]
    public void MatchesDirectProcessing()
    {
        var rig = new Rig();
        var signal = TestSignals.Busy(20_000);
        var frames = new List<AnalysisFrame>();
        // capture-callback-sized writes, pumped before the backlog passes 100 ms
        for (int pos = 0; pos < signal.Length; pos += 256)
        {
            rig.Audio.Write(signal.AsSpan(pos, Math.Min(256, signal.Length - pos)));
            if (pos % 2048 == 0)
            {
                rig.Pump.PumpOnce();
                frames.AddRange(rig.DrainAll());
            }
        }
        rig.Pump.PumpOnce();
        frames.AddRange(rig.DrainAll());

        TestSignals.AssertBitIdentical(TestSignals.Analyze(signal, () => signal.Length), frames);
        Assert.True(rig.Latest.TryGetLatest(out var latest));
        Assert.Equal(frames[^1], latest);
        Assert.Equal(0, rig.Analyzer.Diagnostics.OverrunCount);
    }

    [Fact]
    public void BacklogOver100msIsDroppedAndAnalyzerReset()
    {
        var rig = new Rig();
        rig.Audio.Write(TestSignals.Busy(10_000));

        Assert.Equal(1, rig.Pump.PumpOnce());

        // keeps one window (2048) of the newest audio, drops the rest
        const long resume = 10_000 - VoiceAnalyzer.WindowSamples;
        Assert.Equal(1, rig.Analyzer.Diagnostics.OverrunCount);
        Assert.Equal(resume, rig.Analyzer.Diagnostics.DroppedSamples);
        var frame = Assert.Single(rig.DrainAll());
        Assert.Equal(resume + VoiceAnalyzer.WindowSamples / 2, frame.WindowCenterSample);
    }

    [Fact]
    public void IgnoresAudioWrittenBeforeItStarted()
    {
        var audio = new SpscOverwriteRing<float>(1 << 17);
        audio.Write(new float[3000]);
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
        var pump = new LiveAnalysisPump(audio, analyzer, new FrameQueue(analyzer.Diagnostics), new TripleBuffer<AnalysisFrame>());

        Assert.Equal(0, pump.PumpOnce());
        audio.Write(new float[VoiceAnalyzer.WindowSamples]);
        Assert.Equal(1, pump.PumpOnce());
        Assert.Equal(0, analyzer.Diagnostics.OverrunCount);
    }

    [Fact]
    public void SteadyStatePumpDoesNotAllocate()
    {
        var rig = new Rig();
        var chunk = TestSignals.Busy(256);
        var dest = new AnalysisFrame[rig.Frames.Capacity];
        for (int i = 0; i < 100; i++)
        {
            rig.Audio.Write(chunk);
            rig.Pump.PumpOnce();
            rig.Frames.Drain(dest, out _);
            rig.Latest.TryGetLatest(out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            rig.Audio.Write(chunk);
            rig.Pump.PumpOnce();
            rig.Frames.Drain(dest, out _);
            rig.Latest.TryGetLatest(out _);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void RejectsQueueReportingToOtherDiagnostics()
    {
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
        Assert.Throws<ArgumentException>(() => new LiveAnalysisPump(
            new SpscOverwriteRing<float>(1 << 17), analyzer,
            new FrameQueue(new AnalyzerDiagnostics()), new TripleBuffer<AnalysisFrame>()));
    }

    [Fact]
    public void StalledConsumerOverrunsReachAnalyzerDiagnostics()
    {
        var rig = new Rig();
        var chunk = TestSignals.Busy(VoiceAnalyzer.HopSamples);
        int produced = 0;
        while (produced < rig.Frames.Capacity + 44)
        {
            rig.Audio.Write(chunk);
            produced += rig.Pump.PumpOnce();
        }

        rig.DrainAll();
        Assert.Equal(produced - rig.Frames.Capacity, rig.Analyzer.Diagnostics.FrameQueueOverruns);
    }

    [Fact]
    public void PumpOnceStopsAtTheAudioPresentOnEntry()
    {
        // regression: PumpOnce used to chase the write index, so when capture
        // outran analysis it never returned and Stop() blocked in Join()
        var rig = new Rig();
        var hop = TestSignals.Busy(VoiceAnalyzer.HopSamples);
        rig.Audio.Write(TestSignals.Busy(VoiceAnalyzer.WindowSamples));

        int chunks = 0;
        rig.Pump.AfterChunk = () =>
        {
            if (++chunks < 1000)
                rig.Audio.Write(hop);  // capture keeps arriving while each chunk is analyzed
        };
        rig.Pump.PumpOnce();

        Assert.Equal(1, chunks);
    }

    [Fact]
    public void StopReturnsWhileCaptureContinues()
    {
        var rig = new Rig();
        var chunk = TestSignals.Busy(256);
        using var captureDone = new CancellationTokenSource();
        var capture = new Thread(() =>
        {
            while (!captureDone.IsCancellationRequested)
                rig.Audio.Write(chunk);
        });

        rig.Pump.Start();
        capture.Start();
        Thread.Sleep(100);
        var stop = new Thread(rig.Pump.Stop);
        stop.Start();
        bool stoppedDuringCapture = stop.Join(TimeSpan.FromSeconds(2));
        bool captureWasRunning = capture.IsAlive;

        captureDone.Cancel();
        capture.Join();
        stop.Join();  // lets a hung Stop() finish so the thread doesn't leak

        Assert.True(captureWasRunning);
        Assert.True(stoppedDuringCapture, "Stop() hung while capture was running");
    }

    [Fact]
    public void DedicatedThreadDeliversFrames()
    {
        var rig = new Rig();
        rig.Pump.Start();
        try
        {
            var signal = TestSignals.Busy(4800);
            for (int pos = 0; pos < signal.Length; pos += 480)
                rig.Audio.Write(signal.AsSpan(pos, 480));

            // frame ends at 2048, 2528, …, 4448
            const int expected = 6;
            var frames = new List<AnalysisFrame>();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (frames.Count < expected && DateTime.UtcNow < deadline)
            {
                frames.AddRange(rig.DrainAll());
                Thread.Sleep(1);
            }
            Assert.Equal(expected, frames.Count);
        }
        finally
        {
            rig.Pump.Stop();
        }
    }
}
