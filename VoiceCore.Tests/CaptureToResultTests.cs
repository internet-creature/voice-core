using System.Diagnostics;
using VoiceCore.Streaming;

namespace VoiceCore.Tests;

public class ArrivalLogTests
{
    [Fact]
    public void FindsTheWriteThatDeliveredEachSample()
    {
        var log = new ArrivalLog();
        log.Record(256);
        long first = Stopwatch.GetTimestamp();
        Thread.Sleep(2);
        log.Record(512);

        Assert.True(log.TryGetArrival(0, out long a));
        Assert.True(log.TryGetArrival(255, out long b));
        Assert.True(log.TryGetArrival(256, out long c));
        Assert.True(log.TryGetArrival(511, out long d));

        Assert.Equal(a, b);
        Assert.True(a <= first);
        Assert.Equal(c, d);
        Assert.True(c > first);
    }

    [Fact]
    public void SamplesNotYetLoggedAreNotFound()
    {
        var log = new ArrivalLog();
        log.Record(256);
        Assert.False(log.TryGetArrival(256, out _));
        log.Record(512);
        Assert.True(log.TryGetArrival(256, out _));
    }

    [Fact]
    public void LappedRecordsAreSkippedNotMisread()
    {
        var log = new ArrivalLog(capacity: 4);
        for (int i = 1; i <= 10; i++)
            log.Record(i * 100L);

        // records for writes 1–6 were overwritten; sample 50 resolves to the oldest survivor
        Assert.True(log.TryGetArrival(50, out _));
        Assert.True(log.TryGetArrival(999, out _));
        Assert.False(log.TryGetArrival(1000, out _));
    }
}

public class CaptureToResultHistogramTests
{
    private static long Ms(double ms) => (long)(ms / 1000 * Stopwatch.Frequency);

    [Fact]
    public void PercentilesComeFromTheHistogram()
    {
        var d = new AnalyzerDiagnostics();
        Assert.Equal(TimeSpan.Zero, d.CaptureToResultPercentile(95));

        for (int i = 1; i <= 100; i++)
            d.RecordCaptureToResult(Ms(i * 0.1));  // 0.1 … 10.0 ms

        Assert.Equal(100, d.CaptureToResultCount);
        Assert.Equal(5.0, d.CaptureToResultPercentile(50).TotalMilliseconds, 0.02);
        Assert.Equal(9.5, d.CaptureToResultPercentile(95).TotalMilliseconds, 0.02);
        Assert.Equal(10.0, d.CaptureToResultMax.TotalMilliseconds, 0.01);
    }

    [Fact]
    public void LatenciesPast100msLandInOverflow()
    {
        var d = new AnalyzerDiagnostics();
        d.RecordCaptureToResult(Ms(1));
        d.RecordCaptureToResult(Ms(250));
        Assert.Equal(TimeSpan.MaxValue, d.CaptureToResultPercentile(100));
        Assert.Equal(250, d.CaptureToResultMax.TotalMilliseconds, 0.01);
    }

    [Fact]
    public void ResetClears()
    {
        var d = new AnalyzerDiagnostics();
        d.RecordCaptureToResult(Ms(3));
        d.ResetCaptureToResult();
        Assert.Equal(0, d.CaptureToResultCount);
        Assert.Equal(TimeSpan.Zero, d.CaptureToResultMax);
    }
}

public class PumpCaptureToResultTests
{
    private sealed class Rig
    {
        public readonly SpscOverwriteRing<float> Audio = new(1 << 17);
        public readonly ArrivalLog Arrivals = new();
        public readonly VoiceAnalyzer Analyzer = new(AnalysisConfig.Default);
        public readonly LiveAnalysisPump Pump;

        public Rig(bool withArrivals = true) =>
            Pump = new LiveAnalysisPump(Audio, Analyzer, new FrameQueue(Analyzer.Diagnostics), new TripleBuffer<AnalysisFrame>(),
                withArrivals ? Arrivals : null);

        public void Capture(int samples)
        {
            Audio.Write(new float[samples]);
            Arrivals.Record(Audio.PublishedIndex);
        }
    }

    [Fact]
    public void EveryFrameGetsAMeasurement()
    {
        var rig = new Rig();
        int frames = 0;
        for (int i = 0; i < 40; i++)
        {
            rig.Capture(256);
            frames += rig.Pump.PumpOnce();
        }
        Assert.True(frames > 10);
        Assert.Equal(frames, rig.Analyzer.Diagnostics.CaptureToResultCount);
    }

    [Fact]
    public void MeasuresFromArrivalNotFromPump()
    {
        var rig = new Rig();
        rig.Capture(VoiceAnalyzer.WindowSamples);
        Thread.Sleep(20);  // the analysis thread is late
        rig.Pump.PumpOnce();

        Assert.True(rig.Analyzer.Diagnostics.CaptureToResultPercentile(50) >= TimeSpan.FromMilliseconds(19));
    }

    [Fact]
    public void WithoutAnArrivalLogNothingIsMeasured()
    {
        var rig = new Rig(withArrivals: false);
        rig.Capture(VoiceAnalyzer.WindowSamples * 2);
        Assert.True(rig.Pump.PumpOnce() > 0);
        Assert.Equal(0, rig.Analyzer.Diagnostics.CaptureToResultCount);
    }

    [Fact]
    public void SteadyStateStillDoesNotAllocate()
    {
        var rig = new Rig();
        var chunk = new float[256];
        Allocation.AssertSteadyStateFree(() =>
        {
            rig.Audio.Write(chunk);
            rig.Arrivals.Record(rig.Audio.PublishedIndex);
            rig.Pump.PumpOnce();
        });
    }
}
