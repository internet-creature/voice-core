using System.Runtime.InteropServices;
using PortAudioSharp;

namespace VoiceProbe.Capture;

/// <summary>
/// The pieces of the PortAudio C API that PortAudioSharp2 doesn't wrap: host API
/// names, format queries, and the WASAPI stream info that requests raw capture.
/// Checked against PortAudio 19.7.0 (revision 147dd722), the build PortAudioSharp2
/// 1.0.6 ships.
/// </summary>
internal static class PortAudioInterop
{
    private const string Library = "portaudio";
    private const int PaFormatIsSupported = 0;
    private const uint PaFloat32 = 0x1;

    public const int PaWasapiHostApiType = 13;  // PaHostApiTypeId.paWASAPI

    [StructLayout(LayoutKind.Sequential)]
    private struct PaStreamParameters
    {
        public int Device;
        public int ChannelCount;
        public uint SampleFormat;
        public double SuggestedLatency;
        public IntPtr HostApiSpecificStreamInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaHostApiInfo
    {
        public int StructVersion;
        public int Type;
        public IntPtr Name;
        public int DeviceCount;
        public int DefaultInputDevice;
        public int DefaultOutputDevice;
    }

    /// <summary>pa_win_wasapi.h PaWasapiStreamInfo, version 1 (56 bytes on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PaWasapiStreamInfo
    {
        public uint Size;
        public int HostApiType;
        public uint Version;
        public uint Flags;
        public uint ChannelMask;
        public IntPtr HostProcessorOutput;
        public IntPtr HostProcessorInput;
        public int ThreadPriority;
        public int StreamCategory;  // eAudioCategoryOther = 0
        public int StreamOption;    // eStreamOptionRaw = 1
    }

    [DllImport(Library, EntryPoint = "Pa_IsFormatSupported")]
    private static extern int IsFormatSupported(ref PaStreamParameters input, IntPtr output, double sampleRate);

    [DllImport(Library, EntryPoint = "Pa_IsFormatSupported")]
    private static extern int IsFormatSupportedOutput(IntPtr input, ref PaStreamParameters output, double sampleRate);

    [DllImport(Library, EntryPoint = "Pa_GetHostApiInfo")]
    private static extern IntPtr GetHostApiInfoPtr(int hostApi);

    public static (string Name, int Type) GetHostApi(int hostApi)
    {
        var ptr = GetHostApiInfoPtr(hostApi);
        if (ptr == IntPtr.Zero)
            return ($"host API {hostApi}", -1);
        var info = Marshal.PtrToStructure<PaHostApiInfo>(ptr);
        return (Marshal.PtrToStringUTF8(info.Name) ?? $"host API {hostApi}", info.Type);
    }

    public static bool SupportsInput(int device, int channels, int sampleRate)
    {
        var p = new PaStreamParameters
        {
            Device = device,
            ChannelCount = channels,
            SampleFormat = PaFloat32,
            SuggestedLatency = PortAudio.GetDeviceInfo(device).defaultLowInputLatency,
        };
        return IsFormatSupported(ref p, IntPtr.Zero, sampleRate) == PaFormatIsSupported;
    }

    public static bool SupportsOutput(int device, int channels, int sampleRate)
    {
        var p = new PaStreamParameters
        {
            Device = device,
            ChannelCount = channels,
            SampleFormat = PaFloat32,
            SuggestedLatency = PortAudio.GetDeviceInfo(device).defaultLowOutputLatency,
        };
        return IsFormatSupportedOutput(IntPtr.Zero, ref p, sampleRate) == PaFormatIsSupported;
    }

    /// <summary>
    /// Unmanaged PaWasapiStreamInfo asking for raw capture (bypass Windows audio
    /// engine effects, Windows 8.1+). Caller frees with <see cref="Marshal.FreeHGlobal"/>
    /// after the stream is opened; PortAudio copies it at open.
    /// </summary>
    /// <remarks>
    /// If Windows rejects raw mode, PortAudio 19.7 only logs it and opens the stream
    /// anyway, so a successful open does not prove the effects were bypassed.
    /// </remarks>
    public static IntPtr AllocWasapiRawStreamInfo()
    {
        var info = new PaWasapiStreamInfo
        {
            Size = (uint)Marshal.SizeOf<PaWasapiStreamInfo>(),
            HostApiType = PaWasapiHostApiType,
            Version = 1,
            StreamOption = 1,
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PaWasapiStreamInfo>());
        Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
        return ptr;
    }
}
