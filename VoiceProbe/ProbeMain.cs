using System.Globalization;
using Godot;
using VoiceCore;
using VoiceProbe.Capture;

namespace VoiceProbe;

/// <summary>
/// Build step 3 probe: capture → analyzer → RMS meter, on either capture path,
/// with the diagnostics, loopback test and camera-test flash the latency budget
/// needs (spec §3.1, §5, §8). Disposable: keep logic in VoiceCore and
/// VoiceProbe.Capture, not here.
/// </summary>
/// <remarks>
/// Headless self-tests, for checking hardware from a terminal:
/// <c>--selftest=&lt;device name part&gt;[,seconds]</c> captures on the native path
/// and prints diagnostics; <c>--loopback=&lt;output part&gt;,&lt;input part&gt;</c> runs the
/// loopback test. Both go after <c>--</c> on the Godot command line and quit when done.
/// <c>--autostart</c> opens the UI already capturing from the preselected device, and
/// prints diagnostics when capture stops; add <c>--capture=godot</c> for the Godot path.
/// </remarks>
public partial class ProbeMain : Control
{
    private enum CapturePath { Native, Godot }

    // camera test: flash when a frame's peak jumps this far above the recent maximum
    private const float ClapMinPeakDbfs = -30f;
    private const float ClapJumpDb = 20f;
    private const double FlashSeconds = 0.1;

    private readonly AnalysisConfig _config = AnalysisConfig.Default;
    private readonly AnalysisFrame[] _drain = new AnalysisFrame[FrameQueueDrainSize];
    private const int FrameQueueDrainSize = 256;

    private AnalysisPipeline? _pipeline;
    private NativeCapture? _native;
    private GodotCapture _godot = null!;
    private SessionLog? _log;
    private CapturePath _path;

    private OptionButton _pathPicker = null!, _devicePicker = null!, _outputPicker = null!;
    private CheckBox _rawToggle = null!, _cameraToggle = null!;
    private Button _startButton = null!, _loopbackButton = null!;
    private Label _status = null!, _diagnostics = null!, _loopbackResult = null!, _cameraHint = null!;
    private LevelMeter _meter = null!;
    private LevelTrace _trace = null!;
    private ColorRect _flash = null!;

    private readonly Queue<float> _recentPeaks = new();
    private double _flashRemaining;
    private int _flashCount;
    private double _diagnosticsTimer;
    private bool _printDiagnosticsOnStop;
    private Task<LoopbackResult>? _loopbackTask;
    private IReadOnlyList<AudioDevice> _nativeInputs = [], _nativeOutputs = [];

    public override void _Ready()
    {
        _godot = new GodotCapture();
        AddChild(_godot);
        BuildUi();

        var args = ParseUserArgs();
        if (args.TryGetValue("selftest", out var selftest))
            RunSelfTest(selftest);
        else if (args.TryGetValue("loopback", out var loopback))
            RunLoopbackSelfTest(loopback);
        else
        {
            if (args.TryGetValue("capture", out var path) && path == "godot")
                _pathPicker.Select((int)CapturePath.Godot);
            RefreshDevices();
            if (args.ContainsKey("autostart"))
            {
                _printDiagnosticsOnStop = true;
                StartCapture();
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_pipeline is not null)
        {
            DrainFrames();
            if (_pipeline.Latest.TryGetLatest(out var latest))
                _meter.Show(latest.RmsDbfs, latest.PeakDbfs, latest.Clipping, delta);

            _diagnosticsTimer -= delta;
            if (_diagnosticsTimer <= 0)
            {
                _diagnosticsTimer = 0.25;
                _diagnostics.Text = DiagnosticsText();
            }
        }

        _flashRemaining -= delta;
        _flash.Visible = _flashRemaining > 0;

        if (_loopbackTask is { IsCompleted: true })
        {
            _loopbackResult.Text = _loopbackTask.IsFaulted
                ? $"Loopback failed: {_loopbackTask.Exception?.GetBaseException().Message}"
                : $"Loopback: {_loopbackTask.Result}";
            _log?.Write("loopback", _loopbackResult.Text);
            _loopbackTask = null;
            _loopbackButton.Disabled = false;
        }
    }

    public override void _ExitTree() => StopCapture();

    // --- capture lifecycle ---

    private void StartCapture()
    {
        StopCapture();
        _path = (CapturePath)_pathPicker.Selected;
        _pipeline = new AnalysisPipeline(_config);
        _log = SessionLog.Create(ProjectSettings.GlobalizePath("user://sessions"));
        _log.WriteAnalyzer(_config);
        _log.Write("capture.path", _path == CapturePath.Native ? "native (PortAudio)" : "godot (AudioEffectCapture)");
        _log.Write("display.vsync", DisplayServer.WindowGetVsyncMode());

        try
        {
            if (_path == CapturePath.Native)
            {
                var device = _nativeInputs[_devicePicker.Selected];
                _native = NativeCapture.Open(device, _pipeline, _rawToggle.ButtonPressed);
                _log.Write("portaudio.version", DeviceCatalog.PortAudioVersion);
                _log.Write("device.name", device.Label);
                _log.Write("device.default_sample_rate", device.DefaultSampleRate);
                _log.Write("device.max_input_channels", device.MaxInputChannels);
                _log.Write("capture.format", _native.Format);
                _log.Write("capture.resampler_delay_samples", _native.ResamplerDelaySamples.ToString("0.0", CultureInfo.InvariantCulture));
                _log.Write("os_processing.raw", _native.Raw);
                _pipeline.Start();
                _native.Start();
                _status.Text = $"Capturing {device.Label}: {_native.Format}. Raw mode: {RawText(_native.Raw)}";
            }
            else
            {
                string device = GodotCapture.InputDevices()[_devicePicker.Selected];
                _pipeline.Start();
                _godot.Begin(device, _pipeline);
                _log.Write("device.name", _godot.DeviceName);
                _log.Write("capture.format", _godot.Format);
                _log.Write("godot.mix_rate", AudioServer.GetMixRate());
                _log.Write("godot.input_mix_rate", AudioServer.GetInputMixRate());
                _log.Write("os_processing.raw", "unknown (Godot path cannot request raw capture)");
                _status.Text = $"Capturing {_godot.DeviceName} through Godot: {_godot.Format}. Raw mode: not available on this path.";
            }
            _startButton.Text = "Stop";
            _trace.Clear();
        }
        catch (Exception e)
        {
            _log.Write("error", e.Message);
            _status.Text = $"Could not start capture: {e.Message}";
            StopCapture();
        }
    }

    private void StopCapture()
    {
        if (_printDiagnosticsOnStop && _pipeline is not null)
            GD.Print(DiagnosticsText());
        _native?.Dispose();
        _native = null;
        _godot.End();
        _pipeline?.Dispose();
        if (_pipeline is not null && _log is not null)
            _log.WriteDiagnostics("final", _pipeline.Diagnostics);
        _log?.Dispose();
        if (_log is not null)
            _status.Text += $"\nSession log: {_log.Path}";
        _log = null;
        _pipeline = null;
        _startButton.Text = "Start";
    }

    private void DrainFrames()
    {
        while (true)
        {
            int n = _pipeline!.Frames.Drain(_drain, out long dropped);
            if (dropped > 0)
                _trace.Clear();  // a gap: don't draw across it (spec §2)
            for (int i = 0; i < n; i++)
            {
                _trace.Add(_drain[i].RmsDbfs);
                if (_cameraToggle.ButtonPressed)
                    DetectClap(_drain[i].PeakDbfs);
            }
            if (n < _drain.Length)
                return;
        }
    }

    /// <summary>
    /// Camera test (spec §3.1): flash the screen when a transient reaches the
    /// analyzer. Film the clap and the screen at 240 fps; frames between them are
    /// user-to-photon. The flash fires off analyzed frames, so it includes the
    /// analyzer's algorithmic delay, as the game will.
    /// </summary>
    private void DetectClap(float peakDbfs)
    {
        float recentMax = _recentPeaks.Count == 0 ? LevelMeter.MinDb : _recentPeaks.Max();
        if (peakDbfs >= ClapMinPeakDbfs && peakDbfs - recentMax >= ClapJumpDb && _flashRemaining <= 0)
        {
            _flashRemaining = FlashSeconds;
            _flashCount++;
            _cameraHint.Text = $"Flashes: {_flashCount}. Film the clap and this window at 240 fps and count frames between them.";
        }
        _recentPeaks.Enqueue(float.IsNaN(peakDbfs) ? LevelMeter.MinDb : peakDbfs);
        while (_recentPeaks.Count > 20)  // 200 ms of frames
            _recentPeaks.Dequeue();
    }

    private string DiagnosticsText()
    {
        var d = _pipeline!.Diagnostics;
        string Ms(TimeSpan t) => t == TimeSpan.MaxValue ? ">100" : t.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"frames produced        {d.FramesProduced}",
            $"capture-to-result      p50 {Ms(d.CaptureToResultPercentile(50))}  p95 {Ms(d.CaptureToResultPercentile(95))}  max {Ms(d.CaptureToResultMax)} ms   (Gate B: p95 < 10 ms)",
            $"algorithmic delay      {_pipeline.Analyzer.AlgorithmicDelaySamples} samples = {_pipeline.Analyzer.AlgorithmicDelaySamples / 48.0:0.0} ms (constant, reported separately)",
            $"max analysis / frame   {d.MaxAnalysisTimePerFrame.TotalMilliseconds:0.000} ms",
            $"overruns               {d.OverrunCount} ({d.DroppedSamples} samples dropped)   frame-queue overruns {d.FrameQueueOverruns}   capture gaps {d.CaptureGaps}",
            $"latency not measured   {d.CaptureToResultMissed} frames (should stay 0)",
        };
        if (_native is not null)
        {
            var driver = _native.DriverReportedInputLatency;
            lines.Add($"native callbacks       {_native.Callbacks}   input overflows {_native.InputOverflows}   driver-reported input latency {(driver is { } t ? $"{t.TotalMilliseconds:0.0} ms" : "not reported")}");
        }
        else
        {
            lines.Add($"godot pulls            {_godot.Pulls}   largest pull {_godot.LargestPullFrames} frames (arrival = pull time; hides Godot's buffering)   buffer overflows {_godot.Overflows}");
        }
        return string.Join('\n', lines);
    }

    // --- loopback ---

    private void StartLoopback()
    {
        if (_loopbackTask is not null)
            return;
        if (_nativeOutputs.Count == 0 || _outputPicker.Selected < 0)
        {
            _loopbackResult.Text = "Loopback needs PortAudio output devices, and none were found.";
            return;
        }
        var input = LoopbackInput();
        if (input is null)
            return;
        var output = _nativeOutputs[_outputPicker.Selected];
        _loopbackButton.Disabled = true;
        _loopbackResult.Text = $"Running loopback {output.Label} → {input.Label} (~5 s)…";
        _loopbackTask = Task.Run(() => LoopbackTest.Run(input, output));
    }

    /// <summary>
    /// The loopback test runs through PortAudio on either path. On the Godot path,
    /// the selected Godot input is matched to its PortAudio device by name.
    /// </summary>
    private AudioDevice? LoopbackInput()
    {
        if (_pathPicker.Selected == (int)CapturePath.Native)
        {
            if (_devicePicker.Selected >= 0 && _devicePicker.Selected < _nativeInputs.Count)
                return _nativeInputs[_devicePicker.Selected];
            _loopbackResult.Text = "Pick an input device first.";
            return null;
        }

        string? godotDevice = _devicePicker.Selected >= 0 ? _devicePicker.GetItemText(_devicePicker.Selected) : null;
        var match = DeviceCatalog.MatchInput(_nativeInputs, godotDevice, DeviceCatalog.DefaultInput);
        if (match is null)
            _loopbackResult.Text = $"Loopback runs through PortAudio, which has no device matching '{godotDevice}'. Switch to the Native path to pick one.";
        return match;
    }

    // --- self-tests (headless) ---

    private void RunSelfTest(string spec)
    {
        var parts = spec.Split(',');
        double seconds = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 3;
        var device = DeviceCatalog.Inputs().First(d => d.Label.Contains(parts[0], StringComparison.OrdinalIgnoreCase));
        _pipeline = new AnalysisPipeline(_config);
        _native = NativeCapture.Open(device, _pipeline, requestRaw: true);
        _pipeline.Start();
        _native.Start();
        GD.Print($"selftest: {device.Label}: {_native.Format}, raw={_native.Raw}");
        GetTree().CreateTimer(seconds).Timeout += () =>
        {
            DrainFrames();
            GD.Print(DiagnosticsText());
            var d = _pipeline.Diagnostics;
            bool ok = d.FramesProduced > 0 && d.OverrunCount == 0 && d.CaptureGaps == 0
                      && _native.InputOverflows == 0 && d.CaptureToResultMissed == 0;
            StopCapture();
            GetTree().Quit(ok ? 0 : 1);
        };
    }

    private void RunLoopbackSelfTest(string spec)
    {
        var parts = spec.Split(',');
        var output = DeviceCatalog.Outputs().First(d => d.Label.Contains(parts[0], StringComparison.OrdinalIgnoreCase));
        var input = DeviceCatalog.Inputs().First(d => d.Label.Contains(parts[1], StringComparison.OrdinalIgnoreCase));
        var result = LoopbackTest.Run(input, output);
        GD.Print($"loopback {output.Label} -> {input.Label}: {result}");
        GetTree().Quit(result.Detected > 0 ? 0 : 1);
    }

    private static Dictionary<string, string> ParseUserArgs() =>
        OS.GetCmdlineUserArgs()
            .Where(a => a.StartsWith("--", StringComparison.Ordinal))
            .Select(a => a[2..].Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv.Length > 1 ? kv[1] : "");

    // --- UI ---

    private void RefreshDevices()
    {
        _devicePicker.Clear();
        // PortAudio lists are needed on both paths: the loopback test always uses them
        try
        {
            _nativeInputs = DeviceCatalog.Inputs();
            _nativeOutputs = DeviceCatalog.Outputs();
        }
        catch (Exception e)
        {
            _status.Text = $"PortAudio failed to load: {e.Message}";
            _nativeInputs = _nativeOutputs = [];
        }

        if (_pathPicker.Selected == (int)CapturePath.Native)
        {
            foreach (var d in _nativeInputs)
                _devicePicker.AddItem(d.Label);
            var preferred = DeviceCatalog.DefaultInput();
            if (preferred is not null)
                _devicePicker.Select(_nativeInputs.ToList().FindIndex(d => d.Index == preferred.Index));
            _rawToggle.Disabled = false;
        }
        else
        {
            foreach (string d in GodotCapture.InputDevices())
                _devicePicker.AddItem(d);
            _rawToggle.Disabled = true;
        }

        _outputPicker.Clear();
        foreach (var d in _nativeOutputs)
            _outputPicker.AddItem(d.Label);
        int cable = _nativeOutputs.ToList().FindIndex(d => d.Name.StartsWith("CABLE Input", StringComparison.Ordinal));
        if (cable >= 0)
            _outputPicker.Select(cable);
    }

    private void BuildUi()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 16);
        AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);

        column.AddChild(new Label { Text = "VoiceProbe — build step 3: capture → level meter" });

        var captureRow = new HBoxContainer();
        column.AddChild(captureRow);
        _pathPicker = new OptionButton();
        _pathPicker.AddItem("Native (PortAudio)");
        _pathPicker.AddItem("Godot (AudioEffectCapture)");
        _pathPicker.ItemSelected += _ => { StopCapture(); RefreshDevices(); };
        captureRow.AddChild(_pathPicker);
        _devicePicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, FitToLongestItem = false };
        captureRow.AddChild(_devicePicker);
        _rawToggle = new CheckBox { Text = "Request raw (WASAPI)", ButtonPressed = true };
        captureRow.AddChild(_rawToggle);
        _startButton = new Button { Text = "Start", CustomMinimumSize = new Vector2(90, 0) };
        _startButton.Pressed += () => { if (_pipeline is null) StartCapture(); else StopCapture(); };
        captureRow.AddChild(_startButton);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "Pick a device and press Start." };
        column.AddChild(_status);

        _meter = new LevelMeter();
        column.AddChild(_meter);
        column.AddChild(new Label { Text = "RMS, every frame (last 6 s)", Modulate = new Color(1, 1, 1, 0.6f) });
        _trace = new LevelTrace();
        column.AddChild(_trace);

        _diagnostics = new Label();
        _diagnostics.AddThemeFontOverride("font", new SystemFont { FontNames = ["Consolas", "Cascadia Mono", "monospace"] });
        _diagnostics.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_diagnostics);

        var loopbackRow = new HBoxContainer();
        column.AddChild(loopbackRow);
        loopbackRow.AddChild(new Label { Text = "Loopback out:" });
        _outputPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, FitToLongestItem = false };
        loopbackRow.AddChild(_outputPicker);
        _loopbackButton = new Button { Text = "Run loopback test" };
        _loopbackButton.Pressed += StartLoopback;
        loopbackRow.AddChild(_loopbackButton);
        _loopbackResult = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = "Loopback plays tone bursts on the output and finds them on the input device selected above. " +
                   "Use a cable, VB-Cable (CABLE Input → CABLE Output), or speakers into the mic.",
        };
        column.AddChild(_loopbackResult);

        _cameraToggle = new CheckBox { Text = "Camera test: flash the screen on a clap" };
        column.AddChild(_cameraToggle);
        _cameraHint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.7f) };
        column.AddChild(_cameraHint);

        _flash = new ColorRect { Color = Colors.White, Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _flash.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_flash);
    }

    private static string RawText(RawCaptureStatus raw) => raw switch
    {
        RawCaptureStatus.Requested => "requested (Windows applies it only if the device supports raw; not confirmed)",
        RawCaptureStatus.RejectedFellBack => "rejected, opened with OS processing",
        RawCaptureStatus.Unavailable => "unavailable on this host API (use a WASAPI device)",
        _ => "not requested",
    };
}
