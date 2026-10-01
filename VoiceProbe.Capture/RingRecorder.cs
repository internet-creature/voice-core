using System.Runtime.InteropServices;
using System.Text;
using VoiceCore.Streaming;

namespace VoiceProbe.Capture;

/// <summary>
/// Mono 32-bit float WAV written incrementally; the header's sizes are patched
/// when it's disposed, so the file is valid once recording stops.
/// </summary>
public sealed class StreamingWavWriter : IDisposable
{
    private const int HeaderBytes = 44;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private long _samples;

    public StreamingWavWriter(string path, int sampleRate = VoiceCore.VoiceAnalyzer.SampleRate)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream, Encoding.ASCII);
        _writer.Write("RIFF"u8);
        _writer.Write(0);                        // patched on dispose
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16);
        _writer.Write((short)3);                 // IEEE float
        _writer.Write((short)1);                 // mono
        _writer.Write(sampleRate);
        _writer.Write(sampleRate * sizeof(float));
        _writer.Write((short)sizeof(float));
        _writer.Write((short)32);
        _writer.Write("data"u8);
        _writer.Write(0);                        // patched on dispose
    }

    public string Path { get; }
    public long Samples => _samples;

    public void Write(ReadOnlySpan<float> samples)
    {
        _writer.Write(MemoryMarshal.AsBytes(samples));  // WAV is little-endian, as are the targets
        _samples += samples.Length;
    }

    public void Dispose()
    {
        long dataBytes = _samples * sizeof(float);
        _writer.Flush();
        _stream.Position = 4;
        _writer.Write((int)Math.Min(int.MaxValue, 36 + dataBytes));
        _stream.Position = 40;
        _writer.Write((int)Math.Min(int.MaxValue, dataBytes));
        _writer.Dispose();
    }
}

/// <summary>
/// Opt-in session recording (spec §0: retention is opt-in per session, visibly
/// indicated, and deletable). Reads the audio ring as a second, read-only reader on
/// its own thread and streams it to disk, so file I/O never touches the capture
/// callback or the analysis thread. What's recorded is exactly what the analyzer
/// sees: 48 kHz mono after the format policy.
/// </summary>
public sealed class RingRecorder : IDisposable
{
    private readonly SpscOverwriteRing<float> _ring;
    private readonly StreamingWavWriter _writer;
    private readonly float[] _chunk = new float[16384];
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _position;
    private long _droppedSamples;

    /// <summary>Starts recording from the ring's current position into a new file at <paramref name="path"/>.</summary>
    public RingRecorder(SpscOverwriteRing<float> ring, string path)
    {
        _ring = ring;
        _position = ring.PublishedIndex;
        _writer = new StreamingWavWriter(path);
        _thread = new Thread(Run) { IsBackground = true, Name = "VoiceProbe recorder" };
        _thread.Start();
    }

    public string Path => _writer.Path;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_writer.Samples / VoiceCore.VoiceAnalyzer.SampleRate);

    /// <summary>Audio lost because the recorder fell a whole ring behind (a stalled disk). Should stay 0.</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
        Drain();  // whatever arrived before stop
        _writer.Dispose();
    }

    private void Run()
    {
        while (!_stop)
        {
            Drain();
            Thread.Sleep(20);
        }
    }

    private void Drain()
    {
        while (true)
        {
            long published = _ring.PublishedIndex;
            long oldest = _ring.ClaimedIndex - _ring.Capacity;
            if (_position < oldest)
            {
                Interlocked.Add(ref _droppedSamples, oldest - _position);
                _position = oldest;
            }
            int n = (int)Math.Min(published - _position, _chunk.Length);
            if (n <= 0)
                return;
            var span = _chunk.AsSpan(0, n);
            if (!_ring.TryCopy(_position, span))
                continue;  // lapped mid-copy: the next pass skips ahead
            _writer.Write(span);
            _position += n;
        }
    }
}
