using VoiceCore.Batch;
using VoiceCore.Streaming;
using VoiceCore.Synthetic;
using VoiceProbe.Capture;

namespace VoiceCore.Tests.Probe;

public class NoiseFloorCalibrationTests
{
    private static AnalysisFrame Rms(float dbfs) => new() { RmsDbfs = dbfs };

    [Fact]
    public void TakesThe10thPercentileSoTransientsDontRaiseIt()
    {
        var c = new NoiseFloorCalibration();
        Assert.Equal(200, c.FramesNeeded);  // 2 s of 10 ms frames
        for (int i = 0; i < 200; i++)
            c.Add(Rms(i < 150 ? -65 + i % 3 : -20));  // quiet room, then a door slam
        Assert.True(c.IsComplete);
        Assert.Equal(-65, c.Result(), 0.01);
    }

    [Fact]
    public void IgnoresNonFiniteFramesAndExtraFrames()
    {
        var c = new NoiseFloorCalibration(seconds: 0.05);
        c.Add(Rms(float.NaN));
        for (int i = 0; i < 20; i++)
            c.Add(Rms(-60));
        Assert.Equal(5, c.FramesCollected);
        Assert.Equal(-60, c.Result());
    }

    [Fact]
    public void NoFramesIsAnError() =>
        Assert.Throws<InvalidOperationException>(() => new NoiseFloorCalibration().Result());
}

public class PumpNoiseFloorTests
{
    [Fact]
    public void FloorIsHandedToTheAnalysisThreadAtTheNextPump()
    {
        var audio = new SpscOverwriteRing<float>(1 << 17);
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
        var pump = new LiveAnalysisPump(audio, analyzer, new FrameQueue(analyzer.Diagnostics), new TripleBuffer<AnalysisFrame>());

        pump.SetCalibratedNoiseFloor(-55);
        Assert.Equal(-70, analyzer.CalibratedNoiseFloorDbfs);  // not yet: the analyzer belongs to the pump
        pump.PumpOnce();
        Assert.Equal(-55, analyzer.CalibratedNoiseFloorDbfs);
    }

    [Fact]
    public void RejectsNaNAndOutOfRange()
    {
        var audio = new SpscOverwriteRing<float>(1 << 17);
        var analyzer = new VoiceAnalyzer(AnalysisConfig.Default);
        var pump = new LiveAnalysisPump(audio, analyzer, new FrameQueue(analyzer.Diagnostics), new TripleBuffer<AnalysisFrame>());
        Assert.Throws<ArgumentOutOfRangeException>(() => pump.SetCalibratedNoiseFloor(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => pump.SetCalibratedNoiseFloor(6));
    }
}

public class RecordingTests
{
    private static string TempWav() => Path.Combine(Path.GetTempPath(), $"voicecore-{Guid.NewGuid():N}.wav");

    [Fact]
    public void RecorderCapturesExactlyWhatTheRingCarried()
    {
        var ring = new SpscOverwriteRing<float>(1 << 17);
        ring.Write(new float[1000]);  // before recording starts: not recorded
        string path = TempWav();
        try
        {
            var signal = TestSignals.Busy(30_000);
            using (var recorder = new RingRecorder(ring, path))
            {
                for (int pos = 0; pos < signal.Length; pos += 256)
                {
                    ring.Write(signal.AsSpan(pos, Math.Min(256, signal.Length - pos)));
                    if (pos % 4096 == 0)
                        Thread.Sleep(1);
                }
            }
            var wav = WavReader.Read(path);
            Assert.Equal(48000, wav.SampleRate);
            Assert.True(wav.IsFloat);
            Assert.Equal(signal, wav.Samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReaderHandlesPcm16Stereo()
    {
        // left = +0.5, right = −0.25, as 16-bit PCM
        string path = TempWav();
        try
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                short left = 16384, right = -8192;
                int frames = 100, dataBytes = frames * 4;
                w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(48000); w.Write(48000 * 4); w.Write((short)4); w.Write((short)16);
                w.Write("data"u8); w.Write(dataBytes);
                for (int i = 0; i < frames; i++) { w.Write(left); w.Write(right); }
            }
            var wav = WavReader.Read(path);
            Assert.Equal(2, wav.Channels);
            Assert.Equal(100, wav.Samples.Length);
            Assert.All(wav.Samples, s => Assert.Equal(0.5f, s));  // channel 0 only (§3 downmix policy)
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReaderRoundTripsTheSynthWriter()
    {
        string path = TempWav();
        try
        {
            var samples = Suites.Tone(220, HarmonicProfile.Voice).Samples;
            WavWriter.WriteFloat32(path, samples);
            Assert.Equal(samples, WavReader.Read(path).Samples);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class NoteNameTests
{
    [Theory]
    [InlineData(440.0, "A4 +0¢")]
    [InlineData(261.6256, "C4 +0¢")]
    [InlineData(150.0, "D3 +37¢")]
    [InlineData(65.4064, "C2 +0¢")]
    [InlineData(445.0, "A4 +20¢")]
    [InlineData(430.0, "A4 −40¢")]
    public void NamesTheNearestNoteAndCents(double hz, string expected) => Assert.Equal(expected, NoteNames.Of(hz));
}
