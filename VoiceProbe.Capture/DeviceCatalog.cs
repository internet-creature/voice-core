using PortAudioSharp;

namespace VoiceProbe.Capture;

public sealed record AudioDevice(int Index, string Name, string HostApi, bool IsWasapi, int MaxInputChannels, int MaxOutputChannels, double DefaultSampleRate, double DefaultLowInputLatency)
{
    public string Label => $"{Name} [{HostApi}]";
}

/// <summary>PortAudio device listing. Call <see cref="EnsureInitialized"/> once per process.</summary>
public static class DeviceCatalog
{
    private static readonly Lock Gate = new();
    private static readonly char[] LineBreaks = [(char)13, (char)10];
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            PortAudio.LoadNativeLibrary();
            PortAudio.Initialize();
            _initialized = true;
        }
    }

    public static string PortAudioVersion
    {
        get
        {
            EnsureInitialized();
            return PortAudio.VersionInfo.versionText;
        }
    }

    public static IReadOnlyList<AudioDevice> All()
    {
        EnsureInitialized();
        var devices = new List<AudioDevice>();
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            var (hostName, hostType) = PortAudioInterop.GetHostApi(d.hostApi);
            devices.Add(new AudioDevice(i, CleanName(d.name), hostName, hostType == PortAudioInterop.PaWasapiHostApiType,
                d.maxInputChannels, d.maxOutputChannels, d.defaultSampleRate, d.defaultLowInputLatency));
        }
        return devices;
    }

    /// <summary>
    /// Input devices, WASAPI first: it is the only Windows host API where raw
    /// (unprocessed) capture can be requested, and it has the lowest latency.
    /// </summary>
    public static IReadOnlyList<AudioDevice> Inputs() =>
        All().Where(d => d.MaxInputChannels > 0).OrderBy(d => d.IsWasapi ? 0 : 1).ThenBy(d => d.Index).ToList();

    /// <summary>
    /// The system default input, as its WASAPI entry when one exists. PortAudio's
    /// default is usually the MME entry, whose name Windows truncates to 31
    /// characters, so names are matched by prefix.
    /// </summary>
    public static AudioDevice? DefaultInput()
    {
        EnsureInitialized();
        int index = PortAudio.DefaultInputDevice;
        if (index == PortAudio.NoDevice)
            return null;
        var all = All();
        var fallback = all[index];
        return all.FirstOrDefault(d => d.IsWasapi && d.MaxInputChannels > 0 && d.Name.StartsWith(fallback.Name, StringComparison.Ordinal))
            ?? fallback;
    }

    public static IReadOnlyList<AudioDevice> Outputs() =>
        All().Where(d => d.MaxOutputChannels > 0).OrderBy(d => d.IsWasapi ? 0 : 1).ThenBy(d => d.Index).ToList();

    /// <summary>The format this device would be opened with under the §3 policy.</summary>
    public static CaptureFormat ChooseFormat(AudioDevice device) =>
        FormatPolicy.Choose(device.Label, device.MaxInputChannels,
            (rate, channels) => PortAudioInterop.SupportsInput(device.Index, channels, rate));

    /// <summary>Some driver names span lines (e.g. Bluetooth hands-free entries).</summary>
    internal static string CleanName(string name) =>
        string.Join(' ', name.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
