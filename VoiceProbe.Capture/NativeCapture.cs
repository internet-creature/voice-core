using System.Runtime.InteropServices;
using PortAudioSharp;

namespace VoiceProbe.Capture;

/// <summary>What happened to the request for unprocessed (raw) capture (spec §3).</summary>
public enum RawCaptureStatus
{
    /// <summary>Not a WASAPI device: no raw option exists on this path.</summary>
    Unavailable,

    /// <summary>
    /// Requested and the stream opened. Windows applies raw mode only if the device
    /// supports it, and PortAudio doesn't report the outcome, so this is not proof.
    /// </summary>
    Requested,

    /// <summary>The stream would not open with the raw request; opened without it.</summary>
    RejectedFellBack,

    NotRequested,
}

/// <summary>
/// In-process PortAudio capture (the "native" path in the phase 1a capture
/// comparison). The callback converts to 48 kHz mono and writes to the ring:
/// no allocation, no locking, no logging (spec §2).
/// </summary>
public sealed unsafe class NativeCapture : IDisposable
{
    /// <summary>Capture buffer size from the latency budget (spec §5).</summary>
    public const uint FramesPerBuffer = 256;

    private readonly PortAudioSharp.Stream _stream;
    private readonly CaptureConverter _converter;
    private readonly AnalysisPipeline _sink;
    private readonly PortAudioSharp.Stream.Callback _callback;  // held so the GC can't collect it
    private readonly int _channels;
    private long _callbacks;
    private long _inputOverflows;
    private long _driverLatencyMicrosSum;
    private long _driverLatencyReports;

    private NativeCapture(AudioDevice device, CaptureFormat format, AnalysisPipeline sink, bool requestRaw)
    {
        Device = device;
        Format = format;
        _channels = format.Channels;
        _sink = sink;
        _converter = new CaptureConverter(format, sink.Write);
        _callback = OnInput;

        if (!device.IsWasapi)
        {
            Raw = requestRaw ? RawCaptureStatus.Unavailable : RawCaptureStatus.NotRequested;
            _stream = Open(IntPtr.Zero);
            return;
        }
        if (!requestRaw)
        {
            Raw = RawCaptureStatus.NotRequested;
            _stream = Open(IntPtr.Zero);
            return;
        }

        var rawInfo = PortAudioInterop.AllocWasapiRawStreamInfo();
        try
        {
            _stream = Open(rawInfo);
            Raw = RawCaptureStatus.Requested;
        }
        catch (PortAudioException)
        {
            _stream = Open(IntPtr.Zero);
            Raw = RawCaptureStatus.RejectedFellBack;
        }
        finally
        {
            Marshal.FreeHGlobal(rawInfo);  // PortAudio copied it at open
        }
    }

    public AudioDevice Device { get; }
    public CaptureFormat Format { get; }
    public RawCaptureStatus Raw { get; }
    public double ResamplerDelaySamples => _converter.ResamplerDelaySamples;

    public long Callbacks => Interlocked.Read(ref _callbacks);

    /// <summary>
    /// Callbacks where PortAudio reported the driver dropped input. Each one is
    /// marked as a capture gap, so the analyzer resets instead of analyzing across it.
    /// </summary>
    public long InputOverflows => Interlocked.Read(ref _inputOverflows);

    /// <summary>
    /// Mean of PortAudio's per-callback estimate of how long ago the buffer's first
    /// sample hit the ADC; null when the host API doesn't report it (WASAPI
    /// doesn't). Driver-reported; the camera test is the real measurement.
    /// </summary>
    public TimeSpan? DriverReportedInputLatency
    {
        get
        {
            long n = Interlocked.Read(ref _driverLatencyReports);
            return n == 0 ? null : TimeSpan.FromMicroseconds((double)Interlocked.Read(ref _driverLatencyMicrosSum) / n);
        }
    }

    /// <summary>Opens <paramref name="device"/> under the §3 format policy; call <see cref="Start"/> to begin.</summary>
    public static NativeCapture Open(AudioDevice device, AnalysisPipeline sink, bool requestRaw = true)
    {
        DeviceCatalog.EnsureInitialized();
        return new NativeCapture(device, DeviceCatalog.ChooseFormat(device), sink, requestRaw);
    }

    public void Start() => _stream.Start();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_stream.IsStopped)
            _stream.Stop();
        _stream.Dispose();
    }

    private bool _disposed;

    private PortAudioSharp.Stream Open(IntPtr hostApiSpecificStreamInfo)
    {
        var input = new StreamParameters
        {
            device = Device.Index,
            channelCount = Format.Channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = Device.DefaultLowInputLatency,
            hostApiSpecificStreamInfo = hostApiSpecificStreamInfo,
        };
        return new PortAudioSharp.Stream(input, null, Format.SampleRate, FramesPerBuffer, StreamFlags.ClipOff, _callback, null);
    }

    private StreamCallbackResult OnInput(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        Interlocked.Increment(ref _callbacks);
        if ((statusFlags & StreamCallbackFlags.InputOverflow) != 0)
        {
            // input was discarded before this buffer: don't join the two sides
            Interlocked.Increment(ref _inputOverflows);
            _converter.Reset();
            _sink.MarkGap();
        }
        if (timeInfo.inputBufferAdcTime > 0 && timeInfo.currentTime >= timeInfo.inputBufferAdcTime)
        {
            Interlocked.Add(ref _driverLatencyMicrosSum, (long)((timeInfo.currentTime - timeInfo.inputBufferAdcTime) * 1e6));
            Interlocked.Increment(ref _driverLatencyReports);
        }

        if (input != IntPtr.Zero)
            _converter.WriteInterleaved(new ReadOnlySpan<float>((void*)input, checked((int)frameCount * _channels)));
        return StreamCallbackResult.Continue;
    }
}
