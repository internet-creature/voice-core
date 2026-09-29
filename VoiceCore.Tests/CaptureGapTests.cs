using VoiceCore.Streaming;
using VoiceProbe.Capture;

namespace VoiceCore.Tests;

/// <summary>
/// Regression (Sol review of step 3, P1): when the driver drops input, the next
/// samples used to be analyzed as if continuous, so frames straddled the hole.
/// </summary>
public class CaptureGapTests
{
    private sealed class Rig
    {
        public readonly SpscOverwriteRing<float> Audio = new(1 << 17);
        public readonly CaptureGapLog Gaps;
        public readonly VoiceAnalyzer Analyzer = new(AnalysisConfig.Default);
        public readonly FrameQueue Frames;
        public readonly LiveAnalysisPump Pump;

        public Rig(int gapCapacity = CaptureGapLog.DefaultCapacity)
        {
            Gaps = new CaptureGapLog(gapCapacity);
            Frames = new FrameQueue(Analyzer.Diagnostics, capacity: 1024);
            Pump = new LiveAnalysisPump(Audio, Analyzer, Frames, new TripleBuffer<AnalysisFrame>(), gaps: Gaps);
        }

        /// <summary>Writes in capture-sized chunks, pumping every <paramref name="pumpEvery"/> writes.</summary>
        public void Capture(float[] samples, int chunk, int pumpEvery)
        {
            int writes = 0;
            for (int pos = 0; pos < samples.Length; pos += chunk)
            {
                Audio.Write(samples.AsSpan(pos, Math.Min(chunk, samples.Length - pos)));
                if (++writes % pumpEvery == 0)
                    Pump.PumpOnce();
            }
        }

        public List<AnalysisFrame> Drain()
        {
            Pump.PumpOnce();
            var dest = new AnalysisFrame[Frames.Capacity];
            int n = Frames.Drain(dest, out _);
            return dest.Take(n).ToList();
        }
    }

    [Theory]
    [InlineData(256, 1)]   // pump after every write: the gap falls on a chunk boundary
    [InlineData(256, 7)]   // pump rarely: the gap falls mid-chunk
    [InlineData(1000, 2)]  // (backlog between pumps stays under the 100 ms drop threshold)
    public void AnalyzerResetsExactlyAtTheGap(int chunk, int pumpEvery)
    {
        var before = TestSignals.Busy(5000, seed: 1);
        var after = TestSignals.Busy(12_000, seed: 2);

        var rig = new Rig();
        rig.Capture(before, chunk, pumpEvery);
        long gapAt = rig.Audio.PublishedIndex;
        rig.Gaps.Record(gapAt);
        rig.Capture(after, chunk, pumpEvery);
        var frames = rig.Drain();

        // no window spans the gap
        Assert.All(frames, f => Assert.True(
            f.ResultAvailableSample <= gapAt || f.WindowCenterSample - VoiceAnalyzer.WindowSamples / 2 >= gapAt,
            $"frame centered at {f.WindowCenterSample} spans the gap at {gapAt}"));

        // after the gap, frames are exactly what a fresh analyzer makes from the new audio
        var afterGap = frames.Where(f => f.ResultAvailableSample > gapAt).ToList();
        TestSignals.AssertBitIdentical(TestSignals.Analyze(after, () => 700, startIndex: gapAt), afterGap);
        Assert.Equal(1, rig.Analyzer.Diagnostics.CaptureGaps);
    }

    [Fact]
    public void GapsAlreadyPassedByAnOverrunAreIgnored()
    {
        var rig = new Rig();
        rig.Audio.Write(new float[2000]);
        rig.Gaps.Record(2000);
        rig.Audio.Write(new float[10_000]);  // backlog > 100 ms: the pump drops past the gap
        rig.Pump.PumpOnce();

        Assert.Equal(1, rig.Analyzer.Diagnostics.OverrunCount);
        Assert.Equal(0, rig.Analyzer.Diagnostics.CaptureGaps);
    }

    [Fact]
    public void LostGapRecordsStillForceAReset()
    {
        var rig = new Rig(gapCapacity: 2);
        for (int i = 0; i < 5; i++)
        {
            rig.Gaps.Record(rig.Audio.PublishedIndex);
            rig.Audio.Write(new float[100]);
        }
        rig.Pump.PumpOnce();
        Assert.True(rig.Analyzer.Diagnostics.CaptureGaps >= 1);
    }

    [Fact]
    public void PipelineMarkGapReachesTheAnalyzer()
    {
        using var pipeline = new AnalysisPipeline(AnalysisConfig.Default);
        pipeline.Write(TestSignals.Busy(4000, seed: 1));
        pipeline.Pump.PumpOnce();
        long gapAt = pipeline.Audio.PublishedIndex;
        pipeline.MarkGap();
        pipeline.Write(TestSignals.Busy(4000, seed: 2));
        pipeline.Pump.PumpOnce();

        Assert.Equal(1, pipeline.Diagnostics.CaptureGaps);
        var dest = new AnalysisFrame[64];
        int n = pipeline.Frames.Drain(dest, out _);
        Assert.DoesNotContain(dest.Take(n), f => f.ResultAvailableSample > gapAt && f.WindowCenterSample - 1024 < gapAt);
    }

    [Fact]
    public void ConverterResetForgetsResamplerHistory()
    {
        var input = Enumerable.Range(0, 3000).Select(i => (float)Math.Sin(i * 0.05)).ToArray();

        var fresh = new List<float>();
        new CaptureConverter(new CaptureFormat(44100, 1), s => fresh.AddRange(s.ToArray())).WriteMono(input);

        var reused = new List<float>();
        var converter = new CaptureConverter(new CaptureFormat(44100, 1), s => reused.AddRange(s.ToArray()));
        converter.WriteMono(Enumerable.Repeat(0.7f, 5000).ToArray());
        converter.Reset();
        reused.Clear();
        converter.WriteMono(input);

        Assert.Equal(fresh, reused);
    }

    [Fact]
    public void SteadyStateWithGapLogDoesNotAllocate()
    {
        var rig = new Rig();
        var chunk = new float[256];
        Allocation.AssertSteadyStateFree(() =>
        {
            rig.Audio.Write(chunk);
            rig.Pump.PumpOnce();
        });
    }
}

/// <summary>Regression (Sol review of step 3, P2): arrivals published after the audio could be missed.</summary>
public class ArrivalOrderingTests
{
    [Fact]
    public void AMissingArrivalIsCountedNotSilentlyDropped()
    {
        var audio = new SpscOverwriteRing<float>(1 << 17);
        var arrivals = new ArrivalLog();
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
        var pump = new LiveAnalysisPump(audio, analyzer, new FrameQueue(analyzer.Diagnostics), new TripleBuffer<AnalysisFrame>(), arrivals);

        audio.Write(new float[VoiceAnalyzer.WindowSamples]);  // audio visible, arrival not yet logged
        Assert.Equal(1, pump.PumpOnce());

        Assert.Equal(1, analyzer.Diagnostics.CaptureToResultMissed);
        Assert.Equal(0, analyzer.Diagnostics.CaptureToResultCount);
    }

    [Fact]
    public void PipelineLogsArrivalBeforeAudioIsVisible()
    {
        // capture and analysis race on separate threads; with arrivals logged
        // first, every frame the pump sees has its arrival already
        using var pipeline = new AnalysisPipeline(AnalysisConfig.Default);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var capture = new Thread(() =>
        {
            var chunk = new float[48];
            while (!stop.IsCancellationRequested)
                pipeline.Write(chunk);
        });
        capture.Start();
        while (!stop.IsCancellationRequested)
            pipeline.Pump.PumpOnce();
        capture.Join();

        var d = pipeline.Diagnostics;
        Assert.True(d.FramesProduced > 100);
        Assert.Equal(0, d.CaptureToResultMissed);
        Assert.Equal(d.FramesProduced, d.CaptureToResultCount);
    }
}

/// <summary>Regression (Sol review of step 3, P2): loopback on the Godot path used the wrong mic.</summary>
public class LoopbackDeviceMatchTests
{
    private static AudioDevice Dev(int index, string name, bool wasapi) =>
        new(index, name, wasapi ? "Windows WASAPI" : "MME", wasapi, 1, 0, 48000, 0.01);

    private static readonly AudioDevice[] Inputs =
    [
        Dev(47, "Microphone (AT2020USB-X)", wasapi: true),
        Dev(45, "Microphone (Insta360 Link 2C Pro)", wasapi: true),
        Dev(1, "Microphone (AT2020USB-X)", wasapi: false),
        Dev(4, "Microphone (Insta360 Link 2C Pr", wasapi: false),  // MME truncates to 31 chars
    ];

    [Fact]
    public void PrefersTheWasapiEntryWithTheSameName() =>
        Assert.Equal(47, DeviceCatalog.MatchInput(Inputs, "Microphone (AT2020USB-X)", () => null)!.Index);

    [Fact]
    public void DefaultMeansTheSystemDefault() =>
        Assert.Equal(45, DeviceCatalog.MatchInput(Inputs, "Default", () => Inputs[1])!.Index);

    [Fact]
    public void UnknownNamesDoNotFallBackToSomeOtherMic() =>
        Assert.Null(DeviceCatalog.MatchInput(Inputs, "Headset (Some Bluetooth Thing)", () => Inputs[0]));
}
